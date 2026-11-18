# 02-Cookbook

A runnable, narrated tour of the DIDComm v2.1 library. Each section is a small program that demonstrates one capability end-to-end: it bootstraps two test identities, performs the operation, and prints what happened. Read the source alongside the output and you should be able to copy the patterns into your own application.

The cookbook is complete: one section per API task letter in the project PRD's §14.2 master list, A through GG. Sections CC–FF cover the mediator service: a wallet enrolling with a mediator, picking up its mail over HTTP and live over WebSocket, and running a mediator yourself. Section GG sends through a proxy you name.

## Sections

| Section | What it demonstrates |
|---|---|
| **A** — Dependency-injection setup | The one-time startup wiring: `AddDidComm(b => ...)` with a net-did resolver, your secrets resolver, HTTP + WebSocket transports, protocol handlers, and option tweaks — then one `DidCommClient` resolved from the container and used for a round-trip. |
| **B** — Build a message | The fluent `MessageBuilder`: only `type` is required, `id` and `typ` are auto-populated. Custom extension headers, `thid` threading, `created_time`, and the full JSON printed so you see exactly what goes on the wire. |
| **C** — Pack plaintext | The bare JWM form — no encryption, no signature. For debugging and inspection only; the round-trip shows all three security flags coming back `false`. |
| **D** — Pack signed | A standalone JWS envelope: anyone can read it, everyone can prove the sender wrote it (non-repudiation). Includes `to` on purpose — a signed message without it invites surreptitious forwarding, and the client warns when it's missing. |
| **E** — Pack anoncrypt | Confidential with an anonymous sender: omit `From` and the library selects the anonymous key agreement (ECDH-ES). Bob can read it but learns nothing about who sent it. |
| **F** — Pack authcrypt | The default posture: one layer gives confidentiality AND sender authentication (ECDH-1PU) — deniable, not provable to third parties. |
| **G** — Sign-then-encrypt | `SignFrom` inside the encrypted-pack options adds a signature *inside* the encryption: secret, authenticated, and provable at once. `NonRepudiation` comes back `true`. |
| **H** — Protect the sender | `ProtectSender = true` wraps authcrypt in an outer anoncrypt so the sender's key id (`skid`) is hidden from mediators and observers. The section decodes both outer JOSE headers so you can see the `skid` disappear from the wire. |
| **I** — Choose content encryption | The `Enc` option swept across its values, and the guard rail shown live: authcrypt + GCM is refused at pack time with an error naming the rule (FR-ENC-09). |
| **J** — Multi-recipient | One envelope encrypted to Bob AND Carol: the body is encrypted once, the key wrapped once per recipient key (every keyAgreement key each reader publishes on the chosen curve). Counts the `recipients` entries on the wire and prints every `AllRecipientKids` entry on unpack. |
| **K** — Unpack and inspect metadata | After unpacking a packed message, what does the library tell you about it? Encrypted? Signed? Who sent it? Which key decrypted? Every field of `UnpackResult` is printed against a maximally-protective envelope so you can see how each flag corresponds to a layer. |
| **L** — Attachments | The three attachment shapes — inline JSON, inline base64, and linked-with-hash — round-tripped through an encrypted envelope, plus the refusal of a link with no integrity hash. |
| **M** — Threading & ACKs | Continuing a thread with `thid`, requesting receipt with `please_ack`, and answering with `ack`. |
| **N** — DID rotation via `from_prior` | How Alice changes the DID she identifies as without breaking Bob's trust: she signs a tiny `from_prior` JWT with a key her old DID had advertised, ships it inside her first message under the new DID, and Bob's unpack validates it automatically. Also shows the safety rule — rotation messages cannot be sent in the clear. |
| **O** — Routing via a mediator | When a recipient publishes a `DIDCommMessaging` service with `routingKeys`, setting `Forward = true` on the pack call makes the library automatically: resolve the route, reverse-order anoncrypt-wrap a `forward` per routing key, and surface the transport URI on `PackEncryptedResult.ServiceEndpoint`. A mediator then unwraps the outer layer via `ForwardProcessor` and emits the onward payload — and Bob unpacks it as if no mediator had been involved. |
| **P** — Send over a transport | The pack-then-route work is done; now the bytes go on the wire. `DidCommClient.SendAsync` packs (with `Forward = true` by default), reaches into the registered `ITransportRouter`, picks a transport whose `CanHandle` accepts the endpoint URI's scheme, and POSTs through it. The section uses an in-process `TestServer` as Bob's inbox so the example stays offline. |
| **Q** — Receive over HTTP | The matching server side. `app.MapDidCommEndpoint("/didcomm", onReceive)` validates `Content-Type`, enforces `MaxReceiveBytes → 413`, unpacks via `DidCommClient.UnpackAsync`, hands the result to the inline `onReceive` delegate, and returns `202 Accepted`. The section also walks the 415 (wrong content type) and 413 (oversize body) negative cases. |
| **R** — Receive / chat over WebSocket | One packed envelope per WebSocket *message* (FR-TRN-09). The server reassembles fragmented frames before unpacking; the receiver is one-way (the server doesn't send protocol replies on the same socket, FR-TRN-10). The section also subscribes to the transport's `Lifecycle` event so the reader sees `Connected`/`Disconnected` hooks fire. |
| **S** — Trust Ping | Liveness in two messages: `TrustPing.CreatePing` sent through the loopback transport, answered automatically by the built-in handler with `thid == ping.Id`. |
| **T** — Discover Features | The initiator side of feature discovery: `DiscoverFeaturesClient.QueryFeaturesAsync` sends a `queries`, awaits the peer's correlated `disclose`, and lists what the peer supports. |
| **U** — Report Problem | Building spec-conformant problem reports: the code taxonomy, comment interpolation with `args`, escalation, and the cascade guard that stops problem-report ping-pong. |
| **V** — Out-of-Band invitation | Creating an invitation, encoding it as a `?_oob=<base64url>` URL, decoding it on the recipient side, and threading the response via `pthid = invitation.Id`. |
| **W** — Empty message | The header-only envelope: `Message.Empty()` carrying just an `ack` — a receipt with no body. |
| **X** — Custom protocol handler | The protocol extension point: implement `IProtocolHandler`, register it with `AddProtocol<T>()`, and watch the dispatcher route an inbound message to it and return its reply. |
| **Y** — Custom secrets resolver & keystore bridge | Both ways to own the key-custody seam: a hand-written `ISecretsResolver` (a tiny "mock KMS" showing the two-method contract), and `NetDidKeyStoreSecretsResolver` bridging a NetCrypto `IKeyStore` — which signs and encrypts without a private byte ever leaving the store. |
| **Z** — Custom transport | The transport extension point: implement `IDidCommTransport` for a made-up `memq://` scheme, let `TransportRouter` pick it by endpoint scheme in a full `SendAsync`, read what the request carries (`LocalDid`, `PeerDid`, `ReturnRoute`, `MaxResponseBytes`), refuse a return-route send a one-way transport cannot answer, and see the router refuse a scheme nobody handles. |
| **AA** — net-did integration + `did:web` rejection | Implicitly, every section is using net-did to resolve DIDs. This section makes that explicit, and shows the deliberate exception: `did:web` is refused at every entry point because its trust model leaves it vulnerable to silent key substitution. Use `did:webvh` if you need a web-resolvable DID. |
| **BB** — Profiles & i18n | Negotiating which DIDComm dialect to speak from a peer's `accept` list, and speaking it in the right language: `lang`/`accept-lang` headers with the preference persisted per thread — and proven not to leak across threads. |
| **CC** — Enroll with a mediator | A wallet with no public address enrolls with a real mediator over HTTP: `MediationClient.RequestMediationAsync` (a `mediate-deny` is a `null` grant), a public DID whose service is `DidCommServiceBuilder.ForMediation(grant)`, `UpdateRecipientsAsync` (retrying is safe; the first peer to register a DID gets it), `QueryRecipientsAsync`. Then an older wallet that speaks Coordinate Mediation 2.0 (`MediatorClientOptions.CoordinateMediationVersion = V2`, as for the Identus or Blocktrust mediators) enrolls with the same mediator: its grant names the mediator's first routing DID, and it sees the DID registered in 3.0. Then the wrong peer (a plain agent → `MediationNotSupportedException`), the `didcomm:transport/queue` URI that `SendAsync` refuses, and every Coordinate Mediation message built and parsed. A wallet installs `DidComm.Mediator.Client` plus `DidComm.Transports.Http` (or `.WebSocket` for live delivery). |
| **DD** — Pick up queued messages | The mailbox over HTTP with `PickupClient`: status → fetch (each message opened and acknowledged) → discard one that will not open with `AckAsync`. Delivery is at-least-once with repeats filtered per mailbox; a message older than the age limit (30 days by default) is dropped without notice. The mediator side drops messages straight into a queue with `MessagePickupQueue.DeliverAsync`; every Message Pickup message is built and parsed; and DD.3 walks the `IMessageStore` contract call by call for anyone writing a persistent store. |
| **EE** — Live delivery over WebSocket | Messages that come back over a connection the client opened. On the mediator: a protocol handler answers a `return_route: all` request by pushing notices through `context.Session` (`TrySendAsync`, whose size-bounded form answers `TooLarge` without sending anything) and watches the session's `Closed` token. On the client, by hand: a `WebSocketDidCommTransport` with an `IInboundFrameConsumer` of its own, which skips any frame whose authcrypt sender is not the connection's peer (compared as DID subjects). The usual way: `UseWebSocketTransport(o => o.ReceiveInbound = true)` + `UseInboundDispatch()` + `AddMediatorClient()`, then `PickupClient.ReceiveLiveAsync`. A live item is acknowledged when you ask for the next one, and `AckAsync(item.Id)` discards the last one. Polling in live mode is permanent: a delayed message that becomes due is not pushed, and the next poll (one `LivePollInterval` after the previous drain) picks it up. Delivery is at-least-once, and the session's `Closed` token, not the registry's best-effort events, marks a session's end. |
| **FF** — Run a minimal mediator in 20 lines | A complete mediator is `AddMediator()` + `MapDidCommMediator("/didcomm").RequireRateLimiting(...)`, with an allow-list `IMediationPolicy` because enrolment is open by default. `AddMediator()` refuses a host `IForwardSink`: use `AddRoutingMediator()` for that. Then the roles composed yourself: à la carte in a container (`AddCoordinateMediation()` before `AddRoutingMediator()`, `AddMessagePickup()`, and a sink that calls `MessagePickupQueue.DeliverAsync`; live push stays `AddMediator()`'s), and with no container at all (the queue, the Coordinate Mediation 3.0 and 2.0 handlers over one store, the pickup handler and a dispatcher, driven in-process; a 2.0 `mediate-request` gets the same grant, its `routing_did` a single string). `AddCoordinateMediation()` serves 2.0 too unless `EnableV2 = false`. The library's endpoints run replay admission before dispatch; a receive path of your own must run it itself (`IInboundReplayGuard.TryAdmit`, keyed on the authcrypt sender's DID subject), and the section shows a captured `status-request` played again being refused as `Duplicate` before any handler runs. Last, the replay guard: each message id is admitted once, and two hosts' clocks must agree within a minute by default (`ReplayProtectionOptions.ClockSkew`). The mediator references `DidComm.Mediator` + `DidComm.Mediator.AspNetCore`; a wallet references `DidComm.Mediator.Client` and a transport. |
| **GG** — Send through a proxy you trust | The transports connect directly and never pick up a proxy from the environment (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, the system proxy). When your network's only way out is a proxy, name it with `TrustedProxy` on `UseHttpTransport` and `UseWebSocketTransport` (credentials in `WebProxy.Credentials`, never in the address). The section sends through a stand-in egress proxy on loopback: an allowed address goes through it (202), the cloud-metadata address and a private address are refused by the library before the proxy is asked, and a WebSocket tunnel to an address the proxy does not allow is refused by the proxy. Past the proxy the library cannot see where a connection ends up, so your proxy must refuse internal destinations; where only the proxy can look up names, set `ResolveDnsNames = false`. |

Cookbook letters come from the project's PRD §14.2, which is the master list of the API tasks the library must demonstrate. The PRD/FR cross-references live in each section's XML doc and the project CHANGELOG.

## Run it

```sh
dotnet run --project samples/02-Cookbook
```

Or via the smoke test (no process spawn — useful for CI):

```sh
dotnet test --filter FullyQualifiedName~CookbookSmokeTests
```

## Expected output (shape)

Identifiers change every run because fresh `did:peer:2` identities are minted each time; the structure is stable. The run opens by minting the shared identities, then prints one banner per section, in letter order:

```
  • Minted alice = did:peer:2.Ez6LS…
  • Minted bob   = did:peer:2.Ez6LS…
  • Minted alice2 (rotation target) = did:peer:2.Ez6LS…

== Section A — Dependency-injection setup ==
...
== Section BB — Profiles & i18n ==
...
== Section FF — Run a minimal mediator in 20 lines ==
...
== Section GG — Send through a proxy you trust ==
```

Each section follows the same frame — steps (`•`), key = value report lines, and closing `note:` lines. Section K in full, as a representative example:

```
== Section K — Unpack and inspect metadata ==
  • Pack: encrypt for Bob, authenticate Alice as sender, add an inner signature.
  • Unpack as Bob (… bytes on the wire).
    Encrypted = True
    Authenticated = True
    NonRepudiation = True
    AnonymousSender = False
    ContentEncryption = A256CBC-HS512
    KeyWrap = ECDH-1PU+A256KW
    SignatureAlgorithm = EdDSA
    SignerKid = did:peer:…#key-2
    SenderKid = did:peer:…#key-1
    RecipientKid = did:peer:…#key-1
    AllRecipientKids.Count = 1
    Stack = Encrypted ⊃ Signed ⊃ Plaintext
    FromPrior = <null>
    Message.From = did:peer:…
    Message.Body[content] = Hi Bob — this is the metadata-rich envelope.
    note: Three flags are true at once because three layers stack: the outer JWE gives Encrypted+Authenticated, the inner JWS adds NonRepudiation.
```

The smoke test asserts every banner (and one distinctive outcome per tricky section), so a section dropping out of the run fails CI.

## Code layout

- [`Program.cs`](Program.cs) — entry point. The `RunAsync(TextWriter)` overload is the testable seam used by the smoke test. Sections are registered in §14.2 letter order.
- [`CookbookContext.cs`](CookbookContext.cs) — one-time bootstrap: DI graph plus the three test identities.
- [`LoopbackTransport.cs`](LoopbackTransport.cs) — the in-process `IDidCommTransport` that lets protocol sections (S, T) run request/response flows without a network.
- [`CookbookMediatorHost.cs`](CookbookMediatorHost.cs) — a complete mediator (`AddMediator()` + `MapDidCommMediator("/didcomm")`, rate-limited per client address) on a free 127.0.0.1 port, started and disposed by sections CC and DD. Its DID names its own address, so the port is picked first (`LoopbackPort`); production addresses are fixed and https.
- [`Sections/`](Sections/) — one `Section_<Letter>_<Name>.cs` file per §14.2 task letter, each a static class with a `RunAsync(CookbookContext)`.

Shared helpers (`Narrator`, `PeerIdentityFactory`, `LoopbackPort`) live in [`../_shared/`](../_shared/) so future sample apps can reuse them.
