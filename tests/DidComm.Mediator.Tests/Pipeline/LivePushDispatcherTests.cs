using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="PushDispatcher"/> at the component level, driven through live sessions (QA-1, ADV-2,
/// ADV-3, PR #109 review round 2): what the store hands out and when, a take, a registration check
/// or a give-back that fails or ignores its token, the deadline's callback, the bound on unsettled
/// work, one push in flight per grant, shutdown, the execution context, and no DID, queue id or
/// exception message in any line. These tests used the push to a recipient's endpoint as their
/// vehicle until it left #95 (tracked in #110); the dispatcher paths they pin are the live ones.
/// </summary>
public sealed class LivePushDispatcherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;

    private static void NoLeaks(LivePushWorld world, LivePushGrant grant, params string[] more)
        => PipelineLogs.Leaking(world.Fixture.Logs, new[] { grant.Peer, grant.Recipient, world.Mediator.Did, HandlerBoomException.Marker }.Concat(more).ToArray())
            .Should().BeEmpty("no line may carry a DID, a queue id or an exception message");

    private static async Task<IReadOnlyList<QueuedMessage>> PickupFromAsync(IMessageStore store, string grantPeerDid, DateTimeOffset now)
        => await store.LeaseAsync(new MessageLeaseRequest(grantPeerDid, null, 10, long.MaxValue, now, new MessageLease("dispatcher-pickup", now + TimeSpan.FromSeconds(60))), CancellationToken.None);

    // ---- what the store hands out ----------------------------------------------------------------

    [Fact]
    public async Task A_message_not_due_yet_is_not_pushed()
    {
        await using var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("not-due");
        var fixture = world.Fixture;
        var result = await fixture.Queue.DeliverAsync(grant.Recipient, PushFixture.Payload(20), new PickupDeliveryOptions(NotBefore: fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(5), AllowLive: false));

        fixture.Dispatcher.Wake(result.GrantPeerDid!).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome.
        fixture.PushOutcomeLog.Should().BeEmpty();
        LivePushWorld.Deliveries(session).Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_payload_of_any_admissible_size_is_taken_when_idle()
    {
        // L-020: a push pins no payload (it takes what the store holds), so any message the queue
        // admitted can be pushed.
        await using var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("large");
        var fixture = world.Fixture;

        fixture.Dispatcher.Wake((await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(fixture.Queue.MaxAdmissiblePayloadBytes))).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        LivePushWorld.Deliveries(session).Should().ContainSingle().Which.Payload.Length.Should().Be(fixture.Queue.MaxAdmissiblePayloadBytes);
    }

    [Fact]
    public async Task A_wake_up_whose_peer_has_no_live_session_counts_that_once_and_takes_nothing()
    {
        // L-020 (PR #109 review R1-2): the workers run whenever AddMediator() is used. A wake-up
        // whose peer has no live session any more (the sink wakes only when it saw one) ends with its
        // own outcome and leases nothing.
        const string Peer = "did:example:dispatcher-no-session";
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Peer + "-r");

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Peer + "-r")).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoLiveSession);
        (await PickupFromAsync(fixture.Store, Peer, fixture.Time.GetUtcNow())).Should().ContainSingle("nothing took a lease on it");
    }

    // ---- the take and the registration check ---------------------------------------------------------

    [Fact]
    public async Task A_store_that_fails_to_take_the_record_is_a_failure_and_nothing_is_sent()
    {
        var store = new ScriptedMessageStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var (grant, session) = await world.LiveGrantAsync("take-fails");
        var fixture = world.Fixture;
        var stored = await world.StoreAsync(grant);
        store.OnLease = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        LivePushWorld.Deliveries(session).Should().BeEmpty();
        store.Inner.MessageCount.Should().Be(1);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains(nameof(HandlerBoomException), StringComparison.Ordinal));
        NoLeaks(world, grant, stored.MessageId);
    }

    [Fact]
    public async Task A_take_that_ignores_the_deadline_frees_the_worker_and_when_it_returns_its_record_is_given_back_unsent()
    {
        // ADV-3 and L-020 (PR #109 review round 2, D-3/D-5): the worker stops waiting at the deadline;
        // the abandoned take runs on and, when it returns, sees the deadline passed, sends nothing and
        // gives the record back at once; the grant is not served again before that.
        var store = new ScriptedMessageStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var (grant, session) = await world.LiveGrantAsync("take-late");
        var fixture = world.Fixture;
        var stored = await world.StoreAsync(grant);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<QueuedMessage>>? lateTake = null;
        store.OnLease = (request, _) =>
        {
            entered.TrySetResult();
            lateTake = TakeLateAsync();
            return new ValueTask<IReadOnlyList<QueuedMessage>>(lateTake);

            async Task<IReadOnlyList<QueuedMessage>> TakeLateAsync()
            {
                await release.Task; // ignores its token
                return await store.Inner.LeaseAsync(request, CancellationToken.None);
            }
        };

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the take still runs; its grant waits for it (D-3)");

        store.OnLease = null;
        release.SetResult();
        (await lateTake!.WaitAsync(Wait)).Should().ContainSingle("the abandoned take did lease the record when it finally ran");
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the push ended what it took");

        (await PickupFromAsync(store.Inner, grant.Peer, fixture.Options.Now())).Should().ContainSingle("given back at once, not left to its lease")
            .Which.Id.Should().Be(stored.MessageId);
        LivePushWorld.Deliveries(session).Should().BeEmpty("an abandoned take never leads to a send");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "one outcome per push attempt, whatever the abandoned call did later");
        NoLeaks(world, grant);
    }

    [Fact]
    public async Task A_registration_check_that_throws_is_a_failure_and_never_a_push()
    {
        var mediation = new ScriptedMediationStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMediationStore>(mediation));
        var (grant, session) = await world.LiveGrantAsync("check-throws");
        var fixture = world.Fixture;
        var stored = await world.StoreAsync(grant);
        mediation.OnFind = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker + " " + grant.Recipient);

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Should().BeEmpty("a registration that cannot be confirmed is never pushed to");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
        NoLeaks(world, grant, stored.MessageId);
    }

    [Fact]
    public async Task A_registration_check_that_returns_a_grant_of_another_peer_is_unregistered()
    {
        var mediation = new ScriptedMediationStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMediationStore>(mediation));
        var (grant, session) = await world.LiveGrantAsync("check-other");
        var fixture = world.Fixture;
        var stored = await world.StoreAsync(grant);
        mediation.OnFind = (_, _) => ValueTask.FromResult<MediationGrant?>(new MediationGrant(grant.Peer + "x", new[] { world.Mediator.Did }));

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    // ---- the give-back and the deadline's callback -------------------------------------------------

    [Fact]
    public async Task A_give_back_that_ignores_the_cancellation_frees_the_worker_at_the_deadline_and_holds_its_grant_until_it_ends()
    {
        var parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedMessageStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var (grant, session) = await world.LiveGrantAsync("give-back");
        session.OnPush = (_, _) => Task.FromResult(ReturnRouteSendResult.NotSent);
        var fixture = world.Fixture;
        store.OnRelease = (_, _, _, _) =>
        {
            entered.TrySetResult();
            return new ValueTask<int>(parked.Task); // ignores the token
        };
        fixture.Dispatcher.Wake((await world.StoreAsync(grant)).GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        fixture.Dispatcher.Unsettled.Should().Be(1, "the give-back holds the push");
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the give-back still runs: the grant is not served again before it ends");
        parked.SetResult(1);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the give-back ended and the push settled");
    }

    [Fact]
    public async Task A_cancellation_callback_that_throws_cannot_escape_the_push_deadline()
    {
        var gate = new PushGate();
        await using var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("callback");
        session.OnPush = async (_, ct) =>
        {
            ct.Register(() => throw new HandlerBoomException(HandlerBoomException.Marker));
            await gate.PassAsync(ct);
            return ReturnRouteSendResult.Sent;
        };
        var fixture = world.Fixture;
        fixture.Dispatcher.Wake((await world.StoreAsync(grant)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        fixture.Invoking(f => f.Time.Advance(PushTimeout)).Should().NotThrow("the deadline's callback cancels inside a try");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
        LivePushWorld.Deliveries(session).Should().BeEmpty();
    }

    // ---- bounds and fairness (D-3, D-4) ----------------------------------------------------------------

    [Fact]
    public async Task With_every_operation_unsettled_a_wake_up_starts_nothing_and_the_next_one_after_they_settle_is_served()
    {
        // L-020 (PR #109 review round 2, D-4): what is bounded is the work that has not ended — at most
        // 2 x PushConcurrency operations between start and settlement, abandoned ones included. Beyond
        // it a wake-up starts nothing (Push-Skipped-Saturated) and its message stays queued.
        var gate = new PushGate();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = world.Fixture;
        fixture.Dispatcher.MaxUnsettled.Should().Be(2);
        var stalled = new List<LivePushGrant>();
        foreach (var label in new[] { "unsettled-1", "unsettled-2" })
        {
            var (grant, session) = await world.LiveGrantAsync(label);
            session.OnPush = async (_, _) =>
            {
                await gate.PassAsync(CancellationToken.None); // ignores the deadline
                return ReturnRouteSendResult.Sent;
            };
            stalled.Add(grant);
        }
        var (third, thirdSession) = await world.LiveGrantAsync("unsettled-3");

        for (var n = 0; n < stalled.Count; n++)
        {
            fixture.Dispatcher.Wake((await world.StoreAsync(stalled[n])).GrantPeerDid).Should().BeTrue();
            await gate.WaitForArrivalsAsync(n + 1, Wait);
            fixture.Time.Advance(PushTimeout);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        fixture.Dispatcher.Unsettled.Should().Be(2, "both abandoned sends still run");

        var waiting = await world.StoreAsync(third);
        fixture.Dispatcher.Wake(waiting.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(1);
        LivePushWorld.Deliveries(thirdSession).Should().BeEmpty("the third grant's message was not sent");

        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned sends ended");
        fixture.Dispatcher.Wake(waiting.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        LivePushWorld.Deliveries(thirdSession).Select(d => d.Id).Should().Equal(new[] { waiting.MessageId }, "the third grant's message, at its next wake-up");
    }

    [Fact]
    public async Task One_grant_never_has_two_pushes_in_flight_while_grants_together_use_every_worker()
    {
        var inFlight = new ConcurrentDictionary<string, int>();
        var maxPerGrant = 0;
        var maxTotal = 0;
        var total = 0;
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 4);
        var fixture = world.Fixture;
        var jobs = new List<StoredMessage>();
        for (var g = 0; g < 6; g++)
        {
            var label = $"fair-{g}";
            var (grant, session) = await world.LiveGrantAsync(label);
            session.OnPush = async (_, ct) =>
            {
                var mine = inFlight.AddOrUpdate(label, 1, (_, n) => n + 1);
                var all = Interlocked.Increment(ref total);
                InterlockedMax(ref maxPerGrant, mine);
                InterlockedMax(ref maxTotal, all);
                await Task.Yield();
                await Task.Delay(1, ct);
                inFlight.AddOrUpdate(label, 0, (_, n) => n - 1);
                Interlocked.Decrement(ref total);
                return ReturnRouteSendResult.Sent;
            };
            for (var j = 0; j < 10; j++)
                jobs.Add(await world.StoreAsync(grant, (byte)j));
        }

        jobs.Should().OnlyContain(job => fixture.Dispatcher.Wake(job.GrantPeerDid));
        await fixture.Dispatcher.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        maxPerGrant.Should().Be(1, "a grant's pushes go out one after another");
        maxTotal.Should().BeGreaterThan(1, "different grants are pushed in parallel");
        maxTotal.Should().BeLessThanOrEqualTo(4);
        fixture.Pushes(PushOutcomes.PushedLive).Should().Be(60);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task Wake_takes_a_bounded_grant_did_and_the_dispatcher_keeps_nothing_else_of_a_message()
    {
        // R1-1 under D-1: the dispatcher holds no per-message state at all; per grant it keeps the
        // grant's DID, bounded by construction.
        await using var fixture = PushFixture.Create();
        FluentActions.Invoking(() => fixture.Dispatcher.Wake("did:example:" + new string('d', 2048))).Should().Throw<ArgumentException>();
        fixture.Dispatcher.GrantEntries.Should().Be(0);
    }

    // ---- shutdown --------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_while_a_push_holds_a_throwing_cancellation_callback_neither_throws_nor_loses_a_message()
    {
        var gate = new PushGate();
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1);
        var (grant, session) = await world.LiveGrantAsync("shutdown");
        session.OnPush = async (_, ct) =>
        {
            ct.Register(() => throw new HandlerBoomException(HandlerBoomException.Marker));
            await gate.PassAsync(ct);
            return ReturnRouteSendResult.Sent;
        };
        var fixture = world.Fixture;
        var dispatcher = fixture.Dispatcher;
        dispatcher.Wake((await world.StoreAsync(grant, 1)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        for (var i = 0; i < 2; i++)
            dispatcher.Wake((await world.StoreAsync(grant, (byte)(2 + i))).GrantPeerDid).Should().BeTrue();

        dispatcher.Invoking(d => d.Dispose()).Should().NotThrow("the shutdown cancels inside a try");
        await dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => dispatcher.Unsettled == 0, "the cancelled push settled");

        // L-020 (PR #109 review round 2, D-2): the two wake-ups during the push coalesced into one held
        // wake-up; the push in flight and that wake-up each end once (Push-Skipped-Shutdown), not one
        // outcome per message; every message stays queued.
        fixture.PushOutcomeLog.Should().HaveCount(2).And.OnlyContain(o => o == PushOutcomes.SkippedShutdown);
        fixture.Store.MessageCount.Should().Be(3, "the push in flight was cancelled; all stay queued");
        LivePushWorld.Deliveries(session).Should().BeEmpty();
        dispatcher.Wake((await world.StoreAsync(grant, 9)).GrantPeerDid).Should().BeFalse();
        fixture.Pushes(PushOutcomes.SkippedShutdown).Should().Be(3);
        dispatcher.Invoking(d => d.Dispose()).Should().NotThrow("disposing twice is harmless");
        await dispatcher.DisposeAsync().AsTask().WaitAsync(Wait);
        NoLeaks(world, grant);
    }

    // ---- the execution context -------------------------------------------------------------------

    [Fact]
    public async Task Workers_do_not_inherit_the_execution_context_of_whoever_woke_them()
    {
        var marker = new AsyncLocal<string?>();
        string? seen = "not called";
        await using var world = await LivePushWorld.StartAsync();
        var (grant, session) = await world.LiveGrantAsync("context");
        session.OnPush = (_, _) =>
        {
            seen = marker.Value;
            return Task.FromResult(ReturnRouteSendResult.Sent);
        };
        var job = await world.StoreAsync(grant);

        marker.Value = "request-scoped";
        world.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        seen.Should().BeNull();
    }

    [Fact]
    public async Task Building_the_dispatcher_inside_a_suppressed_flow_still_starts_its_workers()
    {
        const string Peer = "did:example:dispatcher-suppressed";
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Peer + "-r");
        PushDispatcher dispatcher;
        using (ExecutionContext.SuppressFlow())
            dispatcher = fixture.Dispatcher;

        dispatcher.Wake((await fixture.StoreAsync(Peer + "-r")).GrantPeerDid).Should().BeTrue();
        await dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.SkippedNoLiveSession }, "a worker served the wake-up");
    }
}
