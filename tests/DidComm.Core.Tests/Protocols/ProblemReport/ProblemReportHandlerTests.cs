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

    private static ProblemReportHandler BuildHandler(int cascadeThreshold = 5)
        => new(Options.Create(new ProblemReportOptions { CascadeThreshold = cascadeThreshold }));

    [Fact]
    public async Task Warning_report_does_not_increment_error_count()
    {
        var handler = BuildHandler();
        var warning = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "w.m.req.expired", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();
        var reply = await handler.HandleAsync(warning, Ctx(warning, store), CancellationToken.None);
        reply.Should().BeNull();
        // No state was created for "thread-1" since warnings don't trigger error accounting.
        store.Get("thread-1").Should().BeNull();
    }

    [Fact]
    public async Task Error_report_increments_error_count_and_returns_null_under_threshold()
    {
        var handler = BuildHandler(cascadeThreshold: 5);
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();

        for (var i = 1; i <= 5; i++)
        {
            var reply = await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
            reply.Should().BeNull($"report #{i} is at or under the threshold");
        }
        store.GetOrCreate("thread-1").ErrorCount.Should().Be(5);
    }

    [Fact]
    public async Task Cascade_guard_emits_max_errors_exceeded_on_first_breach()
    {
        var handler = BuildHandler(cascadeThreshold: 2);
        var error = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var store = new InMemoryThreadStateStore();

        await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
        await handler.HandleAsync(error, Ctx(error, store), CancellationToken.None);
        store.GetOrCreate("thread-1").ErrorCount.Should().Be(2);

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
        var handler = BuildHandler(cascadeThreshold: 1);
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
        store.GetOrCreate("thread-1").MaxErrorsNoticeSent.Should().BeFalse(
            "the cascade flag stays unset so a later repliable report can still fire the trip");

        var thirdRepliable = ProblemReportApi.Create(
            from: "did:peer:alice", to: "did:peer:bob",
            code: "e.p.xfer.cant-use-endpoint", pthid: "thread-1");
        var trip = await handler.HandleAsync(thirdRepliable, Ctx(thirdRepliable, store), CancellationToken.None);
        trip.Should().NotBeNull("the deferred cascade-stop must fire on the next repliable report");
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        store.GetOrCreate("thread-1").MaxErrorsNoticeSent.Should().BeTrue();
    }

    [Fact]
    public async Task Cascade_trip_fires_exactly_once_under_concurrent_dispatches()
    {
        // FR-PROTO-10: concurrent inbound error reports on the same pthid (singleton handler
        // fed by parallel transports) must yield exactly one cascade-stop. Without per-thread
        // atomicity around the increment + threshold check, races would either double-emit OR
        // skip the trip equality entirely. This stress test pins the fix.
        var handler = BuildHandler(cascadeThreshold: 5);
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
        store.GetOrCreate("thread-race").MaxErrorsNoticeSent.Should().BeTrue();
    }

    [Fact]
    public void Ctor_validates_options_eagerly()
    {
        // FR-PROTO-10: a negative CascadeThreshold silently disables the cascade guard (count > -1+1
        // = 0 is true from the first report). The handler must reject the misconfig at construction.
        var act = () => new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = -1 }));
        act.Should().Throw<InvalidOperationException>().WithMessage("*CascadeThreshold*");
    }
}
