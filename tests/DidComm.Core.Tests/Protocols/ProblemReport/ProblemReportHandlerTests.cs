using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

public sealed class ProblemReportHandlerTests
{
    private static ProtocolContext Ctx(Message m, IThreadStateStore? store = null)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            false, false, false, false, null, null, null, null, null, null,
            Array.Empty<string>(), null);
        var threads = store ?? new InMemoryThreadStateStore();
        var ownThread = threads.GetOrCreate(m.Thid ?? m.Id);
        return new ProtocolContext(unpacked, ownThread, Client: null, new DidCommOptions(), threads);
    }

    private static ProblemReportHandler BuildHandler(int cascadeThreshold = 5, CascadeBudgetStore? budget = null)
        => new(Options.Create(new ProblemReportOptions { CascadeThreshold = cascadeThreshold }), budget ?? new CascadeBudgetStore());

    [Fact]
    public async Task Warning_report_does_not_increment_error_count()
    {
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(budget: budget);
        var warning = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "w.m.req.expired", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();
        var reply = await handler.HandleAsync(warning, Ctx(warning, store), CancellationToken.None);
        reply.Should().BeNull();
        // A warning code must not create or advance the FR-PROTO-10 budget entry (PR #40 review: assert
        // against the budget store, since the handler no longer writes the general store for any code).
        budget.Peek("thread-1").ErrorCount.Should().Be(0);
        budget.Count.Should().Be(0, "warnings create no budget entry at all");
    }

    [Fact]
    public async Task Error_report_increments_error_count_and_returns_null_under_threshold()
    {
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(cascadeThreshold: 5, budget);
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();

        for (var i = 1; i <= 5; i++)
        {
            var reply = await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
            reply.Should().BeNull($"report #{i} is at or under the threshold");
        }
        budget.Peek("thread-1").ErrorCount.Should().Be(5);
    }

    [Fact]
    public async Task Cascade_guard_emits_max_errors_exceeded_on_first_breach()
    {
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(cascadeThreshold: 2, budget);
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();

        await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
        await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
        budget.Peek("thread-1").ErrorCount.Should().Be(2);

        // The third error trips the guard (>threshold of 2).
        var trip = await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
        trip.Should().NotBeNull();
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        trip!.Pthid.Should().Be("thread-1");
        // Reply is addressed back to the original sender of the error report.
        trip.From.Should().Be("did:peer:bob");
        trip.To.Should().Equal("did:peer:alice");
    }

    [Fact]
    public async Task Cascade_guard_emits_exactly_once_then_silent()
    {
        var handler = BuildHandler(cascadeThreshold: 1);
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();

        await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None); // count=1, at threshold
        var trip = await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None); // count=2, trip
        trip.Should().NotBeNull();

        // Subsequent errors on same thread → null.
        for (var i = 0; i < 3; i++)
        {
            var more = await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
            more.Should().BeNull();
        }
    }

    [Fact]
    public async Task Returns_null_when_inbound_code_is_absent_or_malformed()
    {
        var handler = BuildHandler();
        var noBody = new MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithFrom("did:peer:alice").WithTo("did:peer:bob").WithPthid("p")
            .Build();
        (await handler.HandleAsync(noBody, Ctx(noBody), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Wrong_message_type_returns_null()
    {
        var handler = BuildHandler();
        var notAProblem = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:peer:alice").WithTo("did:peer:bob").WithPthid("p")
            .Build();
        (await handler.HandleAsync(notAProblem, Ctx(notAProblem), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void ProtocolUri_matches_spec()
    {
        BuildHandler().ProtocolUri.Should().Be("https://didcomm.org/report-problem/2.0");
    }

    [Fact]
    public async Task Cascade_trip_is_deferred_when_threshold_lands_on_an_unrepliable_report()
    {
        // FR-PROTO-10: when the report that pushes the count past the threshold has no From or
        // To (e.g. anoncrypt), the cascade-stop emission must be deferred — leaving the
        // MaxErrorsNoticeSent flag unset — so the NEXT repliable report can fire the trip.
        // Without the deferral, the counter would land past the threshold and every subsequent
        // report would fall into the silent-ignore branch, so the cascade-stop is never emitted.
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(cascadeThreshold: 1, budget);
        var store = new InMemoryThreadStateStore();

        var first = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        (await handler.HandleAsync(first, Ctx(first, store), CancellationToken.None)).Should().BeNull();

        var unrepliable = new MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithBody(new System.Text.Json.Nodes.JsonObject { ["code"] = "e.p.xfer.cant-use-endpoint" })
            .WithPthid("thread-1")
            .Build();
        (await handler.HandleAsync(unrepliable, Ctx(unrepliable, store), CancellationToken.None))
            .Should().BeNull("trip emission requires a repliable target");
        budget.Peek("thread-1").NoticeSent.Should().BeFalse(
            "the cascade flag stays unset so a later repliable report can still fire the trip");

        var thirdRepliable = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var trip = await handler.HandleAsync(thirdRepliable, Ctx(thirdRepliable, store), CancellationToken.None);
        trip.Should().NotBeNull("the deferred cascade-stop must fire on the next repliable report");
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        budget.Peek("thread-1").NoticeSent.Should().BeTrue();
    }

    [Fact]
    public async Task Cascade_trip_fires_exactly_once_under_concurrent_dispatches()
    {
        // FR-PROTO-10: concurrent inbound error reports on the same pthid (singleton handler
        // fed by parallel transports) must yield exactly one cascade-stop. Without per-thread
        // atomicity around the increment + threshold check, races would either double-emit OR
        // skip the trip equality entirely. This stress test pins the fix.
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(cascadeThreshold: 5, budget);
        var store = new InMemoryThreadStateStore();
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-race");

        const int concurrent = 200;
        var tasks = new Task<Message?>[concurrent];
        for (var i = 0; i < concurrent; i++)
            tasks[i] = Task.Run(() => handler.HandleAsync(error, Ctx(error, store), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is not null).Should().Be(1,
            "the cascade-stop must be emitted exactly once across all concurrent reports on the same pthid");
        budget.Peek("thread-race").NoticeSent.Should().BeTrue();
    }

    [Fact]
    public async Task Sustained_unrepliable_stream_clamps_counter_and_log_then_fires_on_repliable_FrProto10_29()
    {
        // Issue #29: a sustained stream of unrepliable (from-less) over-threshold reports must NOT grow
        // the counter (or log) without bound — the trip is deferred (no repliable target), but the work
        // is clamped. The counter sits at threshold+1; a later repliable report still fires the trip.
        const int threshold = 2;
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(threshold, budget);

        var unrepliable = new MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithBody(new System.Text.Json.Nodes.JsonObject { ["code"] = "e.p.xfer.cant-use-endpoint" })
            .WithPthid("thread-x")
            .Build();

        for (var i = 0; i < 50; i++)
        {
            var reply = await handler.HandleAsync(unrepliable, Ctx(unrepliable), CancellationToken.None);
            reply.Should().BeNull("an unrepliable report has no target for the cascade-stop");
        }

        // Clamped at threshold+1 (NOT 50), trip still deferred.
        budget.Peek("thread-x").ErrorCount.Should().Be(threshold + 1);
        budget.Peek("thread-x").NoticeSent.Should().BeFalse();

        // A later repliable report on the same pthid fires the deferred cascade-stop.
        var repliable = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-x");
        var trip = await handler.HandleAsync(repliable, Ctx(repliable), CancellationToken.None);
        trip.Should().NotBeNull();
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
    }

    [Fact]
    public async Task Cascade_budget_survives_a_general_store_thid_flood_FrProto10_36()
    {
        // Issue #36: the cascade budget lives in a DEDICATED store, so flooding the dispatcher's
        // general thread store with cheap fresh thids cannot evict and reset a victim pthid's budget.
        // On main (where both share one store) the flood would reset the victim and the trip would not
        // fire; here the budget survives and the guard still trips.
        const int threshold = 2;
        var budget = new CascadeBudgetStore();
        var handler = BuildHandler(threshold, budget);
        var generalStore = new InMemoryThreadStateStore(maxEntries: 10); // tiny so the flood definitely evicts

        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "victim");

        await handler.HandleAsync(error, Ctx(error, generalStore), CancellationToken.None); // budget: victim=1
        for (var i = 0; i < 1000; i++) generalStore.GetOrCreate($"flood-a-{i}"); // evict everything in the general store
        await handler.HandleAsync(error, Ctx(error, generalStore), CancellationToken.None); // budget: victim=2
        for (var i = 0; i < 1000; i++) generalStore.GetOrCreate($"flood-b-{i}");

        // The dedicated budget is intact despite the general-store flood.
        budget.Peek("victim").ErrorCount.Should().Be(2);

        var trip = await handler.HandleAsync(error, Ctx(error, generalStore), CancellationToken.None);
        trip.Should().NotBeNull("the cascade budget was not reset by the general-store flood, so the 3rd report trips");
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
    }

    [Fact]
    public void Ctor_validates_options_eagerly()
    {
        // FR-PROTO-10: a negative CascadeThreshold silently disables the cascade guard (count > -1+1
        // = 0 is true from the first report). The handler must reject the misconfig at construction.
        var act = () => new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = -1 }));
        act.Should().Throw<InvalidOperationException>().WithMessage("*CascadeThreshold*");
    }

    [Fact]
    public async Task Cascade_stop_never_double_emits_even_under_eviction_pressure()
    {
        // Red-team (#36): the budget store's LRU eviction must NOT split the lock seam. An earlier
        // design locked on the evictable per-pthid state object, so under eviction two concurrent
        // callers could lock DIFFERENT instances for the same pthid and double-emit. The atomic,
        // pthid-keyed store must yield AT MOST ONE cascade-stop for the victim despite concurrent
        // reports + a flood that drives constant eviction (tiny cap).
        const int threshold = 2;
        var budget = new CascadeBudgetStore(maxEntries: 8);
        var handler = BuildHandler(threshold, budget);
        var store = new InMemoryThreadStateStore();
        var victim = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "victim");

        var tasks = new List<Task<Message?>>();
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => handler.HandleAsync(victim, Ctx(victim, store), CancellationToken.None)));
            var n = i;
            tasks.Add(Task.Run(() => handler.HandleAsync(
                ProblemReportApi.Create(from: "did:peer:f", to: "did:peer:t", code: "e.p.xfer.cant-use-endpoint", pthid: $"flood-{n}"),
                Ctx(victim, store), CancellationToken.None)));
        }
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is not null && r.Pthid == "victim"
                && ProblemReportApi.ReadCode(r) == ProblemReportApi.MaxErrorsExceededCode)
            .Should().BeLessThanOrEqualTo(1, "the cascade-stop must never double-emit, even under eviction");
    }

    [Fact]
    public async Task A_tripped_thread_stays_silenced_after_a_flood_of_fresh_pthids()
    {
        // Red-team (#36): once a thread trips, its silenced decision survives a flood of fresh
        // (non-tripped) pthids — the budget's LRU prefers evicting not-yet-tripped entries, so the
        // tripped victim is not reset and re-emit. (Would fail under a plain LRU that evicts the idle
        // tripped victim.)
        const int threshold = 1;
        var budget = new CascadeBudgetStore(maxEntries: 16);
        var handler = BuildHandler(threshold, budget);
        var store = new InMemoryThreadStateStore();
        var victim = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "victim");

        await handler.HandleAsync(victim, Ctx(victim, store), CancellationToken.None);
        (await handler.HandleAsync(victim, Ctx(victim, store), CancellationToken.None)).Should().NotBeNull(); // trips

        for (var i = 0; i < 500; i++)
        {
            await handler.HandleAsync(
                ProblemReportApi.Create(from: "did:peer:f", to: "did:peer:t", code: "e.p.xfer.cant-use-endpoint", pthid: $"flood-{i}"),
                Ctx(victim, store), CancellationToken.None);
        }

        budget.Peek("victim").NoticeSent.Should().BeTrue("the tripped decision survives the fresh-pthid flood");
        (await handler.HandleAsync(victim, Ctx(victim, store), CancellationToken.None))
            .Should().BeNull("a tripped thread stays silent — no re-emit after the flood");
    }

    [Fact]
    public async Task Budget_store_stays_bounded_even_at_a_cap_of_one()
    {
        // PR #40 review: maxEntries=1 must actually bound the store. A naive low-water (== cap for
        // maxEntries=1) made Evict() a no-op, so the store grew by one per distinct pthid without limit.
        var budget = new CascadeBudgetStore(maxEntries: 1);
        var handler = BuildHandler(budget: budget);
        var store = new InMemoryThreadStateStore();

        for (var i = 0; i < 200; i++)
        {
            var r = ProblemReportApi.Create(
                from: "did:peer:a", to: "did:peer:b", code: "e.p.xfer.cant-use-endpoint", pthid: $"p-{i}");
            await handler.HandleAsync(r, Ctx(r, store), CancellationToken.None);
        }

        budget.Count.Should().BeLessThanOrEqualTo(1, "the cap must hold even at maxEntries=1");
    }
}
