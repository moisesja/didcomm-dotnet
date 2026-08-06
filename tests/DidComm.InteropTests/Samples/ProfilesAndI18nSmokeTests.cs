using System.IO;
using FluentAssertions;
using Xunit;
using ProfilesProgram = DidComm.Samples.ProfilesAndI18n.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>10-ProfilesAndI18n</c> sample. Invokes
/// <see cref="ProfilesProgram.RunAsync"/> directly (no process spawn, fully offline) and
/// asserts accept-based profile selection over both advertisement channels, the mismatch
/// report, the chess i18n flow with its thread-scoped preference, and the bad-lang report.
/// </summary>
public sealed class ProfilesAndI18nSmokeTests
{
    [Fact]
    public async Task RunAsync_NegotiatesProfilesAndHonorsThreadScopedLanguage()
    {
        var writer = new StringWriter();

        await ProfilesProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // FR-PROF-01: selection over the OOB invitation's accept array…
        transcript.Should().Contain("Invitation accept = didcomm/aip1, didcomm/v2");
        transcript.Should().Contain("Negotiated profile = didcomm/v2");
        transcript.Should().Contain("Choose(null — peer makes no claim) = didcomm/v2");
        // …and over the DID document's DIDCommMessaging service accept.
        transcript.Should().Contain("Service accept = didcomm/v2");
        transcript.Should().Contain("Negotiated from service accept = didcomm/v2");

        // FR-PROF-02: no shared profile → null → problem-report, no crash.
        transcript.Should().Contain("Choose([didcomm/aip1, didcomm/v3]) = <null> (no shared profile)");
        transcript.Should().Contain("Report code = e.p.msg.unsupported");

        // FR-I18N-01/03: the chess move round-trips lang and accept-lang, in French.
        transcript.Should().Contain("Bob unpacks lang = fr");
        transcript.Should().Contain("Bob unpacks accept-lang = fr, en");
        transcript.Should().Contain("Comment (interpreted as French) = C'est échec et mat.");
        transcript.Should().Contain("Reply lang = fr");
        transcript.Should().Contain("Reply comment = Échec et mat. Bien joué.");

        // FR-I18N-02: the preference is thread state — honored on the follow-up without
        // re-declaration, invisible to a concurrent thread.
        transcript.Should().Contain("Follow-up carries accept-lang = False");
        transcript.Should().Contain("Second reply on chess thread — lang = fr");
        transcript.Should().Contain("Second reply honors the recorded preference = True");
        transcript.Should().Contain("Concurrent-thread reply lang = en");
        transcript.Should().Contain("Chess preference did not leak = True");

        // FR-I18N-04: unsatisfiable accept-lang produced w.msg.bad-lang on the failing thread.
        transcript.Should().Contain("Report code = w.msg.bad-lang");
        transcript.Should().Contain("Report pthid == failing thid = True");
        transcript.Should().Contain("languages available here: en, fr.");
    }
}
