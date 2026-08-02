using System.IO;
using FluentAssertions;
using Xunit;
using ChatProgram = DidComm.Samples.WebSocketChat.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>05-WebSocketChat</c> sample. Invokes
/// <see cref="ChatProgram.RunAsync"/> directly (no process spawn — the sample hosts both
/// agents on dynamic loopback ports itself) and asserts trust-ping liveness, the
/// discover-features handshake, the scripted chat, and the reconnect-after-drop outcome.
/// </summary>
public sealed class WebSocketChatSmokeTests
{
    [Fact]
    public async Task RunAsync_ChatsPingsDiscoversAndReconnects()
    {
        var writer = new StringWriter();

        await ChatProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // Trust ping (FR-PROTO-04): Bob auto-replied, threaded and authenticated.
        transcript.Should().Contain("ping-response thid == ping.id = True");
        transcript.Should().Contain("ping-response authenticated = True");

        // Discover features (FR-PROTO-05): the disclose completed the round trip and the
        // custom chat handler shows up in Bob's registry reflection.
        transcript.Should().Contain("- protocol = https://didcomm.org/trust-ping/2.0");
        transcript.Should().Contain("- protocol = https://didcomm.org/basicmessage/2.0");

        // The scripted chat ran both directions.
        transcript.Should().Contain("[bob] received: \"Hello Bob — one envelope per WebSocket message.\"");
        transcript.Should().Contain("[alice] received: \"Loud and clear, Alice.\"");
        transcript.Should().Contain("[alice] received: \"Ship it.\"");

        // Reconnect after drop (FR-TRN-11): lifecycle events fired, the offline send was
        // refused after the backoff budget, and the conversation resumed post-restart.
        transcript.Should().Contain("[alice transport] Connected");
        transcript.Should().Contain("[alice transport] SendFailed");
        transcript.Should().Contain("offline send refused after exhausting the reconnect budget");
        transcript.Should().Contain("failed after exhausting the reconnect budget");
        transcript.Should().Contain("[alice] received: \"Back online — nothing lost but time.\"");
    }
}
