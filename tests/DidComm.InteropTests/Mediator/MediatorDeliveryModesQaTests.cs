using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// QA-2 (#95), delivery modes end to end: live push over the mediator's WebSocket and the queue — the
/// D6 order (live first, then the queue), a session that closes before or during a push, two live
/// sessions, and a live session that stops reading. (A message whose expiry or age limit passes while
/// the grant's push waits: MediatorLivePushAdv4Tests.)
/// </summary>
public sealed class MediatorDeliveryModesQaTests
{
    // ---- D6: live first, then the queue ----------------------------------------------------------

    [Fact]
    public async Task A_live_session_takes_the_message_first()
    {
        // L-020 (PR #109 review R1-2): the forward is now only stored by the forward pump (Queued) and
        // the push workers push it live (Pushed-Live); the old Delivered-Live forward outcome and the
        // empty push counter described the in-pump live push.
        await using var f = await QaPushFixture.StartAsync();
        using var socket = await f.Mediator.ConnectAsync();
        await GoLiveAsync(f.Mediator, socket, f.Bob);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        var pushed = (await f.Mediator.ReceiveOverSocketAsync(socket, f.Bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, pushed.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        f.Mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.PushedLive }, "the live session takes it, once");
        (await AckOverSocketAsync(f.Mediator, socket, f.Bob, pushed.Id)).MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        f.ShouldNotLeak(pushed.Id);
    }

    [Fact]
    public async Task After_the_live_session_closes_the_next_message_is_queued_for_pickup()
    {
        await using var f = await QaPushFixture.StartAsync();
        var socket = await f.Mediator.ConnectAsync();
        await GoLiveAsync(f.Mediator, socket, f.Bob);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        socket.Dispose();
        await WaitForNoLiveSessionAsync(f.Mediator, f.Bob.Did);

        var note = await Qa.SendNoteAsync(f.Mediator, f.Alice, f.B);
        await f.Mediator.PushesIdleAsync();

        f.Mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        f.Mediator.PushOutcomes.Should().BeEmpty("with no live session nothing is woken, and the message waits for pickup");
        f.Mediator.Store.MessageCount.Should().Be(1);
        var delivered = (await f.Mediator.FetchAsync(f.Bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(f.B, delivered.Payload, f.Alice)).Message.Id.Should().Be(note.Id);
        f.ShouldNotLeak(delivered.Id);
    }

    [Fact]
    public async Task A_live_session_aborted_around_the_push_never_loses_the_message()
    {
        // Whichever side wins (the push is written before the server notices the abort, or the
        // session is gone first), the message ends with one outcome and can be fetched on a new
        // connection: a live lease ends with its session (L-044: assert the invariant, not the race).
        // L-020 (PR #109 review R1-2): the forward is always stored (Queued) and at most one push job
        // follows (none when the session was already gone), so the race is now on the push counter.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob);

        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var post = mediator.PostAsync(packed);
        socket.Abort();
        socket.Dispose();
        (await post).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().HaveCountLessThanOrEqualTo(1, "one outcome for the push job, when the session was still there to get one");
        IReadOnlyList<DeliveredMessage> fetched = Array.Empty<DeliveredMessage>();
        (await ReturnRouteTestHost.WaitUntilAsync(() =>
        {
            fetched = mediator.FetchAsync(bob).GetAwaiter().GetResult();
            return fetched.Count > 0;
        }, MediatorTestServer.Timeout)).Should().BeTrue("the message is fetchable once the aborted session's lease has ended");
        var delivered = fetched.Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await mediator.AcknowledgeAsync(bob, delivered.Id)).MessageCount.Should().Be(0);
        mediator.LeakingLogLines(delivered.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task With_two_live_sessions_each_message_goes_to_exactly_one_the_newest_and_then_the_older_once_the_newest_closes()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var older = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, older, bob);
        var newer = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, newer, bob);

        var first = await Qa.SendNoteAsync(mediator, alice, bob, content: "first");
        var onNewer = (await mediator.ReceiveOverSocketAsync(newer, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, onNewer.Payload, alice)).Message.Id.Should().Be(first.Id);
        // The older socket's next frame is the answer to its own request: nothing was pushed to it.
        (await AckOverSocketAsync(mediator, older, bob, onNewer.Id)).MessageCount.Should().Be(0);

        await newer.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        newer.Dispose();
        await WaitForLiveSessionCountAsync(mediator, bob.Did, 1);

        var second = await Qa.SendNoteAsync(mediator, alice, bob, content: "second");
        var onOlder = (await mediator.ReceiveOverSocketAsync(older, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, onOlder.Payload, alice)).Message.Id.Should().Be(second.Id);
        (await AckOverSocketAsync(mediator, older, bob, onOlder.Id)).MessageCount.Should().Be(0);

        // L-020 (PR #109 review R1-2): stored by the forward pump, pushed live by the push workers.
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        await older.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(onNewer.Id, onOlder.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_session_that_stops_reading_takes_what_its_push_bound_allows_and_the_rest_is_queued_never_both()
    {
        // Invariant for every interleaving: each message is pushed live (one frame on the socket) or
        // stays queued (fetchable, not leased to the socket), never both, never neither.
        // L-020 (PR #109 review R1-2): every forward is now counted Queued when stored; whether it was
        // pushed live is the push counter's Pushed-Live (one push job per message, all eight fit the
        // grant's share of the push backlog).
        const int Sent = 8;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Sessions.MaxPendingPushes = 2);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob);

