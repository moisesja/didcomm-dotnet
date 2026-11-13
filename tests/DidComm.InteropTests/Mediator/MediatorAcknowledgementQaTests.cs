using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), the transport acknowledgement (FR-ROUTE-11): over HTTP nothing of a forward reaches the
/// queue until its 202 has been committed — for several forwards held at once, and never for one whose
/// request is aborted first; over the mediator's WebSocket a forward proceeds at once and writes
/// nothing back; a forward carrying <c>return_route: all</c> and <c>please_ack</c> never opens a
/// session and is never acknowledged (FR-ROUTE-07), whichever envelope and transport carry it.
/// </summary>
public sealed class MediatorAcknowledgementQaTests
{
    [Fact]
    public async Task Several_forwards_held_at_OnStarting_reach_the_store_zero_times_until_their_202s_are_committed()
    {
        Qa.RaiseThreadPoolFloor();
        var store = new QaParkingStore();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var packed = new List<string>();
        for (var i = 0; i < 5; i++)
            packed.Add((await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did, $"m{i}"), bob.Did))).Packed);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;

        var posts = packed.Select(p => Task.Run(() => mediator.PostAsync(p))).ToArray();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 5, "all five admitted while their responses are held");
        // FlushAsync waits for the parked acknowledgements, so it cannot be used here; a bounded look instead.
        (await ReturnRouteTestHost.WaitUntilAsync(() => store.Entered > 0 || mediator.Outcomes.Count > 0, TimeSpan.FromMilliseconds(300)))
            .Should().BeFalse("the sink never runs before the sender holds its 202");
        posts.Should().OnlyContain(p => !p.IsCompleted);

        gate.SetResult();
        (await Task.WhenAll(posts)).Should().OnlyContain(r => r.Status == HttpStatusCode.Accepted && r.Body.Length == 0);
        await mediator.FlushAsync();
        store.Entered.Should().Be(5);
        mediator.Outcomes.Should().Equal(Enumerable.Repeat(ForwardOutcomes.Queued, 5));
    }

    [Fact]
    public async Task A_forward_aborted_before_its_202_never_reaches_the_store_even_after_later_forwards_are_relayed()
    {
        var store = new QaParkingStore();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMessageStore>(store));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (aborted, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did, "aborted"), bob.Did));
        mediator.Controls.AbortNextPost = true;

        var act = async () => await mediator.PostAsync(aborted);
        await act.Should().ThrowAsync<Exception>();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the abort released the reservation");
        var kept = await Qa.SendNoteAsync(mediator, alice, bob, content: "kept");

        store.Entered.Should().Be(1, "only the acknowledged forward ever reached the store");
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnacknowledged, ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(kept.Id);
    }

    [Fact]
    public async Task Over_the_mediators_WebSocket_a_forward_is_relayed_at_once_and_nothing_is_written_back()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));

        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count == 1, "relayed without any acknowledgement to wait for");
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        // The socket is anonymous (anoncrypt): a pickup request from Bob on it gets the first frame back.
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(1, "the forward wrote nothing back before this answer");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_forward_with_return_route_all_and_please_ack_opens_no_session_and_is_never_acknowledged(bool authcryptForward, bool overWebSocket)
    {
        var network = new MediatorNetwork();
        await using var inbox = await InboxServer.StartAsync(network);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Network = network);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        // Alice has her own endpoint: an acknowledgement sent "back" would arrive there.
        await using var alice = await QaSender.CreateAsync(mediator, webSocket: false, new DidCommServiceSpec(inbox.Endpoint("/alice").ToString(), Accept: new[] { "didcomm/v2" }));
        var note = alice.Note(bob.Did);
        var inner = (await alice.Client.PackEncryptedAsync(note, new PackEncryptedOptions(new[] { bob.Did }, From: alice.Did))).Message;
        var forward = ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner });
        forward.PleaseAck = new List<string> { string.Empty };
        forward.CreatedTime = mediator.Clock.Now.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(forward, ReturnRouteMode.All, thread: null);
        var packed = (await alice.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(new[] { mediator.Did }, From: authcryptForward ? alice.Did : null))).Message;

        if (overWebSocket)
        {
            using var socket = await mediator.ConnectAsync();
            await ReturnRouteTestHost.SendAsync(socket, packed);
            await MediatorTestServer.WaitUntilAsync(() => mediator.Outcomes.Count == 1, "relayed");
            var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
            (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(1, "no frame was written for the forward");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }
        else
        {
            var response = await mediator.PostAsync(packed);
            response.Status.Should().Be(HttpStatusCode.Accepted, "a forward never gets a session, so never a 200 carrying an answer");
            response.Body.Should().BeEmpty();
        }
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        inbox.Entered.Should().Be(0, "please_ack on a forward is ignored (FR-ROUTE-07)");
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        var unpacked = await bob.Client.UnpackAsync(Encoding.UTF8.GetString(delivered.Payload.Span));
        unpacked.Message.Id.Should().Be(note.Id);
        unpacked.SenderKid.Should().StartWith(alice.Did + "#");
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_forward_is_outside_replay_admission_even_authcrypted_with_return_route_so_each_copy_is_relayed()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var forward = ForwardMessage.Create(mediator.Did, bob.Did, new[] { await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did) });
        forward.CreatedTime = mediator.Clock.Now.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(forward, ReturnRouteMode.All, thread: null);
        var packed = (await alice.Client.PackEncryptedAsync(forward, new PackEncryptedOptions(new[] { mediator.Did }, From: alice.Did))).Message;

        await Qa.PostForwardAsync(mediator, packed);
        await Qa.PostForwardAsync(mediator, packed);
        using var socket = await mediator.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await mediator.FlushAsync();

        // As specified (FR-RR-08: forward is exempt from replay admission even with return_route), each
        // copy is relayed: a captured forward replays into the recipient's quota (see QA-2 report).
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(3);
    }
}
