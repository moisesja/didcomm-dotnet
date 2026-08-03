using System.IO;
using FluentAssertions;
using Xunit;
using ProblemsProgram = DidComm.Samples.ProblemsAndProtocols.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>07-ProblemsAndProtocols</c> sample. Invokes
/// <see cref="ProblemsProgram.RunAsync"/> directly (no process spawn, fully offline) and
/// asserts the taxonomy, interpolation, escalation, cascade-guard, bad-lang, Empty-ACK,
/// custom-handler, observer, feature-provider, and Trace-posture outcomes.
/// </summary>
public sealed class ProblemsAndProtocolsSmokeTests
{
    [Fact]
    public async Task RunAsync_WalksProblemsProtocolsObserversAndTracing()
    {
        var writer = new StringWriter();

        await ProblemsProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // FR-PROTO-08: taxonomy parts and structural (per-segment) prefix matching.
        transcript.Should().Contain("Sorter / Scope / Descriptor = e / p / xfer.cant-use-endpoint");
        transcript.Should().Contain("StartsWith(\"e.p.xfer\") = True");
        transcript.Should().Contain("StartsWith(\"e.p.xf\") = False");
        transcript.Should().Contain("TryParse(\"x.p.bad-sorter\") = False");

        // FR-PROTO-07: REQUIRED pthid, {n} interpolation, '?' for missing args, extras appended.
        transcript.Should().Contain("Create without pthid = refused (ArgumentException — pthid is REQUIRED)");
        transcript.Should().Contain("Rendered comment = Unable to use the https://agents.r.us/inbox endpoint for did:peer:2.");
        transcript.Should().Contain("Missing arg renders '?' = Field thid clashed with ?.");
        transcript.Should().Contain("Extra args are appended = Only first is referenced. [extra: second, third]");
        transcript.Should().Contain("body.escalate_to = mailto:admin@sad-agent.example");

        // FR-PROTO-09: warning → error with the scope preserved; errors don't escalate.
        transcript.Should().Contain("Escalated code = e.m.xfer.failed");
        transcript.Should().Contain("Escalating an error = refused (only warnings escalate)");

        // FR-PROTO-10: budget of 2 → trip on the 3rd report, then silence.
        transcript.Should().Contain("report #2 outcome = NoReply");
        transcript.Should().Contain("report #3 outcome = ReplyProduced");
        transcript.Should().Contain("Cascade-stop code = e.p.req.max-errors-exceeded");
        transcript.Should().Contain("report #4 outcome = NoReply");
        transcript.Should().Contain("Post-trip reports stay silent = True");

        // FR-I18N-04: both bad-lang forms plus the thread-aware factory's null path.
        transcript.Should().Contain("Code = w.msg.bad-lang");
        transcript.Should().Contain("Fatal form code = e.msg.bad-lang");
        transcript.Should().Contain("Satisfiable preference produces = <null> (no report warranted)");

        // FR-PROTO-06: header-only Empty ACK consumed with no reply.
        transcript.Should().Contain("ack[] names the request = True");
        transcript.Should().Contain("Empty dispatch outcome = NoReply");
        transcript.Should().Contain("Handled by = https://didcomm.org/empty/1.0");

        // FR-PROTO-03: the custom lets_do_lunch handler answered on-thread.
        transcript.Should().Contain("Dispatched to = https://didcomm.org/lets-do-lunch/1.0");
        transcript.Should().Contain("Reply thid == proposal.id = True");
        transcript.Should().Contain("Bob accepted = True");

        // FR-PROTO-12: the read-only observer saw all four cascade reports, authenticated.
        transcript.Should().Contain("All four cascade reports observed = True");
        transcript.Should().Contain("Observed problem-report count = 4");
        transcript.Should().Contain("Every observation was authenticated = True");

        // FR-PROTO-05: the custom goal-code provider surfaced through Discover Features.
        transcript.Should().Contain("- disclosed = goal-code: org.example.lunch");

        // FR-PROTO-11a: off by default, allowlist-gated when opted in, loud misconfig.
        transcript.Should().Contain("ShouldReport (defaults) = False");
        transcript.Should().Contain("ShouldReport (opted in, allowlisted) = True");
        transcript.Should().Contain("ShouldReport (opted in, NOT allowlisted) = False");
        transcript.Should().Contain("EnableTracing without an allowlist = refused at composition time");
    }
}
