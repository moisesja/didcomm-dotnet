# Lessons

Patterns captured from user corrections, surprising successes, or non-obvious
constraints encountered during didcomm-dotnet development. New entries get
appended; stale entries get edited or removed when the project moves past them.

Format per entry:

- **Lesson** — the rule.
- **Why** — the incident or reasoning that motivated it.
- **How to apply** — the cue that triggers the lesson.

---

## L-001 — Pause and ask which library owns a primitive before building it locally.

- **Lesson:** When implementing a generic cryptographic / SSI primitive,
  explicitly evaluate whether it belongs in `net-did` (or another foundational
  library) before writing it inside didcomm-dotnet.
- **Why:** Phase 0 originally planned to ship ~150 LOC of crypto primitives
  (raw ECDH, P-521, ECDSA P1363, off-curve point validation, Concat KDF) inside
  `DidComm.Core`. The user pushed back: "Doesn't net-did already provide this?"
  Inspection showed ~30% of the surface was already in net-did and ~50% was
  generic SSI infrastructure that belonged there. We filed five upstream issues
  (#60–#64), waited for net-did 1.3.0, and Phase 0's scope shrank to the
  JOSE-specific composition layer (A256CBC-HS512, A256KW, 1PU KDF wrapper,
  AEAD dispatch).
- **How to apply:** Before writing any crypto/encoding/key-management code in
  didcomm-dotnet, ask: "Could net-did, zcap-dotnet, or a future VC library use
  this?" If yes, file an upstream issue first.

## L-002 — DIDComm crypto runs through net-did for sign/verify/raw-ECDH only.

- **Lesson:** Sign, Verify, and raw `DeriveSharedSecret` always delegate to
  `NetDid.Core.ICryptoProvider`. AEAD, AES-KW, and the 1PU KDF wrapper stay
  local because they are JOSE-specific compositions with no consumer outside
  DIDComm.
- **Why:** A256CBC-HS512 is defined nowhere except RFC 7518. AES-KW (RFC 3394)
  is technically generic but the BCL has no public API and ~95% of real-world
  use is JOSE. The 1PU "tag-in-SuppPubInfo" wiring is draft-madden-specific.
  Trying to push these into net-did would be over-fitting net-did to DIDComm.
- **How to apply:** When adding a new primitive, classify it as "generic SSI"
  (net-did) vs "JOSE-specific composition" (DidComm.Core). If unsure, lean
  net-did — second consumers can always retract.

## L-003 — `dotnet build /warnaserror` rejects partial XML docs.

- **Lesson:** Every public/internal member that has any `<param>` tag MUST have
  one for every parameter. Partial XML docs trip CS1573 which becomes an error
  under `TreatWarningsAsErrors=true`.
- **Why:** Half-documented `ICryptoProvider` methods broke the first build.
  `<NoWarn>` suppresses CS1591 (missing doc altogether) but not CS1573
  (incomplete doc) — those are independent rules.
- **How to apply:** When writing an XML doc comment for a method, either
  document every parameter or document none. Don't half-do it.

## L-004 — Watch for namespace shadowing between DidComm and NetDid `ICryptoProvider`.

- **Lesson:** Both `DidComm.Crypto.ICryptoProvider` and
  `NetDid.Core.ICryptoProvider` exist. When a file in `DidComm.Crypto.*` takes
  one as a parameter, the C# resolver prefers the local namespace.
- **Why:** `Ecdh1PuKdf` originally took `ICryptoProvider cryptoProvider` and
  expected it to be the NetDid one. The compiler resolved to
  `DidComm.Crypto.ICryptoProvider` (string-based dispatch) and the call site
  failed with CS1503 "cannot convert from KeyType to string".
- **How to apply:** When a `DidComm.*` file consumes net-did's
  `ICryptoProvider`, fully qualify it (e.g.
  `using NetDidICryptoProvider = NetDid.Core.ICryptoProvider;`) at the top
  of the file.

## L-006 — Promoting `internal` to `public` cascades through the type graph.

- **Lesson:** When a Phase boundary requires exposing a public method whose
  parameter/return type was previously `internal`, every transitively-referenced
  type along that signature must also be promoted. `warnaserror` flags this as
  CS0051 / CS0053 "inconsistent accessibility", and the fix is mechanical but
  easy to forget when planning.
- **Why:** Phase 3 added `ISecretsResolver.FindAsync(...) → Jwk?`. The Plan
  agent enumerated the message-shape types that needed to go public
  (`Message`, `Attachment`, …) but missed `Jwk` itself — also `EnvelopeKind`
  (returned in `UnpackResult.Stack`). Each surfaced as a build break that
  required a second pass.
- **How to apply:** When promoting a type to public, walk every public
  member's signature and confirm each referenced type is at least as public.
  In planning notes, list "transitive public surface" alongside the primary
  promotion list.

## L-007 — Filter by held-private-key before picking a curve, not after.

- **Lesson:** When the facade picks a sender / signer key for authcrypt or
  sign-then-encrypt, intersect the DID's public verification methods with the
  `ISecretsResolver`'s held kids **before** scoring curves, not after. Otherwise
  the facade picks a curve where a public key exists but the matching private
  doesn't, then throws `SecretNotFoundException` after committing to an envelope
  shape.
- **Why:** Alice's Appendix B keyAgreement list starts with
  `did:example:alice#key-x25519-not-in-secrets-1` — a key whose private half is
  deliberately absent from Appendix A. The first authcrypt round-trip failed
  because the facade picked X25519 (common to alice and bob) and then asked
  Appendix A for the matching private key, which doesn't exist.
- **How to apply:** Any time the facade reaches into `ISecretsResolver` for a
  sender / signer private key, gate the candidate set on
  `FindPresentAsync(candidateKids)` first; only then run curve-selection logic.

## L-008 — Move input-shape guards above resolver calls so unit tests can reach them.

- **Lesson:** When the facade gains a new option that has shape-only validity
  rules (e.g. Phase 4's `Forward = true` requiring single-recipient + a non-null
  `IServiceEndpointResolver`), put those checks at the top of the public
  method, BEFORE any DID-resolution or curve-selection step. Otherwise the
  unit tests that drive the new check via cheap stubs (empty key service, no
  secrets) hit the resolver path first and throw the wrong exception type.
- **Why:** Phase 4 Checkpoint D landed `PackEncryptedAsync(Forward: true)`
  with the Forward-shape checks at the END of the method. The unit test
  asserting "multi-recipient Forward throws InvalidOperationException" failed
  with DidResolutionException because the recipient resolution loop ran
  first against an empty key-service stub. Moving the Forward checks above
  the recipient resolution made the tests deterministic and the error
  message reach the user before any I/O.
- **How to apply:** When adding a public-API option whose validity is purely
  about argument shape, check it BEFORE doing anything async, anything that
  hits a resolver, or anything that throws an unrelated exception type. Match
  the order to what a caller would expect to see when they violate the
  contract.

## L-009 — Re-read the spec text before adding "obvious" expansions.

- **Lesson:** When a referenced spec says "the mediator's keyAgreement keys
  are implicitly prepended to routingKeys", don't ALSO append the mediator's
  own routingKeys to that combined list on the assumption that it's the
  symmetric thing to do. Implement exactly what the spec text describes.
- **Why:** Phase 4 Checkpoint C's `MediatorEndpointExpander` added a third
  category — "Mediator's own routingKeys come after its keyAgreement" — based
  on extrapolation from Endpoint Example 2's wording. This produced a
  3-layer onion when only 2 layers were warranted, and surfaced as a test
  failure in Checkpoint D when the inner-wrap inspection caught the extra
  hop. The mediator's own routingKeys apply only when the mediator is itself
  the message recipient; when it merely SERVES as an endpoint, they don't
  enter the route.
- **How to apply:** Before adding a "symmetric" extension to a spec
  algorithm, re-read the exact spec paragraph and ask "did the spec author
  actually mention this case?". If not, leave it out. If a reference impl
  diverges, file an issue and pin the spec quote as the source of truth.

## L-010 — Polly + HttpClient: build a fresh HttpRequestMessage on every retry attempt.

- **Lesson:** When wrapping `HttpClient.SendAsync` in a Polly retry, the callback
  MUST construct a new `HttpRequestMessage` on every attempt. `HttpClient` rejects
  a request instance that has already been sent with `InvalidOperationException`
  ("request has already been sent"), so the second retry crashes before it even
  hits the wire. The `HttpContent` itself (e.g. `ByteArrayContent`) is replayable
  but the message wrapper is one-shot.
- **Why:** Phase 5 Checkpoint B's `HttpDidCommTransport` originally built one
  `HttpRequestMessage` per redirect hop and replayed it inside the Polly callback.
  The 200/202 happy path passed; the 5xx-retry test failed on attempt #2 with the
  reuse exception. Moving `BuildRequest(...)` INSIDE the Polly callback fixed it.
- **How to apply:** Any `ResiliencePipeline` (or generic retry wrapper) whose
  callback hits `HttpClient.SendAsync` must construct the `HttpRequestMessage`
  inside the callback, not before it.

## L-011 — Polly `RetryStrategyOptions.MaxRetryAttempts` rejects 0; skip the strategy entirely.

- **Lesson:** Polly v8's `RetryStrategyOptions.MaxRetryAttempts` is annotated
  `[Range(1, int.MaxValue)]`. Setting it to `0` throws `ValidationException`
  ("The 'RetryStrategyOptions<...>' are invalid"). To express "no retries",
  the factory must omit `AddRetry(...)` from the pipeline builder altogether
  rather than passing 0.
- **Why:** Phase 5 Checkpoint C's TestServer round-trip configured the HTTP
  transport with `MaxRetryAttempts = 0` to keep the test fast and deterministic;
  the factory blew up during transport construction with the validation error
  before any send ever happened.
- **How to apply:** For any Polly options that expose `MaxRetryAttempts`,
  `MinimumThroughput`, etc., guard the `AddXxx(...)` call with an `if (count > 0)`
  rather than passing the zero down — Polly v8's validators are strict.

## L-012 — Phase 5 csproj prune warnings: Microsoft.AspNetCore.App provides Extensions.* packages.

- **Lesson:** When a test or app project declares
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for ASP.NET Core,
  the shared framework already brings in `Microsoft.Extensions.DependencyInjection`,
  `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options`, etc. Explicit
  `<PackageReference>` entries for those become unnecessary, and the .NET 9+ NuGet
  pruning rules emit `NU1510` ("will not be pruned") — which `TreatWarningsAsErrors`
  upgrades to a build failure.
- **Why:** Phase 5 Checkpoint C's InteropTests pulled in
  `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Http`
  explicitly, then added the shared framework reference for TestHost; the build
  broke on the duplicate.
- **How to apply:** Any project that adds the AspNetCore shared framework reference
  must drop redundant `Microsoft.Extensions.*` package references at the same time.
  If a non-shared dep is genuinely needed (e.g. `Microsoft.Extensions.Http.Polly`),
  keep it; otherwise let the framework reference satisfy it.

## L-013 — Wrap transport-library exceptions at the transport boundary, and clamp Polly option floors.

- **Lesson:** A custom `IDidCommTransport` must convert library-specific failures
  (`WebSocketException`, `TimeoutException`, exhausted Polly budget) into the
  library's own `TransportException` before they escape — otherwise the FR-API-07
  promise that callers pattern-match a single category silently breaks for one
  transport while holding for another. The HTTP transport wrapped; the WebSocket
  transport didn't, and the gap only showed on the failure path (happy-path tests
  passed). Caller-initiated cancellation is the one exception to preserve as-is
  (filter `when (ct.IsCancellationRequested)` and rethrow). Separately, Polly v8's
  `CircuitBreakerStrategyOptions.MinimumThroughput` is `[Range(2, …)]`: feeding a
  user-configured threshold straight through throws `ValidationException` at
  construction when the value is 1 — clamp with `Math.Max(2, …)`.