        var sent = new List<string>();
        for (var i = 0; i < Sent; i++)
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, flush: false, content: $"m{i}")).Id);
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(Sent);
        mediator.PushOutcomes.Should().HaveCount(Sent);
        var live = (int)mediator.PushOutcome(PushOutcomes.PushedLive);
        var queued = Sent - live;
        live.Should().BeGreaterThan(0);

        var received = new List<DeliveredMessage>();
        for (var i = 0; i < live; i++)
            received.AddRange((await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null));
        received.Should().HaveCount(live);
        (await AckOverSocketAsync(mediator, socket, bob, received.Select(m => m.Id).ToArray())).MessageCount.Should().Be(queued);

        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer = await mediator.AskOverSocketAsync(socket, bob, request);
        var fetched = queued == 0 ? Array.Empty<DeliveredMessage>() : answer.Delivery(request.Id);
        fetched.Should().HaveCount(queued);
        var all = received.Concat(fetched).ToList();
        all.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        var ids = new List<string>();
        foreach (var m in all)
            ids.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, m.Payload, alice)).Message.Id);
        ids.Should().BeEquivalentTo(sent);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task A_live_push_left_unacknowledged_on_a_session_that_closes_is_fetchable_from_the_other_session()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var older = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, older, bob);
        var newer = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, newer, bob);

        var note = await Qa.SendNoteAsync(mediator, alice, bob);
        var pushed = (await mediator.ReceiveOverSocketAsync(newer, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        await newer.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        newer.Dispose();
        await WaitForLiveSessionCountAsync(mediator, bob.Did, 1);

        IReadOnlyList<DeliveredMessage> got = Array.Empty<DeliveredMessage>();
        (await ReturnRouteTestHost.WaitUntilAsync(() =>
        {
            var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
            var answer = mediator.AskOverSocketAsync(older, bob, request).GetAwaiter().GetResult();
            got = answer.Message.Type == MessagePickup.DeliveryType ? answer.Delivery(request.Id) : Array.Empty<DeliveredMessage>();
            return got.Count > 0;
        }, MediatorTestServer.Timeout)).Should().BeTrue("the closed session's lease ended");
        var again = got.Should().ContainSingle().Subject;
        again.Id.Should().Be(pushed.Id, "the same record, delivered again (at least once)");
        (await MediatorRecipient.UnpackFromSenderAsync(bob, again.Payload, alice)).Message.Id.Should().Be(note.Id);
        (await AckOverSocketAsync(mediator, older, bob, again.Id)).MessageCount.Should().Be(0);
        await older.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    internal static async Task GoLiveAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer)
    {
        var live = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, peer, live)).Status(live.Id).LiveDelivery.Should().BeTrue();
    }

    internal static async Task<PickupStatus> AckOverSocketAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, params string[] ids)
    {
        var ack = MessagePickup.CreateMessagesReceived(peer.Did, mediator.Did, ids);
        return (await mediator.AskOverSocketAsync(socket, peer, ack)).Status(ack.Id);
    }

    internal static Task WaitForNoLiveSessionAsync(MediatorTestServer mediator, string peerDid) => WaitForLiveSessionCountAsync(mediator, peerDid, 0);

    internal static Task WaitForLiveSessionCountAsync(MediatorTestServer mediator, string peerDid, int count)
    {
        var registry = mediator.App.Services.GetRequiredService<DidComm.Transports.ReturnRoute.IReturnRouteSessionRegistry>();
        return MediatorTestServer.WaitUntilAsync(() => (registry.Find(peerDid)?.Count(s => s.IsLive) ?? 0) == count, "the server saw the socket close");
    }
}

/// <summary>A mediator with Bob enrolled with recipient DID B, and Alice.</summary>
internal sealed class QaPushFixture : IAsyncDisposable
{
    public required MediatorTestServer Mediator { get; init; }
    public required TestPeer Bob { get; init; }
    public required TestPeer B { get; init; }
    public required MediatorSender Alice { get; init; }

    public static async Task<QaPushFixture> StartAsync(Action<MediatorOptions>? mediator = null)
    {
        var server = await MediatorTestServer.StartAsync(o => o.Mediator = mediator);
        var bob = await server.NewPeerAsync();
        (await server.RequestMediationAsync(bob)).Should().Equal(server.Did);
        var b = await server.NewRecipientAsync();
        await server.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await server.NewSenderAsync();
        return new QaPushFixture { Mediator = server, Bob = bob, B = b, Alice = alice };
    }

    /// <summary>No DID or key id in any log line.</summary>
    public void ShouldNotLeak(params string[] extra)
        => Mediator.LeakingLogLines(extra.Append(B.Did).ToArray()).Should().BeEmpty();

    public ValueTask DisposeAsync() => Mediator.DisposeAsync();
}
