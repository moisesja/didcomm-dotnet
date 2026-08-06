using System.IO;
using FluentAssertions;
using Xunit;
using MediatorProgram = DidComm.Samples.MediatorAgent.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>04-MediatorAgent</c> sample. Invokes
/// <see cref="MediatorProgram.RunAsync"/> directly (no process spawn — the sample hosts its
/// two ASP.NET Core apps on dynamic loopback ports itself) and asserts the full
/// Alice → Mediator → Bob path: SSRF refusal by default, forward receipt, relay, and
/// authenticated delivery.
/// </summary>
public sealed class MediatorAgentSmokeTests
{
    [Fact]
    public async Task RunAsync_RoutesAliceToBobThroughTheMediator()
    {
        var writer = new StringWriter();

        await MediatorProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // The SSRF guard refused the loopback endpoint under the default policy (task O setup).
        transcript.Should().Contain("Default policy refused, as designed:");
        transcript.Should().Contain("This is blocked by default to prevent SSRF");

        // The mediator received a Routing 2.0 forward and relayed the onward payload (task Q).
        transcript.Should().Contain("[mediator] received an envelope (type = https://didcomm.org/routing/2.0/forward)");
        transcript.Should().Contain("[mediator] forward unwrapped — next hop did:peer:2.");
        transcript.Should().Contain("(HTTP 202)");

        // Alice's SendAsync used the mediator endpoint resolved from Bob's DID (tasks O/P).
        transcript.Should().Contain("Transport HTTP status = 202");

        // Bob got the original message, authenticated, addressed to him.
        transcript.Should().Contain("[bob] Content = Routed through the mediator.");
        transcript.Should().Contain("[bob] Authenticated = True");
        transcript.Should().Contain("[bob] From == alice = True");
        transcript.Should().Contain("[bob] RecipientAddressing = Addressed");
    }
}
