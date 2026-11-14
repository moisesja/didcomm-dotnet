using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-5 (#95, PR #109 review round 2): a break-it pass on the redesigned push dispatcher and its
/// invariants D-1..D-5. Every test drives the real sink (as the forward pump calls it) or the real
/// dispatcher in a container built by <c>AddMediator()</c> (no generic host), with a manual clock
/// for the deadlines and a rendezvous for each interleaving (L-044); every push is a live push.
/// Tests marked <c>Skip</c> are confirmed defects: each fails when the skip is removed.
/// </summary>
public sealed class PushDispatcherAdv5Tests
{
    private const string Recipient = "did:example:adv5-recipient";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly int PerRun = new MessagePickupOptions().MaxDeliveryBatch;

    // ---- D-1 / liveness: which messages a live push takes --------------------------------------------

    // ADV-5 DEFECT 1 (P2), fixed in round 8: "arrived after live-on" was two readings of the wall
    // clock; it is now the store's arrival sequence against the live-on status's high-water mark.
    [Fact]
    public async Task Defect1_after_the_clock_steps_back_a_message_stored_for_a_live_recipient_is_still_pushed_live()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var clock = new SteppableClock(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        await using var fixture = PushFixture.Create(
            didComm: o => o.Clock = clock.Now,
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);

        // An NTP correction of ten seconds after live delivery went on: the session stays live and healthy.
        clock.Step(TimeSpan.FromSeconds(-10));
        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 1), "fwd-adv5-step-1");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        Deliveries(session).Should().ContainSingle("the message was stored, due, while the recipient's session had live delivery on");

        // Once the clock is past the live-on instant again, a later message is pushed; the one stored
        // in the step-back window must have been pushed too (it is never pushed later: it is "older").
        clock.Step(TimeSpan.FromSeconds(20));
        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 2), "fwd-adv5-step-2");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        Deliveries(session).Should().HaveCount(2);
    }

    // ADV-5 DEFECT 2 (P2), fixed in round 8: the message is numbered when it is stored, after the
    // live-on status read, so it is after the mark whenever its clock reading was taken.
    [Fact]
    public async Task Defect2_a_message_stored_after_live_delivery_went_on_is_pushed_even_when_its_clock_reading_came_first()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var mediation = new ScriptedMediationStore();
        await using var fixture = PushFixture.Create(
            services: s => s.AddSingleton<IMediationStore>(mediation),
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);

        var inLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookups = 0;
        mediation.OnFind = async (_, ct) =>
        {
            if (Interlocked.Increment(ref lookups) == 1)
            {
                inLookup.TrySetResult();
                await proceed.Task; // the store's grant lookup (a database round trip for a host store)
            }
            return await mediation.GetGrantAsync(peer.Did, ct);
        };

        // The forward's DeliverAsync has read the clock (T0) and is in its grant lookup.
        var forward = ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 1), "fwd-adv5-race");
        await inLookup.Task.WaitAsync(Wait);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        // Live delivery goes on at T0 + 1 s; the status that answers it is read now: nothing is stored yet.
        await fixture.GoLiveAsync(session);
        (await fixture.Store.GetStatusAsync(peer.Did, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(0);

        proceed.SetResult();
        await forward.WaitAsync(Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.Store.MessageCount.Should().Be(1);
        Deliveries(session).Should().ContainSingle("it was stored after live delivery went on and after the status that went with it, so nothing else tells the recipient about it");
    }

    [Fact]
    public async Task A_store_that_ignores_AfterSequence_is_refused_and_no_record_from_before_live_delivery_is_pushed_or_left_leased()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var store = new ScriptedMessageStore();
        store.OnLease = (request, ct) => store.Inner.LeaseAsync(request with { AfterSequence = null }, ct);
        await using var fixture = PushFixture.Create(
            services: s => s.AddSingleton<IMessageStore>(store),
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var before = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);

        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 2), "fwd-adv5-after");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        Deliveries(session).Should().BeEmpty("the record the store handed out was stored before live delivery went on");
        // Round 8: turning live delivery on wakes the grant too, so the store's refused take is counted twice.
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed);
        var now = fixture.Time.GetUtcNow();
        var free = await store.Inner.LeaseAsync(new MessageLeaseRequest(peer.Did, null, 10, int.MaxValue, now, new MessageLease("adv5-pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None);
        free.Select(r => r.Id).Should().Contain(before.MessageId).And.HaveCount(2, "the refused take gave its lease back: both wait for pickup");
    }

    // ---- D-3: one operation per grant ------------------------------------------------------------------

    // ADV-5 DEFECT 5 (P3), fixed in round 8: PruneLocked removes an entry only when the map holds
    // that very entry, so a worker holding a dropped one cannot remove its busy replacement.
    [Fact]
    public async Task Defect5_a_stale_grant_entry_never_lets_a_second_operation_of_the_same_grant_start()
    {
        MessagePickupLive.RaiseThreadPoolFloor(2);
        var logger = new SaturationParkingLogger();
        var script = new LiveScript("a", "b", "c", "h");
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 2, services: s => s.AddSingleton<ILogger<PushDispatcher>>(logger));
        var grants = await LiveGrantsAsync(world, script, "a", "b", "c", "h", "k", "g");
        var d = world.Fixture.Dispatcher;
        d.MaxUnsettled.Should().Be(4);
        try
        {
            // Three pushes whose sessions never take them and ignore their token: abandoned at the deadline.
            await StoreAndWakeAsync(world, grants, "a");
            await StoreAndWakeAsync(world, grants, "b");
            await script.Arrived("a").WaitAsync(Wait);
            await script.Arrived("b").WaitAsync(Wait);
            world.Fixture.Time.Advance(PushTimeout);
            await d.WaitForIdleAsync(Wait);
            await StoreAndWakeAsync(world, grants, "c");
            await script.Arrived("c").WaitAsync(Wait);
            world.Fixture.Time.Advance(PushTimeout);
            await d.WaitForIdleAsync(Wait);
            d.Unsettled.Should().Be(3);

            // H holds one worker: the bound is reached.
            await StoreAndWakeAsync(world, grants, "h");
            await script.Arrived("h").WaitAsync(Wait);
            d.Unsettled.Should().Be(4);

            // G's wake meets the bound; the worker that took it is parked in its log line, holding G's entry.
            logger.Armed = true;
            await StoreAndWakeAsync(world, grants, "g");
            await logger.Parked.Task.WaitAsync(Wait);

            // H ends; the free worker serves G's next wake on that same entry, whose end prunes it.
            script.Release("h", ReturnRouteSendResult.Sent);
            await MessagePickupLive.UntilAsync(() => d.Unsettled == 3, "H settled");
            d.Wake(grants["g"].Peer).Should().BeTrue();
            await MessagePickupLive.UntilAsync(() => script.Calls("g") == 1 && d.Unsettled == 3 && d.GrantEntries == 3, "G's first operation pushed and settled; only A, B and C (cooling) keep entries");

            // G again: a new entry, its push parked (the session ignores its token).
            script.ParkNext("g");
            await StoreAndWakeAsync(world, grants, "g");
            await script.Arrived("g").WaitAsync(Wait);
            d.Unsettled.Should().Be(4);

            // One abandoned push ends (room for one operation), then the parked worker finishes its skip.
            script.Release("a", ReturnRouteSendResult.NotSent);
            await MessagePickupLive.UntilAsync(() => d.Unsettled == 3, "A's abandoned push settled");
            logger.Release.Set();
            // The only free worker is the one that was parked: K's push shows it has finished the skip.
            await StoreAndWakeAsync(world, grants, "k");
            await MessagePickupLive.UntilAsync(() => script.Calls("k") == 1, "K pushed by the worker that was parked");

            // A third message for G while G's operation is still in flight, then K again, served after it.
            await StoreAndWakeAsync(world, grants, "g");
            await StoreAndWakeAsync(world, grants, "k");
            await MessagePickupLive.UntilAsync(() => script.Calls("k") == 2, "K's second push, queued behind G's wake-up");

            script.Release("g", ReturnRouteSendResult.Sent);
            await d.WaitForIdleAsync(Wait);

            script.MaxInFlight("g").Should().Be(1, "a grant has at most one operation at a time (D-3; MediatorOptions.PushConcurrency: at most one push in flight per grant)");
            script.Calls("g").Should().Be(3);
        }
        finally
        {
            logger.Release.Set();
            script.ReleaseAll();
        }
    }

    [Fact]
    public async Task A_revoked_and_re_granted_peer_gets_no_second_operation_while_its_abandoned_push_runs()
    {
        var script = new LiveScript();
        await using var world = await LivePushWorld.StartAsync();
        var grants = await LiveGrantsAsync(world, script, "g");
        var (grant, session) = (grants["g"], script.Session("g"));
        var fixture = world.Fixture;
        await world.StoreAsync(grant, 1);
        script.ParkNext("g");

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await script.Arrived("g").WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Unsettled.Should().Be(1);

        (await fixture.Queue.RevokeAndPurgeAsync(grant.Peer)).Purged.Should().Be(1);
        await fixture.EnrolAsync(grant.Peer, grant.Recipient);
        fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);
        var second = await world.StoreAsync(grant, 2);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("held until the abandoned push settles");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        script.Calls("g").Should().Be(1, "no second operation for the re-granted peer while the first one's send runs");

        script.Release("g", ReturnRouteSendResult.NotSent);
        await MessagePickupLive.UntilAsync(() => script.Calls("g") == 2, "the held wake-up is served once the abandoned push settles");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Unsettled.Should().Be(0);
        LivePushWorld.Deliveries(session).Select(m => m.Id).Should().Equal(new[] { second.MessageId }, "the purged record is never pushed; the new grant's is");
    }

    // ---- D-4: the bound on unsettled work, and what it costs ---------------------------------------------

    [Fact]
    public async Task With_twice_PushConcurrency_grants_whose_pushes_never_end_no_other_grant_is_pushed_however_long_it_waits()
    {
        // The bound (D-4) holds; the documented residual ("a healthy recipient's push can wait up to
        // about ceil(K / W) x PushTimeout") does not: 2 x W pushes that ignore cancellation and never
        // end stop every push of every other recipient for as long as they run. Pickup still works.
        var script = new LiveScript("a", "b");
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1);
        var grants = await LiveGrantsAsync(world, script, "a", "b", "k");
        var fixture = world.Fixture;
        var d = fixture.Dispatcher;
        try
        {
            foreach (var stalled in new[] { "a", "b" })
            {
                await StoreAndWakeAsync(world, grants, stalled);
                await script.Arrived(stalled).WaitAsync(Wait);
                fixture.Time.Advance(PushTimeout);
                await d.WaitForIdleAsync(Wait);
            }
            d.Unsettled.Should().Be(d.MaxUnsettled);

            for (var round = 0; round < 10; round++)
            {
                await StoreAndWakeAsync(world, grants, "k");
                await d.WaitForIdleAsync(Wait);
                fixture.Time.Advance(TimeSpan.FromMinutes(10));
            }

            script.Calls("k").Should().Be(0, "100 minutes, ten wake-ups, and K (healthy) was never pushed");
            fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(10);
            d.Unsettled.Should().Be(2, "the bound held");
            d.GrantEntries.Should().BeLessThanOrEqualTo(2, "only the two busy grants keep an entry");
            var now = fixture.Time.GetUtcNow();
            (await fixture.Store.LeaseAsync(new MessageLeaseRequest(grants["k"].Peer, null, 32, int.MaxValue, now, new MessageLease(null, now + TimeSpan.FromMinutes(1))), CancellationToken.None))
                .Should().HaveCount(10, "pickup still gets every message");

            // One stalled push ends: K's next wake-up is served (the pickup's lease above has run out by then).
            script.Release("a", ReturnRouteSendResult.NotSent);
            await MessagePickupLive.UntilAsync(() => d.Unsettled == 1, "A settled");
            fixture.Time.Advance(TimeSpan.FromMinutes(2));
            await StoreAndWakeAsync(world, grants, "k");
            await d.WaitForIdleAsync(Wait);
            script.Calls("k").Should().BeGreaterThan(0, "pushes resume once an unsettled operation ends");
        }
        finally
        {
            script.ReleaseAll();
        }
    }

    [Fact]
    public async Task A_busy_grant_yields_its_worker_after_MaxDeliveryBatch_pushes_and_another_grant_is_served_in_between()
    {
        var script = new LiveScript();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1);
        var grants = await LiveGrantsAsync(world, script, "p", "q");
        var fixture = world.Fixture;
        for (var i = 0; i < 2 * PerRun + 5; i++)
            await world.StoreAsync(grants["p"], (byte)i);
        await world.StoreAsync(grants["q"], 200);
        script.ParkNext("p");

        fixture.Dispatcher.Wake(grants["p"].Peer).Should().BeTrue();
        await script.Arrived("p").WaitAsync(Wait); // P's first push holds the only worker
        fixture.Dispatcher.Wake(grants["q"].Peer).Should().BeTrue();
        script.Release("p", ReturnRouteSendResult.Sent);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var labels = script.Order.Select(o => o.Label).ToList();
        labels.Should().HaveCount(2 * PerRun + 6);
        labels.IndexOf("q").Should().Be(PerRun, "Q is served as soon as P has made one run of MaxDeliveryBatch pushes");
        script.Order.Where(o => o.Label == "p").Select(o => o.First)
            .Should().Equal(Enumerable.Range(0, 2 * PerRun + 5).Select(i => LivePushWorld.Seed(i)), "P's messages go out in the order they were stored, across its runs");
    }

    // ---- liveness across a cooldown ---------------------------------------------------------------------

    [Fact]
    public async Task A_message_whose_wake_up_was_skipped_in_a_cooldown_is_pushed_by_the_next_wake_up_oldest_first_and_not_before()
    {
        // MediatorOptions, AddMediator and the architecture map say every message stored while the
        // recipient is live (and due) is pushed. A wake-up skipped in a cooldown is not served later
        // on its own: the message waits until the grant's next wake-up (or pickup).
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        var slowOnce = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            if (Interlocked.Exchange(ref slowOnce, 0) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a slow resolution, once; honours the deadline
            }
            return ReturnRouteSendResult.Sent;
        };

        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 1), "fwd-adv5-cool-1");
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        // The worker may have stopped waiting before the cancelled send finished: wait for it to settle.
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the timed-out push settled");
        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 2), "fwd-adv5-cool-2");
        fixture.Time.Advance(PushTimeout * (PushDispatcher.CooldownPushTimeouts + 1));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown);
        Deliveries(session).Should().BeEmpty("the cooldown is over, the session is live and healthy, and both messages are still not pushed");

        await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, 3), "fwd-adv5-cool-3");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        Deliveries(session).Select(d => d.Id).Should().HaveCount(3).And.OnlyHaveUniqueItems();
        fixture.PushOutcomeLog.Skip(2).Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive, PushOutcomes.PushedLive);
    }

    // ---- clock: the cooldown -------------------------------------------------------------------------------

    // ADV-5 DEFECT 6 (P3), fixed in round 8: the cooldown is measured on the monotonic clock
    // (TimeProvider.GetTimestamp / GetElapsedTime).
    [Fact]
    public async Task Defect6_a_backward_step_of_the_wall_clock_does_not_lengthen_a_cooldown()
    {
        WallStepClock? wall = null;
        await using var world = await LivePushWorld.StartAsync(services: s => wall = StepWall(s));
        var (grant, session) = await world.LiveGrantAsync("wall-back");
        var entered = SlowOnce(session);
        var fixture = world.Fixture;
        await world.StoreAsync(grant, 1);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await entered.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the timed-out push settled");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        wall!.Step(TimeSpan.FromHours(-1)); // an NTP correction; the monotonic clock is unaffected
        fixture.Time.Advance(PushTimeout * (PushDispatcher.CooldownPushTimeouts + 1));
        await world.StoreAsync(grant, 2);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("2 x PushTimeout has passed on the monotonic clock");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.PushedLive).Should().Be(2, "the timed-out message (given back) and the new one, oldest first");
    }

    [Fact]
    public async Task A_forward_step_of_the_wall_clock_does_not_shorten_a_cooldown()
    {
        // Round 8, with Defect6: the pause is measured on the monotonic clock in both directions.
        WallStepClock? wall = null;
        await using var world = await LivePushWorld.StartAsync(services: s => wall = StepWall(s));
        var (grant, session) = await world.LiveGrantAsync("wall-forward");
        var entered = SlowOnce(session);
        var fixture = world.Fixture;
        await world.StoreAsync(grant, 1);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await entered.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the timed-out push settled");

        wall!.Step(TimeSpan.FromHours(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeFalse("no time has passed on the monotonic clock: the grant is still paused");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown);
    }

    // ---- support ---------------------------------------------------------------------------------------------

    /// <summary>A live grant per label, each session's pushes answered by <paramref name="script"/>.</summary>
    private static async Task<Dictionary<string, LivePushGrant>> LiveGrantsAsync(LivePushWorld world, LiveScript script, params string[] labels)
    {
        var grants = new Dictionary<string, LivePushGrant>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var (grant, session) = await world.LiveGrantAsync("adv5-" + label);
            script.Attach(label, session);
            grants[label] = grant;
        }
        return grants;
    }

    private static async Task StoreAndWakeAsync(LivePushWorld world, IReadOnlyDictionary<string, LivePushGrant> grants, string label)
    {
        var stored = await world.StoreAsync(grants[label]);
        world.Fixture.Dispatcher.Wake(stored.GrantPeerDid);
    }

    /// <summary>The session's first push waits until the deadline cancels it (a slow resolution, once); later ones go out.</summary>
    private static Task SlowOnce(LiveTestSession session)
    {
        var slowOnce = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            if (Interlocked.Exchange(ref slowOnce, 0) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // honours the deadline
            }
            return ReturnRouteSendResult.Sent;
        };
        return entered.Task;
    }

    /// <summary>Make the dispatcher's clock the manual one with a steppable wall-clock reading.</summary>
    private static WallStepClock StepWall(IServiceCollection services)
    {
        var manual = (ManualTimeProvider)services.Last(x => x.ServiceType == typeof(TimeProvider)).ImplementationInstance!;
        var wall = new WallStepClock(manual);
        services.AddSingleton<TimeProvider>(wall);
        return wall;
    }

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();

    /// <summary>What the forward pump does with a relayed forward: hand it to the mediator's sink.</summary>
    private static Task ForwardAsync(PushFixture fixture, string mediatorDid, string recipient, byte[] payload, string forwardId)
        => fixture.Sink.AcceptAsync(new ForwardProcessingResult(recipient, payload, null, Delay: null), Context(fixture, mediatorDid, forwardId), CancellationToken.None);

    private static ProtocolContext Context(PushFixture fixture, string mediatorDid, string forwardId)
        => new(
            new UnpackResult(
                new Message { Id = forwardId, Type = ForwardConstants.ForwardTypeUri },
                new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
                Encrypted: true,
                Authenticated: false,
                NonRepudiation: false,
                AnonymousSender: true,
                ContentEncryption: null,
                KeyWrap: null,
                SignatureAlgorithm: null,
                SignerKid: null,
                SenderKid: null,
                RecipientKid: mediatorDid + "#key-1",
                AllRecipientKids: new[] { mediatorDid + "#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forwardId),
            Client: null,
            fixture.Options,
            new InMemoryThreadStateStore());

    /// <summary>A wall clock a test can step back or forward (the facade's <c>DidCommOptions.Clock</c>).</summary>
    private sealed class SteppableClock(DateTimeOffset start)
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset Now() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Step(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>The dispatcher's <see cref="TimeProvider"/>: the manual one, whose wall-clock reading a test can step (timers and timestamps stay monotonic).</summary>
    private sealed class WallStepClock(ManualTimeProvider inner) : TimeProvider
    {
        private long _offset;

        public void Step(TimeSpan by) => Interlocked.Add(ref _offset, by.Ticks);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow().AddTicks(Interlocked.Read(ref _offset));

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>
    /// The live sessions of several grants, keyed by label: the labels named at construction never
    /// take a push and ignore their token until released; a label armed with <see cref="ParkNext"/>
    /// parks its next push the same way; every other push is taken at once. Counts pushes and the
    /// most in flight per label, and records each push's label and first payload byte in order.
    /// </summary>
    private sealed class LiveScript(params string[] alwaysPark)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _arrived = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ReturnRouteSendResult>> _release = new();
        private readonly ConcurrentDictionary<string, int> _armed = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private readonly ConcurrentDictionary<string, int> _inFlight = new();
        private readonly ConcurrentDictionary<string, int> _maxInFlight = new();
        private readonly ConcurrentDictionary<string, LiveTestSession> _sessions = new();

        public ConcurrentQueue<(string Label, byte First)> Order { get; } = new();

        public void Attach(string label, LiveTestSession session)
        {
            _sessions[label] = session;
            session.OnPush = (message, _) => PushAsync(label, message);
        }

        public LiveTestSession Session(string label) => _sessions[label];

        public Task Arrived(string label) => _arrived.GetOrAdd(label, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void Release(string label, ReturnRouteSendResult result) => Gate(label).TrySetResult(result);

        public void ReleaseAll()
        {
            foreach (var gate in _release.Values)
                gate.TrySetResult(ReturnRouteSendResult.NotSent);
        }

        public void ParkNext(string label) => _armed[label] = 1;

        public int Calls(string label) => _calls.TryGetValue(label, out var n) ? n : 0;

        public int MaxInFlight(string label) => _maxInFlight.TryGetValue(label, out var n) ? n : 0;

        private async Task<ReturnRouteSendResult> PushAsync(string label, Message message)
        {
            _calls.AddOrUpdate(label, 1, (_, n) => n + 1);
            var now = _inFlight.AddOrUpdate(label, 1, (_, n) => n + 1);
            _maxInFlight.AddOrUpdate(label, now, (_, n) => Math.Max(n, now));
            if (MessagePickup.TryParseDelivery(message, out var delivered, out _) && delivered.Count > 0)
                Order.Enqueue((label, delivered[0].Payload.Span[0]));
            try
            {
                var park = Array.IndexOf(alwaysPark, label) >= 0 || _armed.TryRemove(label, out _);
                if (!park)
                    return ReturnRouteSendResult.Sent;
                var gate = Gate(label);
                _arrived.GetOrAdd(label, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                return await gate.Task; // ignores its token
            }
            finally
            {
                _inFlight.AddOrUpdate(label, 0, (_, n) => n - 1);
            }
        }

        private TaskCompletionSource<ReturnRouteSendResult> Gate(string label)
            => _release.GetOrAdd(label, _ => new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>The dispatcher's logger: parks the thread that writes the first "saturated" line after it is armed, until released.</summary>
    private sealed class SaturationParkingLogger : ILogger<PushDispatcher>
    {
        private int _armed;

        public bool Armed
        {
            get => Volatile.Read(ref _armed) == 1;
            set => Volatile.Write(ref _armed, value ? 1 : 0);
        }

        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("have not ended yet", StringComparison.Ordinal))
                return;
            if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
                return;
            Parked.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
