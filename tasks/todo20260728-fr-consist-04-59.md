# FR-CONSIST-04 — surface the "recipient not in `to`" warning (issue #59)

Approved plan: `/Users/moises/.claude/plans/shimmying-kindling-quokka.md` (2026-07-28).

## Context (short)

`AddressingConsistency.IsRecipientInTo` has no production caller, so the spec's SHOULD-level
"recipient not named in `to`" warning is never emitted. The decrypting kid's subject alone can
never fire it (FR-CONSIST-02 MUST throws first), so per user decision the identity comes from a
new `DidCommOptions.OwnIdentifiers` setting, unioned with the decrypting kid's subject, and the
outcome is surfaced as data (`UnpackResult.RecipientAddressing`), never failing delivery.

## Checklist

- [x] Branch `feat/fr-consist-04-59` off main
- [x] `src/DidComm.Core/Facade/RecipientAddressing.cs` — new enum (`NotEvaluated=0`, `Addressed`, `NotAddressed`)
- [x] `DidCommOptions.OwnIdentifiers` — `IReadOnlyCollection<string>`, default empty
- [x] `Composition/UnpackResult` — `public RecipientAddressing RecipientAddressing { get; init; }` (non-positional)
- [x] `Facade/UnpackResult` — same member, `internal init`, positional ctor untouched
- [x] `EnvelopeReader.UnpackAsync` — `ownIdentifiers` param; computed after FR-CONSIST-02 in the plaintext branch; set on internal record
- [x] `DidCommClient.UnpackAsync` — pass own identifiers; copy member in projection
- [x] Envelope-layer tests (new `RecipientAddressingTests` + TestRunner passthrough), 11 cases
- [x] Facade tests: `OwnIdentifiers` end-to-end, plus construction validation and snapshot-safety
- [x] Docs: PRD FR-CONSIST-04 + FR-API-04 rows; stale XML docs; cookbook Section K; CHANGELOG `[Unreleased]` closing #59
  - (no `docs/codebase-architecture.md` exists in this repo — only the PRD; nothing to sync)
- [x] Verify: `dotnet build /warnaserror`, `dotnet test`, `dotnet pack -c Release` (ApiCompat vs 1.3.0)
- [x] Adversarial pass — findings triaged and the real ones fixed (below)

## Review

### What shipped

The rule now lives with its siblings as `AddressingConsistency.CheckRecipientAddressing`, called from
the `EnvelopeReader` plaintext branch immediately after the FR-CONSIST-02 MUST (so a rejection always
wins) and surfaced as `UnpackResult.RecipientAddressing`. Own identity = the decrypting kid's DID
subject ∪ `DidCommOptions.OwnIdentifiers`.

**Design change during implementation:** the orphaned `IsRecipientInTo` was *replaced* rather than
called. Wiring it verbatim would have re-parsed the whole `to` list once per identifier, which is what
created the timing oracle and the CPU multiplier below. Folding the rule into a single-pass
`CheckRecipientAddressing` in the same class keeps FR-CONSIST-04 where its siblings live, gives it a
production caller, and leaves no shadow orphan. `AddressingConsistency` is `internal`, so removing the
old predicate is not a public API change; its unit test was rewritten against the new function.

### Adversarial findings and disposition

Nine findings. Three were real and are fixed; the rest are documentation or accepted design.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| 1 | High | Identity-enumeration **timing oracle**: early-exit on first match let an unauthenticated 1 MiB plaintext reveal whether a guessed DID is ours and its index in `OwnIdentifiers` (675× spread) | **Fixed** — single-pass subject set, no early exit. Re-measured: 9.3 / 9.3 / 9.5 ms for first / middle / last match vs 11.0 ms miss — flat |
| 2 | High | **CPU amplification**: cost was O(\|to\| × \|own\|) (~24 ms per identity per MiB; 1.2 s at 50) | **Fixed** by the same rewrite → O(\|to\| + \|own\|). Re-measured 9.5 ms (0 ids) → 11.6 ms (50) → 13.5 ms (200) |
| 3 | Med | "Never affects delivery" was **false**: enumerating the caller's live mutable `OwnIdentifiers` mid-unpack threw `InvalidOperationException`, dropping the message | **Fixed** — snapshotted at `DidCommClient` construction; regression test added |
| 5 | Med | A typo'd identifier was skipped silently, leaving the warning permanently dead with no operator signal | **Fixed** — `ArgumentException` at construction; the per-message skip stays as defense in depth |
| 4 | Med | `Addressed` is attacker-authored on unauthenticated envelopes; omitting `to` silences the warning | **Documented** — trust-boundary remarks on the enum, `UnpackResult`, and the cookbook. Splitting `NotEvaluated` into no-`to` vs not-configured is a possible follow-up |
| 6, 7 | Low | A `with`-clone or an in-place `Message.To` edit leaves the enum stale; no snapshot backstop like the #56 bindings | **Documented** as the weaker channel (plan scoped snapshot/observation mirroring out). Follow-up candidate |
| 8, 9 | Info | `DidSubjectOf` normalization gaps all fail *safe* (toward a spurious warning, never a suppressed one); decrypting-kid match short-circuits `OwnIdentifiers` on the encrypted path | Accepted; noted |

Explicitly cleared: FR-CONSIST-02/01/03/05 ordering unchanged (advisory runs strictly after every
MUST); direct forgery blocked (`internal init` — verified by compile failure from an external
assembly); no NFR-04 leak (bare enum, nothing logged, no new DID/kid data); `DidSubjectOf` cannot
throw on wire input; no second public-`UnpackResult` construction site that could drop the value.

### Verification

- `dotnet build DidComm.sln -c Release /warnaserror` — 0 warnings, 0 errors
- `dotnet test DidComm.sln -c Release` — **842 passed** (681 Core + 161 interop), 0 failed
- `dotnet pack DidComm.sln -c Release` — ApiCompat / package validation clean against the 1.3.0 baseline (member is non-positional `public get` / `internal init`; positional ctors untouched)
- Cookbook Section K runs and prints `RecipientAddressing = Addressed`
- Timing/DoS fixes measured directly through the public facade, not asserted by inspection
