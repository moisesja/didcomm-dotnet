using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// Live mode and the WebSocket under Message Pickup 4.0 (#100; 4.0 L28, L85, L302-L308): the real
/// <see cref="PickupClient"/> pinned to 4.0 against the real <c>AddMediator()</c> host. A forward
/// that arrives while the stream is live is pushed as a 4.0 <c>delivery</c> without <c>thid</c> and
/// acknowledged one way (no <c>return_route</c>, stamped, nothing answers it); a 3.0 and a 4.0
/// stream live on one mediator at once each get only their own pushes, in their own version; a
/// message whose acknowledgement never came is queued again when the connection closes, or, with the
/// connection open, when its lease runs out; a stream whose session
/// socket is closed mid-way keeps delivering under 4.0; an idle live socket outlasts many
/// <c>MessageReceiveTimeout</c> limits on both the mediator's and the client's clock; a 4.0 fetch over
/// the WebSocket acknowledges one way on the session socket; and live mode over HTTP is refused by
/// the client before anything is sent.
/// </summary>
public sealed class PickupV4LiveEndToEndTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    // Not the default (60 s): the transports are configured, so the idle wait spans ten of their limits.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(37);

    [Fact]
    public async Task A_message_pushed_live_under_4_0_arrives_as_a_delivery_without_thid_and_is_acknowledged_one_way()
    {
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true,
            o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen));
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        // Live is on (its dispatch ended) and the drain asked once: what arrives now is pushed.
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream's drain asked once");
        observer.PickupKindsFrom(bob.Did).Should().Equal("4.0/live-on", "4.0/delivery-request");

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "live");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "it arrived while live delivery was on");

        // What reached the client's dispatcher: a 4.0 delivery with one attachment and no thread (L34, L263).
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        var push = clientSeen.From(mediator.Did).Should().ContainSingle(r => r.Kind == "4.0/delivery" && r.Attachments == 1).Subject;
        push.Thid.Should().BeNull("a live delivery answers no request");

        // Asking for the next item acknowledges it: one way, without return_route, stamped; nothing answers it.
        var next = PickupE2E.MoveNext(stream);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "the pushed message was acknowledged");
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received", "the acknowledgement was dispatched");
        var ack = observer.From(bob.Did).Single(r => r.Kind == "4.0/messages-received");
        ack.ReturnRoute.Should().Be(ReturnRouteMode.None, "4.0 removed return_route from messages-received (L35)");
        ack.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "the send path stamps it by protocol (FR-RR-09)");
        await PickupE2E.StopAsync(stream, next, cts);

        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/live-off", "the stream turns live delivery off at its end");
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        clientSeen.From(mediator.Did).Should().NotContain(r => string.Equals(r.Thid, ack.Id, StringComparison.OrdinalIgnoreCase), "nothing answers a 4.0 messages-received (L294)");
        observer.PickupKindsFrom(bob.Did).Should().OnlyContain(k => k.StartsWith("4.0/", StringComparison.Ordinal), "a pinned stream speaks 4.0 only");
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_3_0_and_a_4_0_live_stream_for_different_grants_on_one_mediator_each_get_only_their_own_pushes_in_their_own_version()
    {
        // The live framing is the session's (#100 threat model "cross-version"): two recipients with
        // their own grants, one live in 3.0 and one in 4.0, on the same mediator at the same time.
        var observer = new PickupWireObserver();
        var seen3 = new PickupWireObserver();
        var seen4 = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3, webSocket: true,
            o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(seen3));
        await using var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true,
            o => o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(seen4));
        var (bob3, r3) = await PickupE2E.EnrolAsync(mediator, harness3);
        var (bob4, r4) = await PickupE2E.EnrolAsync(mediator, harness4);
        var alice = await harness3.NewAliceAsync();

        using var cts3 = new CancellationTokenSource();
        using var cts4 = new CancellationTokenSource();
        var stream3 = harness3.Pickup.ReceiveLiveAsync(mediator.Did, bob3.Did, harness3.WebSocketEndpoint, cts3.Token).GetAsyncEnumerator();
        var stream4 = harness4.Pickup.ReceiveLiveAsync(mediator.Did, bob4.Did, harness4.WebSocketEndpoint, cts4.Token).GetAsyncEnumerator();
        var first3 = PickupE2E.MoveNext(stream3);
        var first4 = PickupE2E.MoveNext(stream4);
        await observer.WaitForAsync(bob3.Did, r => r.Kind == "3.0/delivery-request", "the 3.0 stream is live and drained once");
        await observer.WaitForAsync(bob4.Did, r => r.Kind == "4.0/delivery-request", "the 4.0 stream is live and drained once");

        var to4 = await PickupE2E.SendAsync(mediator, alice, r4, "for the 4.0 stream");
        var to3 = await PickupE2E.SendAsync(mediator, alice, r3, "for the 3.0 stream");
        (await PickupE2E.Within(first4)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream4.Current, alice, r4, to4);
        (await PickupE2E.Within(first3)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream3.Current, alice, r3, to3);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2, "each arrived while its recipient's stream was live");

        var next3 = PickupE2E.MoveNext(stream3);
        var next4 = PickupE2E.MoveNext(stream4);
        await Qa2.StoreHoldsAsync(mediator, 0, "both pushes were acknowledged");
        await PickupE2E.StopAsync(stream3, next3, cts3);
        await PickupE2E.StopAsync(stream4, next4, cts4);

        // Each client's dispatcher got exactly one push, in its own version's delivery.
        await harness3.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        await harness4.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        seen3.From(mediator.Did).Should().ContainSingle(r => r.Attachments == 1).Which.Kind.Should().Be("3.0/delivery");
        seen4.From(mediator.Did).Should().ContainSingle(r => r.Attachments == 1).Which.Kind.Should().Be("4.0/delivery");
        seen4.From(mediator.Did).Single(r => r.Attachments == 1).Thid.Should().BeNull("a 4.0 live delivery answers no request (L34)");
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob3.Did).Should().OnlyContain(k => k.StartsWith("3.0/", StringComparison.Ordinal));
        observer.PickupKindsFrom(bob4.Did).Should().OnlyContain(k => k.StartsWith("4.0/", StringComparison.Ordinal));
        mediator.LeakingLogLines(r3.Did, r4.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_message_not_acknowledged_before_the_connection_closed_is_queued_again_and_a_later_fetch_delivers_it()
    {
        // 4.0 L308: "If a message is not acknowledged, the message MUST be added to the queue for
        // later pickup." The clock never moves: the lease ends with the connection, not by time.
        LeaseRecordingStore store = null!;
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigureServices = s => LeaseRecordingStore.Register(s, x => store = x));
        await using var later = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true);
        TestPeer bob, recipient;
        PickupItem taken;
        await using (harness)
        {
            (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
            later.Hold(bob);
            later.Hold(recipient);
            var alice = await harness.NewAliceAsync();
            var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator();
            var first = PickupE2E.MoveNext(stream);
            await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream's drain asked once");
            var note = await PickupE2E.SendAsync(mediator, alice, recipient, "never acknowledged");
            (await PickupE2E.Within(first)).Should().BeTrue();
            taken = stream.Current;
            PickupE2E.ShouldBeAlicesNote(taken, alice, recipient, note);
            await mediator.PushesIdleAsync();
            mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "precondition: it was pushed live, under the session's lease");

            // While the connection is open the message stays leased: another fetch finds nothing to deliver.
            var during = await later.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
            during.Items.Should().BeEmpty("the pushed message is leased to the live session until it is acknowledged");
            store.OwnerReleases.Should().NotContain(n => n > 0, "precondition: no lease was given back yet");

            // Ends without asking for another item: the last item is never acknowledged.
            await stream.DisposeAsync().AsTask().WaitAsync(MediatorTestServer.Timeout);
            store.Inner.MessageCount.Should().Be(1, "the item was handed over, not acknowledged");
        } // the container goes: its WebSocket closes, and with it the mediator's session

        // The session's leases are given back when the mediator sees the close: wait for that, then fetch once.
        await MediatorTestServer.WaitUntilAsync(() => store.OwnerReleases.Contains(1), "the closed session's lease went back to the queue");
        var again = await PickupE2E.Within(later.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));

        again.Items.Should().ContainSingle().Which.Id.Should().Be(taken.Id, "the same queued message, delivered again");
        PickupV4E2E.Opened(again.Items[0]).Message.Id.Should().Be(taken.Unpacked!.Message.Id);
        store.Inner.MessageCount.Should().Be(0, "this time it was acknowledged");
    }

    [Fact]
    public async Task A_live_message_left_unacknowledged_on_an_open_connection_is_queued_again_when_its_lease_runs_out()
    {
        // 4.0 L308 without a close: the connection stays open and the stream keeps the item without
        // acknowledging it. The mediator's clock (DidCommOptions.Clock, which times leases) passes
        // the lease, and the message is delivered to the next fetch. The client's clock never moves
        // (its stream's next cycle is a day away), so nothing on the client fetches it meanwhile.
        var clientTime = new QaManualTimeProvider();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = clientTime;
            o.Client = c => c.LivePollInterval = TimeSpan.FromHours(24);
        });
        await using var other = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        other.Hold(bob);
        other.Hold(recipient);
        var alice = await harness.NewAliceAsync();
        var lease = new MessagePickupOptions().LeaseTimeout;

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream's drain asked once");
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "left unacknowledged");
        (await PickupE2E.Within(first)).Should().BeTrue();
        var pushed = stream.Current;
        PickupE2E.ShouldBeAlicesNote(pushed, alice, recipient, note);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "precondition: it was pushed live, under the session's lease");

        mediator.Clock.Advance(lease - TimeSpan.FromSeconds(1));
        var within = await PickupE2E.Within(other.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));
        mediator.Clock.Advance(TimeSpan.FromSeconds(2));
        var after = await PickupE2E.Within(other.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));

        within.Items.Should().BeEmpty("inside its lease the pushed message is the live session's");
        after.Items.Should().ContainSingle("the lease ran out: the message went back to the queue for later pickup").Which.Id.Should().Be(pushed.Id);
        PickupV4E2E.Opened(after.Items[0]).Message.Id.Should().Be(note.Id);
        mediator.Store.MessageCount.Should().Be(0, "the fetch acknowledged it");

        // The stream's late acknowledgement of the same id finds nothing left to remove and harms nothing.
        // (The other container's fetch acknowledged it too, as Bob: count the acknowledgements so far first.)
        await mediator.FlushObserversAsync();
        var acks = observer.From(bob.Did).Count(r => r.Kind == "4.0/messages-received");
        var next = PickupE2E.MoveNext(stream);
        await MediatorTestServer.WaitUntilAsync(() => observer.From(bob.Did).Count(r => r.Kind == "4.0/messages-received") == acks + 1, "the stream acknowledged the item it held");
        observer.From(bob.Did).Last(r => r.Kind == "4.0/messages-received").Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal(new[] { pushed.Id }, "it names the item the stream held");
        harness.Connects.Should().Be(1, "one connection throughout: the lease ran out while it was open");
        await PickupE2E.StopAsync(stream, next, cts);
        mediator.Store.MessageCount.Should().Be(0);
        observer.PickupKindsFrom(bob.Did).Should().OnlyContain(k => k.StartsWith("4.0/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_4_0_stream_whose_session_socket_is_closed_mid_way_keeps_delivering()
    {
        // The mediator closes the client's session socket mid-stream (another socket of Bob's takes
        // the only session slot). The item already handed over is acknowledged one way on a fresh
        // socket; a message that arrived meanwhile waits in the queue; the next cycle turns live
        // delivery on again and drains it with a 4.0 delivery-request; and pushes flow again.
        var time = new QaManualTimeProvider();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o =>
            o.ConfigureServices = s => s.AddOptions<ReturnRouteSessionOptions>().Configure(x => x.MaxSessionsPerPeer = 1));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = time;
            o.Client = c => c.LivePollInterval = Poll;
        });
        var transport = harness.Services.GetRequiredService<WebSocketDidCommTransport>();
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream's first drain asked");
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m1);
        await mediator.PushesIdleAsync();

        using var raw = await mediator.ConnectAsync();
        var takeover = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(raw, bob, takeover)).Status40(takeover.Id);
        await MediatorTestServer.WaitUntilAsync(() => transport.LiveSessionSocketCount == 0, "the client noticed its session socket was closed");

        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "no live session is left for Bob: m2 waits in the queue");

        var mark = time.Mark();
        var next = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 1, "m1 was acknowledged");
        await time.WaitForTimerAsync(mark, Poll, MediatorTestServer.Timeout);
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0, "nothing is waiting for an answer");
        next.IsCompleted.Should().BeFalse();
        time.Advance(Poll);
        (await PickupE2E.Within(next)).Should().BeTrue("the next cycle turned live delivery on again and drained what waited");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m2);

        var third = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 0, "m2 was acknowledged");
        var m3 = await PickupE2E.SendAsync(mediator, alice, recipient, "m3");
        (await PickupE2E.Within(third)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m3);
        var last = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 0, "m3 was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);

        await mediator.FlushObserversAsync();
        var kinds = observer.PickupKindsFrom(bob.Did);
        kinds.Should().OnlyContain(k => k.StartsWith("4.0/", StringComparison.Ordinal));
        kinds.Count(k => k == "4.0/live-on").Should().BeGreaterThanOrEqualTo(2, "the cycle turned live delivery on again");
        kinds.Count(k => k == "4.0/messages-received").Should().BeGreaterThanOrEqualTo(3, "m1, m2 and m3 were each acknowledged");
        observer.From(bob.Did).Where(r => r.Kind == "4.0/messages-received").Should().OnlyContain(r => r.ReturnRoute == ReturnRouteMode.None);
    }

    [Fact]
    public async Task An_idle_4_0_live_socket_outlasts_many_receive_limits_on_the_mediators_and_the_clients_clock_and_gets_the_next_push()
    {
        // Both ends time only a message that has started; the wait for the next one is never timed
        // (HH, FR-TRN-09). Both clocks are hand-moved (L-065): ten limits pass on each, and no timer
        // fires or is created for the idle wait on either side.
        var serverTime = new QaManualTimeProvider();
        var clientTime = new QaManualTimeProvider();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o =>
        {
            // The mediator's purge runs every 5 minutes by default: once a day keeps it out of the
            // idle wait, so the only timers that could fire are the ones the test is about.
            o.Mediator = m => m.PurgeInterval = TimeSpan.FromHours(24);
            o.ConfigureServices = s => s.AddSingleton<TimeProvider>(serverTime);
            o.PostConfigureServices = s => s.AddOptions<DidCommReceiveOptions>().Configure(r => r.MessageReceiveTimeout = Limit);
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = clientTime;
            o.WebSocket = w => w.MessageReceiveTimeout = Limit;
            // Far beyond the idle wait, so the stream's periodic cycle never fires during it.
            o.Client = c => c.LivePollInterval = TimeSpan.FromHours(24);
        });
        mediator.GlobalReceiveOptions.MessageReceiveTimeout.Should().Be(Limit, "precondition: the mediator's sockets have the limit");
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "live-on, then the drain's delivery-request");

        // Wait for the client itself: no request waiting and one timer, the stream's next cycle.
        await MediatorTestServer.WaitUntilAsync(
            () => harness.Channel.PendingCount == 0 && clientTime.HasOnlyTimerDueIn(TimeSpan.FromHours(24)),
            "the stream finished its first cycle, and the only client timer left is its next cycle");
        serverTime.HasOnlyTimerDueIn(TimeSpan.FromHours(24)).Should().BeTrue("precondition: the mediator's only timer is its daily purge");
        var clientMark = clientTime.Mark();
        var serverMark = serverTime.Mark();
        var clientFired = clientTime.TimersFired;
        var serverFired = serverTime.TimersFired;

        serverTime.Advance(10 * Limit);
        clientTime.Advance(10 * Limit);

        serverTime.TimersFired.Should().Be(serverFired, "the mediator's wait for the session socket's next message is not timed");
        clientTime.TimersFired.Should().Be(clientFired, "the client's wait for the next push is not timed");
        serverTime.Mark().Should().Be(serverMark, "no mediator timer was created for the idle wait");
        clientTime.Mark().Should().Be(clientMark, "no client timer was created for the idle wait");
        first.IsCompleted.Should().BeFalse("nothing was queued yet");

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "after a long idle");
        (await PickupE2E.Within(first)).Should().BeTrue("the arrival reaches the idle stream");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "it was pushed live on the idle socket, not fetched by a cycle");

        var next = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 0, "the push was acknowledged");
        await PickupE2E.StopAsync(stream, next, cts);
        observer.PickupKindsFrom(bob.Did).Should().OnlyContain(k => k.StartsWith("4.0/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_4_0_fetch_over_the_WebSocket_acknowledges_one_way_on_the_session_socket_stamped()
    {
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "over the socket");

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10, serviceEndpointOverride: harness.WebSocketEndpoint);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "the one-way acknowledgement was applied");
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received", "the acknowledgement was dispatched");
        var kinds = observer.PickupKindsFrom(bob.Did);
        kinds.Should().Equal("4.0/delivery-request", "4.0/messages-received");
        var records = observer.From(bob.Did).Where(r => r.Kind.StartsWith("4.0/", StringComparison.Ordinal)).ToArray();
        records[0].ReturnRoute.Should().Be(ReturnRouteMode.All, "the request opens the session");
        records[1].ReturnRoute.Should().Be(ReturnRouteMode.None, "the acknowledgement carries no return_route (L35)");
        records[1].CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "and is stamped all the same (FR-RR-09)");
        harness.Connects.Should().Be(1, "it rode the session socket (FR-RR-07)");
        (await harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Live_mode_over_HTTP_is_refused_by_the_client_before_anything_is_sent()
    {
        var posts = 0;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigurePipeline = app => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            Interlocked.Increment(ref posts);
            await next(context);
        }));
        await using var pinned = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        await using var asking = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate);
        var bob = await pinned.NewPeerAsync();
        asking.Hold(bob);
        await mediator.EnrolAsync(bob);
        var before = Volatile.Read(ref posts);

        foreach (var pickup in new[] { pinned.Pickup, asking.Pickup })
        {
            await FluentActions.Awaiting(() => pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, true)).Should().ThrowAsync<InvalidOperationException>();
            await using var stream = pickup.ReceiveLiveAsync(mediator.Did, bob.Did).GetAsyncEnumerator();
            await FluentActions.Awaiting(() => PickupE2E.Within(stream.MoveNextAsync())).Should().ThrowAsync<InvalidOperationException>();
        }

        Volatile.Read(ref posts).Should().Be(before, "the route is refused before anything is sent: not even the version question (L306)");
        PickupV4E2E.NegotiatedVersions(mediator.Logs).Should().BeEmpty();
    }
}
