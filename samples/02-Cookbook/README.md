# 02-Cookbook

A runnable, narrated tour of the DIDComm v2.1 library. Each section is a small program that demonstrates one capability end-to-end: it bootstraps two test identities, performs the operation, and prints what happened. Read the source alongside the output and you should be able to copy the patterns into your own application.

The cookbook will grow with each phase of the project. The table below names what's shipped today plus where to find more.

## Sections shipped today

| Section | What it demonstrates |
|---|---|
| **K** — Unpack and inspect metadata | After unpacking a packed message, what does the library tell you about it? Encrypted? Signed? Who sent it? Which key decrypted? Every field of `UnpackResult` is printed against a maximally-protective envelope so you can see how each flag corresponds to a layer. |
| **N** — DID rotation via `from_prior` | How Alice changes the DID she identifies as without breaking Bob's trust: she signs a tiny `from_prior` JWT with a key her old DID had advertised, ships it inside her first message under the new DID, and Bob's unpack validates it automatically. Also shows the safety rule — rotation messages cannot be sent in the clear. |
| **O** — Routing via a mediator | When a recipient publishes a `DIDCommMessaging` service with `routingKeys`, setting `Forward = true` on the pack call makes the library automatically: resolve the route, reverse-order anoncrypt-wrap a `forward` per routing key, and surface the transport URI on `PackEncryptedResult.ServiceEndpoint`. A mediator then unwraps the outer layer via `ForwardProcessor` and emits the onward payload — and Bob unpacks it as if no mediator had been involved. |
| **P** — Send over a transport | The pack-then-route work is done; now the bytes go on the wire. `DidCommClient.SendAsync` packs (with `Forward = true` by default), reaches into the registered `ITransportRouter`, picks a transport whose `CanHandle` accepts the endpoint URI's scheme, and POSTs through it. The section uses an in-process `TestServer` as Bob's inbox so the example stays offline. |
| **Q** — Receive over HTTP | The matching server side. `app.MapDidCommEndpoint("/didcomm", onReceive)` validates `Content-Type`, enforces `MaxReceiveBytes → 413`, unpacks via `DidCommClient.UnpackAsync`, hands the result to the inline `onReceive` delegate, and returns `202 Accepted`. The section also walks the 415 (wrong content type) and 413 (oversize body) negative cases. |
| **R** — Receive / chat over WebSocket | One packed envelope per WebSocket *message* (FR-TRN-09). The server reassembles fragmented frames before unpacking; the receiver is one-way (the server doesn't send protocol replies on the same socket, FR-TRN-10). The section also subscribes to the transport's `Lifecycle` event so the reader sees `Connected`/`Disconnected` hooks fire. |
| **AA** — net-did integration + `did:web` rejection | Implicitly, every section is using net-did to resolve DIDs. This section makes that explicit, and shows the deliberate exception: `did:web` is refused at every entry point because its trust model leaves it vulnerable to silent key substitution. Use `did:webvh` if you need a web-resolvable DID. |

Cookbook letters come from the project's PRD §14.2, which is the master list of the API tasks the library must demonstrate. Sections A, B, and D–L map to earlier phases; sections O through BB land with Phase 4–6 (routing, transports, protocols). The PRD/FR cross-references live in each section's XML doc and the project CHANGELOG.

## Run it

```sh
dotnet run --project samples/02-Cookbook
```

Or via the smoke test (no process spawn — useful for CI):

```sh
dotnet test --filter FullyQualifiedName~CookbookSmokeTests
```

## Expected output (shape)

Identifiers change every run because three fresh `did:peer:2` identities are minted each time; the structure is stable:

```
  • Minted alice = did:peer:2.Ez6LS…
  • Minted bob   = did:peer:2.Ez6LS…
  • Minted alice2 (rotation target) = did:peer:2.Ez6LS…

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

== Section N — DID rotation via from_prior ==
  • Build the from_prior JWT — Alice signs it with her OLD authentication key.
    jwt.length = …
    jwt.head = eyJhbGciOiJFZERTQSI…
  • Pack the rotation message as an encrypted envelope from alice2 to bob.
  • Bob unpacks. The library verifies the JWT against alice's old DID Document.
    FromPrior.Sub = did:peer:…   (= alice2)
    FromPrior.Iss = did:peer:…   (= alice)
    FromPrior.Iat = …
    Sub == message.From = True
  • Safety demo: a plaintext envelope cannot carry from_prior — the pack call must throw.
    note: PackPlaintextAsync refused: messages carrying 'from_prior' MUST be encrypted (FR-ROT-03). Call PackEncryptedAsync instead.

== Section O — Routing via a mediator (automatic forward wrapping) ==
  • Minted mediator = did:peer:2.Ez6LS…
  • Alice packs an authcrypt message for Bob with Forward = true.
    ServiceEndpoint = https://mediator.example/inbox
    FallbackServiceEndpoints = 0
    OutermostEnvelopeBytes = …
  • Mediator receives, unwraps via ForwardProcessor, and reads body.next.
    NextHop = did:peer:…   (= Bob)
    OnwardPayloadBytes = …
  • Bob unpacks the onward payload as if no mediator had been involved.
    Authenticated = True
    From = did:peer:…   (= Alice)
    ContentMatched = Routed through the mediator.

== Section P — Send over a transport (HTTP chosen by endpoint scheme) ==
  • Alice picks SendAsync(...) and overrides the endpoint to point at Bob's in-process inbox.
    TransportEndpoint = http://localhost/didcomm
    HttpStatusCode = 202
    Accepted = True
    ContentReceivedByBob = Section P: bytes on the wire.

== Section Q — Receive over HTTP (ASP.NET Core MapDidCommEndpoint) ==
  • Alice POSTs the packed envelope with application/didcomm-encrypted+json.
    Status = 202
    From = did:peer:…   (= Alice)
    Authenticated = True
    Content = Section Q: receive side spotlight.
  • Wrong Content-Type → 415 Unsupported Media Type.
    Status = 415
  • Body > MaxReceiveBytes → 413 Payload Too Large.
    Status = 413

== Section R — Receive over WebSocket (MapDidCommWebSocket + binary frames) ==
  • Alice sends one envelope as a single binary WebSocket message.
    note: Lifecycle: Connected → ws://localhost/ws/didcomm
    Accepted = True
    TransportEndpoint = ws://localhost/ws/didcomm
    ContentReceivedByBob = Section R: bytes over WS.
    note: Lifecycle: Disconnected → ws://localhost/ws/didcomm

== Section AA — net-did integration & the did:web refusal ==
  • Implicit integration: every prior section resolved did:peer DIDs through this pipeline.
    Resolver = NetDidKeyService over CompositeDidResolver (did:key + did:peer)
    Encrypt to a did:web recipient = refused (web) → did:web:example.com
    Authcrypt as a did:web sender = refused (web) → did:web:example.com
    Sign-then-encrypt with a did:web signer = refused (web) → did:web:example.com
    Standalone signed envelope from did:web = refused (web) → did:web:example.com
```

## Code layout

- [`Program.cs`](Program.cs) — entry point. The `RunAsync(TextWriter)` overload is the testable seam used by the smoke test.
- [`CookbookContext.cs`](CookbookContext.cs) — one-time bootstrap: DI graph plus the three test identities.
- [`Sections/Section_K_UnpackMetadata.cs`](Sections/Section_K_UnpackMetadata.cs)
- [`Sections/Section_N_FromPriorRotation.cs`](Sections/Section_N_FromPriorRotation.cs)
- [`Sections/Section_O_RoutingViaMediator.cs`](Sections/Section_O_RoutingViaMediator.cs)
- [`Sections/Section_P_SendOverTransport.cs`](Sections/Section_P_SendOverTransport.cs)
- [`Sections/Section_Q_ReceiveHttp.cs`](Sections/Section_Q_ReceiveHttp.cs)
- [`Sections/Section_R_ReceiveWebSocket.cs`](Sections/Section_R_ReceiveWebSocket.cs)
- [`Sections/Section_AA_NetDidAndDidWebRejection.cs`](Sections/Section_AA_NetDidAndDidWebRejection.cs)

Shared helpers (`Narrator`, `PeerIdentityFactory`) live in [`../_shared/`](../_shared/) so future sample apps can reuse them.
