using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;
using MediatorProgram = DidComm.Samples.MediatorAgent.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>04-MediatorAgent</c> sample. Invokes
/// <see cref="MediatorProgram.RunAsync"/> directly (no process spawn — the sample hosts its
/// two ASP.NET Core apps on dynamic loopback ports itself) and asserts the full
/// Alice → Mediator → Bob path through the library's routing role (<c>AddRoutingMediator()</c>)
/// and the host's own <c>IForwardSink</c>: the SSRF refusal by default (facade and transport), the
/// mediator's Discover Features answer returned on the request, the 202 before the relay, the
/// relay with <c>SendPackedAsync</c>, Bob's authenticated delivery, Carol dropped by
/// <c>NullForwardSink</c>, the forward for an unregistered DID dropped by the handler, and the
/// same relay composed without a DI container.
/// </summary>
public sealed class MediatorAgentSmokeTests
{
    [Fact]
    public async Task RunAsync_RoutesAliceToBobThroughTheRoutingMediator()
    {
        var writer = new StringWriter();

        await MediatorProgram.RunAsync(writer);

        var transcript = writer.ToString();
        transcript.Should().NotContain("UNEXPECTED");

        // The routing role is registered and declares its role; its options are validated.
        transcript.Should().Contain("Role declared for https://didcomm.org/routing/2.0 = mediator");
        transcript.Should().Contain("A negative MaxDelay is refused by Validate(), as designed.");
        transcript.Should().Contain("[mediator] registered recipients = 2");
        transcript.Should().Contain("[mediator] relays for Bob = True");
        transcript.Should().Contain("[mediator] Bob's inbox added to the relay's directory = True");

        // The SSRF guard refused the loopback endpoint under the default policy, in the facade and
        // again in the HTTP transport's connect-time check.
        transcript.Should().Contain("Default policy refused, as designed:");
        transcript.Should().Contain("This is blocked by default to prevent SSRF");
        transcript.Should().Contain("The transport's own connection check refused it too:");
        transcript.Should().Contain("Refusing to connect to '127.0.0.1:");

        // Discover Features with return_route all: the disclose came back on the same request.
        transcript.Should().Contain("return_route = All");
        transcript.Should().Contain("Answer arrived as HTTP = 200");
        transcript.Should().Contain("Answer type = https://didcomm.org/discover-features/2.0/disclose");
        transcript.Should().Contain("Answer authenticated = True");
        transcript.Should().Contain("Answer threads to the question = True");
        transcript.Should().Contain("Disclosed https://didcomm.org/routing/2.0 roles = mediator");

        // The 202 before the relay, then the relay to Bob's inbox with SendPackedAsync.
        transcript.Should().Contain("Transport HTTP status = 202");
        transcript.Should().Contain("[mediator] the 202 was on the wire before the sink ran = True");
        transcript.Should().Contain("[mediator] relayed to Bob's inbox with SendPackedAsync (HTTP 202).");
        transcript.Should().Contain("[mediator] relay response discarded = False");
        transcript.Should().Contain("[mediator] relay response body = none (0 bytes): a relay never reads one");

        // Bob got the original message, authenticated, addressed to him, with nothing to answer on.
        transcript.Should().Contain("[bob] Content = Routed through the mediator.");
        transcript.Should().Contain("[bob] Authenticated = True");
        transcript.Should().Contain("[bob] From == alice = True");
        transcript.Should().Contain("[bob] RecipientAddressing = Addressed");
        transcript.Should().Contain("[bob] return-route session = none");

        // Carol: registered without an inbox → NullForwardSink; unregistered → dropped by the handler.
        transcript.Should().Contain("outcome = NoInbox (handed to the library's NullForwardSink");
        transcript.Should().Contain("[mediator] Carol unregistered = True");
        transcript.Should().Contain("[mediator] relays for Carol = False");
        transcript.Should().Contain("Transport HTTP status for the unregistered DID = 202");
        transcript.Should().Contain("didcomm.forward.outcome = Dropped-Unknown");

        // The same relay without a DI container: both dispatcher overloads, the held acknowledgement.
        transcript.Should().Contain("== Section 8 — The same relay without a DI container ==");
        transcript.Should().Contain("[mediator] verified forward — next=did:peer:2.");
        transcript.Should().Contain("[bob] Content = Relayed by the hand-built mediator.");
        transcript.Should().Contain("[mediator] relayed before the acknowledgement = False");
        transcript.Should().Contain("[mediator] our transport acknowledged before the sink ran = True");
        transcript.Should().Contain("[bob] Content = Held until the acknowledgement.");
        transcript.Should().Contain("Hand-built handler stopped");
        transcript.Should().Contain("[mediator] registered recipients = 0");

        // Never a full DID in the narration: every did:peer is truncated to at most 64 characters.
        foreach (Match did in Regex.Matches(transcript, @"did:peer:2\.[A-Za-z0-9._:#\-]+"))
            did.Value.Length.Should().BeLessThanOrEqualTo(64, "the sample truncates DIDs it prints");
    }
}