- **Why:** PR #7 review found WebSocket send failures leaking raw exceptions and a
  sub-2 circuit-breaker threshold that would have thrown at startup.
- **How to apply:** At every transport's public boundary, `try/catch` the library
  call and rethrow as `TransportException(message, inner, httpStatusCode, scheme)`,
  preserving cancellation. When forwarding numeric options into a third-party
  policy library, check that library's documented range and clamp rather than
  trusting the caller.

## L-014 — Namespace import shadows a same-named static class.

- **Lesson:** When a static-constants class has the same name as the namespace
  it lives in (e.g. `DidComm.Profiles.Profiles`), any consumer that writes
  `using DidComm.Profiles;` loses the ability to refer to the class as
  `Profiles` — the namespace import wins and `Profiles.DidCommV2` resolves to
  `<consumer-namespace>.Profiles.DidCommV2`, which doesn't exist. Fix is a
  `using ProfilesConst = DidComm.Profiles.Profiles;` alias at the top of the
  consumer; the namespace-vs-class shadowing is silent until first compile.
- **Why:** Phase 6.1 added `DidComm.Profiles.Profiles` and the
  `ProfileNegotiatorTests` + the cookbook `Section_BB_ProfilesAndI18n` both
  failed `CS0234` on `Profiles.DidCommV2` until aliased.
