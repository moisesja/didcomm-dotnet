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
- D6. No change to `IInboundReplayGuard`, `InboundReplayGate`, `ReplayAdmission` (the `FutureDated` doc names the new setting), the scope rule, or any endpoint.

## Threat model (L-031)

- The future window widens from 0 s to 60 s by default. Who can use it: only a sender that authcrypts to us sets `created_time` (it is inside the encrypted, authenticated plaintext); a replaying attacker can only present captured messages whose `created_time` is fixed. A captured message is still admitted at most once per (sender subject, id) within its retention, and is `TooOld` after `created + Window`; widening the future bound adds no instant at which a replay is admitted (the sweep tests prove it for future-dated `created`).
- Memory / availability lever: an authenticated peer (including a fresh self-made DID) can stamp `now + ClockSkew` so each of its entries lives `Window + 2 × ClockSkew` (420 s vs 300 s before at skew 0). Entries remain bounded by `MaxEntries` / `MaxEntriesPerPeer`; saturation remains fail-closed (availability only, W-2). Stated in the docs.
- Host clock: the W-1 limitation (receiver clock jumping forward past retention and back) keeps its shape with `ClockSkew` in place of `ExpiresClockSkew`.
- Hostile configuration: negative and over-`int.MaxValue`-second values refused by `Validate()`; a value mutated after validation is clamped at ≥ 0, and the largest `TimeSpan` cannot overflow the `long` arithmetic (test).

## Steps

### A. Core
- [ ] A1. `ReplayProtectionOptions.ClockSkew` + `Validate()` + XML docs (incl. `MaxEntries` / `MaxEntriesPerPeer` figures, `Window` doc mentions the skew in retention).
- [ ] A2. `InMemoryReplayGuard`: read `ClockSkew`; drop `DidCommOptions` from both constructors; remarks rewritten (rules, retention, clock, worst-case retention).
- [ ] A3. `ReplayAdmission.FutureDated` doc; `DidCommOptions.ExpiresClockSkew` doc; `ForwardProcessingResult` / `MessagePickupQueue` doc lines that cite `ExpiresClockSkew` stay (expiry) — check wording only.
- [ ] A4. `AddDidComm` registration + XML doc; `InboundAdmissionDefaults.Guard` (AspNetCore fallback).

### B. Tests (the issue's acceptance list is the bar)
- [ ] B1. Guard unit tests: helpers set `ClockSkew`, not `ExpiresClockSkew`; defaults `now + 59` / `now + 60` admitted, `now + 61` `FutureDated`; `ClockSkew = 0` reproduces the old edges; the per-second sweep for `created` in the future (0, +30, +59, +60) at skews 0 / 30 / 60 — no instant admits the replay, `Duplicate` until `created + Window + ClockSkew`, `TooOld` after; `ExpiresClockSkew` alone (0, 1 h, negative) changes nothing; clamp of a post-validation negative value; extreme values (largest skew, `DateTimeOffset.MaxValue`) never wrap.
- [ ] B2. Options tests: default 60 s; negative and over-bound refused by `Validate()`, by the guard constructor and at `AddDidComm` resolution.
- [ ] B3. Expiry unaffected: `UnpackAsync` with default options rejects `expires_time = now − 1` and accepts `now`; still rejects it with `ReplayProtectionOptions.ClockSkew` set large (different options object; proves independence); FR-ROUTE-09 tests unchanged.
- [ ] B4. Server endpoint (HTTP + WS): with the default options, `created_time = now + 59` → handler invoked; `now + 61` → 202, handler not invoked, drop line `FutureDated`; `ExpiresClockSkew = 120 s` with default `ClockSkew` → `now + 61` still dropped; existing "clock moves backwards" test moves its 120 s to `ConfigureReplay`.
- [ ] B5. Client inbound consumer (WebSocket push): server clock ahead by 59 s → push dispatched; by 61 s → dropped (client log line `FutureDated`); client `ClockSkew = 0` → a push 1 s ahead dropped.
- [ ] B6. Two hosts 30 s apart (#98 clock-skew case), real `DidCommClient` against the TestServer mediator with `AddCoordinateMediation()` + `AddMessagePickup()`, client clock = server ± 30 s, over HTTP and over WebSocket: trust ping with `return_route: all`; `mediate-request` → `mediate-grant`, `recipient-update` → response; `status-request` → `status`, `delivery-request` → `delivery`, `messages-received` → `status` 0. Negative control: the same with `ClockSkew = 0` on both sides fails where the old behaviour failed (client ahead: the server drops the request; server ahead over WS: the client drops the reply).
- [ ] B7. DX allowlists: rename the two `InMemoryReplayGuard` ctor entries; add `ReplayProtectionOptions::prop:ClockSkew` where the gate needs it (`TODO(#97)` reason); prove the gate is live with a negative control (remove the entry → red).

### C. Docs
- [ ] C1. PRD FR-RR-08 (1), (4), (5) and its acceptance column; FR-RR-09 if it names the skew; §8a list if needed.
- [ ] C2. `docs/codebase-architecture.md` (replay guard section; #92 known-limitation bullet on clock skew).
- [ ] C3. CHANGELOG `[Unreleased]`: new `### Changed — replay admission has its own clock-skew tolerance (FR-RR-08, #105)` flagging the behaviour change; earlier `[Unreleased]` #91 / #92 entries that state `ExpiresClockSkew` for replay are corrected in place (one release, one contract — L-045).

### D. Verification and review passes
- [ ] D1. Architect pass (Plan agent) on this design.
- [ ] D2. Two independent QA agents (boundaries and clock advancement; `ClockSkew = 0` differential against `main`; client and server; concurrency; the retention/age seam; expiry unaffected).
- [ ] D3. One independent adversarial agent (widened future window as a cache/memory lever; off-by-one; skew vs `Window`; overflow at extreme values; config mutation after validation).
- [ ] D4. Full build / test / pack green; counts per project recorded below.

## Review

(filled in at the end)
