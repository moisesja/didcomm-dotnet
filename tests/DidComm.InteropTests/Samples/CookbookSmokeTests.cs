using System.IO;
using DidComm.Samples.Cookbook;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02/04 build+run gate for the <c>02-Cookbook</c> sample. Invokes
/// <see cref="Program.RunAsync"/> directly (no process spawn), capturing console output, and
/// asserts every §14.2 task letter (A–HH) produced its section banner — the cookbook is the
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
        transcript.Should().Contain("Section R — Receive over WebSocket (MapDidCommWebSocket + text frames)");
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
        transcript.Should().Contain("Section GG — Send through a proxy you trust");
        transcript.Should().Contain("Section HH — Limit how long a WebSocket message may take");

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
        transcript.Should().Contain("[mediator] Alice's note, not encrypted = NotEncrypted, id none"); // DD: the mailbox stores only encrypted messages
        transcript.Should().Contain("[mediator] an envelope Bob cannot open = Queued");    // DD: encrypted for someone else is still stored
        transcript.Should().Contain("[bob] status = 2 message(s)");                         // DD: status
        transcript.Should().Contain("[bob] opened = \"Hello Bob, from your mailbox.\" (authenticated sender: True)"); // DD: fetch
        transcript.Should().Contain("[bob] could not open = ");                             // DD: failure reported
        transcript.Should().Contain("[bob] discarded the unopenable one; the mediator's answer = none"); // DD: AckAsync discards; under 4.0, which the wallet negotiated, nothing answers it
        transcript.Should().Contain("EnqueueAsync m1 again = DuplicateId");                 // DD.3: store contract
        transcript.Should().Contain("Store bounded at 2 per grant: EnqueueAsync ×3 = Enqueued, Enqueued, GrantMessageLimitReached"); // DD.3
        transcript.Should().Contain("connection-2 closed unacknowledged; the next live push takes = b2 (#2)"); // DD.3: a returned live push is not pushed again
        transcript.Should().Contain("A delivery-request takes = b1 (came back from a live push: True)");      // DD.3: only a delivery-request gets it
        transcript.Should().Contain("ReturnUnsentAsync(b2, connection-3's lease) = 1");                         // DD.3: a push that was not sent is given back
        transcript.Should().Contain("The next live push takes = b2 (#2)");                                     // DD.3: and may be pushed live again
        transcript.Should().Contain("[bob] fetch again = drained True, status carried False");     // DD: the wallet negotiated 4.0, whose drained fetch has no status
        transcript.Should().Contain("[bob] status after the drain (GetStatusAsync) = 0 message(s)"); // DD: so it asks
        transcript.Should().MatchRegex(@"\[bob, 4\.0\] status = 1 message\(s\), oldest arrived at \d+ s, longest waited not reported"); // DD: 4.0 has no longest_waited_seconds
        transcript.Should().MatchRegex(@"\[bob, 3\.0\] status = 1 message\(s\), oldest arrived at \d+ s, longest waited reported");    // DD: a client pinned to 3.0
        transcript.Should().Contain("[bob] both versions give the same arrival time, in whole seconds = True"); // DD: 4.0's milliseconds, rounded down
        transcript.Should().Contain("[bob, 3.0] drained fetch = drained True, status 0");        // DD: 3.0 drains with a status
        transcript.Should().Contain("[bob, 4.0] drained fetch = drained True, status none");     // DD: 4.0 with an empty delivery
        transcript.Should().Contain("[bob, 4.0] GetStatusAsync after the drain = 0 message(s)"); // DD
        transcript.Should().Contain("memq:// is one-way and cannot carry back the answer this send asked for"); // Z: return route refused
        transcript.Should().Contain("[mediator] TrySendAsync(notice, 256 bytes) = TooLarge"); // EE: bounded session send
        transcript.Should().Contain("[mediator] TrySendAsync(notice, 64 KiB) = Sent");      // EE
        transcript.Should().Contain("[bob] Frame 1 authcrypted by the mediator = True");    // EE: frame consumer
        transcript.Should().Contain("[bob] Frame 2 authcrypted by the mediator = True");    // EE: frame consumer
        transcript.Should().NotContain("skipped a frame");                                  // EE: every frame came from the peer
        transcript.Should().Contain("Session closed (its Closed token fired) = True");      // EE: Closed token
        transcript.Should().Contain("[bob] live = Hello again");                            // EE: live pickup
        transcript.Should().Contain("[bob] discarded the last one; the mediator's answer = none"); // EE: AckAsync(last); the stream spoke 4.0, so nothing answers it
        transcript.Should().Contain("[bob] granted (on the allow-list) = True");            // FF: allow-list grants
        transcript.Should().Contain("[carol] granted = False");                             // FF: allow-list denies
        transcript.Should().Contain("AddMediator() with a host IForwardSink = refused");    // FF: refusal shown
        transcript.Should().Contain("[mediator] Message Pickup versions served = https://didcomm.org/message-pickup/4.0, https://didcomm.org/messagepickup/3.0"); // FF.1: EnableV4 on
        transcript.Should().Contain("With Pickup.EnableV4 = false, Message Pickup versions served = https://didcomm.org/messagepickup/3.0"); // FF.1: EnableV4 off
        transcript.Should().Contain("MessagePickup40Handler = https://didcomm.org/message-pickup/4.0, roles: mediator"); // FF.2 (b): built by hand
        transcript.Should().Contain("[bob] 4.0 status-request answered with = https://didcomm.org/message-pickup/4.0/status"); // FF.2 (b): answered through the hand-built path
        transcript.Should().Contain("[bob] 4.0 status: messages waiting / on the request's thread = 0 / True"); // FF.2 (b)
        transcript.Should().Contain("[sink] MessagePickupQueue.DeliverAsync = Queued");      // FF.2: à la carte
        transcript.Should().Contain("[bob] mediate-request answered with a grant = True");  // FF.2: no container
        transcript.Should().Contain("[mediator] the same status-request again = Duplicate"); // FF.2 (b): own receive path runs replay admission
        transcript.Should().Contain("[mediator] the replay reached a handler = False");     // FF.2 (b): refused before dispatch
        transcript.Should().Contain("msg-1, again = Duplicate");                            // FF.3: replay guard
        transcript.Should().Contain("SaturationCount / ClockBehindRefusalCount = 1 / 1");   // FF.3
        transcript.Should().Contain("The proxy received = POST http://203.0.113.10/didcomm HTTP/1.1"); // GG: HTTP through the named proxy
        transcript.Should().Contain("The proxy received = CONNECT 198.51.100.7:80 HTTP/1.1");  // GG: WebSocket tunnelled through it
        transcript.Should().Contain("Refused = by the library, before the proxy was asked");   // GG: checks that need no socket
        transcript.Should().Contain("Refused = by the proxy");                                 // GG: past the proxy, its rules decide
        transcript.Should().Contain("TimeSpan.Zero = refused (ArgumentOutOfRangeException)"); // HH: the setter's rule
        transcript.Should().Contain("CloseStatus = 1008 (PolicyViolation)");                   // HH: silent and unfinished closed
        transcript.Should().Contain("Bob received = Section HH: the next message, after an idle wait longer than the limit."); // HH: idle is untimed
        transcript.Should().Contain("The unfinished message reached Bob = False");            // HH: the partial message is dropped
    }
}
