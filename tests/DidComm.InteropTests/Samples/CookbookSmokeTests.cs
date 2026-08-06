using System.IO;
using DidComm.Samples.Cookbook;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02/04 build+run gate for the <c>02-Cookbook</c> sample. Invokes
/// <see cref="Program.RunAsync"/> directly (no process spawn), capturing console output, and
/// asserts every §14.2 task letter (A–BB) produced its section banner — the cookbook is the
/// backbone of API coverage, so a section silently dropping out must fail CI.
/// </summary>
public sealed class CookbookSmokeTests
{
    [Fact]
    public async Task RunAsync_PrintsEverySectionBanner()
    {
        var writer = new StringWriter();

        await Program.RunAsync(writer);

        var transcript = writer.ToString();

        // One banner per §14.2 task letter, in the order Program.RunAsync registers them.
        transcript.Should().Contain("Section A — Dependency-injection setup");
        transcript.Should().Contain("Section B — Build a message");
        transcript.Should().Contain("Section C — Pack plaintext (debug/inspection only)");
        transcript.Should().Contain("Section D — Pack signed (non-repudiable, no confidentiality)");
        transcript.Should().Contain("Section E — Pack anoncrypt (confidential, anonymous sender)");
        transcript.Should().Contain("Section F — Pack authcrypt (confidential + sender authenticated — the default)");
        transcript.Should().Contain("Section G — Sign-then-encrypt (add non-repudiation)");
        transcript.Should().Contain("Section H — Protect the sender (anoncrypt wraps authcrypt)");
        transcript.Should().Contain("Section I — Choose content encryption explicitly");
        transcript.Should().Contain("Section J — Multi-recipient (one envelope, several readers)");
        transcript.Should().Contain("Section K — Unpack and inspect metadata");
        transcript.Should().Contain("Section L — Attachments (inline json / base64 / linked-with-hash)");
        transcript.Should().Contain("Section M — Threading & ACKs");
        transcript.Should().Contain("Section N — DID rotation via from_prior");
        transcript.Should().Contain("Section O — Routing via a mediator (automatic forward wrapping)");
        transcript.Should().Contain("Section P — Send over a transport (HTTP chosen by endpoint scheme)");
        transcript.Should().Contain("Section Q — Receive over HTTP (ASP.NET Core MapDidCommEndpoint)");
        transcript.Should().Contain("Section R — Receive over WebSocket (MapDidCommWebSocket + binary frames)");
        transcript.Should().Contain("Section S — Trust Ping (liveness)");
        transcript.Should().Contain("Section T — Discover Features (initiator: ask, then await the answer)");
        transcript.Should().Contain("Section U — Report Problem (taxonomy, interpolation, escalation, cascade)");
        transcript.Should().Contain("Section V — Out-of-Band invitation (build + URL encode/decode)");
        transcript.Should().Contain("Section W — Empty 1.0 (header-only ACK)");
        transcript.Should().Contain("Section X — Custom IProtocolHandler (lets_do_lunch)");
        transcript.Should().Contain("Section Y — Custom ISecretsResolver (mock KMS) & the net-did IKeyStore bridge");
        transcript.Should().Contain("Section Z — Custom transport (IDidCommTransport + TransportRouter)");
        transcript.Should().Contain("Section AA — net-did integration & the did:web refusal");
        transcript.Should().Contain("Section BB — Profiles & i18n");

        // Distinctive per-section outcomes, so a section that banners but silently degrades
        // still fails the gate.
        transcript.Should().Contain("Round-trip Authenticated = True");                     // A: DI-resolved client works
        transcript.Should().Contain("Typ (auto-populated) = application/didcomm-plain+json"); // B: FR-MSG-13
        transcript.Should().Contain("KeyWrap = ECDH-ES+A256KW");                            // E: anoncrypt derivation
        transcript.Should().Contain("KeyWrap = ECDH-1PU+A256KW");                           // F: authcrypt derivation
        transcript.Should().Contain("Stack = Encrypted ⊃ Signed ⊃ Plaintext");              // G: sign-then-encrypt
        transcript.Should().Contain("Outer skid = <null>");                                 // H: skid hidden from mediators
        transcript.Should().Contain("A256GCM is forbidden for authcrypt envelopes (FR-ENC-09)"); // I: rejection shown
        transcript.Should().Contain("Recipients on the wire = 2");                          // J: multi-recipient JWE
        transcript.Should().Contain("Attachment 'data' must include 'hash' when 'links' is present (FR-ATT-03)"); // L
        transcript.Should().Contain("FromPrior.Sub");                                       // N: rotation validated
        transcript.Should().Contain("D = <null> (private scalar never leaves the store)");  // Y: opaque custody
        transcript.Should().Contain("Keystore round-trip NonRepudiation = True");           // Y: opaque signing worked
        transcript.Should().Contain("No registered IDidCommTransport handles scheme 'https'"); // Z: router dispatch
        transcript.Should().Contain("refused (web)");                                       // AA: did:web rejection
    }
}
