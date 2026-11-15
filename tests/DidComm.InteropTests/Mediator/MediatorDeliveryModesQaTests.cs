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
        // The live-on wake-up is served while the session is open (#119): served after the close, it
        // would find no session and count Push-Skipped-No-Live-Session.
        await GoLiveAsync(f.Mediator, socket, f.Bob, waitForLiveOnWakeUp: true);
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

    /// <summary>Where the abort of a live connection lands relative to a live push.</summary>
    public enum AbortPoint
    {
        /// <summary>The push reached the peer: the message is leased to the session, and that lease ends with it.</summary>
        AfterThePushArrived,

        /// <summary>The push operation has started for the message (parked in its lease): the session is gone when it sends.</summary>
        WhileThePushTakesTheMessage,

        /// <summary>The connection is gone before the forward arrives: nothing is pushed.</summary>
        BeforeTheForwardArrives,
    }

    [Theory]
    [InlineData(AbortPoint.AfterThePushArrived)]
    [InlineData(AbortPoint.WhileThePushTakesTheMessage)]
    [InlineData(AbortPoint.BeforeTheForwardArrives)]
    public async Task A_live_session_aborted_around_the_push_never_loses_the_message(AbortPoint abort)
    {
        // Each order the abort can land in, forced (#119; L-044: drive the interleaving instead of
        // hoping for it): the forward is always stored (Queued), it ends with at most one push outcome,
        // and it can be fetched on a new connection — a live lease ends with its session.
        LeaseGatedStore? store = null;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => LeaseGatedStore.Register(s, x => store = x));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var socket = await mediator.ConnectAsync();
        // The live-on wake-up is served first: still running, it could take the forward while the
        // forward's own wake-up is held, and that wake-up would then add a second outcome.
        await GoLiveAsync(mediator, socket, bob, waitForLiveOnWakeUp: true);

        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        ParkedCall? take = null;
        if (abort == AbortPoint.BeforeTheForwardArrives)
            await AbortAsync(mediator, socket, bob);
        else if (abort == AbortPoint.WhileThePushTakesTheMessage)
            take = store!.ParkLiveLease();
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        if (abort == AbortPoint.AfterThePushArrived)
        {
            (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle();
            await AbortAsync(mediator, socket, bob);
        }
        else if (take is not null)
        {
            await take.Arrived.WaitAsync(MediatorTestServer.Timeout);
            await AbortAsync(mediator, socket, bob);
            take.Release();
        }
        await mediator.PushesIdleAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        switch (abort)
        {
            case AbortPoint.AfterThePushArrived:
                mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive);
                break;
            case AbortPoint.WhileThePushTakesTheMessage:
                // The session may still take the frame between leaving the registry and closing (the
                // frame is then discarded and the lease ends with the session), or refuse it.
                mediator.PushOutcomes.Should().ContainSingle().Which.Should().BeOneOf(PushOutcomes.PushedLive, PushOutcomes.Failed);
                break;
            default:
                mediator.PushOutcomes.Should().BeEmpty("with no live session nothing is woken");
                break;
        }
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

        static async Task AbortAsync(MediatorTestServer mediator, WebSocket socket, TestPeer bob)
        {
            socket.Abort();
            socket.Dispose();
            await WaitForNoLiveSessionAsync(mediator, bob.Did);
        }
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
        // A peer that stops reading makes the mediator's send wait once its TCP window is full.
        // TestHost's socket never makes a send wait (#119), so the mediator's side of the socket is
        // held here: its writer stays in m0's send. The session then takes MaxPendingPushes more frames,
        // queued behind that send; every later push finds no free slot and is refused (Push-Failed, the
        // operation ends, the message stays queued, and the oldest refused one is tried again at each
        // later wake-up). Each message is pushed live (one frame on the socket, leased to it) or stays
        // queued (fetchable), never both, never neither; once the peer reads again, the slots come back.
        const int Sent = 8;
        const int MaxPendingPushes = 2;
        var sockets = new GatedSockets();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.Sessions.MaxPendingPushes = MaxPendingPushes;
            o.ConfigurePipeline = sockets.Install;
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob, waitForLiveOnWakeUp: true);
        var server = await sockets.AcceptedAsync(0);
        server.Hold();

        // The peer stops reading: m0's frame is in the mediator's send and stays there. Then one
        // message at a time, each push served before the next message arrives.
        var sent = new List<string> { (await Qa.SendNoteAsync(mediator, alice, bob, content: "m0")).Id };
        await MediatorTestServer.WaitUntilAsync(() => server.SendsWaiting == 1, "m0's frame is in the mediator's send");
        await mediator.PushesIdleAsync();
        for (var i = 1; i < Sent; i++)
        {
            sent.Add((await Qa.SendNoteAsync(mediator, alice, bob, content: $"m{i}")).Id);
            await mediator.PushesIdleAsync();
        }

        const int Live = 1 + MaxPendingPushes;
        const int Queued = Sent - Live;
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(Sent);
        mediator.PushOutcomes.Should().Equal(
            Enumerable.Repeat(PushOutcomes.PushedLive, Live).Concat(Enumerable.Repeat(PushOutcomes.Failed, Queued)),
            "the frame in the send and the queued ones are pushed; every later push finds no free slot");
        server.SendsWaiting.Should().Be(1, "nothing more was sent while the peer did not read");

        // The peer reads again: exactly the pushed frames arrive, in order.
        server.Open();
        var received = new List<DeliveredMessage>();
        for (var i = 0; i < Live; i++)
            received.Add((await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject);
        var pushedIds = new List<string>();
        foreach (var m in received)
            pushedIds.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, m.Payload, alice)).Message.Id);
        pushedIds.Should().Equal(sent.Take(Live), "the pushed frames are the first messages, once each");

        // Never both, never neither: before any acknowledgement, a delivery-request hands out exactly the
        // refused messages (the pushed ones are leased to the socket, the refused ones are not).
        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var fetched = (await mediator.AskOverSocketAsync(socket, bob, request)).Delivery(request.Id);
        fetched.Select(m => m.Id).Should().NotIntersectWith(received.Select(m => m.Id));
        var fetchedIds = new List<string>();
        foreach (var m in fetched)
            fetchedIds.Add((await MediatorRecipient.UnpackFromSenderAsync(bob, m.Payload, alice)).Message.Id);
        fetchedIds.Should().BeEquivalentTo(sent.Skip(Live), "every refused message is queued for pickup");
        (await AckOverSocketAsync(mediator, socket, bob, received.Concat(fetched).Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);

        // The slots came back: the next message is pushed live.
        var late = await Qa.SendNoteAsync(mediator, alice, bob, content: "late");
        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, pushed.Payload, alice)).Message.Id.Should().Be(late.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes[^1].Should().Be(PushOutcomes.PushedLive);
        (await AckOverSocketAsync(mediator, socket, bob, pushed.Id)).MessageCount.Should().Be(0);
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

    /// <summary>
    /// Turn live delivery on over <paramref name="socket"/>; returns on the <c>status</c> that answers it.
    /// Turning live delivery on wakes the grant, and that wake-up's push operation may still be running
    /// when the answer arrives: a message stored right after can then be taken by it while the message's
    /// own wake-up is held, which adds an outcome when that push fails or the session is gone (#119).
    /// <paramref name="waitForLiveOnWakeUp"/> also waits until that wake-up was served (the grant woke
    /// before the answer was built, so the push workers are not idle until it is), so the test's own
    /// wake-ups are not coalesced with it (L-044) — what <c>PushFixture.GoLiveAsync</c> does in the unit tests.
    /// </summary>
    internal static async Task GoLiveAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, bool waitForLiveOnWakeUp = false)
    {
        var live = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, peer, live)).Status(live.Id).LiveDelivery.Should().BeTrue();
        if (waitForLiveOnWakeUp)
            await mediator.PushesIdleAsync();
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