- **How to apply:** When naming a static-constants/holder class, prefer
  pluralizing the namespace (e.g. namespace `DidComm.Profile`, class
  `DidComm.Profile.Profiles`) OR plan to ship a `using XxxConst = ...;` alias
  alongside every consumer. The earlier rename is cheap; the alias is forever.

## L-018 — Pass the thread-state STORE to handlers, not just the per-message thread.

- **Lesson:** When a protocol handler tracks state on a thread other than the
  *current* message's thread, exposing only the dispatcher-resolved
  `context.Thread` is not enough. The handler needs `IThreadStateStore` to
  look up the right thread by id. Report Problem 2.0 is the canonical case:
  the report itself is a top-level message (its own `thid` is a fresh UUID),
  but the FAILING thread it tracks is named by `pthid`. Without store access,
  the cascade guard's increment lands on the wrong thread state and never
  trips, even though the per-test unit assertion looks green.
- **Why:** Phase 6.2c's first integration test for the FR-PROTO-10 cascade
  guard exposed this: the unit tests passed because they constructed a
  ThreadState manually and mutated it directly; the integration test packed
  three real ProblemReports, dispatched them, and the guard never tripped
  because each report's `context.Thread` was a fresh state keyed off the
  report's own UUID. Fix was to add `ProtocolContext.Threads` exposing
  `IThreadStateStore` and have the handler use `context.Threads.GetOrCreate(message.Pthid)`.
