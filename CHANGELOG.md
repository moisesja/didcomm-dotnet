# Changelog

All notable changes to didcomm-dotnet are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
