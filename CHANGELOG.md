# Changelog

All notable changes to didcomm-dotnet are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed — rotation JWT compliance (`feat/security-rotation-compliance`)

Resolves two Low-severity audit findings on the `from_prior` rotation path (GitHub issues #25, #26).
Both passed a PoC-backed adversarial red-team pass; full suite (625 tests) green.

- **`FromPriorBuilder` now emits `exp`/`nbf` (#25, FR-ROT-05).** The builder previously serialized only
  `{ iat, iss, sub }`, silently discarding any `FromPriorClaims.Exp`/`Nbf` — so every self-issued
  rotation JWT was non-expiring and the validator-side freshness control (already enforced by
  `DidCommClient.ValidateFromPriorFreshness`) could never fire on tokens this library minted. Claims are
  now built via an insertion-ordered `JsonObject` (`exp, iat, iss, nbf, sub`), emitting `exp`/`nbf` only
  when set (the no-expiry payload is byte-identical to before). A new
  `BuildAsync(claims, signerJwk, TimeSpan lifetime)` overload sets `exp = iat + lifetime` (rejecting a
  sub-second lifetime, which would floor to an already-expired token) so issuing a freshness-bounded
  rotation JWT is one call. The PRD's rotation cookbook/API-matrix is realigned to the actual API
  (`FromPriorBuilder.BuildAsync` + `MessageBuilder.WithFromPrior`); the previously-documented
  `WithDidRotation` helper was never built and is noted as possible future DX.
- **`from_prior` validator rejects a `crit` protected header (#26, RFC 7515 §4.1.11).** The validator
  hand-parses the JWS header and previously read only `alg`/`kid`, silently ignoring `crit` — unlike
  `JwsParser`/`JweParser`, which fail closed. It now rejects any `crit` member with `ProtocolException`,
  before signature verification, bringing the rotation path to parity.

### Fixed — receive-path correctness (`feat/security-receive-correctness`)

Resolves three Low-severity audit findings on the unpack/JOSE receive surface (GitHub issues
#22, #23, #24). Each ships with tests and an adversarial red-team pass; full suite (615 tests) green.

- **Unpack honors its exception contract for non-canonical field lengths (#22).** `EnvelopeReader`
  gains a defensive `ArgumentException → MalformedMessageException` boundary catch at both the JWS and
  JWE delegation points (neutral message; `InnerException` preserved). Note: the originally-reported
  escape is **already resolved upstream** — the delegated `DataProofsDotnet.Jose` parser now wraps the
  AEAD's wrong-length-iv/tag `ArgumentException` (and base64 `FormatException`) as `MalformedJoseException`.
  Covered by a regression test on the unpack contract plus a unit test that drives the boundary catch
  directly (a throwing consumer lookup → `MalformedMessageException`).
- **Strict JOSE base64url decoding (#24).** `Base64Url.Decode` now rejects `=` padding, embedded ASCII
  whitespace, and standard-base64 `+`/`/` (anything outside `[A-Za-z0-9-_]`) with `FormatException`,
  per RFC 7515 §2 / RFC 4648 §3.2-3.3, closing a parser-differential / non-canonical-encoding gap on
  the strict-no-pad paths: `from_prior` JWTs, OOB URLs (FR-OOB-02), and JWK material. **Attachment
  `data.base64` is deliberately kept lenient** (new `Base64Url.DecodeRelaxed`): FR-ATT-02 imposes no
  no-pad requirement and the mediator only relays bytes the recipient re-parses strictly, so a peer's
  padded forward attachment is still relayed rather than refused (PR #38 review). Either way a
  genuinely-invalid forward attachment maps to `MalformedMessageException` rather than escaping the
  mediator as a raw `FormatException` (red-team follow-up).
- **`UnpackResult.AnonymousSender` derived from the outermost encrypt layer (#23).** The flag is now
  computed from the outermost encrypt layer's alg (matching its documented semantics) instead of
  OR-accumulating across layers. The contradiction the finding described (`authcrypt(anoncrypt)` →
  both flags true) was already unreachable after the #17 composition gate; this makes the code match
  its contract by construction.

### Security — Medium-severity audit remediation (`feat/security-medium-cluster`)

Remediates the five Medium-severity findings from the multi-agent security & compliance audit
(GitHub issues #17–#21), plus two lower-severity defects on the same code path (#28, #33). Each fix
ships with negative and regression tests, and each was then put through an adversarial red-team pass
which surfaced two further hardening items (folded in below) and two deeper residuals (filed as
follow-ups). The full suite (600 tests) is green.

- **Illegal envelope compositions rejected (#17, FR-ENV-04a).** `EnvelopeReader.Unpack` now tracks
  each unwrapped layer with its authenticated/anonymous flag and enforces the legal DIDComm v2.1
  receive grammar `anoncrypt? authcrypt? sign? plaintext` before any content/consistency processing.
  Illegal nestings — sign-outside-encrypt (FR-ENV-05), `anoncrypt(anoncrypt)`, `authcrypt(authcrypt)`,
  `authcrypt(anoncrypt)`, double-sign — are rejected with `MalformedMessageException`. The grammar
  admits every spec Appendix C unpack vector, including `authcrypt(sign)` (FR-ENV-03) and
  `anoncrypt(authcrypt(sign))` (Appendix C.3). The auth/anon flag is what distinguishes the legal
  `anoncrypt(authcrypt)` from the illegal `anoncrypt(anoncrypt)` (indistinguishable at envelope-kind
  level). PRD gains **FR-ENV-04a** documenting the receive-acceptance set.
- **Controller-aware resolver authorization (#18, FR-CONSIST-06 / DD-01).** `NetDidKeyService`
  `IsKeyAuthorizedAsync` now walks the raw resolved verification methods (not the curve-projected
  JWKs, which dropped `controller`) and authorizes a `kid` only when its id-subject **and** its
  `controller` resolve to the asserted DID — rejecting a key listed under the DID's relationship but
  controlled by a different DID, and cross-DID embedded ids. did:key / did:peer keep authorizing
  (controller == subject by construction).
- **`from_prior` JWT parse errors normalized (#19).** `FromPriorValidator` wraps base64url decode and
  all JSON reads in one boundary that surfaces malformed input (`FormatException`,
  `InvalidOperationException`, `ArgumentException` on an empty segment, `JsonException`,
  `KeyNotFoundException`) as the typed `ProtocolException` with a generic message — closing an
  unhandled-exception path out of `UnpackAsync` and removing the header-vs-claims info leak.
- **Receive endpoint response oracle closed (#20, #28, #33).** Both ASP.NET Core HTTP receive overloads
  collapse every input- or handler-triggered failure to a single opaque `400` with an empty body,
  logging the reason server-side only. This closes the verbatim-`CryptoException` decryption /
  recipient-`kid` enumeration oracle (#20), the `Consistency`/`Resolution`/`UnsupportedDidMethod`
  exceptions that previously escaped to `500` + dev stack-trace (#28), and the echoed handler-bug
  detail (#33). The 202 success, 415, and 413 paths are unchanged; cancellation still propagates.
  Red-team hardening: the inline-callback overload now also wraps `onReceive` so a consumer callback
  that throws after a successful unpack returns 202 (logged) instead of escaping as a distinguishable
  `500` — removing an unpack-success oracle the uniform-400 path would otherwise leave. (A residual
  *timing* side-channel in the underlying decrypt path — held-vs-unheld recipient kid — is **not**
  closed by this change and is tracked as a follow-up.) A downstream DID-resolution timeout
  (`TaskCanceledException` with the request token not cancelled — e.g. a hung `did:webvh` host) also
  collapses to the uniform 400 rather than escaping as a `500`; only a genuine client abort propagates.
  **Operator note:** a receiver-side handler bug (FR-THR-04 `InvalidOperationException`) now returns
  `400` instead of `500` for response uniformity — it is logged at `Error` level, so alert on the
  `DidComm.AspNetCore` error log rather than on a 5xx rate for handler faults.
- **Thread-state store bounded (#21).** `InMemoryThreadStateStore` is now capped
  (`DefaultMaxEntries = 10_000`, overridable via ctor) with approximate-LRU eviction, converting an
  unbounded-growth memory-exhaustion DoS (attacker-chosen, unauthenticated `thid`/`pthid`) into a
  few-MB bound. Secure-by-default — no DI wiring change. Red-team hardening: eviction is **single-flight**
  (an `Interlocked` guard) so N concurrent inserters over the cap can't each run a full O(n log n)
  snapshot-sort — closing a CPU-amplification DoS. (A residual cascade-guard-reset-via-eviction vector,
  costing the attacker ~`cap` messages per suppressed report, is tracked as a follow-up alongside #29.)

### Changed — delegate the envelope layer to DataProofsDotnet.Jose (`feat/delegate-jose-dataproofs`)

didcomm-dotnet no longer carries its own cryptography or JWE/JWS assembly. The entire `Crypto/`
folder and almost all of `Jose/` were deleted; envelope build/parse is delegated to
**`DataProofsDotnet.Jose` (1.0.1)** which runs on **`NetCrypto` (1.1.0)**. This realizes the
foundation-stack split (architectural-path §5.6) and removes ~435+ lines of hand-rolled,
security-sensitive crypto from this repo. Suite: **475 unit + 99 interop** green under `warnaserror`;
all DIDComm v2.1 Appendix C byte-equivalence vectors (anoncrypt/authcrypt/signed, every curve) pass.

- **Crypto substrate → NetCrypto.** AES-256-CBC-HMAC-SHA512 / A256GCM / XC20P, RFC 3394 A256KW, the
  ECDH-ES & ECDH-1PU (`Ze‖Zs`) Concat-KDF, raw ECDH, sign/verify, JWK conversion, base64url, and the
  RFC 7518 §6.2.2 on-curve `epk` validation are all NetCrypto's now (byte-identical to the deleted
  in-repo versions, proven by the interop vectors).
- **JOSE composition → DataProofsDotnet.Jose.** Multi-recipient `JweBuilder`/`JweParser`
  (ECDH-ES+A256KW anoncrypt, ECDH-1PU+A256KW authcrypt, single shared `epk`, apu/apv binding,
  single-curve enforcement, authcrypt-pins-A256CBC) and `JwsBuilder`/`JwsParser` (Flattened/General,
  compact) are delegated. DidComm's internal secret/sender lookups now directly satisfy DataProofs'
  `IJweRecipientKeyResolver`/`IJweSenderKeyResolver` (no adapter), and its `Jwk` is the shared
  `DataProofsDotnet.Jose.Jwk`.
- **`NetDid.Core` → 2.0.x, resolution-only.** All crypto primitives moved out of net-did in 2.0.0;
  the `NetDidKeyService` adapter now materializes Multikey verification methods via
  `JwkConversion.FromMultikey`, and the optional `IKeyStore`→`ISecretsResolver` bridge re-targets
  `NetCrypto.IKeyStore`. The direct `NSec.Cryptography` dependency is dropped.
- **DIDComm-specific rules stay in didcomm.** Legal compositions + media-type pinning (FR-ENV),
  recipient defaulting (FR-ENC-19), curve negotiation (FR-ENC-04), and the addressing-consistency
  checks (FR-CONSIST-01..06) remain in `EnvelopeWriter`/`EnvelopeReader`/`DidCommClient`. Because the
  delegated JWS parser returns verified-payload bytes (not a DIDComm message), FR-CONSIST-03
  (signed `from` ↔ signer kid) and FR-SIG-06 (inner signed JWM must carry `to`) are now enforced in
  `EnvelopeReader` against the deserialized inner message.
- **Pack APIs are now async** end-to-end (`EnvelopeWriter.PackSignedAsync`/`PackEncryptedAsync`,
  `ForwardWrapper.WrapAsync`, `FromPriorBuilder.BuildAsync`) since DataProofs signs through an async
  `ISigner`. `from_prior` is built via DataProofs' compact JWS.
- **Upstream fixes landed** from this integration: NetCrypto 1.1.0 (on-curve guarantee in
  `ExtractPublicKey`, `IKeyStore.DeriveSharedSecretAsync`, `Base64Url`, AEAD size constants —
  crypto-dotnet #10/#11/#12); DataProofsDotnet.Jose 1.0.1 (surface the verified JWS signer kid from
  the unprotected header for DIDComm v2.1 conformance — dataproofs-dotnet #10).
- **Receive-side defense-in-depth:** a verified JWS that surfaces **no** signer kid is now rejected
  (`CryptoException`) rather than reported authenticated — the `from`↔signer binding (FR-CONSIST-03)
  never silently no-ops, independent of the delegated parser's behavior.

### Breaking

- `FromPriorBuilder.Build(FromPriorClaims, Jwk)` → **`BuildAsync(FromPriorClaims, Jwk, CancellationToken)`**
  (method name **and** return type change). DataProofs signs through an async `ISigner`, so direct
  callers must `await` the new method. The primary `DidCommClient` pack/unpack facade is unchanged
  (it was already async); `ForwardWrapper.WrapAsync` is `internal`.
- **ES512 / P-521 *signing* dropped.** The old `KeyTypeMapper` mapped `P-521 → ES512` for JWS; the
  delegated signer scopes JWS to EdDSA / ES256 / ES384 / ES256K, so a P-521 signer JWK now raises
  `NotSupportedException`. **Not a DIDComm v2.1 regression** — signed messages require only
  EdDSA / ES256 / ES256K (none were tested with P-521), and P-521 *key agreement*
  (anoncrypt/authcrypt) is unaffected and still tested.

### Notes

- The deleted in-repo crypto unit tests (AEAD tag, off-curve `epk`, key-wrap, malformed JWK
  negatives) are carried by the upstream suites — NetCrypto (`EcPointValidatorTests`, the
  AES-GCM / AES-CBC-HMAC / XChaCha20-Poly1305 / AES-KW cipher tests, `JwkConverterTests`,
  input-validation fuzz) and DataProofsDotnet.Jose (`Parity/*`, `Conformance/NegativePathTests`,
  `Hardening/*`). This repo's 99 interop **vectors** backstop byte-equivalence; the primitive
  negatives live where the primitives now live.
- **Follow-up (not in this PR):** enabling a package lockfile (`packages.lock.json`,
  `RestorePackagesWithLockFile`) to pin the resolved transitive graph is worthwhile now that the
  security-critical core lives in transitive packages, but it currently conflicts with this repo's
  `Directory.Build.props`-level `TargetFramework` (NuGet's lock-file restore evaluates an empty TF →
  NETSDK1013). It needs `TargetFramework` moved into each csproj first; tracked separately.

## [0.1.0-preview.1]

### Security — full-codebase audit remediation (`feat/security-hardening`)

A defensive security + correctness review of the whole library, fixed in priority order. Suite:
**606 unit + 99 interop** under `warnaserror` (was 578 + 96). No public API removed; several
receive-side paths now reject inputs they previously tolerated (all spec-conformant tightenings).

- **WebSocket SSRF (HIGH):** the WS transport now pins each connection to a guard-vetted IP via a
  `SocketsHttpHandler.ConnectCallback` (mirroring the HTTP transport), closing a DNS-rebinding TOCTOU
  and the `ResolveDnsNames=false` bypass. TLS still validates against the original host.
- **SSRF classifier:** `OutboundEndpointGuard` now blocks IPv4 embedded in IPv6 alternate forms
  (IPv4-compatible `::/96`, 6to4 `2002::/16`, NAT64 `64:ff9b::/96`) and Teredo `2001::/32`, and
  normalizes trailing-dot hosts.
- **JWE receive validation:** reject any `enc` outside `{A256CBC-HS512, A256GCM, XC20P}` and pin
  authcrypt (ECDH-1PU) to `A256CBC-HS512` (FR-ENC-09); enforce `crit` (RFC 7516) and bind `apu` to
  `skid` with the FR-ENC-17 `apu` fallback (FR-ENC-14); require well-typed members so malformed JWEs
  yield `MalformedMessageException`.
- **JSON:** reject duplicate member names (`AllowDuplicateProperties = false`) across the message
  model and every raw JWE/JWS/`from_prior`/forward parse — closes a parser-differential vector.
- **Keys:** reject OKP (Ed25519/X25519) JWKs whose `x` is not exactly 32 bytes before import.
- **DID rotation:** `from_prior` is accepted only on a sender-authenticated envelope (authcrypt or
  signed), not plain anoncrypt, and is checked for `exp`/`nbf` freshness (FR-ROT-03/05 reconciled in
  the PRD). `UnpackResult` documents that `Message.From` is trustworthy only when authenticated.
- **JWS:** enforce `crit`/`b64` (RFC 7515/7797); normalize malformed-input errors.
- **Correctness/defense-in-depth:** control-char + length validation on `from`/`to`; forward
  `body.next` is a bare DID; `SyncSecretsAdapter` avoids a sync-over-async deadlock; canonical
  protocol-version parsing; UTF-8 `apv` hashing; lowercase UUID ids; Discover Features disclosure
  de-duplication; ephemeral-key zeroization on early-throw.

### Added — Phase 6.3 (Out-of-Band 2.0 + NuGet release pipeline)

Closes PRD §10.3 **FR-OOB-01..05** (the last spec built-in protocol) and **NFR-08**
(release pipeline). Suite: **578 unit + 96 interop** under `warnaserror` (was 559 + 93).

- **Out-of-Band 2.0** (`Protocols/OutOfBand/`):
    - `OutOfBand` static API — `CreateInvitation(from, goal?, goalCode?, accept?, attachments?, id?)`
      (goal_code / goal / accept written into `body`, FR-OOB-01); `ToUrl(invitation, baseUrl)` /
      `FromUrl(url)` for the `?_oob=<base64url(plaintext-jwm)>` form (FR-OOB-02), reusing the
      existing compact plaintext serializer + `Base64Url`; `FromPlaintext(json)`; short-form
      `ToShortUrl(baseUrl, oobId)` / `TryGetShortFormId(...)` (FR-OOB-04); `AddWebRedirect(...)` /
      `ReadWebRedirect(...)` for the `web_redirect` block (FR-OOB-05).
    - `OutOfBandInvitation` — read-only projection (`Id`, `From`, `Goal`, `GoalCode`, `Accept`,
      `Attachments`); the invitation `id` is the recipient's response `pthid` (FR-OOB-03).
    - `WebRedirect` record; `IOobInvitationStore` + `InMemoryOobInvitationStore` for the short-form.
    - **No dispatcher handler** — an invitation arrives out of band (URL/QR) and bootstraps a
      follow-up protocol, so there is nothing to route through `ProtocolDispatcher`.
    - **Encoding (FR-OOB-02):** `ToUrl` emits a canonical, reproducible payload — keys sorted at
      every level (the deterministic JSON writer) and the `typ` header dropped — so the same
      invitation always yields the same URL. That key order still differs from the spec's
      illustrative example, so it is not a byte-for-byte reproduction of that example; interop is
      proven instead by decoding the spec's own example fixture + round-trip equality.
- **ASP.NET Core**: `MapDidCommOobEndpoint(pattern, store)` — HTTP GET serving the short-form
  invitation by `_oobid` (200 `application/didcomm-plain+json` / 404 / 400) (FR-OOB-04).
- **Cookbook Section V** (`Section_V_OutOfBandInvitation`) — build → URL → decode round trip,
  short-form retrieval, and `pthid` correlation.
- **NuGet release pipeline (NFR-08)**: `.github/workflows/release.yml` packs + pushes all six
  packages (with symbols + SourceLink) to NuGet.org on a `v*` tag, with the version derived from
  the tag; `Directory.Build.targets` packs the repo `README.md` into each package (fixes NU5039);
  README gains an Install section. _(Publishing requires a `NUGET_API_KEY` repo secret + a pushed
  tag — neither is done here.)_
- **Review hardening**: OOB URL parsing tolerates a trailing `#fragment` (decodes instead of
  failing base64url); the release job is gated behind a `nuget-release` GitHub Environment — add
  required reviewers to enforce manual approval before the irreversible publish.

### Fixed — PR #11 review casualties (carried over from the abandoned local fix branch)

The independent `113608d` that landed on `main` dropped three deliverables the abandoned local
PR #11 branch carried; restored here (behavior was already correct on `main`):

- `DidCommBuilder.EnableTracing` now registers `IOptions<TraceOptions>` (via `TryAddSingleton`)
  symmetrically with `ProblemReportOptions`, so consumers using the Options pattern can resolve it.
- Restored the `Read_helpers_return_null_or_empty_on_non_string_body_fields` regression test
  (adapted to main's index-preserving `ReadArgs` — non-string entries become `""`).
- Restored the `<remarks>` XML doc on `TraceOptions.AllowedReportingUris` documenting canonical
  `AbsoluteUri` allowlist matching.

### Added — Phase 6.2c (Report Problem 2.0 + Trace 2.0 off-by-default)

Closes PRD §12 Phase 6 partially: **FR-PROTO-07, FR-PROTO-08, FR-PROTO-09,
FR-PROTO-10, FR-PROTO-11, FR-PROTO-11a**. Last of the three 6.2 sub-PRs —
**every spec built-in protocol** is now shipped (TrustPing / DiscoverFeatures /
Empty / ProblemReport / Trace).

- **Report Problem 2.0** (`Protocols/ProblemReport/`):
    - `ProblemCode` — typed taxonomy parser for `sorter.scope.descriptor[.sub…]`;
      `IsError` / `IsWarning` / `IsProtocolScoped` / `IsMessageScoped` flags;
      `StartsWith(prefix)` implements FR-PROTO-08 structural-prefix matching.
    - `CommentInterpolator` — FR-PROTO-07 `{n}` interpolation, 1-based; missing
      args render as `?`; unreferenced extras appended in a `[extra: …]` block;
      `{{` / `}}` escape to literal braces.
    - `ProblemReport` static API — `Create(...)` / `Escalate(...)` /
      `ReadCode(...)` / `ReadComment(...)` / `ReadArgs(...)` / `RenderComment(...)`.
      The single message type (`…/report-problem/2.0/problem-report`) plus the
      cascade-stop code (`e.p.req.max-errors-exceeded`).
    - `ProblemReportOptions.CascadeThreshold = 5` (matches `sicpa-dlab/didcomm-python`
      per locked decision).
    - `ProblemReportHandler` — increments `ThreadState.ErrorCount` on the
      **failing thread (pthid)** for inbound errors, emits exactly one
      `e.p.req.max-errors-exceeded` reply on threshold breach, then silently
      ignores subsequent reports on the same `pthid` (FR-PROTO-10).
- **Trace 2.0 — off-by-default** (`Protocols/Trace/`):
    - `Trace` — protocol/header constants (`trace` header, `report_uri` / `trace_id` members).
    - `TraceOptions.Validate()` — `Enabled = true` REQUIRES at least one entry in
      `AllowedReportingUris` per FR-PROTO-11a's "explicitly configured safeguards";
      throws `InvalidOperationException` at startup otherwise.
    - `TraceObserver.ShouldReport(...)` — pure decision surface: returns `false`
      unconditionally when the operator hasn't opted in; otherwise returns the
      validated absolute report URI (allowlist gate + absolute-URI check).
      HTTP POSTing is left to a future integration; the off-by-default guarantee
      is the spec-mandated piece.
    - `TraceObserver.BuildReportBody(...)` — minimal observed-metadata body for
      consumers wiring up the POST themselves.
- **`ProtocolContext.Threads`** — new field exposes `IThreadStateStore` to
  handlers (so ProblemReport can resolve the failing-thread state by `pthid`,
  not the report's own thread). Found via an integration test that exposed
  a real bug in the original design.
- **DI** (`DidCommBuilder`):
    - `AddBuiltInProtocols()` now also registers `ProblemReportHandler` + binds
      `ProblemReportOptions` via the standard Options pattern.
    - `EnableTracing(Action<TraceOptions>)` — opt-in only; validates immediately
      and registers `TraceOptions` as a singleton.
- **Cookbook Section U** (Report Problem) — parses a structured code,
  interpolates a `{n}` comment, escalates a warning to an error with preserved
  scope, and trips the cascade guard in 3 packed messages. 14 sections total.
- **Tests** (+57 unit, +5 interop, total **543 unit + 93 interop**):
    - 18 `ProblemCode` cases (taxonomy parse, malformed-input rejection,
      structural-prefix `StartsWith`, sorter/scope flags).
    - 8 `CommentInterpolator` cases (spec example, missing args → `?`, extras
      appended, brace escapes, unclosed-brace tolerance, non-positional
      placeholder passthrough).
    - 7 `ProblemReport` API tests.
    - 7 `ProblemReportHandler` cases (incl. cascade trip + exactly-once + silent-after).
    - 4 `TraceOptions` validation cases.
    - 7 `TraceObserver` cases (default-off, allowlist match/miss, malformed/missing
      header, non-absolute URI, body-builder metadata).
    - 5 DI integration tests (incl. end-to-end cascade-guard trip over real
      did:peer pack/unpack/dispatch).

### Fixed (Phase 6.2c review)

Addresses 15 review findings on the Phase 6.2c surface (see PR #11 inline comments).

- **`ProblemReportHandler.HandleAsync` rewritten for FR-PROTO-10 correctness.**
    - Atomic increment + threshold check + trip decision under a per-`ThreadState` lock — concurrent
      inbound error reports on the same `pthid` (singleton handler fed by parallel transports) now
      yield exactly one cascade-stop instead of racing on `ErrorCount++`.
    - The cascade-trip is deferred when the threshold-tripping report is unrepliable
      (anoncrypt — no `from`/`to`); without the deferral, the counter landed past the threshold and
      the silent-ignore branch swallowed every subsequent report, so the cascade-stop was never
      emitted (FR-PROTO-10 silently broken).
    - Past-trip reports are now truly silent: short-circuit happens BEFORE the increment + Information
      log, eliminating the unbounded counter growth and per-message log-flood that "silently
      ignores" used to allow.
    - Threshold semantics simplified from `> CascadeThreshold + 1` / `== CascadeThreshold + 1` to a
      single `> CascadeThreshold` decision driven by `ThreadState.MaxErrorsNoticeSent` (new flag).
- **`ProblemReportOptions.Validate()`** — added; the handler ctor calls it so misconfigured
  thresholds (negative, `int.MaxValue`) fail loudly at DI resolution rather than silently
  disabling the cascade guard.
- **`ProblemReport.ReadCode` / `ReadComment` / `ReadArgs`** — pattern-match `JsonValue` instead of
  unconditionally calling `JsonNode.AsValue()`. A malformed peer sending
  `{"code": {"x": 1}}` (etc.) no longer crashes the handler with
  `InvalidOperationException`. `ReadArgs` additionally preserves null/non-string entries as empty
  strings so 1-based positional indexes stay aligned with the on-wire array.
- **`CommentInterpolator.Interpolate`** —
    - Brace-collapse (`{{` → `{`, `}}` → `}`) now runs uniformly, even when `args` is null/empty:
      identical templates render identically regardless of args presence.
    - `{0}` (or any non-positive numeric placeholder) renders as `?` per the 1-based spec, instead
      of leaking the placeholder text + dumping the unused arg into `[extra: …]`.
- **`TraceObserver.ShouldReport`** — three correctness/security fixes:
    - **Scheme guard**: reject any URI whose scheme is not `http`/`https` (previously `file://`,
      `javascript:`, `gopher:`, `data:`, `ftp:` all admitted if allowlisted).
    - **SSRF defense-in-depth**: reject IP-literal hosts that classify as loopback / private /
      link-local / metadata via `OutboundEndpointGuard.IsPrivateOrReserved` — wires up the
      defense-in-depth the docstring already promised.
    - **Allowlist normalisation**: compare both sides via `Uri.AbsoluteUri`. Trailing-slash,
      default-port, percent-encoding, and host-case differences no longer silently drop legitimate
      matches.
- **`ProtocolDispatcher`** — accepts an optional `TraceOptions` (DI-injected when
  `EnableTracing` was called) and invokes `TraceObserver.ShouldReport` on every inbound message.
  An authorised trace intent is logged at `Information`; HTTP POST integration remains deferred,
  but the decision logic is no longer dead in production.
- **`DidCommBuilder.EnableTracing`** uses `TryAddSingleton` so repeated calls are idempotent
  (first-call-wins) instead of silently shadowing prior `TraceOptions`.
- **`ProtocolHandlerRegistry` factory** in `DidCommBuilder` now logs a warning when two
  `IProtocolHandler` registrations target the same PIURI (most commonly: `AddBuiltInProtocols()`
  silently overriding a host's custom handler registered earlier). Last-write-wins semantics are
  preserved — but the override is now observable.

### Breaking (Phase 6.2c review)

- **`ProtocolContext` positional ctor signature.** `Threads` was previously inserted at index 2
  (between `Thread` and `Client`); it is now appended at the end of the positional list so
  callers using the prior positional shape `new ProtocolContext(received, thread, client,
options)` keep compiling. Downstream consumers that built `ProtocolContext` positionally
  against the dd204b0 commit of this PR will need to swap to the new order:
  `new ProtocolContext(received, thread, client, options, threads)`. Callers using named
  parameters are unaffected.

### Added — Phase 6.2b (Discover Features 2.0 + custom-handler cookbook)

Closes PRD §12 Phase 6 partially: **FR-PROTO-05** (Discover Features 2.0 with the
`max_receive_bytes` constraint), and demonstrates **FR-PROTO-03**'s extension
point with a one-file custom `IProtocolHandler` (`lets_do_lunch`).

- **Discover Features 2.0** (`Protocols/DiscoverFeatures/`):
    - `DiscoverFeatures` static API — `CreateQuery(from, to, queries…)`,
      `CreateDisclose(from, to, thid, disclosures…)`, `ReadQueries(msg)`,
      `ReadDisclosures(msg)`; spec constants for the four `feature-type` values
        - the `max_receive_bytes` constraint id.
    - `FeatureQuery` / `FeatureDisclosure` records (spec wire shape:
      `feature-type` is hyphenated, `value` is omitted when null).
    - `DiscoverFeaturesHandler` — routes each query to the matching
      `IFeatureProvider`, concatenates disclosures, emits a single threaded
      `disclose` reply. Unrecognized `feature-type`s are silently skipped;
      empty disclosures are meaningful (not "unsupported").
    - `FeatureMatch` — spec-conformant trailing-`*` prefix matching;
      bare `*` matches everything; otherwise exact.
    - `IFeatureProvider` extension point + two built-in providers:
      `ProtocolFeatureProvider` (reflects the registry) and
      `MaxReceiveBytesConstraintProvider` (advertises `DidCommOptions.MaxReceiveBytes`).
- **DI** (`DidCommBuilder`):
    - `AddBuiltInProtocols()` now also registers `DiscoverFeaturesHandler` + both
      default `IFeatureProvider`s (via `TryAddEnumerable` so re-registration is a no-op).
    - New `AddFeatureProvider<T>()` for consumers that want to expose goal-codes,
      custom headers, or app-specific constraints.
- **Cookbook**:
    - **Section T** (Discover Features) — Alice queries `https://didcomm.org/*` +
      `constraint:max_receive_bytes`; Bob's disclose reply lists all 3 built-in
      PIURIs + the byte-cap value; narrator confirms `thid` matches.
    - **Section X** (Custom IProtocolHandler) — a one-file `lets_do_lunch/1.0`
      protocol shows the FR-PROTO-03 extension point: define a handler, register
      via `ProtocolHandlerRegistry.Register(...)` (or `b.AddProtocol<T>()`), run
      through the dispatcher.
- **Tests** (+27 unit, +3 interop, total **486 unit + 88 interop** green):
    - 8 `FeatureMatch` cases covering wildcard / prefix / exact / empty.
    - 7 `DiscoverFeatures` static-API tests (CreateQuery/Disclose round-trip,
      `feature-type` JSON name, optional `value` omission, malformed-entry tolerance).
    - 9 `DiscoverFeaturesHandler` cases (wildcard match, prefix filter,
      `max_receive_bytes` reflection of `DidCommOptions`, case-insensitive
      `feature-type` matching, unrecognized type silently skipped, disclose-typed
      inbound is terminal, anonymous query rejected).
    - 3 DI-graph integration tests (`AddBuiltInProtocols` registers handler +
      providers; full pack→unpack→dispatch round-trip lists all built-ins +
      `max_receive_bytes`; `AddFeatureProvider<T>()` appends consumer providers).

### Added — Phase 6.2a (Protocol handler registry + TrustPing + Empty)

Closes PRD §12 Phase 6 partially: **FR-PROTO-03..04, FR-PROTO-06**, runtime dispatch
for FR-PROTO-01/02 (parser shipped earlier), and enforces **FR-THR-04 rule 2** at
the dispatcher boundary plus defensive **rule 3** drop of peer rule-2 violations.

- **`IProtocolHandler` + dispatch framework** (`DidComm.Core/Protocols/`):
    - `IProtocolHandler` — `ProtocolUri` + `HandleAsync(Message, ProtocolContext, ct)` returning an optional reply.
    - `ProtocolIdentifier` — PIURI parser (`<doc-uri>/<protocol-name>/<major.minor>`) mirroring `MessageTypeUri`.
    - `ProtocolHandlerRegistry` — case- and punctuation-insensitive PIURI lookup (FR-PROTO-01) with the older-minor-wins interop floor (FR-PROTO-02). Thread-safe; intended as a singleton.
    - `ProtocolContext` — wraps `UnpackResult`, `ThreadState`, optional `DidCommClient`, and `DidCommOptions`.
    - `ProtocolDispatcher` — orchestrates resolution → pre-filter (FR-THR-04 rule 3) → handler invocation → reply safety check (`AckLoopGuard.IsSafeToSend`, FR-THR-04 rule 2) → `DispatchOutcome` (NoHandler / NoReply / ReplyProduced / DroppedAsAckLoop).
- **Trust Ping 2.0** (`Protocols/TrustPing/`):
    - `TrustPing.CreatePing(from, to, responseRequested=true)`, `TrustPing.CreateResponse(ping)`, `TrustPing.IsResponseRequested(ping)` (default `true` when the body member is missing or non-boolean — matches `sicpa-dlab/didcomm-python`).
    - `TrustPingHandler` auto-replies `ping-response` with `thid = ping.id`; suppresses reply when `response_requested = false`; never replies to a `ping-response` (terminal leaf).
- **Empty 1.0** (`Protocols/Empty/`):
    - `EmptyProtocol` constants + `EmptyHandler` (returns `null` reply — Empty is header-only).
    - `Message.Empty()` static factory: pre-seeded `MessageBuilder` for the Empty 1.0 type.
- **`ThreadState.ErrorCount`** added for the FR-PROTO-10 cascade guard (Phase 6.2c wires it).
- **DI** (`DidCommBuilder`):
    - `AddProtocol<T>()` registers an `IProtocolHandler` singleton.
    - `AddBuiltInProtocols()` registers TrustPing + Empty.
    - Registry built via a DI factory that walks every `IProtocolHandler` so order of `AddProtocol` calls doesn't matter.
- **ASP.NET Core endpoint overloads** (`DidCommEndpointRouteBuilderExtensions`):
    - `MapDidCommEndpoint(pattern)` — parameterless overload that dispatches each inbound through the registry; HTTP replies are LOGGED, not returned (FR-TRN-10 one-way).
    - `MapDidCommWebSocket(pattern)` — same, with optional same-socket reply when `DidCommReceiveOptions.AllowSameSocketReplies = true` (default `false` per FR-TRN-10).
- **Cookbook**:
    - **Section S** (Trust Ping liveness) — Alice pings Bob; the dispatcher resolves `TrustPingHandler` and produces a reply with `thid = ping.id`; the response round-trips back.
    - **Section W** (Empty 1.0 ACK) — Bob ACKs a prior message with `Message.Empty().WithAck(...)`; narration shows `AckLoopGuard.IsPureAck = true` and `IsSafeToSend = true`.
- **Tests** (+26 unit, +2 interop, total **461 unit + 77 interop** green):
    - 10 `ProtocolHandlerRegistry` tests (exact match, case/punctuation, older-minor-wins, malformed input, re-registration).
    - 9 `ProtocolDispatcher` tests (no-handler, null/non-null reply, FR-THR-04 rules 2 + 3, thread-state passthrough, full TrustPing + Empty round-trips).
    - 6 `TrustPing` static-API tests (`response_requested` parsing, response construction, validation).
    - 4 `TrustPingHandler` tests (auto-reply, suppression, terminal-leaf, PIURI).
    - 3 `EmptyHandler` tests (null reply, `Message.Empty()` factory, PIURI).
    - 1 `ThreadState.ErrorCount` per-thread isolation test.
    - 2 DI-graph integration tests (`AddBuiltInProtocols` populates the registry; end-to-end pack→unpack→dispatch round-trip for Trust Ping over real `did:peer` identities).

### Security

- **SSRF hardening for outbound sends** (`DidComm.Core/Transports/OutboundEndpointGuard.cs`,
  `DidComm.Core/Transports/OutboundEndpointPolicy.cs`). A recipient's DID-document
  `serviceEndpoint` host is attacker-influenced, so `DidCommClient.SendAsync` now rejects an
  endpoint **resolved from a DID** that targets a private, loopback, link-local, unique-local,
  CGNAT, or cloud-metadata (`169.254.169.254`) address before dispatching the packed envelope.
  IPv4-mapped IPv6 addresses are unwrapped first so they cannot dodge the IPv4 rules.
    - The HTTP transport enforces the same policy at TCP connect time via
      `SocketsHttpHandler.ConnectCallback`, pinning every connection — including each manually
      followed 307 redirect — to a vetted IP, which also defeats redirect-to-internal and DNS
      rebinding.
    - The WebSocket transport vets the host on its default connect path.
    - Tunable via `DidCommOptions.OutboundEndpointPolicy` and each transport's
      `OutboundEndpointPolicy` (`BlockPrivateNetworks` defaults to `true`, plus `AllowedHosts` and
      `ResolveDnsNames`). A caller-supplied `SendOptions.ServiceEndpointOverride` is trusted and
      intentionally bypasses the gate.

### Added — Phase 6.1 (Threading, ACKs, Profiles, i18n)

Closes PRD §12 Phase 6 partially: FR-THR-01..04 (the message-layer surface),
FR-PROF-01/02, FR-I18N-01..03. Lays the message-model and DI foundation the
Phase 6.2 protocol handlers need to honor `please_ack` / `ack` and to localize
human-readable strings per thread.

- **`Message` plaintext model** (`DidComm.Core/Messages/Message.cs`):
    - `PleaseAck` (`please_ack`) — array of message ids to be acknowledged;
      empty string `""` is the spec sentinel for "this current message".
    - `Ack` (`ack`) — array of acknowledged message ids, oldest→newest.
    - `Lang` (`lang`) — IANA language tag for the message's protocol-defined
      human-readable fields (FR-I18N-03).
    - `AcceptLang` (`accept-lang`) — ranked IANA codes the sender prefers on this
      thread (FR-I18N-01/02). Spec name is hyphenated, not snake_case.
    - `Validate()` enforces FR-THR-03 / FR-I18N-01/03 character constraints (with
      the empty-string sentinel allowed in `please_ack`).
- **`MessageBuilder`** gained `WithPleaseAck(...)`, `WithAck(...)`,
  `WithLang(...)`, `WithAcceptLang(...)`.
- **Threading types** (`DidComm.Core/Threading/`):
    - `ThreadState` (`Thid`, `AcceptLang`) — per-thread mutable state record.
    - `IThreadStateStore` + `InMemoryThreadStateStore` — thread-safe
      `ConcurrentDictionary`-backed store. Registered as a singleton in
      `DidCommBuilder` so FR-I18N-02 thread-scoped `accept-lang` persists across
      pack/unpack while concurrent threads stay isolated.
    - `AckLoopGuard` — pure predicates (`IsPureAck`, `RequestsAck`, `IsSafeToSend`)
      that future protocol handlers consume to enforce FR-THR-04 (rule 2 today,
      rules 1/3 wired in Phase 6.2 when handlers dispatch replies).
- **Profile negotiation** (`DidComm.Core/Profiles/`):
    - `Profiles` constants (`DidCommV2`, `DidCommAip1`, `DidCommAip2Env10`).
    - `ProfileNegotiator.Choose(...)` / `IsSupported(...)` — case-insensitive,
      whitespace-tolerant peer-`accept[]` selection (FR-PROF-01/02).
- **Cookbook**:
    - **Section M** (Threading & ACKs) — Alice asks for an ACK with
      `WithPleaseAck`; Bob replies with a thread-correlated pure ACK; the
      `IsSafeToSend` loop-trap is demonstrated.
    - **Section BB** (Profiles & i18n) — `ProfileNegotiator` picks `didcomm/v2`
      from a peer's `accept[]`; the chess example flows with `lang=fr` +
      `accept-lang=[fr,en]`; thread-state isolation is asserted in narration.
- **Tests** (+32, total 396 unit + 69 interop green):
    - 9 round-trip / validation tests for the four new `Message` fields.
    - 6 `InMemoryThreadStateStore` tests including the FR-I18N-02 cross-thread
      isolation assertion.
    - 8 `AckLoopGuard` tests covering pure-ACK detection and FR-THR-04 rule 2.
    - 9 `ProfileNegotiator` tests (overlap, case, whitespace, null/empty edge cases).

### Added — Phase 5 (Transports)

Closes PRD §12 Phase 5: FR-TRN-01..12 + the FR-API-06 transport (413) path.
Ships the HTTP and WebSocket transport bindings plus the ASP.NET Core receive
endpoint, so a packed envelope finally turns into bytes on the wire and the
matching server side accepts them.

- **Core transport abstractions** (`DidComm.Core/Transports/`):
    - `IDidCommTransport` — scheme, `CanHandle(Uri)`, `SendAsync(...)` (FR-TRN-01).
    - `ITransportRouter` + `TransportRouter` (default impl) — dispatches by URI
      scheme; throws `TransportException` when no transport handles a scheme.
    - `TransportRequest`, `TransportResult` records (FR-TRN-02/03/05).
    - `SendOptions` (mirrors `PackEncryptedOptions` + `ServiceEndpointOverride`)
      and `SendResult` (bundles `PackEncryptedResult` + transport outcome +
      endpoint used).
- **Facade** (`DidCommClient`):
    - `Task<SendResult> SendAsync(Message, SendOptions, CancellationToken)` —
      packs with `Forward = true` unless `ServiceEndpointOverride` is set, then
      dispatches via the registered `ITransportRouter`. New 5-arg public ctor
      accepting `(secrets, keyService, serviceResolver, transportRouter, options)`.
    - DI registration in `DidCommServiceCollectionExtensions` now passes
      `ITransportRouter` through to the facade singleton and idempotently registers
      the default router.
- **Exception taxonomy** — `DidComm.Exceptions.TransportException` (derived
  from `DidCommException`) fills the FR-API-07 gap for transport-level
  failures; carries optional `HttpStatusCode` + `Scheme`.
- **HTTPS sender** — new project `DidComm.Transports.Http`:
    - `HttpDidCommTransport` — `IHttpClientFactory`-backed POST; 2xx ⇒ accepted
      (FR-TRN-05); 307 followed manually with a `MaxRedirectHops` cap
      (FR-TRN-06); 301/308 + non-2xx surfaced as `TransportException`; rebuilds
      the request on every Polly retry so `HttpClient` doesn't reject the resend.
    - `HttpTransportOptions` — `RequestTimeout`, `MaxRetryAttempts`,
      `RetryBaseDelay`, `CircuitBreakerFailureThreshold`,
      `CircuitBreakerOpenDuration`, `AllowedSchemes` (default `{"https"}`),
      `MaxRedirectHops`.
    - `HttpResiliencePipelineFactory` — Polly v8 pipeline (retry + circuit
      breaker + per-attempt timeout), driven entirely by the options shape;
      skips the retry strategy when `MaxRetryAttempts == 0`.
    - `HttpDidCommBuilderExtensions.UseHttpTransport(...)` — DI extension that
      registers the transport, the named `"didcomm"` HTTP client, the router,
      and disables auto-redirect on the handler so the transport can enforce
      FR-TRN-06.
- **WebSocket sender** — new project `DidComm.Transports.WebSocket`:
    - `WebSocketDidCommTransport` — one binary message per packed envelope
      (FR-TRN-09); end-of-message flag set on the last fragment; per-endpoint
      connection pool keyed by `Authority + Path`; Polly-driven exponential
      reconnect (1s / 30s / 0.5-jitter — DD-05 / FR-TRN-11); per-send timeout;
      dropped socket recycled on `SendFailed`; `IAsyncDisposable` cleans up on
      container shutdown.
    - `WebSocketTransportOptions` — connect/send timeouts, max reconnect
      attempts + base/max delay, allowed schemes (default `{"wss"}`),
      `WebSocketFactory` + `Connect` seams (used by the InteropTests +
      cookbook to point at a `Microsoft.AspNetCore.TestHost.TestServer` WS
      client without opening a real port).
    - `WebSocketLifecycleEventArgs` + `Lifecycle` event for FR-TRN-11
      observability (Connected / Disconnected / SendFailed / Reconnected).
    - `WebSocketDidCommBuilderExtensions.UseWebSocketTransport(...)`.
- **ASP.NET Core integration** — new project `DidComm.AspNetCore`
  (`<FrameworkReference Include="Microsoft.AspNetCore.App" />`, zero NuGet
  weight):
    - `MapDidCommEndpoint(IEndpointRouteBuilder, string, Func<UnpackResult, CancellationToken, Task>)`
      — minimal-API `POST` mapping (FR-TRN-07). Validates `Content-Type`
      against the configured accept list ⇒ 415 on mismatch; streams the body
      with a hard cap at `DidCommOptions.MaxReceiveBytes` ⇒ 413 (FR-API-06);
      unpacks via `DidCommClient.UnpackAsync`; dispatches to the inline
      receiver; returns 202. `MalformedMessageException` / `CryptoException`
      ⇒ 400; `TransportException` ⇒ 502.
    - `MapDidCommWebSocket(IEndpointRouteBuilder, string, Func<UnpackResult, CancellationToken, Task>)`
      — accepts WebSocket; loops `ReceiveAsync` until `EndOfMessage=true`
      (frame reassembly); honours `MaxReceiveBytes` and closes with 1009
      "Message Too Big" on overflow; one-way per FR-TRN-10.
    - `DidCommReceiveOptions` — per-endpoint accept-list (defaults cover the
      three DIDComm v2.1 media types).
- **DI plumbing** — `DidCommServiceCollectionExtensions` now auto-registers
  `TransportRouter` so DI hosts get the FR-TRN-01 dispatch surface for free
  the moment they call `.UseHttpTransport()` / `.UseWebSocketTransport()`.
  Hand-constructed clients still receive a clean `InvalidOperationException`
  on `SendAsync` when no router was supplied.

### Tests — Phase 5

- `DidComm.Core.Tests/Transports/TransportRouterTests` — scheme dispatch,
  case-insensitive match, no-handler → `TransportException` with the offending
  scheme, null-arg guards.
- `DidComm.Core.Tests/Transports/DidCommClientSendAsyncTests` — no-router
  refusal with an actionable message; empty-recipients refusal.
- `DidComm.InteropTests/Transports/HttpTransportSendTests` — 2xx accepted
  (Theory ×3), 307 followed to a final 2xx, 301/308 refused (Theory ×2), 500
  retried then surfaced as `TransportException`, scheme allow-list refusal,
  Content-Type propagation, case-insensitive `CanHandle`.
- `DidComm.InteropTests/Transports/AspNetCoreReceiveRoundTripTests` — full
  Alice→Bob HTTP round-trip via `TestServer.CreateHandler()`; 415, 413, 400
  negative cases.
- `DidComm.InteropTests/Transports/WebSocketTransportRoundTripTests` — full
  WS round-trip; explicit fragmented-send case (three frames coalesce into
  one envelope) to nail the FR-TRN-09 reassembly invariant; oversize message
  triggers a 1009 close per FR-API-06; `CanHandle` honours allow-list.

### Changed

- `Directory.Packages.props` adds `Polly` 8.5.0 (per the user-confirmed Phase 5
  resilience choice) and bumps `Microsoft.AspNetCore.TestHost` to the version
  that ships in the local SDK cache. `Microsoft.Extensions.Http` is no longer
  pinned in `DidComm.InteropTests` (it now arrives via the AspNet shared
  framework — `NU1510`).
- `DidComm.sln` — three new project entries (`DidComm.Transports.Http`,
  `DidComm.Transports.WebSocket`, `DidComm.AspNetCore`).

### Cookbook (samples/02-Cookbook)

- New sections `P` (send over a transport), `Q` (receive over HTTP — incl. the
  415 / 413 negative branches), `R` (receive / chat over WebSocket — incl.
  lifecycle-event subscription). All three host an in-process `TestServer` so
  the cookbook stays offline-safe; `dotnet run --project samples/02-Cookbook`
  exits 0 with the section banners printed.
- `samples/02-Cookbook/02-Cookbook.csproj` references the three new transport
  projects + `Microsoft.AspNetCore.TestHost` and brings in the AspNet shared
  framework.
- `samples/02-Cookbook/README.md` — table extended; expected-output sample
  refreshed with P/Q/R frames; section file list updated.

### Fixed (Phase 5 review)

- **WebSocket failures now surface as `TransportException` (FR-API-07).**
  `WebSocketDidCommTransport.SendAsync` wraps an exhausted reconnect budget (and
  any other transport-library failure) in `TransportException` — carrying the
  scheme + inner exception via a new ctor overload — instead of leaking the raw
  `WebSocketException` / `TimeoutException`. Caller-initiated cancellation still
  propagates as `OperationCanceledException`.
- **No socket leak on a failed WebSocket connect.** `GetOrConnectAsync` disposes
  the nascent socket when the connect handshake throws or times out (previously
  leaked once per failed reconnect attempt).
- **Malformed `Content-Type` → 415, not 500.** `MapDidCommEndpoint` now treats a
  `Content-Type` header that the media-type parser rejects as an unsupported
  type rather than letting a `FormatException` escape as a 500.
- **WebSocket lifecycle events `Disconnected` / `Reconnected` now fire.**
  `Disconnected` on a dropped socket and on `DisposeAsync`; `Reconnected` when a
  send succeeds after a prior failed attempt. Connect failures now also raise
  `SendFailed`.
- **Polly circuit-breaker no longer throws when `CircuitBreakerFailureThreshold`
  is below 2** — clamped up to Polly's `MinimumThroughput` floor.
- **Per-endpoint WebSocket connect locks** replace the single global semaphore so
  connecting to one endpoint no longer blocks connects to another.
- Removed the unused `WebSocketTransportOptions.Clock` knob and a dead
  `TransportException → 502` branch on the HTTP receive path; the WebSocket
  oversize close now uses `WebSocketCloseStatus.MessageTooBig` instead of a magic
  `1009` cast; `DidCommReceiveOptions` reuses the `DidCommMediaTypes` constants.
- WebSocket round-trip tests and Cookbook section R replaced fixed `Task.Delay`
  drains with bounded polling to remove timing flakiness.

### Added — Phase 4 (Routing & Mediation)

Closes PRD §12 Phase 4: FR-ROUTE-01..08. Sender-side `forward` wrapping,
mediator-side processing, and the conformant `serviceEndpoint` object /
array-of-objects parser with FR-ROUTE-04 mediator-as-DID-endpoint expansion.

- **Forward message** (`Protocols/Routing/`):
    - `ForwardConstants` — `ProtocolIdentifier`
      (`https://didcomm.org/routing/2.0`), `ForwardTypeUri`
      (`…/routing/2.0/forward`), `PayloadMediaType` (alias for
      `application/didcomm-encrypted+json`).
    - `ForwardMessage.Create(mediator, next, packedPayloads, idGenerator?, expiresTimeEpochSeconds?)`
      builds a `Message` with `Type = forward`, `Body.next`, and one
      `AttachmentData.Json`-bearing attachment per packed payload (FR-ROUTE-01).
    - `ForwardMessage.TryParse(message, out next, out payloads)` returns `false`
      for non-forward types, throws `MalformedMessageException` for forwards
      missing `body.next` or `attachments`.
- **Service-endpoint resolution** (`Resolution/`):
    - `DidCommServiceInfo(Uri, RoutingKeys, Accept)` — public record.
    - `IServiceEndpointResolver` — public contract: `ResolveAsync(did, ct)` →
      ordered `IReadOnlyList<DidCommServiceInfo>` (FR-ROUTE-03; preference
      order = FR-ROUTE-08 failover input).
    - `ServiceEndpointParser` (internal) — projects `NetDid.Core.Model.Service`
      entries through the v2.1 canonical shapes (single object / array of
      objects). Bare-string `serviceEndpoint` is gated behind the new
      `DidCommOptions.AllowBareStringServiceEndpoint` toggle (DD-10) and OFF by
      default.
    - `NetDidServiceEndpointResolver` — public default implementation backed by
      `NetDid.Core.IDidResolver`; rejects `did:web` at the perimeter for symmetry
      with `NetDidKeyService`.
    - `ResolvedRoute(TransportUri, RoutingKeyJwks, FallbackUris)` — public
      record.
    - `MediatorEndpointExpander` (internal) — implements FR-ROUTE-04. When the
      primary candidate's `uri` is itself a DID, resolves the mediator's
      `DIDCommMessaging` service, **prepends** its first `keyAgreement` key
      to the recipient's `routingKeys`, refuses a second DID-as-uri hop
      (`ConsistencyException`).
- **Sender-side forward wrapping** (`Composition/ForwardWrapper.cs`,
  internal) — loops `JweBuilder.PackAnoncrypt` over the routing-key JWKs in
  reverse (outermost first), producing one `forward` per layer.
  Content-encryption is fixed to A256CBC-HS512 per layer.
- **Facade** (`Facade/`):
    - `PackEncryptedResult(Message, ServiceEndpoint?, FallbackServiceEndpoints)`
      — **breaking change**: `DidCommClient.PackEncryptedAsync` now returns
      `Task<PackEncryptedResult>` instead of `Task<string>`. Consumers of the
      `.Message` field can append `.Message` to existing call sites.
    - `PackEncryptedOptions.Forward` — when `true`, the facade resolves the
      single recipient's `DIDCommMessaging` service, expands a mediator-as-DID
      endpoint, and wraps forward layers (FR-ROUTE-02). Multi-recipient
      `Forward = true` throws `InvalidOperationException`.
    - New `DidCommClient(secrets, keyService, serviceResolver, options)`
      constructor adds the `IServiceEndpointResolver` slot. The existing
      3-argument constructor still works (routing unavailable without the
      resolver).
    - `DidCommServiceCollectionExtensions.AddDidComm` now passes the optional
      `IServiceEndpointResolver` into the facade, so hosts that call
      `UseNetDidResolver()` get routing automatically.
- **Mediator-side processing** (`Protocols/Routing/`):
    - `ForwardProcessor` — public; drives the supplied `DidCommClient`'s
      `UnpackAsync`, validates the unpacked plaintext is a forward, silently
      drops `please_ack` (FR-ROUTE-07), and emits `ForwardProcessingResult`.
    - `ForwardProcessingResult(NextHop, OnwardPacked, ExpiresTime?, Delay?)`.
    - `ForwardProcessorOptions(Mode, ExtraRecipientRoutingKeys?)` +
      `RewrapMode` enum (`PassThrough`, `ReanoncryptToNext`). Pass-through is
      the default; rewrap (FR-ROUTE-06) re-anoncrypts the payload to `next` to
      keep onion size constant. `expires_time` propagates from the inbound
      forward; `delay_milli` resolves to a `TimeSpan` (negative input →
      randomised between 0 and |n|).

### Vendored spec fixtures (Phase 4 routing)

- `tests/DidComm.InteropTests/fixtures/spec/{endpoint-example-1,endpoint-example-2}.json`
  pinned verbatim from the DIDComm v2.1 spec §Service Endpoint /
  "Using a DID as an endpoint" (Apache-2.0). KAT anchors per L-005.
- `tests/DidComm.InteropTests/fixtures/diddocs/spec/{bob-with-routing,mediator1,mediator2,charlie}.json`
  transcribed from didcomm-python's
  `tests/test_vectors/did_doc/did_doc_{bob,mediator1,mediator2,charlie}.py`
  into the v2.1 canonical service shape (object form with nested
  `routingKeys` / `accept`). Provenance + transcription notes in
  `diddocs/spec/README.md`.
- `tests/DidComm.InteropTests/fixtures/secrets/{mediator1,mediator2}.json`
  reuse Bob's matching private bytes — didcomm-python's own fixtures do the
  same; documented in `fixtures/secrets/README.md`.

### Tests — Phase 4

- 13 forward-message + spec-endpoint tests (Checkpoint A — 11 unit + 2 interop).
- 16 service-endpoint resolver tests (Checkpoint B — 11 parser unit + 1 DI unit + 4 NetDid adapter interop).
- 10 mediator-endpoint expander tests (Checkpoint C — internal contract via `InternalsVisibleTo`).
- 6 sender-side forward wrapping tests (Checkpoint D — 2 facade unit + 4 interop covering single-hop Bob, two-hop Charlie via mediator-as-DID, no-service refusal, Forward=false bypass).
- 13 ForwardProcessor tests (Checkpoint E — option matrix, non-forward refusal, pass-through, FR-ROUTE-07 please_ack silence, expires_time, delay_milli ±, malformed attachment).
- 2 Alice → mediator1 → Bob end-to-end round-trip tests (Checkpoint F — happy path + missing-service-block refusal).
- Cookbook smoke test continues to pass after adding section O.
- Test totals: **300 → 348 unit (+48)** and **31 → 43 interop (+12)**.

### Changed (Phase 4)

- `DidCommClient.PackEncryptedAsync` return type is now `Task<PackEncryptedResult>`. Existing call sites that fed the result straight to `UnpackAsync` need a `.Message` extraction; six in-repo sites updated (4 round-trip tests, 1 rotation interop test, 2 cookbook sections).
- `MediatorEndpointExpander` only weaves the mediator's _keyAgreement_ (FR-ROUTE-04 implicit-prepend), **not** the mediator's own `routingKeys` — per a re-read of the spec text. The mediator's own routingKeys apply only when the mediator is itself the message recipient.

### Fixed (Phase 4 review)

- `ForwardProcessor.ExtractAttachmentBytes` now decodes `attachment.data.base64` with
  `Base64Url` (DIDComm attachments are base64url); previously `Convert.FromBase64String` threw
  `FormatException` on conformant base64url payloads from interop peers.
- `ForwardProcessor` rewrap mode (`RewrapMode.ReanoncryptToNext`) now strips the DID fragment
  from `next` before building the self-addressed forward and resolving the next hop's
  keyAgreement keys, so multi-hop rewrap no longer feeds a fragment'd DID-URL to
  `GetVerificationMethodsAsync`. Added an Alice→mediator→Bob rewrap round-trip test (the
  self-addressed `to == next` envelope is peeled by a forward-aware recipient).
- `ForwardProcessor.ProcessAsync` throws `ConsistencyException` for a forward carrying more than
  one attachment instead of silently relaying only the first and dropping the rest.
- `ForwardProcessor.ExtractDelay` no longer crashes on extreme negative `delay_milli`
  (`long.MinValue` / `-long.MaxValue`); the random hold is computed without overflowing
  `Math.Abs`/`int` and is capped at `int.MaxValue` ms.
- `ForwardMessage.TryParse` raises `MalformedMessageException` (not `InvalidOperationException`)
  when `body.next` is present but not a JSON string.
- `MediatorEndpointExpander` rejects relative/fragment-only routing-key references with a clear
  `DidResolutionException` (previously an empty subject DID produced a confusing
  `ArgumentException`), and drops DID-valued fallback URIs so `FallbackServiceEndpoints` only
  carries usable transport URIs.
- `AddDidComm` registers `DidCommClient` via `TryAddSingleton` (parity with its sibling
  registrations), so a repeated `AddDidComm` no longer double-registers the client.

### Added — Cookbook (PRD §14.2 Phase 4 increment: section O)

- `samples/02-Cookbook/Sections/Section_O_RoutingViaMediator.cs` — narrates
  `Forward = true` end-to-end against a section-local inline
  `IServiceEndpointResolver` so the runnable cookbook needs no fixture
  dependency.
- `samples/02-Cookbook/CookbookContext.cs` exposes `ServiceProvider` so a
  section can mint extra identities from the shared net-did graph.
- `Program.cs` registers Section O in narration order; `README.md` updated
  with the new section description and expected-output frame.

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
      _which_ kids are held but cannot yield decryption-path private keys until
      net-did adds an opaque-ECDH provider.
- **TestSupport** (`tests/DidComm.TestSupport/`):
    - New library (non-test). `InMemorySecretsResolver` is the dictionary-backed
      test fake (FR-SEC-05) — deliberately outside `DidComm.Core` so DD-02 stays
      honest.
- **`JweParser.PeekRecipients`** — lightweight structural peek (recipient kids
    - skid, no crypto) for facade pre-warm scenarios. Wired into the design but
      not yet consumed by the current facade implementation; kept available for
      future caching/optimization work.

### Vendored spec fixtures (FR-IX-01 extension)

- `tests/DidComm.InteropTests/fixtures/diddocs/spec/{alice,bob}.json` — DIDComm
  v2.1 Appendix B DID Documents transcribed from didcomm-python's
  `DID_DOC_*_SPEC_TEST_VECTORS` (Apache-2.0). Provenance + scope note in
  `fixtures/diddocs/spec/README.md`. Charlie / mediator1 / mediator2 are
  intentionally deferred to Phase 4 alongside the FR-ROUTE-\* work that actually
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
    - adapter (did:web rejection, malformed input, missing-doc, embedded JWK,
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

### Fixed (Phase 3 review)

- `FromPriorBuilder` now serializes the JWT protected header via
  `JsonSerializer` instead of string interpolation, so an unusual signer `kid`
  (embedded quote / control char) is escaped rather than injected into the
  header JSON. Output remains byte-identical for well-formed kids (key order
  `alg, kid, typ` preserved), so round-trip / tamper coverage is unchanged.
- `DidCommClient.UnpackAsync` documents the sync-over-async **support
  boundary** (supported on hosts without a captured `SynchronizationContext`;
  use `Task.Run(...)` from legacy UI contexts), and the `ISecretsResolver` /
  `IDidKeyService` contracts now require `ConfigureAwait(false)` in
  implementations. Corrected a stale `ISecretsResolver` remark that claimed the
  facade pre-resolves all secrets, and removed dead doc references
  (`UnpackAsync` "Checkpoint D" forward-reference, a misplaced `PickCommonCurve`
  summary).

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
    - `EphemeralKeyPairTests` — per-curve length contracts, freshness, FR-MSG-06
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
      every FR-CONSIST-\* rule pivots on.
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

[Unreleased]: https://github.com/moisesja/didcomm-dotnet/commits/main
