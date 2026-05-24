# Changelog

All notable changes to didcomm-dotnet are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — Phase 3 (Facade, net-did Integration, Secrets, Rotation)

Closes PRD §12 Phase 3: FR-DID-01..07, FR-SEC-01..05, FR-API-01..08,
FR-CONSIST-06 (resolver-backed authorization now active), FR-ROT-01..06.

- **Public surface promotions** — every type the facade returns or accepts is now
  `public sealed`: `Messages/{Message, Attachment, AttachmentData, MessageBuilder,
  IMessageIdGenerator, UuidV4MessageIdGenerator, MediaTypes}`,
  `Protocols/{MessageTypeUri, ProtocolVersion}`, `Jose/{Jwk, EnvelopeKind}`. Helper
  types (`UnreservedUriChars`, `DidSubject`, all `Composition/*`, all
  `Jose/Signing|Encryption/*`, all `Crypto/*`, the internal lookups) stay internal.
- **Facade** (`Facade/`):
  - `DidCommClient` — sealed, thread-safe (NFR-03). Public methods
    `PackPlaintextAsync`, `PackSignedAsync`, `PackEncryptedAsync`, `UnpackAsync`
    (FR-API-01..03). Auto-detects envelope shape on unpack, enforces
    `expires_time` (FR-API-05) and `MaxReceiveBytes` (FR-API-06), rejects
    `did:web` at every entry point on every DID-bearing field (FR-DID-06).
  - `PackEncryptedOptions`, `ContentEncryptionAlgorithm`, `DidCommOptions`,
    public `UnpackResult` (FR-API-04 metadata + `FromPrior` slot).
  - `MapContentEncryption` enforces FR-ENC-09 (refuses A256GCM / XC20P for
    authcrypt at pack time).
