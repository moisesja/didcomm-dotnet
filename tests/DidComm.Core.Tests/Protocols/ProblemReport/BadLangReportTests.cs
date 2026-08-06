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
    public void ForThread_builds_report_for_sibling_region_subtags()
    {
        // This test previously asserted the OPPOSITE (null): the old matcher treated any two
        // tags with the same primary subtag as interchangeable, which pinned a bug — en-US and
        // en-GB are sibling tags, and neither is a prefix of the other, so under RFC 4647
        // directional matching the preference is NOT satisfiable and a report must be emitted.
        var thread = new ThreadState("t") { AcceptLang = new[] { "en-US" } };

        var report = ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en-GB" });

        report.Should().NotBeNull("en-US and en-GB are siblings — neither extends the other");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_builds_report_for_sibling_script_subtags()
    {
        // sr-Cyrl and sr-Latn share a primary subtag but different scripts; a reader of one
        // often cannot read the other, so this must NOT count as satisfiable.
        var thread = new ThreadState("t") { AcceptLang = new[] { "sr-Cyrl" } };

        var report = ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "sr-Latn" });

        report.Should().NotBeNull("sr-Cyrl is not satisfied by sr-Latn — sibling scripts");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_builds_report_for_traditional_vs_simplified_chinese()
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { "zh-Hant" } };

        var report = ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "zh-Hans" });

        report.Should().NotBeNull("zh-Hant is not satisfied by zh-Hans — sibling scripts");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_returns_null_when_bare_range_matches_more_specific_tag()
    {
        // RFC 4647 basic filtering: the range "en" matches any tag it prefixes at a subtag
        // boundary, so an agent offering en-GB satisfies a preference for plain "en".
        var thread = new ThreadState("t") { AcceptLang = new[] { "en" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en-GB" })
            .Should().BeNull("the range 'en' matches the more specific tag 'en-GB'");
    }

    [Fact]
    public void ForThread_returns_null_when_bare_tag_satisfies_more_specific_range()
    {
        // Lookup-style truncation: a preference for en-US falls back to bare "en" when that is
        // what the agent offers (same direction the ProfilesAndI18n sample's selector uses).
        var thread = new ThreadState("t") { AcceptLang = new[] { "en-US" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("a preference for en-US truncates to the offered bare 'en'");
    }

    [Fact]
    public void ForThread_returns_null_when_broad_request_is_served_by_a_specific_catalog_entry()
    {
        // The emitter contract (FR-I18N-02, "when a matching language is available"): whatever the
        // matcher calls satisfiable, the reply selector must be able to actually produce. This is
        // the basic-filtering direction — a catalog that only holds region-tagged entries still
        // satisfies a bare range, so we stay silent AND the selector must answer in en-GB.
        var thread = new ThreadState("t") { AcceptLang = new[] { "en" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "fr", "en-GB" })
            .Should().BeNull("the range 'en' is matched by the catalog entry 'en-GB'");
    }

    [Fact]
    public void ForThread_returns_null_when_peer_accepts_any_language()
    {
        // RFC 4647 §2.1: the range "*" means "any language is acceptable". Reporting bad-lang at a
        // peer who told us they will take anything is a spurious report.
        var thread = new ThreadState("t") { AcceptLang = new[] { "*" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("'*' accepts any available language");
    }

    [Fact]
    public void ForThread_returns_null_when_wildcard_follows_an_unsatisfiable_preference()
    {
        // Preference order does not matter for satisfiability: fr is unavailable, but the trailing
        // "*" still says "anything works".
        var thread = new ThreadState("t") { AcceptLang = new[] { "fr", "*" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("the wildcard entry accepts the available 'en'");
    }

    [Theory]
    [InlineData((object)new string[0])]
    [InlineData((object)new[] { "", "   " })]
    [InlineData((object)new[] { "-en" })]
    public void ForThread_builds_report_for_wildcard_when_nothing_is_available(string[] availableLangs)
    {
        // "*" accepts anything — but only if there IS something. With no usable language to emit,
        // the report is still warranted.
        var thread = new ThreadState("t") { AcceptLang = new[] { "*" } };

        var report = ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, availableLangs);

        report.Should().NotBeNull("'*' cannot be satisfied by an empty catalog");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Theory]
    // Identical garbage on both sides must not short-circuit through the equality path...
    [InlineData("-en", "-en")]
    [InlineData("-", "-")]
    // ...and neither may a tag whose empty subtag sits at the end or in the middle.
    [InlineData("en-", "en")]
    [InlineData("en--GB", "en")]
    [InlineData("en", "en-")]
    public void ForThread_malformed_tags_with_an_empty_subtag_never_match(string preferred, string offered)
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { preferred } };

        var report = ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { offered });

        report.Should().NotBeNull($"'{preferred}' carries an empty subtag and cannot satisfy or be satisfied by '{offered}'");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_returns_null_when_thread_declared_no_preference()
    {
        var thread = new ThreadState("t"); // AcceptLang null — FR-I18N-02 state never set

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "en" })
            .Should().BeNull("without a declared preference there is nothing to fail");
    }

    [Fact]
    public void ForThread_leading_dash_tags_do_not_match_on_empty_primary_subtag()
    {
        // Malformed tags with a leading '-' have an EMPTY primary subtag. Two empties must not
        // satisfy each other — that would silently suppress the report whenever both sides carry
        // garbage tags (the peer's accept-lang is remote input).
        var thread = new ThreadState("t") { AcceptLang = new[] { "-fonipa" } };

        var report = ProblemReportApi.CreateBadLangForThread(
            Bob, Alice, thread, availableLangs: new[] { "-x-private" });

        report.Should().NotBeNull("an empty primary subtag never satisfies a preference");
        ProblemReportApi.ReadCode(report!).Should().Be("w.msg.bad-lang");
    }

    [Fact]
    public void ForThread_whitespace_and_empty_tags_are_ignored_not_matched()
    {
        var thread = new ThreadState("t") { AcceptLang = new[] { "", "  ", "fr" } };

        ProblemReportApi.CreateBadLangForThread(Bob, Alice, thread, new[] { "", "en" })
            .Should().NotBeNull("blank tags on either side are skipped, and fr∉{en}");
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
