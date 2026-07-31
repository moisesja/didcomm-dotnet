using DidComm.Facade;
using FluentAssertions;
using Xunit;
using QuickstartProgram = DidComm.Samples.Quickstart.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02/05 build+run gate for the <c>01-Quickstart</c> sample — the program the repository
/// README tells a new developer to copy. It runs here on every CI build, so the README's claim that
/// the quickstart works unmodified is enforced rather than asserted.
/// <para>
/// Asserts on <see cref="QuickstartProgram.RunAsync"/>'s returned <see cref="UnpackResult"/> rather
/// than on captured console text: redirecting <c>Console.Out</c> would be a process-wide side effect
/// under xUnit's parallel execution, and the printed lines are what the reader copies — they should
/// stay ordinary <c>Console.WriteLine</c> calls.
/// </para>
/// </summary>
public sealed class QuickstartSmokeTests
{
    [Fact]
    public async Task RunAsync_CompletesAnAuthcryptRoundTripWithTheAdvertisedMetadata()
    {
        var received = await QuickstartProgram.RunAsync();

        received.Message.Body!["text"]!.GetValue<string>().Should().Be("Hello, Bob.");
        received.Encrypted.Should().BeTrue();
        received.Authenticated.Should().BeTrue("the quickstart packs authcrypt, which proves the sender's key");
        received.SenderKid.Should().NotBeNull().And.Subject.As<string>().Should().StartWith("did:peer:2.");
        received.RecipientAddressing.Should().Be(RecipientAddressing.Addressed,
            "the message names Bob in 'to' and Bob is the decrypting recipient (FR-CONSIST-04)");
    }
}
