using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

/// <summary>
/// FR-PROTO-10 cascade budget, keyed by the failing thread's <c>pthid</c>: DIDComm v2.1 threading
/// (threading.md L9, L21) compares a <c>pthid</c> ignoring letter case, so reports whose
/// <c>pthid</c>s differ only in case spend one thread's budget, through the real handler.
/// </summary>
public sealed class CascadeBudgetThreadCaseTests
{
    private const string Alice = "did:peer:alice";
    private const string Bob = "did:peer:bob";

    private static ProtocolContext Ctx(Message m, IThreadStateStore store)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            false, false, false, false, null, null, null, null, null, null,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, store.GetOrCreate(m.Thid ?? m.Id), Client: null, new DidCommOptions(), store);
    }

    private static Message Error(string pthid)
        => ProblemReportApi.Create(from: Alice, to: Bob, code: "e.p.xfer.cant-use-endpoint", pthid: pthid);

    [Fact]
    public async Task Reports_whose_pthids_differ_only_in_case_count_against_one_thread_and_trip_it()
    {
        var budget = new CascadeBudgetStore();
        var handler = new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = 2 }), budget);
        var store = new InMemoryThreadStateStore();
        var first = Error("failing-thread-abc");
        var second = Error("FAILING-THREAD-ABC");
        var third = Error("Failing-Thread-Abc");

        var r1 = await handler.HandleAsync(first, Ctx(first, store), CancellationToken.None);
        var r2 = await handler.HandleAsync(second, Ctx(second, store), CancellationToken.None);
        var trip = await handler.HandleAsync(third, Ctx(third, store), CancellationToken.None);

        r1.Should().BeNull();
        r2.Should().BeNull();
        trip.Should().NotBeNull("the third report on the one thread breaches a budget of 2");
        ProblemReportApi.ReadCode(trip!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        trip!.Pthid.Should().Be("Failing-Thread-Abc", "the cascade-stop names the thread exactly as the report that tripped it did");
        budget.Count.Should().Be(1);
        budget.Peek("failing-thread-abc").ErrorCount.Should().Be(3);
    }

    [Fact]
    public async Task Reports_on_threads_that_differ_in_more_than_case_keep_separate_budgets()
    {
        // Control for the test above: three distinct threads, one report each, nothing trips.
        var budget = new CascadeBudgetStore();
        var handler = new ProblemReportHandler(Options.Create(new ProblemReportOptions { CascadeThreshold = 2 }), budget);
        var store = new InMemoryThreadStateStore();
        var replies = new List<Message?>();

        foreach (var pthid in new[] { "failing-thread-abc", "FAILING-THREAD-ABD", "failing-thread-abc0" })
        {
            var report = Error(pthid);
            replies.Add(await handler.HandleAsync(report, Ctx(report, store), CancellationToken.None));
        }

        replies.Should().AllSatisfy(r => r.Should().BeNull());
        budget.Count.Should().Be(3);
        budget.Peek("failing-thread-abc").ErrorCount.Should().Be(1);
    }
}
