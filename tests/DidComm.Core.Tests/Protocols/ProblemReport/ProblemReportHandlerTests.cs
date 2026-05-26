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
        return new ProtocolContext(unpacked, ownThread, threads, Client: null, new DidCommOptions());
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
}
