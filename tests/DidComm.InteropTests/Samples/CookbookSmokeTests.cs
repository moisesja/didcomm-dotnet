using System.IO;
using DidComm.Samples.Cookbook;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02/04 build+run gate for the <c>02-Cookbook</c> sample. Invokes
/// <see cref="Program.RunAsync"/> directly (no process spawn), capturing console output, and
/// asserts every §14.2 task letter (A–FF) produced its section banner — the cookbook is the
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
        transcript.Should().Contain("Section CC — Enroll with a mediator");
        transcript.Should().Contain("Section DD — Pick up queued messages");
        transcript.Should().Contain("Section EE — Live delivery over WebSocket");
        transcript.Should().Contain("Section FF — Run a minimal mediator in 20 lines");

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

        // Phase 7 mediator sections: outcomes, not just banners.
        transcript.Should().Contain("[bob] granted: routing DIDs = 1");                     // CC: enrolled
        transcript.Should().Contain("[bob] Add public DID = Success");                      // CC: DID registered
        transcript.Should().Contain("[bob] the same add again = NoChange");                 // CC: retry is safe
        transcript.Should().Contain("[alice] add Bob's DID = ClientError");                 // CC: first registrant wins
        transcript.Should().Contain("Refused = MediationNotSupportedException");            // CC: wrong peer
        transcript.Should().Contain("IsQueueTransport(Uri) = True");                        // CC: queue URI
        transcript.Should().Contain("mediate-deny → TryParseDeny = True (and TryParseGrant: False)"); // CC: wire table
        transcript.Should().Contain("[mediator] a DID nobody registered = NotRegistered");   // DD: DeliverAsync refuses
        transcript.Should().Contain("[bob] status = 2 message(s)");                         // DD: status
        transcript.Should().Contain("[bob] opened = \"Hello Bob, from your mailbox.\" (authenticated sender: True)"); // DD: fetch
        transcript.Should().Contain("[bob] could not open = ");                             // DD: failure reported
        transcript.Should().Contain("[bob] discarded the unopenable one; status = 0 message(s)"); // DD: AckAsync discards
        transcript.Should().Contain("EnqueueAsync m1 again = DuplicateId");                 // DD.3: store contract
        transcript.Should().Contain("Store bounded at 2 per grant: EnqueueAsync ×3 = Enqueued, Enqueued, GrantMessageLimitReached"); // DD.3
        transcript.Should().Contain("memq:// is one-way and cannot carry back the answer this send asked for"); // Z: return route refused
        transcript.Should().Contain("[mediator] TrySendAsync(notice, 256 bytes) = TooLarge"); // EE: bounded session send
        transcript.Should().Contain("[mediator] TrySendAsync(notice, 64 KiB) = Sent");      // EE
        transcript.Should().Contain("[bob] Frame 2 authcrypted by the mediator = True");    // EE: frame consumer
        transcript.Should().Contain("Session closed (its Closed token fired) = True");      // EE: Closed token
        transcript.Should().Contain("[bob] live = Hello again");                            // EE: live pickup
        transcript.Should().Contain("[bob] Messages left after discarding the last one = 0"); // EE: AckAsync(last)
        transcript.Should().Contain("[bob] granted (on the allow-list) = True");            // FF: allow-list grants
        transcript.Should().Contain("[carol] granted = False");                             // FF: allow-list denies
        transcript.Should().Contain("AddMediator() with a host IForwardSink = refused");    // FF: refusal shown
        transcript.Should().Contain("[sink] MessagePickupQueue.DeliverAsync = Queued");      // FF.2: à la carte
        transcript.Should().Contain("[bob] mediate-request answered with a grant = True");  // FF.2: no container
        transcript.Should().Contain("msg-1, again = Duplicate");                            // FF.3: replay guard
        transcript.Should().Contain("SaturationCount / ClockBehindRefusalCount = 1 / 1");   // FF.3
    }
}
