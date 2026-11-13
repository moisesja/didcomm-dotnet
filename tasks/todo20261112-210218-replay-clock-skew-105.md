# todo20261112-210218 — FR-RR-08 replay admission gets its own clock-skew tolerance (#105)

Issue: #105 (epic #88; after #94 PR #107 merge `f336823`, before #95). Branch: `feat/replay-clock-skew-105` (L-019: re-verify before every commit).
Owner decision of 2026-11-11 (open decision 2 of the "after #92" update): replay admission gets its own tolerance, `ReplayProtectionOptions.ClockSkew`, default 60 s; `DidCommOptions.ExpiresClockSkew` stays 0 and governs FR-API-05 expiry only.
Binding refinement ("after #94" note for #105): nothing from #94 changes it; pickup requests and the mediator's replies and pushes are all in the FR-RR-08 scope; the end-to-end tests stamp `created_time` from the server's injected clock.
The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`.

## What the code does today (read before designing)

- The skew is not read by `InboundReplayGate`: the gate passes `DidCommOptions.Now()` to `IInboundReplayGuard.TryAdmit`, and the default guard, `InMemoryReplayGuard`, reads `DidCommOptions.ExpiresClockSkew` per call (clamped to ≥ 0, floored to whole seconds) for both the `FutureDated` bound (`created > now + skew`) and the retention (`created + Window + skew`). The `DidCommOptions` constructor argument is used for nothing else.
- Every receive path shares that guard: the AspNetCore registry-aware endpoints (`InboundAdmission`, DI singleton or the per-application fallback built from `IOptions<ReplayProtectionOptions>`) and the client's `InboundDispatchConsumer` (WebSocket pushes, DI singleton). So changing the guard changes both sides.
- **Correction to the issue text (scope item 3):** `SendResult.Returned` is *not* run through the gate. #92 decided (ADV-03, PRD FR-RR-07, `DidCommClient.EvaluateReturnedAsync` "no dispatch and no replay admission") that `Returned` proves identity only; admitting it would drop third-party mediators' unstamped replies. The new setting therefore applies on the client side to what the inbound consumer admits (session-socket pushes and replies over WebSocket); an HTTP `Returned` is judged by FR-API-05 expiry only, unchanged, whatever the clock offset. The two-hosts test still covers HTTP in both directions (the request side is admitted by the server).
- `ExpiresClockSkew` other readers stay as they are: `DidCommClient.UnpackAsync` (FR-API-05), `ValidateFromPriorFreshness` (FR-ROT-05), `ForwardHandler` / `ForwardProcessingResult` (FR-ROUTE-09), `PickupCoordinator.ExpiryInstant` / `MessagePickupQueue` (FR-ROUTE-09 for queued payloads).
- `InMemoryReplayGuard` and `ReplayProtectionOptions` were added in #91 and are unreleased (CHANGELOG `[Unreleased]`, package baseline 1.5.0), so their surface can change without a compatibility shim.

## Design

- D1. `ReplayProtectionOptions.ClockSkew` (`TimeSpan`, default `TimeSpan.FromSeconds(60)`): how far ahead of the receiver's clock a `created_time` may be, and how much longer than `Window` an entry is kept. Whole seconds, floored (a sub-second value counts as zero), the arithmetic the guard already uses. `Validate()` refuses a negative value and one over `int.MaxValue` seconds (the bound `Window` already has; it keeps `created + Window + ClockSkew` far inside `long` and the existing no-wrap test meaningful).
- D2. `InMemoryReplayGuard` reads `_options.ClockSkew` per call (as it reads `Window` today) and still clamps to ≥ 0 in the arithmetic (options are mutable after `Validate()`); it no longer reads `DidCommOptions` at all. Its constructors drop the `DidCommOptions` parameter: `(ReplayProtectionOptions)` and `(ReplayProtectionOptions, ILogger<InMemoryReplayGuard>?)`. Reason: the parameter would be unused, and keeping it would invite the next reader to think the facade options still matter to replay; the type is unreleased. Callers updated: `AddDidComm`'s registration, `InboundAdmissionDefaults.Guard`, tests.
- D3. Rules after the change (unchanged shape, new source): `TooOld` when `created < now − Window`; `FutureDated` when `created > now + ClockSkew`; else entry kept until `created + Window + ClockSkew` inclusive. No gap: age rejection starts after `created + Window`, the entry lives until `created + Window + ClockSkew` ≥ that.
- D4. `ExpiresClockSkew` keeps default 0; its XML doc says it governs FR-API-05 expiry (and the FR-ROT-05 / FR-ROUTE-09 checks that share its arithmetic) and no longer replay admission, pointing at `ReplayProtectionOptions.ClockSkew`.
- D5. Sizing docs follow the longer default retention (L-045): with `ClockSkew` 60 s an entry for a message stamped now lives 360 s instead of 300 s, so the `MaxEntries` / `MaxEntriesPerPeer` rate figures (≈ 109/s and ≈ 3.4/s over 300 s) become ≈ 91/s and ≈ 2.8/s over 360 s; worst case from admission is `Window + 2 × ClockSkew` (a message stamped `now + ClockSkew`), 420 s at the defaults. PRD (5)'s "rate × replay_window" becomes "rate × (replay_window + clock skew)".
- D6. No change to `IInboundReplayGuard`'s signature, `InboundReplayGate`, `ReplayAdmission` (the `FutureDated` doc names the new setting), the scope rule, or any endpoint.
- D7 (added after the QA and red-team passes; RT-1 / QA-A D1). **Readings out of order never re-admit.** The gate reads the clock before the guard's lock, so a later reading can purge first and an earlier one then pass its own age check after the entry is gone (on `main` too: a one-second inversion at its zero skew; a stepped-back host clock does the same). Fix: under the lock the guard keeps `_purgedAt`, the latest instant it purged at (it still purges at every reading that takes the lock; one behind `_purgedAt` can only remove timestamp-free entries), and refuses as `TooOld` any timestamped message whose expiry `created + Window + ClockSkew` is earlier than `_purgedAt` — it may have been forgotten. Honest traffic is unaffected unless the clock steps back by more than `Window + ClockSkew`; then fresh in-scope messages are refused until the clock catches up or the process restarts (availability, never integrity — FR-RR-08 (5)'s principle). This also closes #91's W-1. Timestamp-free entries keep `reading + Window` (an earlier draft used `_purgedAt + Window` and purged only at readings later than `_purgedAt`; the red-team re-check showed both over-retain timestamp-free entries after a step back — F1). Alternatives weighed: purging with a lag (only widens the tolerated inversion), resetting the mark after a large step back (keeps W-1 open for no gain an attacker could not also use); rejected. PRD FR-RR-08 (4) states the rule and its price.
- D8 (QA-A D2 / RT-3). **Settings are copied at construction.** `Window` was read per call, so raising it after admission moved age rejection past retention. The guard now validates a snapshot and keeps its own copies of all six settings; the clamp on the skew is gone (a validated copy cannot be negative).

## Threat model (L-031)

- The future window widens from 0 s to 60 s by default. Who can use it: only a sender that authcrypts to us sets `created_time` (it is inside the encrypted, authenticated plaintext); a replaying attacker can only present captured messages whose `created_time` is fixed. A captured message is still admitted at most once per (sender subject, id) within its retention, and is `TooOld` after `created + Window`; widening the future bound adds no instant at which a replay is admitted (the sweep tests prove it for future-dated `created`).
- Memory / availability lever: an authenticated peer (including a fresh self-made DID) can stamp `now + ClockSkew` so each of its entries lives `Window + 2 × ClockSkew` (420 s vs 300 s before at skew 0). Entries remain bounded by `MaxEntries` / `MaxEntriesPerPeer`; saturation remains fail-closed (availability only, W-2). Stated in the docs.
- Host clock and concurrency (revised by D7): readings that reach the lock out of order, and a host clock that steps back, never re-admit a replay; W-1 is closed. The price is refusal of fresh in-scope traffic after a step back of more than `Window + ClockSkew`.
- Hostile configuration: negative and over-`int.MaxValue`-second values refused by `Validate()`; the guard copies the validated values (D8), so nothing larger or negative reaches the arithmetic, and the largest validated values cannot overflow the `long` arithmetic (test).

## Steps

### A. Core
- [x] A1. `ReplayProtectionOptions.ClockSkew` + `Validate()` + XML docs (incl. `MaxEntries` / `MaxEntriesPerPeer` figures, `Window` doc mentions the skew in retention).
- [x] A2. `InMemoryReplayGuard`: read `ClockSkew`; drop `DidCommOptions` from both constructors; remarks rewritten (rules, retention, clock, worst-case retention).
- [x] A3. `ReplayAdmission.FutureDated` doc; `DidCommOptions.ExpiresClockSkew` doc; `ForwardProcessingResult` / `MessagePickupQueue` doc lines that cite `ExpiresClockSkew` stay (expiry) — check wording only.
- [x] A4. `AddDidComm` registration + XML doc; `InboundAdmissionDefaults.Guard` (AspNetCore fallback).

### B. Tests (the issue's acceptance list is the bar)
- [x] B1. Guard unit tests: helpers set `ClockSkew`, not `ExpiresClockSkew`; defaults `now + 59` / `now + 60` admitted, `now + 61` `FutureDated`; `ClockSkew = 0` reproduces the old edges; the per-second sweep for `created` in the future (0, +30, +59, +60) at skews 0 / 30 / 60 — no instant admits the replay, `Duplicate` until `created + Window + ClockSkew`, `TooOld` after; `ExpiresClockSkew` alone (0, 1 h, negative) changes nothing; clamp of a post-validation negative value; extreme values (largest skew, `DateTimeOffset.MaxValue`) never wrap.
- [x] B2. Options tests: default 60 s; negative and over-bound refused by `Validate()`, by the guard constructor and at `AddDidComm` resolution.
- [x] B3. Expiry unaffected: `UnpackAsync` with default options rejects `expires_time = now − 1` and accepts `now`; still rejects it with `ReplayProtectionOptions.ClockSkew` set large (different options object; proves independence); FR-ROUTE-09 tests unchanged.
- [x] B4. Server endpoint (HTTP + WS): with the default options, `created_time = now + 59` → handler invoked; `now + 61` → 202, handler not invoked, drop line `FutureDated`; `ExpiresClockSkew = 120 s` with default `ClockSkew` → `now + 61` still dropped; existing "clock moves backwards" test moves its 120 s to `ConfigureReplay`.
- [x] B5. Client inbound consumer (WebSocket push): server clock ahead by 59 s → push dispatched; by 61 s → dropped (client log line `FutureDated`); client `ClockSkew = 0` → a push 1 s ahead dropped.
- [x] B6. Two hosts 30 s apart (#98 clock-skew case), real `DidCommClient` against the TestServer mediator with `AddCoordinateMediation()` + `AddMessagePickup()`, client clock = server ± 30 s, over HTTP and over WebSocket: trust ping with `return_route: all`; `mediate-request` → `mediate-grant`, `recipient-update` → response; `status-request` → `status`, `delivery-request` → `delivery`, `messages-received` → `status` 0. Negative control: the same with `ClockSkew = 0` on both sides fails where the old behaviour failed (client ahead: the server drops the request; server ahead over WS: the client drops the reply).
- [x] B7. DX allowlists: rename the two `InMemoryReplayGuard` ctor entries; add `ReplayProtectionOptions::prop:ClockSkew` where the gate needs it (`TODO(#97)` reason); prove the gate is live with a negative control (remove the entry → red).

### C. Docs
- [x] C1. PRD FR-RR-08 (1), (4), (5) and its acceptance column; FR-RR-09 if it names the skew; §8a list if needed.
- [x] C2. `docs/codebase-architecture.md` (replay guard section; #92 known-limitation bullet on clock skew).
- [x] C3. CHANGELOG `[Unreleased]`: new `### Changed — replay admission has its own clock-skew tolerance (FR-RR-08, #105)` flagging the behaviour change; earlier `[Unreleased]` #91 / #92 entries that state `ExpiresClockSkew` for replay are corrected in place (one release, one contract — L-045).

### D. Verification and review passes
- [x] D1. Architect pass (Plan agent) on this design.
- [x] D2. Two independent QA agents (boundaries and clock advancement; `ClockSkew = 0` differential against `main`; client and server; concurrency; the retention/age seam; expiry unaffected).
- [x] D3. One independent adversarial agent (widened future window as a cache/memory lever; off-by-one; skew vs `Window`; overflow at extreme values; config mutation after validation).
- [x] D3b. Re-run the break-it pass on the revised fix (D7, D8) — L-030.
- [x] D3c. Fix what the passes confirmed (D7, D8, doc wording); bring their suites in as regressions, adapted where they encoded the old rule.
- [x] D4. Full build / test / pack green; counts per project recorded below.

## Review

### Passes (the first run's sub-agents were lost when the session ended; all three were rerun from scratch)

- **Architect (Plan agent):** confirmed dropping the `DidCommOptions` constructor parameter (unreleased type) and listed every call site, including the three `ResolveGuard` callers; confirmed `SendResult.Returned` is not replay-admitted (the issue's scope item 3 is wrong) and that the client-side setting applies only to the inbound consumer, and there only to in-scope messages (a ping-response is out of scope, so a negative control must use a mediator-service reply); validation non-negative and ≤ `int.MaxValue` s, not bounded by `Window`; gap-free proof; stale docs list (incl. the fresh-DID saturation duration and the rate figures); test gaps (DI-level "ExpiresClockSkew alone", the fallback guard, the harness changes). All applied.
- **QA-A (server side):** boundaries over a skew × window × sub-second grid, second-by-second sweeps, a 67 208-line trace differential against `main` (identical at `ClockSkew = s` vs `ExpiresClockSkew = s` except the 60 default-option lines +1..+60 s — the bug fixed), concurrency, validation, endpoints. Found D1 (a clock step back of `ClockSkew + 1` s after a purge re-admits; also as out-of-order concurrent readings) and D2 (raising `Window` after construction opens a gap) — both pre-existing on `main`. Fixed by D7 / D8.
- **QA-B (client side, end to end, expiry):** pushes ahead by 0..300 s at client skews default / 0 / 3600; `Returned` unaffected by any `ClockSkew`; two-host matrix ±0..±301 s over HTTP and WebSocket (37 cases; 9 fail on `main`); expiry (FR-API-05, FR-ROT-05, FR-ROUTE-09 forward and queued) follows `ExpiresClockSkew` only; one container as client and server shares one guard. Found: D1 docs not yet updated (step C, done); D2 an invalid `ClockSkew` fails closed only at first use (pre-existing pattern of `Window`; follow-up); D3 the new `ExpiresClockSkew` doc sentence wrong for negative values (fixed).
- **Red team:** RT-1 (the same defect as QA-A D1, proven end to end with a guard that parks the replay between its clock read and the lock; Medium on `main`, Low at the new default) — fixed by D7; RT-2 the widened future bound holds the budget `Window + 2 × ClockSkew` (documented; sizing sentence corrected); RT-3 = QA-A D2 — fixed by D8. Held: boundaries on a forward clock, overflow, negative/huge values, `RequireCreatedTime = false`, expiry independence, fallback guard, no new oracle.
- Kept as regressions (renamed, adapted where they encoded the old rule — the W-1 readmission, a live mutation, a random-walk model without the purge instant, one sweep stepping back past `Window + ClockSkew`): `ReplayClockSkewQaServerTests`, `ReplayClockSkewRedTeamTests` (Core and Interop), `ReplayClockSkewQaEndpointTests`, `ReplayClockSkewQaClientTests`, `ReplayClockSkewQaTwoHostMatrixTests`, `ReplayClockSkewQaExpiryTests`, `ReplayClockSkewQaQueuedExpiryTests`. The random walks now assert "a timestamped message is admitted at most once" on non-monotonic clocks too.

- **Red team, second pass (on 5b776e9):** no integrity break — a 92 160 000-call randomized search over 30 720 configurations (windows from 1 tick to `int.MaxValue` s, skews 0..`int.MaxValue` s, budgets, base times near both ends of `DateTimeOffset`, wild and tame clocks with 28 % reused readings), checked by an oracle written from the docs (no timestamped triple admitted twice, heap/bucket/count agreement, nothing older than the purge instant survives, the purge instant equals the latest host reading — nothing in a message moves it), found 0 violations; the same oracle finds 1 115 / 2 / 2 125 double admissions on the 93c3694 algorithm. Concurrency (16 threads × 25 000 calls × 5 configurations), overflow, the constructor snapshot under concurrent mutation (200 000 constructions) all held. Findings and dispositions:
  - F1 (new in the first draft of D7): timestamp-free entries kept `_purgedAt + Window`, and no purge at readings behind `_purgedAt`, so after a step back a timestamp-free flood held the budget `D + Window` and an honest peer's entries piled up for the whole outage. **Fixed:** `reading + Window` and a purge at every locked call (safe: a reading behind `_purgedAt` can only remove timestamp-free entries). Regressions in `ReplayClockSkewRedTeamTests`.
  - F2: "judged normally" / "honest traffic unaffected" overstated; the exact rule is "admitted iff `created + Window + ClockSkew ≥ _purgedAt`". **Docs corrected** (XML, PRD (4), CHANGELOG, architecture); a theory pins the rule.
  - F3: the new PRD acceptance sentence said "duplicate until created + Window + ClockSkew"; it is a duplicate until created + Window and too old after, entry held to created + Window + ClockSkew. **PRD corrected.**
  - F4: a sub-second `Window` validates and floors to zero — pre-existing (#91). **Follow-up**, not this PR.
  - F5: a guard left behind by one far-future reading refuses everything until rebuilt, invisibly. **Added** a rate-limited Warning with the lag in seconds for refusals at a reading more than `Window + ClockSkew` behind; tested. A separate metric would be new public API; not added.
  - F6: "every call purges" / `Count` remark imprecise. **Docs corrected** (and every locked call purges again).
  - Not kept in the suite: the 25 s all-core randomized search (would disturb timing-sensitive tests in CI) and its reflection-based inspector (brittle across refactors); their result is recorded here and in the PR.

### Negative controls

- Default `ClockSkew` temporarily set to zero: 10 of the then 29 new interop tests and 9 of the then 35 new core tests failed (HTTP with the client behind stayed green, as it must: `Returned` is never admitted).
- DX gate: removing the `prop:ClockSkew` coverage entry turned `PublicApiCoverageTests` red naming the member; the gate also caught the stale constructor entries and, after D8, the now-redundant `RequireCreatedTime` execution entry.

### Results (head of the branch before the PR)

- `dotnet build DidComm.sln -c Release /warnaserror`: 0 warnings, 0 errors.
- `dotnet test DidComm.sln -c Release --no-build`: DidComm.Core.Tests 2082/2082 (main: 1812), DidComm.Mediator.Tests 3033/3033 (main: 3029), DidComm.InteropTests 1015/1015 (main: 771 + the fixtures this checkout has; incl. the DX gates and the interop fixtures).
- `dotnet pack DidComm.sln -c Release --no-build`: exit 0, seven packages (package validation against the 1.5.0 baseline passes; the changed types are unreleased).
- `git diff --check`: clean.

## PR #108 — review round 1 (reviewer comment of 2026-11-13T11:46Z against `a84165e`)

Verdict: approve, no blocking finding; three non-blocking cleanups.

- [x] R1-1. `ReplayAdmission.TooOld` documented only `created_time < now − Window`; it now also states the purge-instant case (`created_time + Window + ClockSkew` earlier than the latest purge instant) and that a large step back refuses even fresh messages.
- [x] R1-2. Three regression names still said "readmits" while asserting refusal: the two core red-team tests and the endpoint step-back test (whose comment also still read "DEFECT PROBE"; it now also asserts the 202 and the `TooOld` drop line).
- [x] R1-3. `ace0021` ("Plans", adding `tasks/todo20261112-pr107-rereview.md`) is the owner's commit, made the same way as `a2c6822` ("PR plans") on #94, which added the #106/#107 review records to `tasks/`. Per the owner's instruction it stays as is: not reverted, not rewritten (L-053).
