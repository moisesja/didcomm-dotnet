using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DidComm.Samples.MediatedAgent.Wallet;
using Xunit;
using MediatedAgentProgram = DidComm.Samples.MediatedAgent.Program;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>11-MediatedAgent</c> sample. Invokes
/// <see cref="MediatedAgentProgram.RunAsync"/> directly (the sample hosts its mediator on a free
/// loopback port itself) and pins each section's outcome: the mediator starts and publishes its
/// invitation, Bob enrolls and registers his DIDs, Alice's message is routed through the mediator,
/// Bob picks it up over HTTP and a second one live over a WebSocket, Carol is refused by the
/// allow-list, and the operator's console changes what the mediator holds. Also pins the wallet
/// project's package boundary: a wallet references the client package, never the mediator side.
/// </summary>
public sealed class MediatedAgentSmokeTests
{
    [Fact]
    public async Task RunAsync_EnrollsSendsPicksUpLiveDeniesAndOperates()
    {
        var writer = new StringWriter();

        await MediatedAgentProgram.RunAsync(writer);

        var transcript = writer.ToString();

        // The sections run in order.
        var banners = new[]
        {
            "== Section 1 — Start the mediator",
            "== Section 2 — Bob enrolls",
            "== Section 3 — Alice sends",
            "== Section 4 — Bob picks up over HTTP",
            "== Section 5 — Bob goes live over a WebSocket",
            "== Section 6 — Carol is refused",
            "== Section 7 — The operator's console",
        };
        banners.Select(b => transcript.IndexOf(b, StringComparison.Ordinal)).Should().BeInAscendingOrder().And.NotContain(-1);

        // 1. The settings are checked (two invalid ones refused, by name), the mediator listens on
        //    loopback under a did:peer:2 that names its address, and publishes its invitation URL.
        transcript.Should().Contain("Settings checked before deploying: OK");
        transcript.Should().Contain("Invalid settings refused: Pickup.MaxMessageAge must be longer than MaxForwardDelay");
        transcript.Should().Contain("Invalid settings refused: ReturnRouteSessionOptions.MaxSessionsPerPeer must not exceed MaxSessions.");
        transcript.Should().Contain("[mediator] listening on http://127.0.0.1:");
        transcript.Should().Contain("[mediator] routing DID = did:peer:2.");
        transcript.Should().Contain("[mediator] primary routing DID is the one minted = True");
        transcript.Should().Contain("[mediator] mediate-request is one of them = True");
        transcript.Should().Contain("[mediator] forward is exempt = True");
        transcript.Should().MatchRegex(@"\[mediator\] invitation URL = http://127\.0\.0\.1:\d+/didcomm/oob\?_oobid=mediator");

        // 2. Bob reads the invitation, is granted, registers two DIDs and lists them.
        transcript.Should().Contain("[bob] invitation from did:peer:2.");
        transcript.Should().Contain("[bob] goal code = request-mediate");
        transcript.Should().Contain("[bob] granted: routing DIDs = 1");
        transcript.Should().Contain("[bob] first routing DID is the mediator's = True");
        transcript.Should().Contain("[bob] registered his public DID: Success");
        transcript.Should().Contain("[bob] registered a second DID, for a shop: Success");
        transcript.Should().Contain("[bob] pagination = count 1, offset 0, remaining 1");
        transcript.Should().Contain("[bob] all registered DIDs: 2");

        // 3. Alice's route is the mediator's DID (FR-ROUTE-04); the mediator accepted and stored it.
        transcript.Should().Contain("Bob's service uri (the mediator's DID) = did:peer:2.");
        transcript.Should().Contain("It is the mediator's DID = True");
        transcript.Should().Contain("Transport HTTP status = 202");
        transcript.Should().Contain("[mediator] stored Alice's message \"Hello Bob\" for pickup");

        // 4. HTTP pickup: one waiting, fetched and opened (authenticated), none left.
        transcript.Should().Contain("[bob] status: 1 message(s)");
        transcript.Should().Contain("[bob] status is about the DID asked for = True");
        transcript.Should().Contain("[bob] fetched: Hello Bob");
        transcript.Should().Contain("[bob] sender authenticated = True");
        transcript.Should().Contain("[bob] status after fetch: 0 message(s)");

        // 5. Live over the WebSocket: the switch, the message received, the last one acknowledged,
        //    the operator finds Bob's session, and it closes when Bob goes offline.
        transcript.Should().Contain("[bob] live delivery = True");
        transcript.Should().Contain("[bob] live: Hello again, Bob");
        transcript.Should().Contain("[bob] acknowledged the last message; status after ack: 0 message(s)");
        transcript.Should().Contain("[mediator] sessions for Bob: 1");
        transcript.Should().Contain("[mediator] session transport = WebSocket");
        transcript.Should().Contain("[mediator] Bob's session closed (its Closed token fired)");

        // 6. The allow-list refuses Carol with mediate-deny, which the client reports as null.
        transcript.Should().Contain("[carol] mediate-deny: RequestMediationAsync returned null");
        transcript.Should().Contain("[carol] mediate-deny: not on the allow-list");

        // 7. The operator's console.
        transcript.Should().Contain("[operator] Bob's registered DIDs = 2 of 2");
        transcript.Should().Contain("[operator] Remove Bob's shop DID: Removed");
        transcript.Should().Contain("[operator] Purged recipient: 1 message(s)");
        transcript.Should().Contain("[operator] queued a notice for Bob: Queued");
        transcript.Should().Contain("[operator] Removed by operator: True");
        transcript.Should().Contain("[operator] pre-provisioned a grant for Carol: True");
        transcript.Should().Contain("[carol] granted: routing DIDs = 1");
        transcript.Should().Contain("[operator] Carol's grant revoked: True");
        transcript.Should().Contain("[operator] Bob's mediation ended: Revoked = True, Purged = 1");
        transcript.Should().Contain("[operator] grants left = 0");

        // Consumers copy samples: no whole DID is ever printed (they are cut to 48 characters).
        Regex.IsMatch(transcript, @"did:peer:2\.[A-Za-z0-9._~-]{60,}").Should().BeFalse("the sample prints DIDs truncated");
    }

    [Fact]
    public void Wallet_references_the_client_package_and_never_the_mediator_side()
    {
        var references = typeof(RecipientWallet).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        // Positive control: the wallet does use the client package.
        references.Should().Contain("DidComm.Mediator.Client");
        references.Should().NotContain("DidComm.Mediator");
        references.Should().NotContain("DidComm.Mediator.AspNetCore");
        references.Should().NotContain(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
