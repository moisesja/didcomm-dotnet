using System.IO;
using FluentAssertions;
using Xunit;
using EnvelopesProgram = DidComm.Samples.EnvelopesAndMessages.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>03-EnvelopesAndMessages</c> sample. Invokes
/// <see cref="EnvelopesProgram.RunAsync"/> directly (no process spawn), capturing console
/// output, and asserts every section banner (tasks C–N) plus the distinctive per-section
/// outcomes — so a section that silently degrades still fails CI.
/// </summary>
public sealed class EnvelopesAndMessagesSmokeTests
{
    [Fact]
    public async Task RunAsync_PrintsEverySectionAndOutcome()
    {
        var writer = new StringWriter();

        await EnvelopesProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // One banner per §14.2 task letter the sample covers.
        transcript.Should().Contain("Section C — Plaintext (debug/inspection only)");
        transcript.Should().Contain("Section D — Signed (non-repudiable, no confidentiality)");
        transcript.Should().Contain("Section E — Anoncrypt (confidential, anonymous sender)");
        transcript.Should().Contain("Section F — Authcrypt (confidential + sender authenticated — the default)");
        transcript.Should().Contain("Section G — Sign-then-encrypt (add non-repudiation)");
        transcript.Should().Contain("Section H — Protect the sender (anoncrypt wraps authcrypt)");
        transcript.Should().Contain("Section I — Content encryption — each algorithm on the composition that allows it");
        transcript.Should().Contain("Section J — Multi-recipient (one envelope, several readers)");
        transcript.Should().Contain("Section K — Unpack metadata — every field, one envelope");
        transcript.Should().Contain("Section L — Attachments (inline json / base64 / linked-with-hash)");
        transcript.Should().Contain("Section M — Threading & ACKs (thid / pthid / please_ack / ack)");
        transcript.Should().Contain("Section N — DID rotation via from_prior");

        // Distinctive outcomes per section.
        transcript.Should().Contain("KeyWrap = ECDH-ES+A256KW");                              // E: anoncrypt derivation
        transcript.Should().Contain("KeyWrap = ECDH-1PU+A256KW");                             // F: authcrypt derivation
        transcript.Should().Contain("Stack = Encrypted ⊃ Signed ⊃ Plaintext");                // G: composition order
        transcript.Should().Contain("Outer skid = <null>");                                   // H: skid hidden
        transcript.Should().Contain("anoncrypt XC20P = XC20P");                               // I: explicit cipher honored
        transcript.Should().Contain("A256GCM is forbidden for authcrypt envelopes (FR-ENC-09)"); // I: refusal
        transcript.Should().Contain("Recipients on the wire = 2");                            // J: multi-recipient JWE
        transcript.Should().Contain("RecipientAddressing = Addressed");                       // K: FR-CONSIST-04 surfaced
        transcript.Should().Contain("must include 'hash' when 'links' is present (FR-ATT-03)"); // L: refusal
        transcript.Should().Contain("Reply thid == opening id = True");                       // M: threading
        transcript.Should().Contain("Side-thread pthid == parent id = True");                 // M: pthid
        transcript.Should().Contain("Empty ACK type = https://didcomm.org/empty/1.0/empty");  // M: Message.Empty
        transcript.Should().Contain("IsSafeToSend (ack that asks for an ack) = False");       // M: loop guard
        transcript.Should().Contain("Sub == message.From = True");                            // N: rotation validated
        transcript.Should().Contain("Termination FromPrior.IsTermination = True");            // N: FR-ROT-06 form
    }
}
