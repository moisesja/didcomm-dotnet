using System.IO;
using FluentAssertions;
using Xunit;
using NetDidProgram = DidComm.Samples.NetDidIntegration.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>09-NetDidIntegration</c> sample. Invokes
/// <see cref="NetDidProgram.RunAsync"/> directly (no process spawn, fully offline) and asserts
/// the did:key derivation, the did:peer numalgo-0 mint, the cross-method round trip, the
/// injected-clock expiry behavior, and the did:web refusal at every entry point.
/// </summary>
public sealed class NetDidIntegrationSmokeTests
{
    [Fact]
    public async Task RunAsync_MintsResolvesMessagesAndRefusesDidWeb()
    {
        var writer = new StringWriter();

        await NetDidProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // FR-DID-05: the Ed25519 did:key derived a real X25519 keyAgreement key.
        transcript.Should().Contain("did:key = did:key:z6Mk");
        transcript.Should().Contain("keyAgreement crv is X25519 = True");
        transcript.Should().Contain("Locally-derived X25519 matches the DID document = True");

        // did:peer numalgo 0 minted, resolved, and used for a verified signature.
        transcript.Should().Contain("Prefix is did:peer:0 = True");
        transcript.Should().Contain("Signed envelope NonRepudiation = True");
        transcript.Should().Contain("SignerKid belongs to did:peer:0 = True");

        // FR-DID-01: methods interoperate — did:key sender, did:peer:2 recipient.
        transcript.Should().Contain("Authenticated (authcrypt) = True");
        transcript.Should().Contain("SenderKid is a did:key kid = True");
        transcript.Should().Contain("RecipientKid is a did:peer kid = True");
        transcript.Should().Contain("Content = Hello across the method boundary.");

        // FR-API-05: expiry follows the injected clock and honors ExpiresClockSkew.
        transcript.Should().Contain("Clock at base+1min = accepted");
        transcript.Should().Contain("Clock at base+1h, no skew = rejected (MalformedMessageException — message expired)");
        transcript.Should().Contain("Clock at base+1h, skew 2h = accepted");

        // FR-DID-06 / DD-08: did:web refused everywhere, with Method='web' on the exception.
        transcript.Should().Contain("Resolution (GetVerificationMethodsAsync) = refused (Method='web', Did='did:web:example.com')");
        transcript.Should().Contain("Pack — did:web recipient = refused (Method='web'");
        transcript.Should().Contain("Pack — did:web sender = refused (Method='web'");
        transcript.Should().Contain("Pack — did:web signer = refused (Method='web'");
        transcript.Should().Contain("SendAsync — did:web recipient = refused (Method='web'");
        transcript.Should().Contain("Unpack — plaintext from did:web = refused (Method='web'");
    }
}
