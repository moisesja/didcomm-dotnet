# 02-Cookbook

Runnable, narrated demonstrations of the [DIDComm v2.1 PRD §14.2](../../docs/didcomm-dotnet_PRD.md) API tasks. As each phase ships its public surface, this Cookbook gains the corresponding sections (FR-DX-04). It is the backbone of the FR-DX-01 100%-coverage gate that Phase 6 will turn on.

## Phase 3 sections (currently shipped)

| § | What it shows | FR coverage |
|---|---|---|
| **K** | Pack an authcrypt(sign(plaintext)) envelope, unpack it, print every `UnpackResult` field | FR-API-04 |
| **N** | `from_prior` DID rotation — alice → alice2, signed JWT, validated on unpack; plus FR-ROT-03 refusal | FR-ROT-01..04 |
| **AA** | The implicit net-did integration every other section uses + explicit `did:web` refusal at every entry point | FR-DID-01, FR-DID-06, DD-08 |

Sections **A, B** (Phase 1) and **D–L** (Phase 2) and **O–BB** (Phases 4–6) are out of scope today and will land alongside their phase boundaries per [PRD §14 note](../../docs/didcomm-dotnet_PRD.md#143-sample-applications-required-deliverables).

## Run it

```sh
dotnet run --project samples/02-Cookbook
```

Or via the smoke test (no process spawn):

```sh
dotnet test --filter FullyQualifiedName~CookbookSmokeTests
```

## Expected output (shape)

Identifiers vary every run because three fresh `did:peer:2` identities are minted; the structure matches this:

```
  • Minted alice = did:peer:2.Ez6LS…
  • Minted bob   = did:peer:2.Ez6LS…
  • Minted alice2 (rotation target) = did:peer:2.Ez6LS…

== Section K — Unpack and inspect metadata (FR-API-04) ==
  • Pack authcrypt(sign(plaintext)) — alice→bob with inner JWS by alice.
  • Unpack as bob (… bytes on the wire).
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
    note: Encrypted=true (outer JWE) + Authenticated=true (authcrypt 1PU) + NonRepudiation=true (inner JWS).

== Section N — DID rotation (from_prior — FR-ROT-*) ==
  • Build from_prior JWT signed by alice's prior authentication key.
    jwt.length = …
    jwt.head = eyJhbGciOiJFZERTQSI…
  • Pack as authcrypt(alice2 → bob) — FR-ROT-03 requires rotation messages be encrypted.
  • Unpack as bob.
    FromPrior.Sub = did:peer:… (== alice2)
    FromPrior.Iss = did:peer:… (== alice)
    FromPrior.Iat = …
    Sub == message.From = True
  • FR-ROT-03 demo: PackPlaintextAsync with from_prior must throw.
    note: PackPlaintextAsync refused: messages carrying 'from_prior' MUST be encrypted (FR-ROT-03). Call PackEncryptedAsync instead.

== Section AA — net-did integration + did:web rejection (FR-DID-01 / FR-DID-06) ==
  • Implicit integration: every prior section resolved did:peer DIDs via NetDidKeyService.
    Resolver = NetDidKeyService over CompositeDidResolver (did:key + did:peer)
    PackEncryptedAsync (recipient) = refused (web) → did:web:example.com
    PackEncryptedAsync (From) = refused (web) → did:web:example.com
    PackEncryptedAsync (SignFrom) = refused (web) → did:web:example.com
    PackSignedAsync (signFrom) = refused (web) → did:web:example.com
```

## Code layout

- [`Program.cs`](Program.cs) — entry point; `RunAsync(TextWriter)` is the testable seam.
- [`CookbookContext.cs`](CookbookContext.cs) — one-time bootstrap (DI graph + three peer identities).
- [`Sections/Section_K_UnpackMetadata.cs`](Sections/Section_K_UnpackMetadata.cs)
- [`Sections/Section_N_FromPriorRotation.cs`](Sections/Section_N_FromPriorRotation.cs)
- [`Sections/Section_AA_NetDidAndDidWebRejection.cs`](Sections/Section_AA_NetDidAndDidWebRejection.cs)

Shared helpers (Narrator, PeerIdentityFactory) live in [`../_shared/`](../_shared/) so future sample apps can reuse them.
