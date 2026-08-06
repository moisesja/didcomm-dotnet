using System.IO;
using FluentAssertions;
using Xunit;
using OobProgram = DidComm.Samples.OutOfBand.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>06-OutOfBand</c> sample. Invokes
/// <see cref="OobProgram.RunAsync"/> directly (no process spawn — the short-URL host binds a
/// dynamic loopback port itself) and asserts the invitation URL wire form, both decode paths,
/// and the pthid correlation of the encrypted response.
/// </summary>
public sealed class OutOfBandSmokeTests
{
    [Fact]
    public async Task RunAsync_EncodesDecodesServesAndCorrelates()
    {
        var writer = new StringWriter();

        await OobProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // FR-OOB-02: the inline URL form is padding-free base64url.
        transcript.Should().Contain("_oob=");
        transcript.Should().Contain("_oob is padding-free base64url = True");

        // The second device decoded the same invitation.
        transcript.Should().Contain("Decoded id == original = True");
        transcript.Should().Contain("Decoded from == Alice = True");

        // FR-OOB-04: the short-URL form was served over HTTP with the plaintext media type.
        transcript.Should().Contain("GET status = 200");
        transcript.Should().Contain("Content-Type = application/didcomm-plain+json");
        transcript.Should().Contain("Fetched id == original invitation = True");

        // FR-OOB-03/05: the encrypted response correlated via pthid and carried web_redirect.
        transcript.Should().Contain("pthid == invitation.id = True");
        transcript.Should().Contain("Correlated to a pending invitation = True");
        transcript.Should().Contain("web_redirect = https://alice.example/welcome");
    }
}
