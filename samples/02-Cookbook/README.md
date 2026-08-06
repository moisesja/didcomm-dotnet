# 02-Cookbook

A runnable, narrated tour of the DIDComm v2.1 library. Each section is a small program that demonstrates one capability end-to-end: it bootstraps two test identities, performs the operation, and prints what happened. Read the source alongside the output and you should be able to copy the patterns into your own application.

The cookbook is complete: one section per API task letter in the project PRD's §14.2 master list, A through BB.

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
| **J** — Multi-recipient | One envelope encrypted to Bob AND Carol: the body is encrypted once, the key wrapped per recipient. Counts the `recipients` entries on the wire and prints every `AllRecipientKids` entry on unpack. |
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
| **Z** — Custom transport | The transport extension point: implement `IDidCommTransport` for a made-up `memq://` scheme, let `TransportRouter` pick it by endpoint scheme in a full `SendAsync`, and see the router refuse a scheme nobody handles. |
| **AA** — net-did integration + `did:web` rejection | Implicitly, every section is using net-did to resolve DIDs. This section makes that explicit, and shows the deliberate exception: `did:web` is refused at every entry point because its trust model leaves it vulnerable to silent key substitution. Use `did:webvh` if you need a web-resolvable DID. |
| **BB** — Profiles & i18n | Negotiating which DIDComm dialect to speak from a peer's `accept` list, and speaking it in the right language: `lang`/`accept-lang` headers with the preference persisted per thread — and proven not to leak across threads. |

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
- [`Sections/`](Sections/) — one `Section_<Letter>_<Name>.cs` file per §14.2 task letter, each a static class with a `RunAsync(CookbookContext)`.

Shared helpers (`Narrator`, `PeerIdentityFactory`) live in [`../_shared/`](../_shared/) so future sample apps can reuse them.