- **How to apply:** Whenever a handler's spec semantics talk about a thread
  identified by a header OTHER than `thid` (e.g. `pthid` for ProblemReport,
  OOB-invitation id for stitched response threads), reach for the store
  directly. Add an integration test that round-trips multiple packed
  messages — unit tests with hand-constructed state will silently lie about
  the handler's real behavior.

## L-017 — DI factories that walk `IEnumerable<T>` can deadlock the graph.

- **Lesson:** When a singleton's DI factory invokes `sp.GetServices<TInterface>()`
  (or `GetService<IEnumerable<TInterface>>()`) and one of those services has a
  ctor dependency that, transitively, depends on the singleton being built,
  the .NET DI container will hang (or in some configurations throw a circular-
  dependency exception). The cycle is invisible at registration time and only
  surfaces on the first resolution. **Fix:** break the cycle by deferring one
  side's resolution to call time — either inject `IServiceProvider` and resolve
  inside the method that needs it, or hand out a `Lazy<T>`/`Func<T>` instead of
  the resolved instance.
- **Why:** Phase 6.2b's `ProtocolHandlerRegistry` singleton factory called
  `sp.GetServices<IProtocolHandler>()`, which forced construction of
  `DiscoverFeaturesHandler`. That handler depended on `IEnumerable<IFeatureProvider>`,
  one of which (`ProtocolFeatureProvider`) had been written to take the
  `ProtocolHandlerRegistry` directly. Result: `dotnet test` hung the testhost
  for 5+ minutes with no diagnostic. Switching `ProtocolFeatureProvider`'s ctor
  to take `IServiceProvider` and resolve the registry on demand via
  `IServiceProvider.GetService(typeof(ProtocolHandlerRegistry))` broke the cycle.
