using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), ending a peer's mediation with <c>MessagePickupQueue.RevokeAndPurgeAsync</c> while its
/// messages are in every state the pipeline can hold them in — queued, leased by a delivery-request,
/// pushed live and not acknowledged, on their way to a direct push — and while a forward for it is
/// admitted but not yet relayed; then the same peer granted again, and a revoked peer's recipient DID
/// moved to another grant.
/// </summary>
public sealed class MediatorRevocationQaTests
{
    [Fact]
    public async Task Revoke_and_purge_removes_queued_leased_and_live_pushed_messages_and_nothing_of_them_is_delivered_afterwards()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var b = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob, bob.Did, b.Did);
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);
        var alice = await mediator.NewSenderAsync();

        // Leased over HTTP, then one queued, then one pushed live and left unacknowledged.
        await Qa.SendNoteAsync(mediator, alice, bob, content: "leased");
        var leased = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        await Qa.SendNoteAsync(mediator, alice, b, content: "queued");
        // L-020 (PR #109 review round 2, D-1): a live push takes only what was stored since live
        // delivery went on; the test clock moves so "queued" is stored strictly before it.
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        await Qa.SendNoteAsync(mediator, alice, bob, content: "live");
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await Qa.SendNoteAsync(mediator, alice, carol, content: "carol's");
        mediator.Store.MessageCount.Should().Be(4);

        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 3));
        mediator.Store.MessageCount.Should().Be(1, "Carol's message is untouched");

        // Bob's late acknowledgements and requests find nothing; new forwards for his DIDs are dropped.
        (await mediator.AcknowledgeAsync(bob, leased.Id, pushed.Id)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
        await Qa.SendNoteAsync(mediator, alice, b, content: "after");
        mediator.Outcomes.Last().Should().Be(ForwardOutcomes.DroppedUnknown);
        // Nothing was pushed to the live socket: its next frame answers this request.
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(0);
        (await mediator.StatusAsync(carol)).MessageCount.Should().Be(1);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(b.Did, leased.Id, pushed.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_direct_push_already_on_the_wire_completes_but_leaves_nothing_behind_after_revoke_and_purge()
    {
        await using var f = await QaPushFixture.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the push is on the wire");
        (await f.Mediator.Queue.RevokeAndPurgeAsync(f.Bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 1));
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();

        f.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.Pushed }, "documented residual: a push already being sent completes");
        f.Mediator.Store.MessageCount.Should().Be(0, "and nothing is resurrected by the late removal");
        (await f.Mediator.FetchAsync(f.Bob)).Should().BeEmpty();
        f.ShouldNotLeak();
    }

    [Fact]
    public async Task A_push_job_waiting_when_the_grant_is_revoked_is_never_sent()
    {
        await using var f = await QaPushFixture.StartAsync(m => m.PushConcurrency = 1);
        var carol = await f.Mediator.NewPeerAsync();
        await f.Mediator.EnrolAsync(carol);
        f.Directory.Map(carol.Did, f.Inbox.Endpoint("/carol"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        await Qa.SendNoteAsync(f.Mediator, f.Alice, carol, content: "carol's, parks the worker");
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the only worker is parked");
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "bob's, waits");
        (await f.Mediator.Queue.RevokeAndPurgeAsync(f.Bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 1));
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();

        // L-020 (PR #109 review round 2, D-1): the purge removed Bob's message, so his wake-up finds
        // nothing to push, which is no outcome (it was Push-Skipped-Unregistered for the job).
        f.Mediator.PushOutcomes.Should().Equal(PushOutcomes.Pushed);
        f.Inbox.Received.Should().ContainSingle().Which.Path.Should().Be("/carol");
        f.Mediator.Store.MessageCount.Should().Be(0);
        f.ShouldNotLeak(carol.Did);
    }

    [Fact]
    public async Task A_waiting_push_job_of_a_purged_message_is_not_sent_after_the_peer_is_granted_again()
    {
        await using var f = await QaPushFixture.StartAsync(m => m.PushConcurrency = 1);
        var carol = await f.Mediator.NewPeerAsync();
        await f.Mediator.EnrolAsync(carol);
        f.Directory.Map(carol.Did, f.Inbox.Endpoint("/carol"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Inbox.Gate = gate;

        await Qa.SendNoteAsync(f.Mediator, f.Alice, carol, content: "carol's, parks the worker");
        await MediatorTestServer.WaitUntilAsync(() => f.Inbox.Entered == 1, "the only worker is parked");
        await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B, content: "bob's, purged while its job waits");
        (await f.Mediator.Queue.RevokeAndPurgeAsync(f.Bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 1));
        (await f.Mediator.RequestMediationAsync(f.Bob)).Should().Equal(f.Mediator.Did);
        await f.Mediator.AddRecipientsAsync(f.Bob, new[] { f.B.Did });
        gate.SetResult();
        await f.Mediator.PushesIdleAsync();

        f.Inbox.Received.Select(r => r.Path).Should().Equal(new[] { "/carol" }, "the purged message is not delivered after the purge");
    }

    [Fact]
    public async Task A_forward_admitted_before_the_revoke_and_relayed_after_it_is_dropped_and_not_kept()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;

        var post = Task.Run(() => mediator.PostAsync(packed));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 1, "admitted: its next was registered when it arrived");
        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 0));
        gate.SetResult();
        (await post).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnknown);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Revoke_and_purge_in_the_middle_of_a_burst_of_forwards_leaves_nothing_held_for_the_revoked_grant()
    {
        // Invariant for every interleaving (L-044): once the revoke has returned and every admitted
        // forward has been relayed, nothing is held for the revoked grant, and each forward ended
        // with exactly one outcome — Queued (stored before the purge, then purged) or Dropped-Unknown.
        Qa.RaiseThreadPoolFloor();
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol);
        var alice = await mediator.NewSenderAsync();
        var forBob = new List<string>();
        for (var i = 0; i < 40; i++)
            forBob.Add((await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did, $"b{i}"), bob.Did))).Packed);
        var (forCarol, _) = await alice.ForwardAsync(mediator.Did, carol.Did, await alice.AuthcryptAsync(alice.Note(carol.Did), carol.Did));

        var posts = forBob.Select((p, i) => Task.Run(async () =>
        {
            if (i == 20)
                await mediator.Queue.RevokeAndPurgeAsync(bob.Did);
            await Qa.PostForwardAsync(mediator, p);
        })).ToList();
        await Task.WhenAll(posts);
        await Qa.PostForwardAsync(mediator, forCarol);
        await mediator.FlushAsync();

        (await mediator.Mediation.GetGrantAsync(bob.Did, CancellationToken.None)).Should().BeNull();
        mediator.Store.MessageCount.Should().Be(1, "only Carol's message is held");
        (await mediator.StatusAsync(carol)).MessageCount.Should().Be(1);
        mediator.Outcomes.Should().HaveCount(41);
        mediator.Outcomes.Should().OnlyContain(o => o == ForwardOutcomes.Queued || o == ForwardOutcomes.DroppedUnknown);
        // Whatever was stored before the purge is gone: grant Bob again and nothing old shows up.
        await mediator.EnrolAsync(bob);
        (await mediator.FetchAsync(bob)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_peer_granted_again_gets_only_what_arrives_for_its_new_grant()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, bob, content: "before");
        var old = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        await Qa.SendNoteAsync(mediator, alice, bob, content: "before, queued");

        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 2));
        await Qa.SendNoteAsync(mediator, alice, bob, content: "in between");
        await mediator.EnrolAsync(bob);
        await Qa.SendNoteAsync(mediator, alice, bob, content: "after");

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.DroppedUnknown, ForwardOutcomes.Queued);
        (await mediator.AcknowledgeAsync(bob, old.Id)).MessageCount.Should().Be(1, "an id of the old grant acknowledges nothing of the new one");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Body!["content"]!.GetValue<string>().Should().Be("after");
        mediator.LeakingLogLines(old.Id, delivered.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_peer_whose_recipient_DID_moved_to_another_grant_never_gets_its_old_messages_when_granted_again()
    {
        // The host revokes through the store alone (no purge), the DID is registered by Carol, and
        // Bob is granted again: the records still held under Bob's key name a DID he no longer owns.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var b = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob, b.Did);
        var alice = await mediator.NewSenderAsync();
        await Qa.SendNoteAsync(mediator, alice, b, content: "for B under Bob");
        (await mediator.Mediation.RevokeAsync(bob.Did, CancellationToken.None)).Should().BeTrue();

        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(carol, b.Did);
        await mediator.EnrolAsync(bob, bob.Did);

        (await mediator.FetchAsync(bob)).Should().BeEmpty("B belongs to Carol now");
        (await mediator.StatusAsync(bob, b.Did)).MessageCount.Should().Be(0);
        (await mediator.FetchAsync(carol)).Should().BeEmpty("the message was accepted under Bob's grant");
        await Qa.SendNoteAsync(mediator, alice, b, content: "for B under Carol");
        var delivered = (await mediator.FetchAsync(carol)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, delivered.Payload, alice)).Message.Body!["content"]!.GetValue<string>().Should().Be("for B under Carol");
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }
}
