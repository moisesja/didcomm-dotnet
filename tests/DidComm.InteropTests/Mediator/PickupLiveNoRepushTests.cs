using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// "Never push it live again" (owner decision on #100, 2026-11-20, #88): a message pushed live and
/// returned unconfirmed waits until the recipient asks for it, under Message Pickup 3.0 and 4.0
/// alike, since one mailbox serves both (4.0 L302: "Live Mode is the practice of delivering newly
/// arriving messages directly to a connected recipient."; L304: "Messages already in the queue are
/// not affected by Live Mode; they MUST still be requested with delivery-request messages."; L308:
/// "If a message is not acknowledged, the message MUST be added to the queue for later pickup."; 3.0
/// L203). Against the real <c>AddMediator()</c> + <c>MapDidCommMediator</c> host over its WebSocket,
/// with hand-rolled requests: a push whose lease runs out with the connection open is not pushed
/// again when a newer message arrives, which is pushed; a delivery-request still gets it; a push
/// left unacknowledged by a connection that closes is not pushed to the recipient's other live
/// connection, nor after a reconnect; a push the session did not take is pushed at the next
/// wake-up; and what came back counts against the grant's quota as before.
/// </summary>
public sealed class PickupLiveNoRepushTests
{
    private static readonly TimeSpan LeaseTimeout = new MessagePickupOptions().LeaseTimeout;

    public static TheoryData<string> Versions => new() { "3.0", "4.0" };

