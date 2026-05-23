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