- **Resolution** (`Resolution/`):
  - `IDidKeyService` — public contract: `GetVerificationMethodsAsync`,
    `IsKeyAuthorizedAsync`, `RejectUnsupportedMethod`. `VerificationRelationship`
    enum (`KeyAgreement`, `Authentication`).
  - `NetDidKeyService` — public adapter wrapping `NetDid.Core.IDidResolver`.
    Method extraction via `NetDid.Core.Parsing.DidParser.ExtractMethod`; rejects
    `did:web` with `UnsupportedDidMethodException` (DD-08). Dereferences fragment
    references against the doc's `verificationMethod` array; materialises JWKs
    from `publicKeyJwk` (off-curve EC points already rejected inside
    `JwkConversion.ExtractPublicKey` by net-did's `EcPointValidator`); silently
    skips multibase-only methods and curves outside the `KeyTypeMapper` set so
    mixed-curve documents still surface usable keys. No internal cache —
    relies on `CachingDidResolver` from `NetDid.Extensions.DependencyInjection`
    (FR-DID-04 "no double-caching").
  - `DidKeyServiceLookups` — internal sync-over-async bridges that satisfy the
    envelope layer's `IInternalSenderKeyLookup` and signer-`Func<string, Jwk?>`
    slots by walking back to the public async `IDidKeyService`.
- **Secrets** (`Secrets/`):
  - `ISecretsResolver` — public contract: `FindAsync(kid)`,
    `FindPresentAsync(kids)`. Consumer-supplied; the library ships no production
    key store per DD-02.
  - `SyncSecretsAdapter` — internal `IInternalSecretsLookup` wrapper that
    blocks sync-over-async on the public resolver (safe under .NET 10's
    no-synchronization-context runtime).
- **Exceptions** — `UnsupportedDidMethodException(method, did, reason)`,
  `DidResolutionException(did, reason, inner?)`, `SecretNotFoundException(kid)`
  (FR-API-07).
- **FR-CONSIST-06 wiring** — `EnvelopeReader.Unpack` gained a
  `Func<string,string,string,bool>? resolverCheck` parameter that fires
  `AddressingConsistency.CheckResolverAuthorization` at three points (sender
  keyAgreement, recipient keyAgreement, signer authentication) once the inner
  plaintext reveals `from`. The facade binds the predicate to
  `IDidKeyService.IsKeyAuthorizedAsync`.
- **DID rotation** (`Protocols/Rotation/`):
  - `Message.FromPrior` typed slot + `MessageBuilder.WithFromPrior` (FR-ROT-01).
  - `FromPriorClaims` record (`Sub`, `Iss`, `Iat`).
  - `FromPriorBuilder` (internal) — emits a compact JWT
    `<b64u(header)>.<b64u(claims)>.<b64u(sig)>` signed by a key authorized in
    the prior DID's `authentication`.
  - `FromPriorValidator` (internal) — three-part split, signature verification
    via `IDidKeyService`, `sub == currentSenderDid` enforcement (FR-ROT-02),
    alg-curve cross-check to defeat downgrade swaps.
  - `DidCommClient` enforces FR-ROT-03: refuses to emit `from_prior` on a
    plaintext or signed-only envelope; raises `ConsistencyException` on unpack
    when a `from_prior`-carrying message arrives unencrypted.
- **DI extension** (`src/DidComm.Extensions.DependencyInjection/`):
  - New csproj. `services.AddDidComm(b => …)` registers `DidCommClient` as
    singleton with `DidCommOptions`, `ISecretsResolver`, and `IDidKeyService`.
  - `DidCommBuilder` methods: `UseNetDidResolver(...)` (defaults to `did:key` +
    `did:peer` via net-did's DI builder; extra methods via the inner action),
    `UseSecretsResolver<T>()` / `UseSecretsResolver(instance)`,
    `Configure(Action<DidCommOptions>)`.
  - Build-time FR-SEC-02 fail-fast: throws `InvalidOperationException` with
    actionable docs-pointer text when `ISecretsResolver` or `IDidKeyService` is
    unregistered.
- **NetDid IKeyStore adapter** (`src/DidComm.Adapters.NetDid/`):
  - New csproj. `NetDidKeyStoreSecretsResolver` bridges
    `NetDid.Core.IKeyStore` → `ISecretsResolver` (FR-SEC-04, SHOULD). XML doc
    surfaces the scope limit: `IKeyStore` exposes signing + public-key surfaces
    only, never raw private bytes, so this adapter is sufficient for resolving
    *which* kids are held but cannot yield decryption-path private keys until
    net-did adds an opaque-ECDH provider.
- **TestSupport** (`tests/DidComm.TestSupport/`):
  - New library (non-test). `InMemorySecretsResolver` is the dictionary-backed
    test fake (FR-SEC-05) — deliberately outside `DidComm.Core` so DD-02 stays
    honest.
- **`JweParser.PeekRecipients`** — lightweight structural peek (recipient kids
  + skid, no crypto) for facade pre-warm scenarios. Wired into the design but
  not yet consumed by the current facade implementation; kept available for
  future caching/optimization work.

### Vendored spec fixtures (FR-IX-01 extension)

- `tests/DidComm.InteropTests/fixtures/diddocs/spec/{alice,bob}.json` — DIDComm
  v2.1 Appendix B DID Documents transcribed from didcomm-python's
  `DID_DOC_*_SPEC_TEST_VECTORS` (Apache-2.0). Provenance + scope note in
  `fixtures/diddocs/spec/README.md`. Charlie / mediator1 / mediator2 are
  intentionally deferred to Phase 4 alongside the FR-ROUTE-* work that actually
  exercises them.

### Tests — Phase 3

Adds **54 new** `DidComm.Core.Tests` cases (299 total) plus **18 new**
`DidComm.InteropTests` cases (30 total).

- `Exceptions/Phase3ExceptionsTests` — the three new typed exceptions carry the
  declared properties and inherit `DidCommException`.
- `Messages/MessageFromPriorTests` — `Message.FromPrior` round-trips, omitted
  when null, `MessageBuilder.WithFromPrior` populates the slot.
- `Secrets/{ISecretsResolverContractTests, InMemorySecretsResolverTests,
  NetDidKeyStoreSecretsResolverTests}` — contract semantics + the two adapters.
- `Resolution/{IDidKeyServiceContractTests, NetDidKeyServiceTests}` — contract
  + adapter (did:web rejection, malformed input, missing-doc, embedded JWK,
  fragment deref, missing reference, unsupported-curve filter,
  multibase-only-skip, `IsKeyAuthorizedAsync` relationship boundary).
- `Consistency/ResolverAuthorizationTests` — predicate-fires-correct-triple,
  null-short-circuit, authorized passes, unauthorized throws.
- `Facade/{DidCommClientUnitTests, DependencyInjectionTests}` — FR-ROT-03
  refusal on plaintext / signed; did:web rejection across every entry point and
  every DID-bearing field; MaxReceiveBytes; fail-fast on missing
  `ISecretsResolver` / `IDidKeyService`; `Configure(...)` applies; instance
  registration overload.
- `Rotation/FromPriorClaimsTests` — record equality + iat inequality.
- InteropTests:
  - `Resolution/AppendixBResolutionTests` — Alice authentication (3 keys),
    Alice keyAgreement (X25519+P256+P521), Bob keyAgreement (9 keys across
    four curves), Bob no-authentication, `IsKeyAuthorizedAsync` relationship
    boundary.
  - `Facade/DidCommClientRoundTripTests` — plaintext, signed, anoncrypt,
    authcrypt, sign-then-encrypt, anoncrypt(authcrypt), authcrypt FR-ENC-09
    refusal — every legal FR-ENV-02 composition through the public facade.
  - `Rotation/FromPriorRotationTests` — builder/validator round-trip, tampered
    signature rejected, mismatched `sub` rejected (FR-ROT-02), signer-not-in-
    authentication rejected, malformed JWT rejected, `DidCommClient.UnpackAsync`
    populates `UnpackResult.FromPrior` end-to-end.

### Changed

- `DidComm.Core.csproj` gained `<InternalsVisibleTo>` entries for the three new
  sibling assemblies (`DidComm.Extensions.DependencyInjection`,
  `DidComm.Adapters.NetDid`, `DidComm.TestSupport`).
- `EnvelopeReader.Unpack` signature gained a trailing optional `resolverCheck`
  parameter (default `null` preserves the Phase 2 behaviour where the
  FR-CONSIST-06 hook is a no-op).
- `SpecActorRegistry.AsSecretsResolver()` — new test helper exposing the
  Appendix A secrets through the public `ISecretsResolver` shape.

### Added — Cookbook (PRD §14.2 Phase 3 increment)

Per the PRD §14 note, the Cookbook gains the API tasks each phase ships.
Phase 3's increment lands here: **K (unpack metadata), N (from_prior rotation),
AA (net-did + did:web rejection)**.

- **`samples/_shared/`** (`DidComm.Samples.Shared`):
  - `Narrator` — labeled console output (section banners, key=value frames,
    notes). Writes through an injectable `TextWriter` so the smoke test
    captures the transcript without process spawning.
  - `PeerIdentityFactory.CreateAsync(manager, keyGenerator, cryptoProvider)`
    mints a `did:peer:2` identity with one X25519 keyAgreement key + one
    Ed25519 authentication key (via net-did's `KeyPairSigner` +
    `DidPeerCreateOptions(Numalgo.Two, ...)`); surfaces the matching private
    JWKs with absolute-DID-URL `Kid` values so they can be loaded into
    `InMemorySecretsResolver`.
- **`samples/02-Cookbook/`** (`DidComm.Samples.Cookbook`):
  - `CookbookContext.BuildAsync()` runs `services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(instance))`,
    mints `alice`, `bob`, and `alice2` peer identities, and resolves the
    `DidCommClient`. Shared by every section.
  - `Program.RunAsync(TextWriter? output)` — testable entry point; `Main`
    wraps it for CLI use.
  - `Sections/Section_K_UnpackMetadata` — packs authcrypt(sign(plaintext))
    alice→bob, unpacks as bob, prints every `UnpackResult` field
    (Encrypted/Authenticated/NonRepudiation/AnonymousSender/ContentEncryption/
    KeyWrap/SignatureAlgorithm/SignerKid/SenderKid/RecipientKid/
    AllRecipientKids/Stack/FromPrior + Message.From + Message.Body).
  - `Sections/Section_N_FromPriorRotation` — builds the `from_prior` JWT via
    the now-public `FromPriorBuilder.Build(claims, signerPrivateJwk)`, packs
    as authcrypt(alice2→bob), unpacks as bob, asserts
    `UnpackResult.FromPrior.Sub == message.From`. Then demonstrates FR-ROT-03
    by attempting `PackPlaintextAsync` with `FromPrior` set and reporting the
    `InvalidOperationException` message.
  - `Sections/Section_AA_NetDidAndDidWebRejection` — every prior section is
    already going through `NetDidKeyService` over a `CompositeDidResolver`
    (did:key + did:peer). This section adds the explicit DD-08 / FR-DID-06
    rejection paths: `PackEncryptedAsync` (recipient, From, SignFrom) and
    `PackSignedAsync` (signFrom) all throw `UnsupportedDidMethodException`
    when given `did:web:example.com`.
  - `README.md` — what each section demonstrates + the expected output shape.
- **`tests/DidComm.InteropTests/Samples/CookbookSmokeTests`** — FR-DX-02
  build+run gate: invokes `Program.RunAsync(StringWriter)` and asserts every
  Phase 3 section banner appears in the transcript, no exceptions, no process
  spawn.

### Public-surface bumps to unblock the Cookbook

- `Protocols/Rotation/FromPriorBuilder` and `FromPriorValidator` promoted
  `internal → public` (Section N consumes them directly). Each gains a no-
  crypto-provider overload as the public entry point; the explicit-provider
  variant stays `internal` for tests/facade reuse.
- `NetDidKeyService` now decodes `publicKeyMultibase` (Multikey) verification
  methods via NetCid's `Multibase` + `Multicodec` + net-did's
  `KeyTypeExtensions.ToKeyType` — needed because `did:peer:2` resolved DID
  Documents emit Multikey form (not JsonWebKey2020). It also absolutizes
  relative VM ids (`#key-1` → `<did>#key-1`) so kids match the envelope
  layer's expectations. The previous "multibase-only methods are skipped"
  test became a "Multikey methods decode to JWK" test; a new
  malformed-multibase test asserts the skip-on-error path.

### Added — Phase 2 (Envelopes + Interop Gate)

Closes PRD §12 Phase 2: FR-ENV-01..07, FR-ENC-04, FR-ENC-09..19, FR-SIG-01..06,
FR-IX-01 (vendored spec Appendix C fixtures), FR-IX-03 (inbound static gate).

- **JWS layer** (`Jose/Signing/`):
  - `JwsBuilder` emits Flattened JSON Serialization for one signer and General JSON
    for multiple (FR-SIG-02). Signs the deterministic canonical bytes of the inner
    plaintext JWM (NFR-10).
  - `JwsParser` accepts both serializations; verifies the signature; runs FR-CONSIST-03
    (signer kid ↔ plaintext `from` DID-subject equality); tolerates kid in either the
    protected or unprotected header per JOSE.
  - `JwsProtectedHeader` DTO with `JsonExtensionData` for unknown-member preservation
    (FR-MSG-15 carries through to the protected header too).
- **JWE layer** (`Jose/Encryption/`):
  - `JweBuilder` packs General-JSON multi-recipient envelopes. `PackAnoncrypt` uses
    ECDH-ES+A256KW with any of A256CBC-HS512 / A256GCM / XC20P; `PackAuthcrypt` uses
    ECDH-1PU+A256KW and **enforces FR-ENC-09**: refuses A256GCM / XC20P for authcrypt.
    Same-curve invariant enforced inside one envelope (FR-ENC-04 / FR-ENC-11); cross-
    curve splitting is the Phase 3 facade's job.
  - `JweParser` decrypts the first recipient whose kid matches a held private key,
    re-derives `apv` from the parsed recipient kids and **rejects on mismatch
    (FR-ENC-13)**, re-validates `epk` through `JwkConversion.ExtractPublicKey` (so
    off-curve EC points throw `CryptoException` per FR-ENC-03 via net-did's
    `EcPointValidator`).
  - `ApvComputer` (FR-ENC-13: base64url(SHA-256(sort(kids).join('.')))),
    `ApuComputer` (FR-ENC-14: base64url(UTF-8(skid))), `JweProtectedHeader` DTO,
    `RecipientWrap` record.
- **Composition** (`Composition/`):
  - `EnvelopeWriter.PackPlaintext` / `PackSigned` / `PackEncrypted` accept explicit
    key material (no resolver lookups in Phase 2). `PackEncrypted` orchestrates the
    legal FR-ENV-02 / FR-ENV-04 compositions: anoncrypt, authcrypt, sign-then-encrypt
    (FR-ENV-05 ordering), and anoncrypt-of-authcrypt (`ProtectSender = true`).
  - `EnvelopeReader.Unpack` auto-detects envelope shape (FR-API-03), recursively
    unwraps up to 4 layers, enforces the addressing-consistency rules as each layer
    is revealed — FR-CONSIST-01 (authcrypt `skid` ↔ plaintext `from`), FR-CONSIST-02
    (recipient kid ↔ `to`), FR-CONSIST-03 (signer kid ↔ `from`), and FR-CONSIST-05
    (authcrypt(sign) inner signer ↔ outer `skid`) — and surfaces FR-API-04 metadata
    (`encrypted`, `authenticated`, `non_repudiation`, `anonymous_sender`, enc/kw/sig
    algorithms, signer/sender/recipient kids, envelope stack). FR-CONSIST-06's
    resolver-backed authorization is wired in Phase 3.
  - `UnpackResult` carries the metadata shape that the Phase 3 public facade will
    surface unchanged.
- **Crypto additions** (`Crypto/`):
  - `Kdf/EcdhEsKdf` — anoncrypt KDF wrapper (`Z = Ze`, tag-free `SuppPubInfo`) plus
    receive-side variant; mirrors the `Ecdh1PuKdf` pattern.
  - `KeyAgreement/EphemeralKeyPair.Generate(crv)` — wraps net-did's
    `DefaultKeyGenerator` to produce one-shot ephemeral keypairs for each pack call;
    `Clear()` zeroes the private half (NFR-09).
  - `KeyAgreement/KeyTypeMapper` — single source of truth for JOSE `crv` ↔
    `KeyType` ↔ JWS `alg` ↔ AEAD key/IV sizes; eliminates ad-hoc dispatch tables
    scattered across the envelope code.
- **Secrets** (`Secrets/`):
  - `IInternalSecretsLookup` and `IInternalSenderKeyLookup` — minimal internal
    contracts so the envelope layer is testable in isolation. The Phase 3 public
    `ISecretsResolver` (FR-SEC-01) will adapt.
- **Exceptions**: `CryptoException` joins the typed hierarchy (FR-API-07). Decrypt /
  verify / unwrap / off-curve failures throw it instead of raw
  `CryptographicException`.
- **Jose plumbing**: `Base64Url` (thin wrapper over `System.Buffers.Text.Base64Url`,
  used by every JOSE encoder/decoder), `EnvelopeKind` enum, `EnvelopeDetector`
  (FR-API-03 structural sniff).

### Fixed — Phase 0 carry-over

- **`Crypto/Kdf/Ecdh1PuKdf.cs`**: the `SuppPubInfo` layout was
  `BE32(keyDataLen*8) ‖ tag`. Per draft-madden-jose-ecdh-1pu-04 §2.3 the tag MUST be
  prefixed with a 4-octet big-endian length: `BE32(keyDataLen*8) ‖ BE32(tagLen) ‖ tag`.
  The original Phase 0 wrapper omitted the prefix; self-round-trip tests masked it
  because both sides used the same (incorrect) layout. Discovered when the SICPA
  Appendix C.3 authcrypt vectors all failed AES-KW unwrap with "integrity check
  failed". The matching Phase 0 KAT was updated to the corrected layout.
- **`Json/DidCommJson.cs` + `Json/DeterministicJsonWriter.cs`**: both serializers now
  use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so the `+` in
  `application/didcomm-plain+json` is emitted literally rather than as `\u002B`. The
  spec vectors carry the literal `+`; deterministic JSON bytes that feed JWS signing
  input and `apv` hashing must match byte-for-byte.

### Tests — Phase 2

Adds **53 new** `DidComm.Core.Tests` cases (245 total) plus **10 new** spec-vector
runners under `DidComm.InteropTests` (12 total: 1 fact, 11 theory cases).

- `Envelopes/Signing/JwsRoundTripTests` — Sign+verify across EdDSA, ES256, ES256K;
  payload tampering rejection; unknown-kid rejection; Flattened vs General serialization
  selection; FR-SIG-06 inner-`to` enforcement; FR-CONSIST-03 wiring.
- `Envelopes/Encryption/AnoncryptRoundTripTests` — Every supported (curve, enc) cell
  per PRD §13.5 anoncrypt row; multi-recipient JWE on same curve (FR-ENC-19);
  cross-curve rejection (FR-ENC-04); apv-tampering detection (FR-ENC-13).
- `Envelopes/Encryption/AuthcryptRoundTripTests` — All four curves
  (X25519/P-256/P-384/P-521); FR-ENC-09 A256GCM rejection; cross-curve sender/recipient
  rejection; missing-sender-lookup rejection; tag-tampering propagates through both
  KEK derivation (FR-ENC-15) and AEAD verification.
- `Envelopes/Encryption/ApvComputerTests` + `ApuComputerTests` + `EnvelopeDetectorTests`
  + `EphemeralKeyPairTests` — per-curve length contracts, freshness, FR-MSG-06
  prefix-normalization for media types.
- `Envelopes/Composition/EnvelopeReaderTests` — End-to-end round-trips for plaintext,
  signed, anoncrypt, authcrypt, anoncrypt(sign), anoncrypt(authcrypt) compositions;
  FR-CONSIST-02 wiring; metadata shape (FR-API-04).
- `Crypto/Kdf/EcdhEsKdfTests` — Sender / receiver KDF agreement; apv sensitivity.
- `Crypto/KeyAgreement/KeyTypeMapperTests` — Routing-table coverage.

### Vendored spec fixtures (FR-IX-01)

DIDComm v2.1 Appendix A/B/C test material harvested from
`sicpa-dlab/didcomm-python` (the SICPA reference impl; same cryptographic baseline as
the spec):

- `secrets/alice.json` + `secrets/bob.json` — 6 + 9 JWKs covering Ed25519 / X25519 /
  P-256 / P-384 / P-521 / secp256k1 (Appendix A).
- `packed/spec/` — 3 signed (C.2 EdDSA / ES256 / ES256K) and 5 encrypted (C.3
  anoncrypt-X25519/XC20P×2, anoncrypt-A256CBC-HS512, anoncrypt-A256GCM,
  authcrypt-X25519, authcrypt-of-signed-P-256, anoncrypt-of-authcrypt-of-signed-P-521)
  packed envelopes.
- `manifest/spec/c2-*.json` + `c3-*.json` — 8 fixture manifests, each running through
  the new `Runner/FixtureDispatcher` and asserting both successful unpack and FR-API-04
  metadata against the SICPA-published expectations.

`InteropTests/Resolution/SpecActorRegistry` loads the Appendix-A secrets once per test
host, exposing both `IInternalSecretsLookup` (for recipient private keys) and
`IInternalSenderKeyLookup` (for authcrypt sender public keys); the resolver-backed
Phase 3 path will subsume this with `IDidKeyService`.

### Added — Phase 1 (Message Model & Consistency)

Closes PRD §12 Phase 1 line items: FR-MSG-01..15, FR-ATT-01..05, FR-CONSIST-01..05
(FR-CONSIST-06 hook present, resolver wiring stubbed for Phase 3), FR-PROTO-01/02,
NFR-10.

- **`Messages/`** — plaintext message model:
  - `Message` — POCO mirroring the §Plaintext Message Structure header set
    (`id`, `type`, `typ`, `to`, `from`, `thid`, `pthid`, `created_time`,
    `expires_time`, `body`, `attachments`) plus a `JsonExtensionData`
    `AdditionalHeaders` bag that survives unpack→repack (FR-MSG-12, FR-MSG-15).
    `Validate()` enforces the §4 structural rules: REQUIRED `id` of unreserved
    URI characters (FR-MSG-02), REQUIRED MTURI `type` (FR-MSG-05), no-fragment
    constraint on `to` / `from` (FR-MSG-07/08), same constraints on
    `thid`/`pthid` (FR-MSG-11).
  - `Attachment` + `AttachmentData` — §Attachments shape with FR-ATT-02 (data
    must carry one of `jws` / `hash` / `links` / `base64` / `json`), FR-ATT-03
    (`links` requires `hash`), FR-ATT-04 (attachment `id` unreserved-char
    requirement) all validated in code.
  - `MessageBuilder` — fluent builder per FR-MSG-13; auto-populates `id` via
    `IMessageIdGenerator` (default `UuidV4MessageIdGenerator`, FR-MSG-03) and
    `typ` (`application/didcomm-plain+json`).
  - `IMessageIdGenerator` carries the FR-MSG-14 uniqueness obligation in its
    XML docs; custom implementations are responsible for it.
  - `MediaTypes` — IANA constants for plaintext / signed / encrypted with
    FR-MSG-06 normalization (`didcomm-plain+json` accepted as equivalent to
    `application/didcomm-plain+json`).
- **`Protocols/`** — MTURI parsing:
  - `MessageTypeUri` — parses
    `<doc-uri>/<protocol-name>/<major.minor>/<message-type>` into four named
    components (FR-PROTO-01); `Matches` comparison is case- and
    punctuation-insensitive on protocol/message and uses
    `ProtocolVersion.IsCompatibleWith` for the version.
  - `ProtocolVersion` — `major.minor` value type with
    `IsCompatibleWith`/`NegotiateWith` implementing FR-PROTO-02 spec semver.
- **`Consistency/`** — addressing-consistency check functions (PRD §4.3):
  - `DidSubject.DidSubjectOf(string)` — delegates to net-did's
    `DidParser.ParseDidUrl` and returns the bare DID subject, the primitive
    every FR-CONSIST-* rule pivots on.
  - `AddressingConsistency` — pure static functions for FR-CONSIST-01..05
    (`CheckAuthcryptFromMatchesSkid`, `CheckRecipientKidInTo`,
    `CheckSignedFromMatchesSignerKid`, `IsRecipientInTo`,
    `CheckAuthcryptInnerSignerMatchesSkid`) plus the FR-CONSIST-06
    `CheckResolverAuthorization` hook (real resolver wiring lands in Phase 3).
- **`Json/`** — deterministic JSON for NFR-10:
  - `DeterministicJsonWriter.WriteUtf8(JsonNode?)` walks the tree and emits a
    UTF-8 byte sequence with object members sorted ASCII-lexicographically at
    every nesting level and no whitespace. Future signing inputs and `apv`
    hashing in Phase 2 route through this writer.
  - `EpochSecondsConverter` enforces integer JSON output for `created_time` /
    `expires_time` (FR-MSG-09) while tolerating string input on read.
  - `DidCommJson.Default` `JsonSerializerOptions` instance with
    `WhenWritingNull` ignore policy so unset optional headers don't appear on
    the wire.
- **`Exceptions/`** — typed failure hierarchy scaffolding (FR-API-07):
  `DidCommException` base + `MalformedMessageException`, `ConsistencyException`,
  `ProtocolException`. Crypto / resolver / transport exceptions land in their
  respective phases.
- **InteropTests fixture payload** — Appendix C.1 "Let's Do Lunch" plaintext
  saved at `tests/DidComm.InteropTests/fixtures/payloads/c1-lets-do-lunch.json`;
  the data-driven runner will wire it into `manifest/spec/` when Phase 2 adds
  the corresponding pack/unpack fixtures.

### Tests — Phase 1

Adds 83 tests to `DidComm.Core.Tests` (86 → 169 total, all green); InteropTests
remains 2/2.

- `Messages/MessageJsonTests` — Appendix C.1 round-trips structurally; body
  absent unpacks to `null` body without error (FR-MSG-10); unknown headers
  survive round-trip (FR-MSG-12, FR-MSG-15); `created_time`/`expires_time`
  serialize as integers (FR-MSG-09) and tolerate string input; null optional
  headers omitted from output.
- `Messages/MessageValidationTests` — FR-MSG-02 / -05 / -07 / -08 / -11
  rejections each have a dedicated case; minimal valid message passes;
  media-type normalization accepts both forms (FR-MSG-06).
- `Messages/MessageBuilderTests` — auto-population of `id`+`typ` (FR-MSG-13),
  custom `IMessageIdGenerator` honored (FR-MSG-03), `Build()` runs validation.
- `Messages/IdGeneratorTests` — default generator emits a lowercase RFC 4122
  UUID v4; **10,000-id no-collision run** satisfies FR-MSG-14.
- `Messages/AttachmentTests` — FR-ATT-01..05: round-trip, data-required
  rejection, links-requires-hash rejection, reserved-char-`id` rejection,
  absent-`id` acceptance, JWS attachment round-trip.
- `Protocols/MessageTypeUriTests` — captures the four components for every
  spec example (`forward`, `ping-response`, `empty`, `problem-report`, plus
  the Appendix C.1 `lets_do_lunch/1.0/proposal`); rejects malformed inputs;
  punctuation- and case-insensitive `Matches`.
- `Protocols/ProtocolVersionTests` — FR-PROTO-02 semver compatibility and
  minor negotiation.
- `Consistency/AddressingConsistencyTests` — FR-CONSIST-01..05 positive and
  negative cases including DID URLs with query/path/fragment (per the §4.3
  normative paragraph); FR-CONSIST-06 short-circuit and reject paths.
- `Json/DeterministicJsonTests` — member ordering, recursive nested sorting,
  whitespace insensitivity, primitives/arrays/null pass-through.

### Added — Phase 0 (Repository & JOSE-Composition Substrate)

Closes PRD §12 Phase 0 line items.

- **Solution scaffolding** — `DidComm.sln`, `src/DidComm.Core`,
  `tests/DidComm.Core.Tests`, `tests/DidComm.InteropTests`. Targets `net10.0`
  per NFR-01 (file-scoped namespaces, nullable enabled, warnings-as-errors).
- **`DidComm.Crypto.ICryptoProvider`** + `DefaultCryptoProvider` — JOSE-shaped
  surface that dispatches by algorithm string (`"EdDSA"`, `"ES256"`,
  `"ES256K"`, `"ES384"`, `"ES512"`, `"ECDH-ES+A256KW"`, `"ECDH-1PU+A256KW"`,
  `"A256CBC-HS512"`, `"A256GCM"`, `"XC20P"`, `"A256KW"`). Sign/verify and raw
  ECDH delegate to `NetDid.Core.ICryptoProvider` 1.3.0+; AEAD, key wrap, and
  the 1PU KDF wrapper are owned locally.
- **AEAD layer** (`Crypto/Aead/`):
    - `AesCbcHmacSha512` — RFC 7518 §5.2.5 (the JOSE-defined encrypt-then-MAC
      composition; mandatory `enc` for authcrypt per FR-ENC-05). Constant-time
      tag check via `CryptographicOperations.FixedTimeEquals` (NFR-09).
    - `AesGcmAead` — thin BCL wrapper for A256GCM.
    - `XChaCha20Poly1305Aead` — thin NSec wrapper for XC20P.
- **`Crypto/KeyWrap/AesKeyWrap`** — RFC 3394 / RFC 7518 §4.4 A256KW. Manual
  implementation because the BCL has no public AES-KW API. Constant-time IV
  integrity check on unwrap.
- **`Crypto/Kdf/Ecdh1PuKdf`** — JOSE 1PU KDF wiring: composes `Z = Ze ‖ Zs`
  from two `NetDid.DeriveSharedSecret` calls, threads the AEAD authentication
  tag into `SuppPubInfo`, and runs net-did's `ConcatKdf`. Implements the 1PU
  encrypt-then-derive-KEK-with-tag ordering required by FR-ENC-15.
- **JOSE primitives** (`Jose/`): `JoseAlgorithms` (algorithm-name constants),
  `Jwk` (DIDComm-shaped JSON Web Key record with `AdditionalData` bag for
  unknown-header preservation per FR-MSG-15 forward compatibility),
  `JwkConversion` (shim over `NetDid.Core.Jwk.JwkConverter` so off-curve EC
  points are rejected at the JWK boundary via net-did's `EcPointValidator`).
- **InteropTests scaffolding** (FR-IX-02) — `fixtures/schema/didcomm-fixture.v1.schema.json`
  (full v1 manifest schema per PRD §13.4), one smoke manifest under
  `fixtures/manifest/spec/_smoke.json`, and the data-driven
  `FixtureDiscoveryTests` xUnit runner that enumerates
  `fixtures/manifest/**/*.json` and emits one theory case per file. Fixtures
  stage inline for Phase 0; the directory layout matches the destination so
  the Phase 2 migration to a standalone `didcomm-dotnet-fixtures` git
  submodule is a `git rm -r` + `git submodule add` (no data restructuring).
- **CI workflow** — `.github/workflows/ci.yml` on `ubuntu-latest` +
  `windows-latest`, `dotnet build /warnaserror` + `dotnet test --configuration
  Release` with TRX + cobertura coverage upload (NFR-08 scaffold).

### Changed

- **PRD §3.1 dependency table** — recorded that the SSI crypto substrate
  (sign/verify with format choice, raw ECDH for X25519/P-256/P-384/P-521,
  off-curve point validation, public Concat KDF) is owned by `NetDid 1.3.0+`.
  DidComm.Core now owns only the JOSE composition layer.
- **PRD §12 Phase 0** — Build line, Exit criteria, and Kickoff prompt revised
  to reflect the smaller scope; FR-ENC-01/02/03 and FR-SIG-01 are now satisfied
  by net-did and exercised here only as integration concerns (deferred to later
  phases).
- **`Portable.BouncyCastle` → `NBitcoin.Secp256k1`** in `Directory.Packages.props`
  for consistency with net-did's secp256k1 implementation choice; secp256k1
  reaches us transitively through net-did, so no direct package reference is
  needed in `DidComm.Core.csproj`.
- **`OpenTelemetry.Api`** bumped `1.10.0 → 1.15.3` to clear NU1902 audit errors
  (GHSA-8785-wc3w-h8q6, GHSA-g94r-2vxg-569j) under warnings-as-errors.

### Tests

- **`DidComm.Core.Tests`** — 86 tests covering the JOSE composition layer:
    - `AesCbcHmacSha512Tests` — **RFC 7518 §B.3 KAT** byte-for-byte (encrypt → expected
      ciphertext + tag; decrypt → recovered plaintext), round-trip on random inputs,
      tamper rejection of ciphertext / tag / AAD, length-validation on key & IV.
    - `AesGcmAeadTests` — round-trip + tamper rejection on ciphertext / tag / AAD,
      length validation, IV-freshness invariant (FR-ENC-08).
    - `XChaCha20Poly1305AeadTests` — round-trip + tamper rejection, 24-byte nonce
      length contract.
    - `AesKeyWrapTests` — **RFC 3394 §4.6 KAT** byte-for-byte (256-bit KEK, 256-bit
      data → 320-bit wrapped output), round-trip across every block-aligned CEK
      length (16, 24, 32, 48, 64), integrity-check rejection on tampered wrapped
      bytes and wrong KEK, malformed-input rejection.
    - `Ecdh1PuKdfTests` — differential composition test against
      `NetDid.Core.Crypto.Kdf.ConcatKdf` (proves `Z = Ze ‖ Zs` ordering and
      tag-in-`SuppPubInfo` wiring), determinism, tag/apu sensitivity, counter-loop
      coverage at `keyDataLen=64`, dispatch over P-256 in addition to X25519.
    - `NetDidDelegationTests` — round-trip sign/verify for every supported JOSE
      algorithm (`EdDSA`, `ES256`, `ES384`, `ES512`, `ES256K`) with P1363 length
      assertion for the ECDSA variants; round-trip `DeriveSharedSecret` on every
      curve (`X25519`, `P-256`, `P-384`, `P-521`) with the ECDH-commutativity
      invariant; **off-curve EC JWK and identity-point JWK both throw
      `CryptographicException` through `JwkConversion.ExtractPublicKey`** (FR-ENC-03
      / RFC 7518 §6.2.2 invalid-curve defense, inherited from net-did's
      `EcPointValidator`); AEAD + key-wrap dispatch round-trips.

### Deferred to Phase 1+

- DIDComm plaintext message model + attachments + MTURI parsing (Phase 1).
- JWE / JWS envelope construction, `apv`/`apu` derivation, sign-then-encrypt
  enforcement (Phase 2 — the interop gate).
- `DidComm` facade, DID resolver adapter over net-did, `did:web` rejection
  (Phase 3).

### Upstream coordination

- Filed and closed five net-did issues that defined the SSI crypto substrate
  net-did 1.3.0 ships: moisesja/net-did#60 (raw ECDH), #61 (P-521), #62 (ECDSA
  IEEE P1363 format), #63 (off-curve EC point rejection — invalid-curve
  defense), #64 (Concat KDF).

[Unreleased]: https://github.com/moisesja/didcomm-dotnet/compare/HEAD...HEAD