- **How to apply:** When a service participates in a DI graph that depends on
  itself transitively — e.g. "a feature catalog that reads the catalog" or
  "a router that depends on its own routes" — give that service a lazy / SP
  handle, not the resolved instance. Note: `Microsoft.Extensions.DependencyInjection`
  is a heavy package for `DidComm.Core`; use the BCL's
  `IServiceProvider.GetService(Type)` to avoid adding the dep just for the
  generic `GetRequiredService<T>` extension.

## L-016 — `git checkout main -- .` overwrites a feature branch's tracked edits silently.

- **Lesson:** Mid-feature-branch, NEVER run `git checkout main -- .` (or its sibling
  `git restore --source=main -- .`) to "see what main looks like" — the path-spec
  form REPLACES the working-tree copy of every tracked file with main's version
  WITHOUT touching untracked files and WITHOUT producing a "your changes will be
  lost" warning. The only safe ways to compare against main mid-branch are
  `git diff main`, `git show main:path/to/file`, or `git worktree add` of main into
  a separate path.
- **Why:** Phase 6.2a almost shipped without `Message.Empty()`, `ThreadState.ErrorCount`,
  the DI registry factory, `AllowSameSocketReplies`, or any of the registry-aware
  endpoint overloads — a single accidental `git checkout main -- .` invocation,
  intended as an investigation step into a baseline test count, wiped out ~280 lines
  of edited code across seven tracked files in one keystroke. The untracked new
  source/test files survived (so the disaster was partially recoverable), and the
  system-reminder tool surface flagged the silent file modifications fast enough
  to re-apply the edits in ~5 minutes — but it could just as easily have shipped.
- **How to apply:** Treat `git checkout <ref> -- <path>` as a DESTRUCTIVE operation,
  exactly like `git reset --hard`. To diff against main: `git diff main -- path` or
  `git show main:path`. To experiment with main's state: `git worktree add` it
  somewhere new, never overlay it on the current branch.

## L-017 — Record-positional parameters use PascalCase, not the lowercase ctor convention.

- **Lesson:** When constructing a positional `record` (or `record class`) with named
  arguments, the parameter name in the call site MUST match the property name
  emitted by the record — which is the CAPITALIZED form even though the record's
  declaration uses `lowercase` (the compiler synthesizes Pascal-cased properties
  and the ctor parameter inherits the *property* name, not the declaration's
  source spelling). Writing `new ProtocolContext(received, thread, client: x, options)`
  fails CS1739 because the synthesized ctor parameter is `Client`, not `client`.
- **Why:** Phase 6.2a's first dispatcher test pass tripped CS1739 on every record
  construction site that used the lowercase form ergonomic of regular ctors. The
  fix is mechanical (PascalCase the named arg) but easy to miss when the rest of
  the codebase uses lowercase ctor params elsewhere.
- **How to apply:** When building a positional record with named arguments, type
  the property name (PascalCase), not the lowercase ctor-parameter spelling.

## L-005 — Self-round-trip tests do NOT prove spec interop for KDFs and serializers.

