using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 for #100 (live mode, leases and transports under Message Pickup 4.0): the real
/// <see cref="PickupClient"/> against the real <c>AddMediator()</c> host, with the gaps the existing
/// suites leave. Which of the mediator's sockets carried a one-way acknowledgement, and that nothing
/// answered it (4.0 L294); a push left unacknowledged when its session closes, before and after the
/// session took it, comes back for its own grant only and the new connection starts with live mode
/// off (L302, L308); one recipient live in both versions on two connections; a push in the other
/// version on a stream's own session (ignored, not acknowledged, delivered after its lease); the
/// count limit and the byte budget of a 4.0 fetch together (L255); a message that is not encrypted
/// never reaching a live stream (#100 OD-1); a stream's one-way acknowledgement refused for too many
/// waiting requests; and <see cref="PickupClient.AckAsync"/> under 4.0 over HTTP and over a WebSocket
/// with and without a session socket.
/// </summary>
public sealed class PickupV4LiveQa2Tests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);
    private const string OtherVersionIgnored = "A live push of another Message Pickup version than the stream speaks was ignored";

    // ---- 1. the one-way acknowledgement of a live push ---------------------------------------------

    [Fact]
    public async Task Qa2_A_4_0_live_push_is_acknowledged_one_way_on_the_streams_own_session_socket_and_nothing_answers_the_acknowledgement()
    {
        var sockets = new CountingSockets();
        var requests = new Qa2RequestCounter();
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigurePipeline = app =>
        {
            requests.Install(app);
            sockets.Install(app);
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream is live and drained once");
        var session = await sockets.AcceptedAsync(0);
        var posts = requests.Posts;

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "acknowledged one way");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        var pushedId = stream.Current.Id;

        // Asking for the next item takes it: the acknowledgement goes one way.
        var next = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received", "the acknowledgement was dispatched");
        await Qa2.StoreHoldsAsync(mediator, 0, "the acknowledgement removed the message");

        // A status-request on the same session socket. The mediator reads that socket in order and
        // writes on it first in, first out, and the client reads it in order too: an answer to the
        // acknowledgement would be dispatched at the client before this status.
        var probe = await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint));
        probe.MessageCount.Should().Be(0);
        await mediator.FlushObserversAsync();
        // L-065: the client's correlator completes the call before its dispatcher hands the answer to
        // the observers, so a flush right after the call can miss it: wait for the record asserted.
        var probeRequestId = observer.From(bob.Did).Last(r => r.Kind == "4.0/status-request").Id;
        await clientSeen.WaitForAsync(mediator.Did, r => r.Kind == "4.0/status" && string.Equals(r.Thid, probeRequestId, StringComparison.OrdinalIgnoreCase), "the probe's status reached the client's observers");
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);

        var ack = observer.From(bob.Did).Single(r => r.Kind == "4.0/messages-received");
        ack.ReturnRoute.Should().Be(ReturnRouteMode.None, "4.0 removed return_route from messages-received (L35)");
        ack.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "stamped by protocol (FR-RR-09)");
        ack.Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(pushedId);
        var probeRequest = observer.From(bob.Did).Last(r => r.Kind == "4.0/status-request");
        var answers = clientSeen.From(mediator.Did);
        answers.Select(r => r.Kind).Should().Equal(new[] { "4.0/status", "4.0/delivery", "4.0/delivery", "4.0/status" },
            "live-on's status, the drain's empty delivery, the push and the probe's status: nothing answered the acknowledgement (L294)");
        answers[2].Thid.Should().BeNull("a live delivery answers no request (L34)");
        answers[3].Thid.Should().Be(probeRequest.Id);
        answers.Should().NotContain(r => string.Equals(r.Thid, ack.Id, StringComparison.OrdinalIgnoreCase));

        // On the wire: one socket, the stream's session socket, carried every request; nothing went over HTTP.
        sockets.Count.Should().Be(1);
        harness.Connects.Should().Be(1);
        requests.Upgrades.Should().Be(1);
        session.MessagesReceived.Should().Be(4, "live-on, the drain, the acknowledgement and the probe all arrived on the session socket");
        requests.Posts.Should().Be(posts + 1, "only Alice's forward was POSTed");

        await PickupE2E.StopAsync(stream, next, cts);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    // ---- 2 and 4. a session that closes with a push unacknowledged -----------------------------------

    public static TheoryData<string, string> Closes => new()
    {
        { "before the session took the push", "the client goes away" },
        { "before the session took the push", "the mediator closes the session" },
        { "after the session took the push", "the client goes away" },
        { "after the session took the push", "the mediator closes the session" },
    };

    [Theory]
    [MemberData(nameof(Closes))]
    public async Task Qa2_A_4_0_push_left_unacknowledged_when_its_session_closes_is_queued_again_for_its_own_grant_only_and_the_new_connection_starts_with_live_mode_off(string moment, string closer)
    {
        // 4.0 L308: "If a message is not acknowledged, the message MUST be added to the queue for
        // later pickup." L302: "If Live Mode is active, and the connection is broken, a new inbound
        // connection starts with Live Mode disabled." Both clocks stay still: every lease ends with
        // its session, never by time. "Before": the push has started (its operation is parked before
        // it reads the store) when the session closes. "After": the session took the push and the
        // client holds it, unacknowledged.
        var before = moment.StartsWith("before", StringComparison.Ordinal);
        var byMediator = closer.Contains("mediator", StringComparison.Ordinal);
        Qa2LiveStore store = null!;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s =>
        {
            Qa2LiveStore.Register(s, x => store = x);
            if (byMediator)
                s.AddOptions<ReturnRouteSessionOptions>().Configure(x => x.MaxSessionsPerPeer = 1);
        });
        var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
        });
        var disposed = false;
        WebSocket? raw = null;
        try
        {
            var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
            var carol = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(carol);
            var alice = await harness.NewAliceAsync();
            var registry = mediator.App.Services.GetRequiredService<IReturnRouteSessionRegistry>();

            using var cts = new CancellationTokenSource();
            var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
            var first = PickupE2E.MoveNext(stream);
            await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && registry.Find(bob.Did).Count == 1, "the stream is live and drained");
            await mediator.PushesIdleAsync(); // the wake-up of live-on was served, so the park below is Alice's push

            var park = before ? store.ParkNextLiveLease() : null;
            var note = await PickupE2E.SendAsync(mediator, alice, recipient, "left unacknowledged");
            string? pushedId = null;
            if (park is not null)
            {
                await park.Arrived.WaitAsync(MediatorTestServer.Timeout);
            }
            else
            {
                (await PickupE2E.Within(first)).Should().BeTrue();
                PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
                pushedId = stream.Current.Id;
                await mediator.PushesIdleAsync();
                mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "precondition: the session took the push");
            }
            store.OwnerReleases.Should().BeEmpty("precondition: the session is open");

            // The session closes; its push is not acknowledged.
            if (byMediator)
            {
                // Another connection of Bob's takes the only session slot, so the mediator closes the stream's.
                raw = await mediator.ConnectAsync();
                var takeover = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
                var counted = (await mediator.AskOverSocketAsync(raw, bob, takeover)).Status40(takeover.Id);
                counted.LiveDelivery.Should().BeFalse("4.0 L302: a new inbound connection starts with Live Mode disabled");
                counted.MessageCount.Should().Be(1);
            }
            else
            {
                disposed = true;
                await harness.DisposeAsync();
            }
            await MediatorTestServer.WaitUntilAsync(() => store.OwnerReleases.Count >= 1, "the mediator saw the stream's session close and gave its leases back");

            if (park is not null)
            {
                park.Release();
                await mediator.PushesIdleAsync();
                mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(0);
                mediator.PushOutcome(PushOutcomes.Failed).Should().Be(1, "the push found its session closed and gave the record back");
                // The close found nothing taken (0); the push, finding its session closed, ended the
                // lease it then took the way the close ends it (#100 ADV4-1): by owner, so it counts
                // as returned (1).
                await MediatorTestServer.WaitUntilAsync(() => store.OwnerReleases.Count == 2, "the push's own release by owner ran");
                store.OwnerReleases.Should().Equal(new[] { 0, 1 }, "the session had taken nothing when it closed, and the push gave back the one it took after");
            }
            else
            {
                store.OwnerReleases.Should().Equal(new[] { 1 }, "the closed session gave back the push it had taken (L308)");
            }

            // Another grant never gets it, though it is in the queue now.
            (await PickupV4Qa2.Fetch40Async(mediator, carol)).Should().BeEmpty("the message is Bob's grant's alone");

            // A later delivery-request of Bob's, on a new connection, gets it.
            raw ??= await mediator.ConnectAsync();
            var again = (await PickupV4Qa2.Fetch40OverSocketAsync(mediator, raw, bob)).Should().ContainSingle("the message was added to the queue for later pickup (L308)").Subject;
            if (pushedId is not null)
                again.Id.Should().Be(pushedId, "the same queued message, delivered again (at least once)");
            (await PickupV4Qa2.InnerIdsAsync(recipient, new[] { again })).Should().Equal(note.Id);
            var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
            var afterFetch = (await mediator.AskOverSocketAsync(raw, bob, status)).Status40(status.Id);
            afterFetch.LiveDelivery.Should().BeFalse("4.0 L302: the new connection has not turned live mode on");
            afterFetch.MessageCount.Should().Be(1, "delivered again, not yet acknowledged");

            // With live mode off on the new connection, a new arrival is queued, not pushed: the next
            // frame on the socket is the answer to the next request.
            var pushedBefore = mediator.PushOutcome(PushOutcomes.PushedLive);
            var later = await PickupE2E.SendAsync(mediator, alice, recipient, "while live mode is off");
            await mediator.PushesIdleAsync();
            mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(pushedBefore);
            var rest = await PickupV4Qa2.Fetch40OverSocketAsync(mediator, raw, bob);
            (await PickupV4Qa2.InnerIdsAsync(recipient, rest)).Should().Equal(later.Id);
            (await PickupV4Qa2.Ack40OverSocketAsync(mediator, raw, bob, again.Id, rest[0].Id)).MessageCount.Should().Be(0);
            Qa2.MessageCount(mediator).Should().Be(0);
            mediator.LeakingLogLines(recipient.Did, carol.Did).Should().BeEmpty();

            if (disposed || first.IsCompleted)
                await stream.DisposeAsync().AsTask().WaitAsync(MediatorTestServer.Timeout); // the item yielded is not taken
            else
                await PickupE2E.StopAsync(stream, first, cts);
        }
        finally
        {
            raw?.Dispose();
            if (!disposed)
                await harness.DisposeAsync();
        }
    }

    // ---- 7. one recipient, both versions ------------------------------------------------------------

    [Fact]
    public async Task Qa2_One_recipient_live_in_3_0_and_4_0_on_two_connections_gets_each_push_on_its_newest_live_connection_in_that_connections_version()
    {
        var observer = new PickupWireObserver();
        var seen3 = new PickupWireObserver();
        var seen4 = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness3 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(seen3);
        });
        var harness4 = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(seen4);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness3);
        harness4.Hold(bob);
        harness4.Hold(recipient);
        var alice = await harness3.NewAliceAsync();

        // The 3.0 connection goes live first, then the 4.0 one: the newest live session takes the pushes.
        using var cts3 = new CancellationTokenSource();
        var stream3 = harness3.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness3.WebSocketEndpoint, cts3.Token).GetAsyncEnumerator();
        var first3 = PickupE2E.MoveNext(stream3);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "3.0/delivery-request", "the 3.0 stream is live and drained");
        await MediatorTestServer.WaitUntilAsync(() => harness3.Channel.PendingCount == 0, "the 3.0 stream waits");
        await mediator.PushesIdleAsync();
        PickupItem m1Item;
        using (var cts4 = new CancellationTokenSource())
        {
            await using var stream4 = harness4.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness4.WebSocketEndpoint, cts4.Token).GetAsyncEnumerator();
            var first4 = PickupE2E.MoveNext(stream4);
            await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the 4.0 stream is live and drained");
            await MediatorTestServer.WaitUntilAsync(() => harness4.Channel.PendingCount == 0, "the 4.0 stream waits");
            await mediator.PushesIdleAsync();

            var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "to the newest live connection");
            (await PickupE2E.Within(first4)).Should().BeTrue("the 4.0 connection is the newest with live delivery on");
            m1Item = stream4.Current;
            PickupE2E.ShouldBeAlicesNote(m1Item, alice, recipient, m1);
            var next4 = PickupE2E.MoveNext(stream4);
            await Qa2.StoreHoldsAsync(mediator, 0, "m1 was acknowledged in 4.0");
            first3.IsCompleted.Should().BeFalse("one session takes a push");
            await PickupE2E.StopAsync(stream4, next4, cts4); // live-off in 4.0, on the 4.0 connection
        }
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/live-off", "the 4.0 stream turned live delivery off");

        // Now the 3.0 connection is the only one live: the next push goes there, framed in 3.0.
        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "to the 3.0 connection");
        (await PickupE2E.Within(first3)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream3.Current, alice, recipient, m2);
        var next3 = PickupE2E.MoveNext(stream3);
        await Qa2.StoreHoldsAsync(mediator, 0, "m2 was acknowledged in 3.0");
        await PickupE2E.StopAsync(stream3, next3, cts3);

        // And a 4.0 stream on a new connection (the 4.0 container gone, a new one) is pushed to in 4.0 again.
        await harness4.DisposeAsync();
        await using var harness4b = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(seen4);
        });
        harness4b.Hold(bob);
        harness4b.Hold(recipient);
        var liveOns = observer.From(bob.Did).Count(r => r.Kind == "4.0/live-on");
        using var cts4b = new CancellationTokenSource();
        var stream4b = harness4b.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness4b.WebSocketEndpoint, cts4b.Token).GetAsyncEnumerator();
        var first4b = PickupE2E.MoveNext(stream4b);
        await MediatorTestServer.WaitUntilAsync(() => observer.From(bob.Did).Count(r => r.Kind == "4.0/delivery-request") == 2 && harness4b.Channel.PendingCount == 0, "the new 4.0 stream is live and drained");
        observer.From(bob.Did).Count(r => r.Kind == "4.0/live-on").Should().Be(liveOns + 1);
        await mediator.PushesIdleAsync();
        var m3 = await PickupE2E.SendAsync(mediator, alice, recipient, "to the new 4.0 connection");
        (await PickupE2E.Within(first4b)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream4b.Current, alice, recipient, m3);
        var next4b = PickupE2E.MoveNext(stream4b);
        await Qa2.StoreHoldsAsync(mediator, 0, "m3 was acknowledged in 4.0");
        await PickupE2E.StopAsync(stream4b, next4b, cts4b);

        await harness3.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        await harness4b.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        seen3.From(mediator.Did).Where(r => r.Thid is null).Select(r => r.Kind).Should().Equal(new[] { "3.0/delivery" }, "the 3.0 connection was pushed m2 only, in 3.0");
        seen4.From(mediator.Did).Where(r => r.Thid is null).Select(r => r.Kind).Should().Equal(new[] { "4.0/delivery", "4.0/delivery" }, "the 4.0 connections were pushed m1 and m3, in 4.0");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(3);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Theory]
    [InlineData("4.0", "3.0")]
    [InlineData("3.0", "4.0")]
    public async Task Qa2_A_push_in_another_version_than_the_stream_speaks_is_ignored_not_acknowledged_and_delivered_after_its_lease(string streamVersion, string otherVersion)
    {
        // The recipient's own container turns live delivery on again on the stream's session socket,
        // in the other version: the session's pushes are then framed in that version (a session's
        // framing is its last live-delivery-change). The stream lets such a push go unacknowledged;
        // the mediator's lease on it runs out (its clock) and the stream's next cycle (its clock)
        // drains it in its own version. A push that came back unacknowledged is never pushed live
        // again (owner decision on #100, 2026-11-20; 4.0 L304), so the cycle's live-delivery-change,
        // which wakes the pushes, cannot race its drain for it: only the drain delivers it.
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        var clientTime = new QaManualTimeProvider();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, streamVersion == "4.0" ? MessagePickupVersion.V4 : MessagePickupVersion.V3, webSocket: true, o =>
        {
            o.Time = clientTime;
            o.Client = c => c.LivePollInterval = Poll;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var lease = new MessagePickupOptions().LeaseTimeout;

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == streamVersion + "/delivery-request", "the stream is live and drained");
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && clientTime.HasOnlyTimerDueIn(Poll), "the stream waits for a push or its next cycle");
        await mediator.PushesIdleAsync();

        Message change = otherVersion == "3.0"
            ? MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true)
            : MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, true);
        await harness.Client.SendAsync(change, new SendOptions(new[] { mediator.Did }, bob.Did, ServiceEndpointOverride: harness.WebSocketEndpoint) { ReturnRoute = ReturnRouteMode.All });
        await observer.WaitForAsync(bob.Did, r => r.Kind == otherVersion + "/live-on", "the change in the other version was handled");
        await mediator.PushesIdleAsync();
        harness.Connects.Should().Be(1, "precondition: the change rode the stream's session socket");

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "framed in the other version");
        await clientSeen.WaitForAsync(mediator.Did, r => r.Kind == otherVersion + "/delivery" && r.Thid is null && r.Attachments == 1, "the push reached the client, framed in the other version");
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(l => l.Contains(OtherVersionIgnored, StringComparison.Ordinal)), "the stream read the push and let it go");
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1);
        first.IsCompleted.Should().BeFalse("a push in another version is not yielded");
        Qa2.MessageCount(mediator).Should().Be(1);
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Should().NotContain(r => r.Kind.EndsWith("/messages-received", StringComparison.Ordinal), "it was not acknowledged");

        // Still inside the lease, nothing gets it. Then the lease runs out, and the stream's next cycle takes it.
        mediator.Clock.Advance(lease + TimeSpan.FromSeconds(1));
        clientTime.Advance(Poll);
        (await PickupE2E.Within(first)).Should().BeTrue("after its lease the message is queued again, and the next cycle gets it in the stream's version");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        var taken = stream.Current.Id;
        var next = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged in the stream's version");
        // The store's removal is a neighbour of what the assertions below read (L-065 rule 1): under
        // 3.0 the stream's messages-received is an exchange, and the mediator removes the message
        // before its status answer reaches the stream. A stop that lands in between cancels that
        // exchange, and the stream, not knowing it was applied, acknowledges the message again as it
        // ends — two acknowledgements (the one failure of a full run on fc1e120, forced here in the
        // #100 diagnosis). So wait for the stream itself: no request waiting for its answer, and its
        // only timer the next cycle, which it arms only once the acknowledgement has ended.
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && clientTime.HasOnlyTimerDueIn(Poll), "the stream's acknowledgement ended and it waits for its next cycle");
        await PickupE2E.StopAsync(stream, next, cts);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "the push that came back unacknowledged was not pushed again");

        await mediator.FlushObserversAsync();
        var acks = observer.From(bob.Did).Where(r => r.Kind.EndsWith("/messages-received", StringComparison.Ordinal)).ToArray();
        acks.Should().OnlyContain(r => r.Kind == streamVersion + "/messages-received");
        acks.SelectMany(r => r.Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>())).Should().Equal(taken);
        await clientSeen.WaitForAsync(mediator.Did, r => r.Kind == streamVersion + "/delivery" && r.Attachments == 1, "the delivery reached the client's observers (L-065)");
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        clientSeen.From(mediator.Did).Where(r => r.Kind == streamVersion + "/delivery" && r.Attachments == 1).Should().ContainSingle("the message reached the stream once in its own version")
            .Which.Thid.Should().NotBeNull("the cycle's delivery-request drained it: it was not pushed live again");
    }

    // ---- 8. the count limit and the byte budget together (L255) ---------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qa2_A_4_0_fetch_of_big_and_small_messages_never_carries_more_than_its_limit_nor_more_than_the_byte_budget(bool webSocket)
    {
        // Slow on purpose: 600 KiB of real ciphertext is packed, relayed and opened.
        const int Kibibyte = 1024;
        const int Limit = 2;
        LeaseRecordingStore store = null!;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => LeaseRecordingStore.Register(s, x => store = x));
        var budget = mediator.Queue.Coordinator.Sizing.BatchPayloadBudget;
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var content in new[] { "big-0", "big-1", "small-0", "small-1", "small-2" })
        {
            var packed = content.StartsWith("big", StringComparison.Ordinal)
                ? (await PickupV4E2E.PackSizedAsync(alice, recipient, 300 * Kibibyte, content)).Packed
                : Encoding.UTF8.GetBytes(await alice.AuthcryptAsync(alice.Note(recipient.Did, content), recipient.Did));
            await PickupV4E2E.QueueAsync(mediator, recipient, packed);
            sizes[content] = packed.Length;
        }
        (sizes["big-0"] + sizes["big-1"]).Should().BeGreaterThan((int)budget, "precondition: the two big ones do not fit one lease");
        (sizes["big-1"] + sizes["small-0"]).Should().BeLessThanOrEqualTo((int)budget, "precondition: a big one and a small one do");

        var batches = new List<IReadOnlyList<string>>();
        for (var round = 0; round < 10; round++)
        {
            var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, Limit, serviceEndpointOverride: webSocket ? harness.WebSocketEndpoint : null));
            if (batch.Drained)
                break;
            batch.Items.Should().OnlyContain(i => i.Error == null);
            batches.Add(PickupV4E2E.Contents(batch));
        }

        batches.Should().HaveCount(3);
        batches[0].Should().Equal(new[] { "big-0" }, "the byte budget stops the lease before the limit (FR-PICK-03)");
        batches[1].Should().Equal(new[] { "big-1", "small-0" }, "the limit stops it here (L255)");
        batches[2].Should().Equal(new[] { "small-1", "small-2" }, "and here");
        batches.Should().OnlyContain(b => b.Count <= Limit);
        batches.Where(b => b.Count > 1).Should().OnlyContain(b => b.Sum(c => (long)sizes[c]) <= budget);
        var leases = store.Leases.Where(l => !l.Live).ToArray();
        leases.Should().OnlyContain(l => l.MaxMessages == Limit && l.MaxPayloadBytes == budget, "every lease asked for the request's limit within the budget, before anything was packed");
        leases.Select(l => l.Taken).Should().Equal(1, 2, 2, 0);
        store.Inner.MessageCount.Should().Be(0, "each fetch acknowledged what it returned");
        if (webSocket)
            harness.Connects.Should().Be(1);
    }

    // ---- 9. OD-1 on the live streams ----------------------------------------------------------------

    [Theory]
    [InlineData("3.0")]
    [InlineData("4.0")]
    public async Task Qa2_A_forward_whose_message_is_not_encrypted_never_reaches_a_live_stream_and_the_next_encrypted_one_does(string version)
    {
        // #100 OD-1: the mailbox refuses a message that is not encrypted, so a live stream of either
        // version never sees it (3.0 L172, 4.0 L261: "the ONLY valid type of attachment").
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, version == "4.0" ? MessagePickupVersion.V4 : MessagePickupVersion.V3, webSocket: true, o =>
        {
            o.Time = new QaManualTimeProvider();
            o.Client = c => c.LivePollInterval = Day;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == version + "/delivery-request", "the stream is live and drained");
        await mediator.PushesIdleAsync();
        var pushes = mediator.PushOutcomes.Count;

        var plaintext = await alice.Client.PackPlaintextAsync(alice.Note(recipient.Did, "not encrypted"));
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, plaintext);
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedPayloadNotEncrypted);
        mediator.PushOutcomes.Should().HaveCount(pushes, "nothing was stored, so nothing was pushed");
        Qa2.MessageCount(mediator).Should().Be(0);

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "encrypted");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        var next = PickupE2E.MoveNext(stream);
        await Qa2.StoreHoldsAsync(mediator, 0, "the encrypted one was acknowledged");
        await PickupE2E.StopAsync(stream, next, cts);
        await clientSeen.WaitForAsync(mediator.Did, r => r.Thid is null && r.Kind == version + "/delivery", "the push reached the client's observers (L-065)");
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        clientSeen.From(mediator.Did).Where(r => r.Thid is null).Select(r => r.Kind).Should().Equal(new[] { version + "/delivery" }, "the only push was the encrypted message");
    }

    // ---- 10. the one-way acknowledgement under MaxPendingRequests ---------------------------------------

    [Fact]
    public async Task Qa2_Under_4_0_a_live_acknowledgement_refused_for_too_many_waiting_requests_is_kept_and_sent_one_way_after_the_back_off()
    {
        // The one-way send counts against MaxPendingRequests like any request (DC-8). Here another
        // call of the same container holds the only slot (a status-request over HTTP whose answer the
        // mediator holds back), so the stream's acknowledgement is refused: it is kept, the stream
        // backs off, and once the slot is free the acknowledgement goes, once.
        var observer = new PickupWireObserver();
        var clientTime = new QaManualTimeProvider();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o =>
        {
            o.Time = clientTime;
            o.Client = c =>
            {
                c.LivePollInterval = Day;
                c.MaxPendingRequests = 1;
            };
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/delivery-request", "the stream is live and drained");
        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "acknowledged after the back-off");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        var pushedId = stream.Current.Id;
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0, "the stream's requests are done");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;
        Task<PickupStatus> parked;
        Task<bool> next;
        try
        {
            parked = harness.Pickup.GetStatusAsync(mediator.Did, bob.Did); // over HTTP: the mediator's DID names its HTTP endpoint
            await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 1, "the status-request holds the one slot");

            var mark = clientTime.Mark();
            next = PickupE2E.MoveNext(stream); // takes the item: the flush is refused
            await clientTime.WaitForTimerAsync(mark, LiveDeliveryStream.FirstBackoff, MediatorTestServer.Timeout);
            Qa2.MessageCount(mediator).Should().Be(1, "the refused acknowledgement was not sent");
            mediator.Logs.Entries.Should().Contain(l => l.Contains("message(s) failed (MediatorBusyException)", StringComparison.Ordinal));
            next.IsCompleted.Should().BeFalse("the stream backs off, it does not end");
        }
        finally
        {
            gate.TrySetResult();
            mediator.Controls.ResponseGate = null;
        }
        (await PickupE2E.Within(parked)).MessageCount.Should().Be(1);
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0, "the slot is free again");
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Should().NotContain(r => r.Kind == "4.0/messages-received", "nothing was acknowledged during the back-off");

        clientTime.Advance(LiveDeliveryStream.FirstBackoff);
        await Qa2.StoreHoldsAsync(mediator, 0, "after the back-off the kept acknowledgement was sent");
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received", "the acknowledgement was dispatched");
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && clientTime.HasOnlyTimerDueIn(Day), "the stream ran its cycle and waits");
        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Where(r => r.Kind == "4.0/messages-received")
            .SelectMany(r => r.Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()))
            .Should().Equal(new[] { pushedId }, "sent once, one way, naming the item");
        next.IsCompleted.Should().BeFalse();
        await PickupE2E.StopAsync(stream, next, cts);
    }

    // ---- 6. AckAsync under 4.0 --------------------------------------------------------------------------

    [Fact]
    public async Task Qa2_AckAsync_under_4_0_over_HTTP_posts_the_acknowledgement_without_return_route_is_answered_202_and_its_status_request_sees_the_removal()
    {
        var posts = new Qa2PostRecorder();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigurePipeline = posts.Install);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "kept");
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10));
        var failed = batch.Items.Should().ContainSingle(i => i.Error != null).Subject;
        (await PickupV4E2E.Status40Async(mediator, bob)).MessageCount.Should().Be(1, "precondition: the fetch acknowledged the one that opened");
        var mark = posts.Count;

        var status = await PickupE2E.Within(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { failed.Id }));

        status.MessageCount.Should().Be(0, "the status-request after the acknowledgement sees it applied");
        posts.Since(mark).Should().Equal(new[] { 202, 200 }, "the acknowledgement is answered 202 with nothing in it (L294), the status-request 200");
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("4.0/delivery-request", "4.0/messages-received", "4.0/status-request", "4.0/messages-received", "4.0/status-request");
        var acks = observer.From(bob.Did).Where(r => r.Kind == "4.0/messages-received").ToArray();
        acks.Should().OnlyContain(r => r.ReturnRoute == ReturnRouteMode.None && r.CreatedTime == mediator.Clock.Now.ToUnixTimeSeconds(), "one way, stamped (L35, FR-RR-09)");
        acks[^1].Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(failed.Id);
        Qa2.MessageCount(mediator).Should().Be(0);
    }

    [Fact]
    public async Task Qa2_AckAsync_under_4_0_over_a_WebSocket_rides_the_session_socket_a_fetch_opened()
    {
        var sockets = new CountingSockets();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigurePipeline = sockets.Install);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "kept");
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10, serviceEndpointOverride: harness.WebSocketEndpoint));
        var failed = batch.Items.Should().ContainSingle(i => i.Error != null).Subject;

        var status = await PickupE2E.Within(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { failed.Id }, harness.WebSocketEndpoint));

        status.MessageCount.Should().Be(0, "read in order on one socket, the status-request sees the acknowledgement applied");
        harness.Connects.Should().Be(1);
        sockets.Count.Should().Be(1);
        sockets.All[0].MessagesReceived.Should().Be(4, "the delivery-request, both acknowledgements and the status-request rode the session socket (FR-RR-07)");
        await mediator.FlushObserversAsync();
        observer.PickupKindsFrom(bob.Did).Should().Equal("4.0/delivery-request", "4.0/messages-received", "4.0/messages-received", "4.0/status-request");
        observer.From(bob.Did).Where(r => r.Kind == "4.0/messages-received").Should().OnlyContain(r => r.ReturnRoute == ReturnRouteMode.None && r.CreatedTime == mediator.Clock.Now.ToUnixTimeSeconds());
        Qa2.MessageCount(mediator).Should().Be(0);
    }

    [Fact]
    public async Task Qa2_AckAsync_under_4_0_as_the_first_WebSocket_call_sends_the_acknowledgement_on_a_socket_of_its_own_so_its_status_can_predate_it()
    {
        // What happens when no session socket exists yet (documented, not a confirmation): the
        // header-less acknowledgement goes one way on the endpoint's pooled socket, the
        // status-request (return_route: all) opens the session socket, and the mediator reads the two
        // sockets independently. Holding the first socket's reads shows the order the mediator may
        // take: the status is answered before the acknowledgement is read, so AckAsync returns a
        // status that still counts the message. AckAsync's contract makes only a status that no
        // longer counts it a confirmation; the acknowledgement is applied once read.
        var sockets = new CountingSockets();
        sockets.HoldReceivesOf(0);
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigurePipeline = sockets.Install);
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;

        var status = await PickupE2E.Within(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { delivered.Id }, harness.WebSocketEndpoint));

        status.MessageCount.Should().Be(1, "the status was read on the session socket before the acknowledgement on the other socket");
        harness.Connects.Should().Be(2, "the pooled socket for the header-less acknowledgement, then the session socket for the status-request");
        sockets.Count.Should().Be(2);
        sockets.All[0].MessagesReceived.Should().Be(0, "precondition: the acknowledgement waits on its own socket");
        sockets.All[1].MessagesReceived.Should().Be(1, "the status-request");
        sockets.All[0].ReleaseReceives();
        await Qa2.StoreHoldsAsync(mediator, 0, "the acknowledgement is applied once the mediator reads it");
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received", "the acknowledgement was dispatched");
        var ack = observer.From(bob.Did).Single(r => r.Kind == "4.0/messages-received");
        ack.ReturnRoute.Should().Be(ReturnRouteMode.None);
        ack.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "stamped although it went on a socket with no session (FR-RR-09)");
        sockets.All[0].MessagesReceived.Should().Be(1);
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint))).MessageCount.Should().Be(0, "asked again, the status confirms it");
    }
}