    // ---- 1-3. the lease runs out with the connection open -------------------------------------------

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_push_whose_lease_ran_out_unacknowledged_is_not_pushed_again_when_a_newer_message_arrives_and_a_delivery_request_gets_it(string version)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob, version);

        var m1 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m1");
        var p1 = await PushAsync(mediator, socket, bob, version);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1);

        // The connection stays open; the push is never acknowledged and its lease runs out (L308).
        mediator.Clock.Advance(LeaseTimeout + TimeSpan.FromSeconds(1));

        // A newer message wakes the grant: it is pushed, and m1 is not pushed again (before or after it).
        var m2 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m2");
        var p2 = await PushAsync(mediator, socket, bob, version);
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { p2 })).Should().Equal(new[] { m2.Id }, "the newer message is pushed live; the returned one is not pushed first");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2, "m1 was pushed once, m2 once");

        // The next frame answers this request (nothing more was pushed): a delivery-request gets m1.
        var fetched = await FetchAsync(mediator, socket, bob, version);
        fetched.Select(m => m.Id).Should().Equal(new[] { p1.Id }, "the returned message waits for the recipient to ask for it (L304); m2 is still leased to its push");
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(m1.Id);
        (await AckAsync(mediator, socket, bob, version, p1.Id, p2.Id)).Should().Be(0, "the acknowledgement removes both");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);
        Qa2.MessageCount(mediator).Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- 4. the connection closes with the push unacknowledged ----------------------------------------

    public static TheoryData<string, string> VersionPairs => new()
    {
        { "3.0", "3.0" },
        { "4.0", "4.0" },
        { "3.0", "4.0" },
        { "4.0", "3.0" },
    };

    [Theory]
    [MemberData(nameof(VersionPairs))]
    public async Task A_push_left_unacknowledged_by_a_connection_that_closed_is_not_pushed_to_another_live_connection_nor_after_a_reconnect(string olderVersion, string newerVersion)
    {
        // Bob has two live connections; the newer one takes the pushes. When it goes away without
        // acknowledging m1, m1 goes back to the mailbox (L308). The older connection is still live,
        // and its live mark is older than m1, so before the owner's ruling m1 was pushed to it at the
        // next arrival; now only a delivery-request gets m1 — on any connection, a reconnect included.
        Qa2LiveStore store = null!;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => Qa2LiveStore.Register(s, x => store = x));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var older = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, older, bob, olderVersion);
        var newer = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, newer, bob, newerVersion);

        var m1 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m1");
        var p1 = await PushAsync(mediator, newer, bob, newerVersion);
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { p1 })).Should().Equal(new[] { m1.Id }, "precondition: the newest live connection took the push");
        store.OwnerReleases.Should().BeEmpty("precondition: its session is open");

        newer.Abort();
        newer.Dispose();
        await MediatorTestServer.WaitUntilAsync(() => store.OwnerReleases.Count == 1, "the mediator saw the session close and gave its leases back");
        store.OwnerReleases.Should().Equal(new[] { 1 }, "the push went back to the mailbox unacknowledged (L308)");

        var m2 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m2");
        var p2 = await PushAsync(mediator, older, bob, olderVersion);
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { p2 })).Should().Equal(new[] { m2.Id }, "the older live connection gets the new arrival, not the returned push");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);

        // A reconnect: live again, a newer arrival is pushed to it, the returned push is not.
        using var again = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, again, bob, newerVersion);
        var m3 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m3");
        var p3 = await PushAsync(mediator, again, bob, newerVersion);
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { p3 })).Should().Equal(m3.Id);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(3);

        var fetched = await FetchAsync(mediator, again, bob, newerVersion);
        fetched.Select(m => m.Id).Should().Equal(new[] { p1.Id }, "a delivery-request gets the returned push (m2 and m3 are leased to their pushes)");
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(m1.Id);
        (await AckAsync(mediator, again, bob, newerVersion, p1.Id, p2.Id, p3.Id)).Should().Be(0);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(3, "nothing was pushed twice");
        await again.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await older.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- 5. a push the session did not take ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_push_the_session_did_not_take_never_reached_the_connection_and_is_pushed_at_the_next_wake_up(string version)
    {
        // Two push slots, and the mediator's side of the socket held (TestHost never back-pressures,
        // #119): m0 is in the writer's send, m1 and m2 take the two slots, and m3 finds none — the
        // session does not take it, and the push gives it back unmarked. Once the peer has read
        // everything (so both slots are free again), the next arrival's wake-up pushes m3 and then
        // the newer message.
        var sockets = new GatedSockets();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.Sessions.MaxPendingPushes = 2;
            o.ConfigurePipeline = sockets.Install;
        });
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob, version);
        var server = await sockets.AcceptedAsync(0);
        server.Hold();

        var sent = new List<Message> { await Qa.SendNoteAsync(mediator, alice, bob, content: "m0") };
        await MediatorTestServer.WaitUntilAsync(() => server.SendsWaiting == 1, "m0's frame is in the mediator's send");
        await mediator.PushesIdleAsync();
        for (var i = 1; i <= 3; i++)
        {
            sent.Add(await Qa.SendNoteAsync(mediator, alice, bob, content: $"m{i}"));
            await mediator.PushesIdleAsync();
        }
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(3, "m0, m1 and m2 were taken");
        mediator.PushOutcome(PushOutcomes.Failed).Should().Be(1, "precondition: the session had no free push slot for m3");
        server.SendsWaiting.Should().Be(1);

        server.Open();
        var taken = new List<DeliveredMessage>();
        for (var i = 0; i < 3; i++)
            taken.Add(await PushAsync(mediator, socket, bob, version));
        (await PickupV4Qa2.InnerIdsAsync(bob, taken)).Should().Equal(sent.Take(3).Select(m => m.Id));

        var m4 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m4");
        var later = new[] { await PushAsync(mediator, socket, bob, version), await PushAsync(mediator, socket, bob, version) };
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, later)).Should().Equal(new[] { sent[3].Id, m4.Id }, "m3 was never pushed: the next wake-up pushes it, oldest first");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(5);

        (await FetchAsync(mediator, socket, bob, version)).Should().BeEmpty("every message is leased to its push");
        (await AckAsync(mediator, socket, bob, version, taken.Concat(later).Select(m => m.Id).ToArray())).Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- 6. the quota --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task What_came_back_from_a_live_push_counts_against_the_grants_quota_as_before(string version)
    {
        const int Quota = 2;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.MaxQueueMessages = Quota);
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob, version);

        var m1 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m1");
        var m2 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m2");
        var pushed = new[] { await PushAsync(mediator, socket, bob, version), await PushAsync(mediator, socket, bob, version) };
        await mediator.PushesIdleAsync();
        mediator.Clock.Advance(LeaseTimeout + TimeSpan.FromSeconds(1)); // both came back unacknowledged

        await Qa.SendNoteAsync(mediator, alice, bob, content: "over the quota");
        await mediator.PushesIdleAsync();
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(1, "the grant is at its quota: what came back from a live push counts like any held message");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(Quota, "nothing was pushed again");
        (await StatusCountAsync(mediator, socket, bob, version)).Should().Be(Quota, "the status counts them as before");
        Qa2.MessageCount(mediator).Should().Be(Quota);

        var fetched = await FetchAsync(mediator, socket, bob, version);
        fetched.Select(m => m.Id).Should().Equal(pushed.Select(m => m.Id));
        (await PickupV4Qa2.InnerIdsAsync(bob, fetched)).Should().Equal(m1.Id, m2.Id);
        (await AckAsync(mediator, socket, bob, version, fetched.Select(m => m.Id).ToArray())).Should().Be(0);

        // The quota is free again, and a new arrival is pushed live as ever.
        var m4 = await Qa.SendNoteAsync(mediator, alice, bob, content: "m4");
        var p4 = await PushAsync(mediator, socket, bob, version);
        await mediator.PushesIdleAsync();
        (await PickupV4Qa2.InnerIdsAsync(bob, new[] { p4 })).Should().Equal(m4.Id);
        (await AckAsync(mediator, socket, bob, version, p4.Id)).Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- steps over a raw socket, in either version ----------------------------------------------------

    // Turn live delivery on; returns on the status that answers it, once the wake-up it caused was served.
    private static async Task GoLiveAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string version)
    {
        if (version == "4.0")
        {
            await PickupV4Qa2.GoLive40Async(mediator, socket, peer);
            return;
        }
        var live = MessagePickup.CreateLiveDeliveryChange(peer.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, peer, live)).Status(live.Id).LiveDelivery.Should().BeTrue();
        await mediator.PushesIdleAsync();
    }

    // The next frame on the socket, read as a live push (a delivery on no thread) of one message.
    private static async Task<DeliveredMessage> PushAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string version)
    {
        var answer = await mediator.ReceiveOverSocketAsync(socket, peer);
        var delivered = version == "4.0" ? answer.Delivery40(thid: null) : answer.Delivery(thid: null);
        return delivered.Should().ContainSingle("a live push carries one message").Subject;
    }

    // A delivery-request; the next frame answers it. An empty list when nothing could be delivered.
    private static async Task<IReadOnlyList<DeliveredMessage>> FetchAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string version)
    {
        if (version == "4.0")
            return await PickupV4Qa2.Fetch40OverSocketAsync(mediator, socket, peer);
        var request = MessagePickup.CreateDeliveryRequest(peer.Did, mediator.Did, 10);
        var answer = await mediator.AskOverSocketAsync(socket, peer, request);
        if (answer.Message.Type == MessagePickup.StatusType)
        {
            answer.Status(request.Id);
            return Array.Empty<DeliveredMessage>();
        }
        return answer.Delivery(request.Id);
    }

    // messages-received as the version sends it, then the count the mediator reports. This is the
    // test's own exchange on one raw socket, not PickupClient.AckAsync (which asks for no status,
    // OD-6): under 4.0 the count comes from a status-request sent after the acknowledgement on the
    // same socket, and the mediator handles one connection's messages in order, so it counts the
    // queue after the acknowledgement. Under 3.0 it is the acknowledgement's own answer.
    private static async Task<int> AckAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string version, params string[] ids)
    {
        if (version == "4.0")
            return (await PickupV4Qa2.Ack40OverSocketAsync(mediator, socket, peer, ids)).MessageCount;
        var ack = MessagePickup.CreateMessagesReceived(peer.Did, mediator.Did, ids);
        return (await mediator.AskOverSocketAsync(socket, peer, ack)).Status(ack.Id).MessageCount;
    }

    private static async Task<int> StatusCountAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, string version)
    {
        if (version == "4.0")
        {
            var request40 = MessagePickup40.CreateStatusRequest(peer.Did, mediator.Did);
            return (await mediator.AskOverSocketAsync(socket, peer, request40)).Status40(request40.Id).MessageCount;
        }
        var request = MessagePickup.CreateStatusRequest(peer.Did, mediator.Did);
        return (await mediator.AskOverSocketAsync(socket, peer, request)).Status(request.Id).MessageCount;
    }
}