- **Lesson:** A pack→unpack round-trip with my own code only proves the two halves
  agree. For anything the spec covers — JOSE KDF byte layouts, JSON encoder choices,
  `apv`/`apu` derivation — write a KAT against a published external value (RFC,
  spec appendix, or an external reference impl's vector) BEFORE writing the
  composition test. The composition test should then be a check, not the gate.
- **Why:** Phase 0 shipped `Ecdh1PuKdf` with a `SuppPubInfo` layout of
  `BE32(keyDataLen*8) || tag` — the AEAD-tag length prefix from draft-madden-04 §2.3
  was missing. The Phase 0 differential test was "round-trip vs an independent
  reference path", but the reference path was hand-written from the same wrong
  reading. Self-consistent ≠ correct. The Phase 0 KAT shipped green; the SICPA
  Appendix C.3 vectors failed AES-KW unwrap with "integrity check failed" the
  moment Phase 2 first ran them. Same lesson applied separately to
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — default `System.Text.Json` escapes
  `+` to `\u002B`, which silently diverges from the spec's literal `+` until a
  byte-level comparison is forced (and self-round-trips can't see it).
- **How to apply:** For every JOSE primitive (KDF, JSON canonicalization, base64url,
  apv/apu, sig format), look up at least one PUBLISHED test vector and pin it as a
  KAT before writing the round-trip. If no spec/RFC vector exists, harvest one from a
  reference impl (askar-crypto, didcomm-rust, didcomm-python) and tag the fixture's
  provenance in a code comment. Treat the spec vector as the authority; treat my
  round-trip as a smoke test.

## L-015 — Validate DID-derived URLs before egress; the serviceEndpoint host is attacker-controlled.

- **Lesson:** Any URL that originates from a resolved DID document (`serviceEndpoint.uri`,
  routing endpoints) is untrusted input. Before the library makes an outbound request to it,
  reject private / loopback / link-local / unique-local / CGNAT / cloud-metadata
  (`169.254.169.254`) destinations by default. Caller-supplied values
  (`SendOptions.ServiceEndpointOverride`) are trusted, like a CLI flag, and may bypass the check.
- **Why:** A full-repo security review found a HIGH-confidence SSRF: `ServiceEndpointParser`
  accepted any `uri` string with no host/scheme validation, and it flowed through
  `DidCommClient.SendAsync` straight into an outbound HTTP/WebSocket POST. With self-asserted
  `did:key`/`did:peer`, an attacker hands the victim a DID whose endpoint is
  `https://169.254.169.254/...` and the victim's server makes the request. The 307-redirect path
  only re-checked the scheme, not the host.
- **How to apply:** Two enforcement points are needed, not one. (1) A pre-send check on the
  resolved URI catches the obvious case. (2) Connect-time validation that pins the socket to a
  vetted IP (HTTP `SocketsHttpHandler.ConnectCallback`) is what actually defeats 307-redirect-to-
  internal and DNS rebinding — a resolve-then-connect check has a TOCTOU gap. Also unwrap
  IPv4-mapped IPv6 (`::ffff:a.b.c.d`) before classifying, and block if ANY resolved address is
  private. Put the IP classifier behind an injectable DNS seam so it unit-tests offline.

---

## L-019 — Re-verify the checked-out branch after any interruption, and immediately before every commit.

- **Lesson:** `git branch --show-current` must be confirmed (a) right after creating the
  feature branch, (b) again after any pause/interruption or long gap, and (c) as the line
  immediately preceding any `git add`/`git commit`. Never assume HEAD is still where you left it.
- **Why:** During Phase 6.3 the feature branch `feat/phase-6-3-oob-nuget` was created and
  confirmed checked out, but several edits later HEAD was found back on `main` (an external
  checkout — IDE, user, or a parallel session — moved it). The user caught it: "you're working
  in the main branch which is a no-no." Nothing was committed, so the fix was a clean
  `git checkout feat/...` that carried the uncommitted changes over (both branches were at the
  same commit). Had a commit run on `main` first, it would have violated the never-commit-to-main
  rule and needed a reset.
- **How to apply:** Working-tree edits are branch-agnostic until committed, so a drift is
  recoverable only if caught before `git commit`. Print `git branch --show-current` as the first
  token of any commit command (e.g. `b=$(git branch --show-current); [ "$b" != main ] && git commit …`)
  and re-check after every interruption. Relates to [[feedback_branching]].

---

## L-020 — A test that fails after a security fix may be *encoding the bug* — verify against the spec before "fixing" the test.

- **Lesson:** When a behavioral hardening change breaks an existing test, do NOT reflexively rewrite
  the test to match the new output. First decide whether the test was asserting correct contract or
  was pinning the pre-existing bug. Only update it once the new behavior is confirmed against the
  spec/PRD.
- **Why:** In the security-hardening pass, fixing forward `body.next` to emit a bare DID (it had been
  emitting a fragment-bearing kid) broke `SenderForwardWrappingTests` — which asserted the *buggy*
  fragment form. The right move was to confirm the routing spec wants a DID, then update the
  assertion. Had the change instead been wrong, blindly editing the test would have hidden a real
  regression.
- **How to apply:** For each test broken by a behavioral change, write one sentence: "this asserts
  <contract> because <spec ref>" or "this pinned the old behavior." Update only the latter. Relates
  to the project rule: prove correctness before marking done.

## L-021 — Put a new receive-side validation check *before* the AAD/crypto step so negative tests can tamper a real envelope.

- **Lesson:** When adding a reject-rule to an envelope parser (enc allow-list, `crit`, `apu`↔`skid`),
  place it before the AEAD/AAD derivation. Then a negative test can build a valid envelope, mutate
  the protected header, and deterministically hit the new check — the (now-broken) AAD never masks it.
- **Why:** The `enc`/`crit`/`apu` checks were positioned right after header decode, so
  `MutateProtectedHeader(...)` tamper tests trigger them cleanly instead of failing later at the tag.
- **How to apply:** Order parser checks: structural → header allow-lists/bindings → key resolution →
  AEAD. Add tamper-a-built-envelope tests for each reject-rule.

---

## L-022 — DataProofs JwsParser sources signer kid ONLY from the protected header; DIDComm spec puts it in the unprotected per-signature header

- **Context:** After delegating JOSE to `DataProofsDotnet.Jose` (1.0.0), 5 InteropTests cross-impl
  fixtures fail: `c2-{eddsa,es256,es256k}-signed` and `c3-authcrypt-{p256,p521}`.
- **Symptom:** `EnvelopeReader.Unpack` throws `ArgumentException: signerKid cannot be empty` from
  `AddressingConsistency.CheckSignedFromMatchesSignerKid` / `CheckAuthcryptInnerSignerMatchesSkid`.
- **Root cause:** The DIDComm v2.1 spec examples (Appendix C.2/C.3) carry the JWS `kid` in the
  per-signature **unprotected** `header` (`protected` holds only `typ`+`alg`). DataProofs'
  `JwsParser` deliberately returns `SignerKid` ONLY from the integrity-protected header (documented:
  an unprotected-header kid is "an unauthenticated routing hint" and is never surfaced), so it
  returns `""`, which the src consistency guards then reject.
- **Scope:** This is a behavioral incompatibility between the DIDComm wire format and the upstream
  `DataProofsDotnet.Jose` security posture. The fix belongs in `src/` (EnvelopeReader/Addressing) or
  upstream DataProofs — NOT in tests. Do NOT weaken/delete these fixtures; they are the
  byte-equivalence gate doing its job.
- **How to apply:** When a parser swap changes where authenticated identity is read from, expect the
  spec-vector interop gate to flag it. Triage by decoding the fixture's protected vs unprotected
  headers before touching any test.
- **Resolution (DONE):** Verified the kid IS used to resolve the verifying key and the signature
  DOES verify — so the kid that produced a successful verification is authentic and safe to surface.
  Filed as **dataproofs-dotnet#10**; fixed upstream in **DataProofsDotnet.Jose 1.0.1** (`JwsParser`
  returns `header.Kid ?? sig.Kid` after verification). didcomm kept its checks intact and never
  weakened the fixtures — all 5 went green on the dependency bump (99/99 interop).

---

## L-023 — A deliberate local `ProjectReference` is a hard constraint; don't let a delegated subagent convert it to a `PackageReference`.

- **Lesson:** The user explicitly chose a local `ProjectReference` to `DataProofsDotnet.Jose` (NuGet
  prefix reservation pending). A test-fixing subagent, hitting a CPM/NU1008 snag, "helpfully"
  converted it to a `PackageReference` + central `PackageVersion 1.0.0` — silently violating the
  instruction. Caught on review and restored.
- **Why:** Reference *kind* (project vs package) is a user-level decision with release implications,
  not an incidental build detail a subagent should change to make a build pass.
- **How to apply:** When delegating, name the hard constraints explicitly ("the dataproofs ref MUST
  stay a local ProjectReference") and re-diff csproj/props after a subagent runs. Treat any
  reference-kind change as needing review, not auto-accept.

## L-024 — A receive-side validation gate must match the spec's *receive-acceptance* set (Appendix C vectors), not the PRD's *emit* set.

- **Lesson:** When building an inbound validation gate (e.g. legal envelope compositions), enumerate
  the accepted set from the spec's interop test vectors, not from the PRD's "what we emit" rules. The
  receive set is a superset of the emit set.
- **Why:** Issue #17's first cut enforced the FR-ENV-02 *emit* compositions (6) plus the FR-ENV-03
  receive-only `authcrypt(sign)` (7 total) and rejected everything else. That broke the real spec
  fixture `c3-authcrypt-p521` — DIDComm v2.1 Appendix C.3 `anoncrypt(authcrypt(sign))`, an inbound
  vector with `outcome: success`. FR-ENV-04 ("never emit `anoncrypt(authcrypt(sign))`") is an *emit*
  MUST-NOT, not a receive prohibition; the PRD was simply silent on the full receive set. Fixed to the
  grammar `anoncrypt? authcrypt? sign? plaintext` (8 shapes) and added PRD **FR-ENV-04a**. Relates to
  L-005 (self-round-trip ≠ interop) and L-020 (verify against spec before trusting a test outcome).
- **How to apply:** For any inbound gate, list the spec/Appendix vectors it must accept and run the
  interop fixtures *before* declaring the allow-set complete. If the PRD only describes emit behavior,
  the receive-acceptance set is incomplete — fill it in (and update the PRD).

## L-025 — Closing a body/status oracle leaves a timing oracle; and amortized-eviction analysis is single-threaded.

- **Lesson:** Two red-team findings on my own #20/#21 fixes. (a) Normalizing error *responses* (uniform
  400, empty body) closes the content/status oracle but NOT the timing oracle — a held-vs-unheld
  recipient kid still separates by ~180 µs because the decrypt path fast-fails before ECDH. (b) An
  eviction whose cost is "amortized O(log n) per insert" is only amortized *single-threaded*; with N
  concurrent inserters over the cap, each independently runs the full O(n log n) snapshot-sort →
  up-to-Nx CPU stampede. Fixed with a single-flight `Interlocked.CompareExchange` gate.
- **Why:** Adversarial agents tasked with *breaking* the just-written fix (per the repo's "use
  adversarial agents to attempt to exploit the code" rule) found both empirically — a body/status test
  (`Should().BeEmpty()`) passes while the timing channel is wide open, and a serial eviction-cost
  argument hides a concurrency stampede.
- **How to apply:** (1) When closing an oracle, enumerate ALL observables — body, status, headers,
  **timing**, and connection behavior — not just the obvious one; if you can't make it constant-time
  cheaply, file it and say so rather than claiming "no longer an oracle." (2) Any "amortized" cost
  argument for shared mutable state must be re-derived under concurrency; guard expensive
  rebuild/evict passes with single-flight. (3) Always run a break-it adversarial pass on a security
  fix before declaring done — the fix that closes the headline issue often leaves a residual.

## L-026 — When you make a shared primitive throw a new/wider exception, audit EVERY call site's exception contract — don't trust the issue's "call sites already handle it" premise.

- **Lesson:** Making `Base64Url.Decode` strict (#24) widened when it throws `FormatException`. The issue
  body asserted the call sites "map it to MalformedMessageException" — but the adversarial pass found
  `ForwardProcessor.ExtractAttachmentBytes` had NO try/catch, so a malformed forward attachment threw a
  raw `FormatException` out of the mediator's `ProcessAsync`. The from_prior site mapped it (#19); the
  forward site did not.
- **Why:** A primitive's exception is part of its contract; changing/widening it ripples to every
  caller. The audit-era issue text described call-site behavior that was only partially true, and the
  pre-existing raw-`FormatException` escape was simply made more reachable.
- **How to apply:** Before changing what a shared decoder/parser throws, `grep` every call site and
  verify each maps the (new or widened) exception to the module's documented contract type — add the
  map where missing, with a regression test per reachable site. Treat the issue's stated premise as a
  hypothesis to verify, not a fact. (This is exactly what the mandatory adversarial pass, AGENTS.md
  §2, is for — run it on every security fix and fix what it finds.)
