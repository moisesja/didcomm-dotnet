using DidComm.Protocols.ProblemReport;
using FluentAssertions;
using Xunit;

// L-014: alias the static API class to dodge namespace shadowing; ThreadState likewise
// collides with System.Threading.ThreadState from the global usings.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;
using ThreadState = DidComm.Threading.ThreadState;

namespace DidComm.Tests.Protocols.ProblemReport;

/// <summary>
/// FR-I18N-04 — <c>w.msg.bad-lang</c> / <c>e.msg.bad-lang</c> problem reports when no
/// acceptable language is available. Acceptance bar: "Bad-lang report constructed."
/// </summary>
public sealed class BadLangReportTests
{
    private const string Bob = "did:peer:bob";
    private const string Alice = "did:peer:alice";

    [Fact]
    public void CreateBadLang_warning_variant_is_default()
    {
        var report = ProblemReportApi.CreateBadLang(
            from: Bob, to: Alice, pthid: "failing-thread", availableLangs: new[] { "en", "es" });

        report.Type.Should().Be(ProblemReportApi.MessageType);
        report.Pthid.Should().Be("failing-thread", "pthid must point at the failing thread (FR-PROTO-07)");
        ProblemReportApi.ReadCode(report).Should().Be("w.msg.bad-lang");
        ProblemReportApi.RenderComment(report).Should().Contain("en, es");
    }

    [Fact]
    public void CreateBadLang_fatal_emits_error_sorter()
    {
        var report = ProblemReportApi.CreateBadLang(
            from: Bob, to: Alice, pthid: "failing-thread", availableLangs: new[] { "en" }, fatal: true);

        ProblemReportApi.ReadCode(report).Should().Be("e.msg.bad-lang");
    }

    [Theory]
    [InlineData(ProblemReportApi.BadLangWarningCode, "w")]
    [InlineData(ProblemReportApi.BadLangErrorCode, "e")]
    public void BadLang_codes_match_the_FrProto08_taxonomy(string code, string expectedSorter)
    {
        // The codes must parse under the FR-PROTO-08 grammar (sorter.scope.descriptor with
        // prefix matching), exactly as any other spec problem-code does.
        var parsed = ProblemCode.Parse(code);

        parsed.Sorter.Should().Be(expectedSorter);
        parsed.Value.Should().EndWith("msg.bad-lang");
        parsed.StartsWith($"{expectedSorter}.msg").Should().BeTrue("prefix matching must recognize the msg descriptor");
        (expectedSorter == "w" ? parsed.IsWarning : parsed.IsError).Should().BeTrue();
    }

    [Fact]
    public void CreateBadLang_with_no_available_languages_says_none()
    {
        var report = ProblemReportApi.CreateBadLang(
            from: Bob, to: Alice, pthid: "t1", availableLangs: Array.Empty<string>());

        ProblemReportApi.RenderComment(report).Should().Contain("none");
    }

    [Fact]
    public void CreateBadLang_carries_escalate_to_when_supplied()
    {
        var report = ProblemReportApi.CreateBadLang(
            from: Bob, to: Alice, pthid: "t1", availableLangs: new[] { "en" },
            escalateTo: "mailto:support@example.com");

        report.Body!["escalate_to"]!.GetValue<string>().Should().Be("mailto:support@example.com");
    }

    [Fact]
    public void ForThread_builds_report_when_no_preference_is_satisfiable()
    {
        var thread = new ThreadState("thread-42") { AcceptLang = new[] { "fr", "de" } };

        var report = ProblemReportApi.CreateBadLangForThread(
            from: Bob, to: Alice, thread, availableLangs: new[] { "en", "es" });

        report.Should().NotBeNull();
        report!.Pthid.Should().Be("thread-42", "the report threads via pthid = the failing thread's thid");
        ProblemReportApi.ReadCode(report).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_returns_null_when_a_preference_matches_exactly()
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { "fr", "EN" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("case-insensitive exact match satisfies the preference");
    }

    [Fact]
    public void ForThread_returns_null_on_primary_subtag_match()
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { "en-US" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en-GB" })
            .Should().BeNull("en-US and en-GB share the primary subtag 'en'");
    }

    [Fact]
    public void ForThread_returns_null_when_thread_declared_no_preference()
    {
        var thread = new ThreadState("t"); // AcceptLang null — FR-I18N-02 state never set

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("without a declared preference there is nothing to fail");
    }

    [Fact]
    public void ForThread_fatal_variant_flows_through()
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { "fr" } };

        var report = ProblemReportApi.CreateBadLangForThread(
            Bob, Alice, thread, new[] { "en" }, fatal: true);

        ProblemReportApi.ReadCode(report!).Should().Be("e.msg.bad-lang");
    }
}
