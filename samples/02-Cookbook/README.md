# 02-Cookbook

A runnable, narrated tour of the DIDComm v2.1 library. Each section is a small program that demonstrates one capability end-to-end: it bootstraps two test identities, performs the operation, and prints what happened. Read the source alongside the output and you should be able to copy the patterns into your own application.

The cookbook will grow with each phase of the project. The table below names what's shipped today plus where to find more.

## Sections shipped today

| Section | What it demonstrates |
|---|---|
| **K** — Unpack and inspect metadata | After unpacking a packed message, what does the library tell you about it? Encrypted? Signed? Who sent it? Which key decrypted? Every field of `UnpackResult` is printed against a maximally-protective envelope so you can see how each flag corresponds to a layer. |
| **N** — DID rotation via `from_prior` | How Alice changes the DID she identifies as without breaking Bob's trust: she signs a tiny `from_prior` JWT with a key her old DID had advertised, ships it inside her first message under the new DID, and Bob's unpack validates it automatically. Also shows the safety rule — rotation messages cannot be sent in the clear. |
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
- [`Sections/Section_AA_NetDidAndDidWebRejection.cs`](Sections/Section_AA_NetDidAndDidWebRejection.cs)

Shared helpers (`Narrator`, `PeerIdentityFactory`) live in [`../_shared/`](../_shared/) so future sample apps can reuse them.
