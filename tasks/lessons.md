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
