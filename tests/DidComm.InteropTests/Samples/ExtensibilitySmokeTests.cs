using System.IO;
using FluentAssertions;
using Xunit;
using ExtensibilityProgram = DidComm.Samples.Extensibility.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>08-Extensibility</c> sample. Invokes
/// <see cref="ExtensibilityProgram.RunAsync"/> directly (no process spawn, fully offline) and
/// asserts the mock-KMS custody invariants and round trips, the IKeyStore bridge with its
/// kidToAlias mapping, and the custom-transport delivery + refusal.
/// </summary>
public sealed class ExtensibilitySmokeTests
{
    [Fact]
    public async Task RunAsync_ExercisesKmsBridgeAndCustomTransport()
    {
        var writer = new StringWriter();

        await ExtensibilityProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // FR-SEC-01/06: the generic overload registered ONE singleton serving both contracts,
        // lookups are public-only, and the opaque handles sign/derive without exposing keys.
        transcript.Should().Contain("IOpaqueKeyResolver is the SAME instance = True");
        transcript.Should().Contain("FindAsync → D = <null> (private scalar never leaves the KMS)");
        transcript.Should().Contain("FindPresentAsync filters to held kids = True");
        transcript.Should().Contain("ResolveSignerAsync signature bytes = 64");
        transcript.Should().Contain("ResolveKeyAgreementAsync → Crv = X25519");
        transcript.Should().Contain("DeriveAsync shared-secret bytes = 32");
        transcript.Should().Contain("Signing kid resolves no ECDH handle = True");

        // The KMS drives the facade in both directions.
        transcript.Should().Contain("Bob sees Authenticated = True");
        transcript.Should().Contain("Bob sees NonRepudiation = True");
        transcript.Should().Contain("SenderKid is Alice's KMS key = True");
        transcript.Should().Contain("Alice unpacked content = Round trip complete.");

        // FR-SEC-04: the bridge maps DID-URL kids to keystore aliases and stays opaque.
        transcript.Should().Contain("Bridge built with kidToAlias mapping = 2 entries");
        transcript.Should().Contain("Container surfaced the bridge as IOpaqueKeyResolver = True");
        transcript.Should().Contain("Bridge FindAsync → D = <null> (keystore-held)");
        transcript.Should().Contain("FindPresentAsync via kidToAlias = True");

        // FR-TRN-01: the DI-registered custom transport was router-selected by scheme, the
        // payload was a normal envelope, and an unhandled scheme was refused.
        transcript.Should().Contain("Registered scheme = memq");
        transcript.Should().Contain("EndpointUsed = memq://bob-inbox/");
        transcript.Should().Contain("Delivered media type = application/didcomm-encrypted+json");
        transcript.Should().Contain("Bob unpacked content = Delivered over a transport invented in this file.");
        transcript.Should().Contain("https send = refused (TransportException:");
    }
}
