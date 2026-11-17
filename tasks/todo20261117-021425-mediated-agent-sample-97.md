# todo20261117-021425 — Sample 11 `MediatedAgent`, sample 04 refresh, cookbook CC–FF, docs (#97)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Issue: #97 (epic #88). Base: `d0ccade` (= `origin/main`, the PR #122 merge). Branch:
`feat/mediated-agent-sample-97` in worktree `.claude/worktrees/orch-97` (L-019: re-verify the branch
before every commit). Child engineers work in their own worktrees branched from this branch.

## Binding inputs (epic #88; GitHub comment ids)

- **#97 body**: scope items 1–5, acceptance, owner decisions D1 (exact versions) and D2 (gate list test).
- **6783818453** (after #89): fix README's "six-phase plan" wording and `CONTRIBUTING.md:7`'s PRD link;
  add the PRD §14.2 snippets CC–FF (stub block); fill the architecture doc's Phase 7 section.
- **6786453408** (after #90): delete the 57 "#90 … until #97" DX allowlist entries; extend (not
  replace) "Mediator routing core (Phase 7, #90)".
- **6804962656** (after #91): delete the "#91 … when #97 lands" entries; CC–FF demonstrate
  `WithReturnRoute`, `context.Session` / `ReturnRoute`, `Find`, the `Closed` token, best-effort
  notifications; extend the #91 section.
- **6811440268** (after #92): delete the "#92 … when #97 lands" entries; CC–FF demonstrate
  `SendOptions.ReturnRoute`, `SendResult.Returned`, `UseInboundDispatch()`, `ReceiveInbound`,
  `SendPackedAsync`; move sample 04 onto `SendPackedAsync`; extend the #92 section.
- **6826025398** (after #93): delete `TODO(#97)` #93 entries; README DX paragraph is stale; FF:
  `AddCoordinateMediation()` before `AddRoutingMediator()` with an allow-list `IMediationPolicy`,
  "the first peer to register a DID gets it"; extend the #93 section.
- **6841076730** (after #94): delete `TODO(#97)` #94 entries; DD/EE show `AddMessagePickup()`,
  `MessagePickupQueue.DeliverAsync`, fetch → acknowledge, live mode with acknowledgement,
  at-least-once; extend the #94 section.
- **6852453202** (after #105): delete the replay-skew entry; cookbook shows
  `ReplayProtectionOptions.ClockSkew` and says clocks must agree within a minute by default.
- **6871566704** (owner, direct push → #110): sample 04 keeps its Bob-inbox story through mailbox
  pickup or the host's own sink; no direct push here.
- **6878358421** (after #95): delete `TODO(#97)` #95 entries; sample 11 runs `AddMediator()` +
  `MapDidCommMediator(...)` + `RequireRateLimiting(...)`, shows an allow-list `IMediationPolicy`,
  `RevokeAndPurgeAsync`, the invitation URL; cookbook: at-least-once, a recipient offline longer
  than the age limit loses those messages; `AddMediator()`'s host-`IForwardSink` refusal wording
  ("use `AddRoutingMediator()`") reused; extend the #95 section.
- **6881144022** (owner): RELEASING.md setup-table row → "Push new packages and package
  versions", glob `DidComm.*`; cookbook says a delayed message becoming due is not pushed, so the
  client polls in live mode.
- **6887693439** (after #96): delete `TODO(#97)` #96 entries; sample 11 exercises the listed
  `MediationClient` / `PickupClient` / options / exception / extension members; cookbook wording
  list (live polling permanent, `LivePollInterval`, at-least-once + per-mailbox repeat filter, ack
  on next item / `AckAsync(item.Id)` for the last, failing message discarded with `AckAsync`,
  WebSocket's three set-up calls, clocks within a minute); RELEASING fix; extend the #96 section.
- **6887757919 / 6894546318** (#113): wallet side = `DidComm.Mediator.Client` + a transport;
  mediator side = `DidComm.Mediator` + `DidComm.Mediator.AspNetCore`; cookbook names the package a
  wallet installs; (coordinator's inference) keep the wallet code in a client-only project.
- **6894624361** (owner D1/D2): exact `[x.y.z]` dependencies on internals-coupled edges, list
  from the actual grants, check the packed nuspecs, consumer impact on top of the CHANGELOG entry
  (name `DidComm.Transports.WebSocket`'s changed Core dependency); D2 test with a negative control;
  nuspec reading optional if cheap.
- **6918389034 / 6938333269** (after #119/#120): L-065 test rules + corollary (a test's trigger
  reaches the product the way production's does); long local runs under `caffeinate -dimsu`.
- **Owner decision (2026-11-17), amending #97's acceptance "no allowlist entries for Mediator
  types" for constructors of library-produced results only:** option (a). The seven
  library-constructed constructors (`GrantRevocation`, `PickupDeliveryResult`, `QueueStatus`,
  `RecipientPage`, `SessionRegistration`, `ReturnRouteSessionEventArgs`, `InboundFrame`) become
  permanent DX allowlist entries, each with a one-line "library-constructed result" reason like the
  existing `SendResult` / `UnpackResult` / `ForwardProcessingResult` / `DispatchOutcome` /
  `WebSocketLifecycleEventArgs` entries. Engineers look for an honest sample home first, so the
  list may shrink. Goes in the PR body and in "Planning update after #97".
- Coordinator stops: never patch `PickupClient`'s live stream; never build direct push (#110);
  a sample-exposed library defect is a stop unless small/local/no public API; the DX gate passing
  only via a public API change or Mediator allowlist entries is a stop; L-063 churn guard.

## Design (architect pass, reconciled)

Architect report: agent a243884fcccbecc68. My calls, each with its reason:

1. **Sample 11 = exe + wallet class library.** `samples/11-MediatedAgent/11-MediatedAgent.csproj`
   (`DidComm.Samples.MediatedAgent`, `public static class Program { Main; RunAsync(TextWriter?) }`)
   hosts the mediator and drives the story; `samples/11-MediatedAgent/Wallet/Wallet.csproj`
   (`DidComm.Samples.MediatedAgent.Wallet`, no `Program`) references only
   `DidComm.Mediator.Client` + the transports + DI. *Why:* it is the boundary a wallet developer
   copies (#113), the gate scans every `DidComm.Samples.*.dll` and executes only `Program.RunAsync`,
   and it costs one csproj.
2. **Known loopback port instead of port 0, for the mediator only.** `AddMediator()` checks its
   routing DIDs in `StartingAsync`, before Kestrel binds, and the DID names the address. A shared
   helper `samples/_shared/LoopbackPort.cs` asks the OS for a free loopback port and retries on
   "address in use". *Why:* still OS-assigned and loopback-only (FR-DX-02); production knows its
   address up front, and the README says so. Not a library defect: the check order is deliberate.
3. **Sample 04 = routing relay only** with `AddRoutingMediator(...)`, the registry-aware
   `MapDidCommEndpoint("/didcomm")`, a host `IForwardSink` that relays with `SendPackedAsync` to a
   bounded host directory, `InMemoryForwardRecipientRegistry`, `NullForwardSink` for a registered
   recipient with no inbox, a Discover Features query with `ReturnRoute: All` reading
   `SendResult.Returned`, and a "same relay without a container" section (public `ForwardHandler`
   / `ForwardProcessor` constructors, both `DispatchAsync` overloads, `Dispose`/`DisposeAsync`).
   Keeps the SSRF-guard demonstration.
4. **Cookbook split by audience.** CC (enroll; wrong-peer `MediationNotSupportedException`; the
   Coordinate Mediation messages on the wire; queue-transport URI) · DD (status → fetch → ack;
   the Message Pickup messages on the wire; `MessagePickupQueue.DeliverAsync`; **the message-store
   contract walk** — moved here from the architect's sample 11 §8 because it is the mailbox's own
   task and keeps sample 11 the end-to-end story) · EE (a return-route session handler; the
   low-level `IInboundFrameConsumer` transport; live pickup) · FF (the 20-line mediator; composing
   the roles yourself, with and without a container; **the replay guard and clock skew** — moved
   here from sample 11 §2 because it is a mediator operator's setting). Overlap between owners is
   allowed; every member needs at least one.
5. **D1 mechanism:** a target in `Directory.Build.targets` after NuGet's
   `_GetProjectReferenceVersions` rewrites `ProjectVersion` to `[%(ProjectVersion)]` for each
   referenced project that grants `InternalsVisibleTo` to this assembly (queried from the
   referenced project, so the grant is the single source of truth), plus a post-`GenerateNuspec`
   check that reads the generated `.nuspec` and fails pack when a friend dependency is not exact.
   *Why the in-pack check, not a test:* CI tests before it packs and packs on ubuntu only; a pack
   failure gates CI and the release workflow alike with no network and no skipped test. Negative
   control: `-p:DidCommSkipFriendPin=true` must fail pack with the check's message.
6. **An unused grant is removed, not pinned.** The architect found `DidComm.Core` →
   `DidComm.Adapters.NetDid` unused. If a build without it is green (and package validation is
   clean), the grant goes and the published adapter keeps `>=`. *Why:* the owner's rule is for
   packages that *call* each other's internals; pinning a published package for a dead grant is
   consumer impact for nothing. Every other grant is verified used the same way.
7. **D2:** `tests/DidComm.InteropTests/DxCoverage/ShippedAssembliesListTests.cs` + a pure helper:
   packable `src/*/*.csproj` == `CoverageComputation.ShippedAssemblies` (negative control: an
   injected extra packable name fails and is named); the friend-edge list from the csproj grants
   is pinned (a new grant = new consumer impact, reviewed deliberately; negative control the same
   way); the nuspec rule unit-tested on synthetic XML (`>=` fails, `[v]` passes, missing fails).
8. **Seven library-constructed constructors** (`GrantRevocation`, `PickupDeliveryResult`,
   `QueueStatus`, `RecipientPage`, `SessionRegistration`, `ReturnRouteSessionEventArgs`,
   `InboundFrame`) have no honest sample home short of a full custom store / registry / receiving
   transport. Asked the owner; **decided 2026-11-17: option (a)**, permanent "library-constructed
   result" entries (see the binding inputs). Engineers first look for an honest home (e.g.
   `InboundFrame` from a custom receiving transport).
9. **Doc drift found by the architect:** `IMessageStore.PurgeRecipientAsync`'s remarks say the
   pickup role calls it; nothing in `src` does. Text-only fix in the remark and the architecture doc.

## Checklist

### 0. Setup and scaffold (orchestrator)
- [x] Worktree + submodule (`dd56125`), branch verified.
- [x] Architect design pass; calls recorded above.
- [ ] Scaffold commit: `samples/_shared/LoopbackPort.cs`; cookbook csproj references to the three
      mediator packages; `Section_CC…FF` stubs (banner only) + `Program.cs` calls; CC–FF banners in
      `CookbookSmokeTests`; this plan.

### 1. Implementation (one engineer per lane, disjoint files)
- [ ] **A — sample 11** (`samples/11-MediatedAgent/**`, `MediatedAgentSmokeTests.cs`, `DidComm.sln`,
      InteropTests csproj reference). Sections: (1) mediator start, DID via `DidCommServiceBuilder`,
      `AddMediator` + `MapDidCommMediator` + `RequireRateLimiting`, invitation URL, options
      `Validate`; (2) Bob discovers the mediator from `/oob`, enrolls, registers his public DID,
      builds his DID document with `ForMediation(grant)`, query + page walk; (3) Alice sends with
      `Forward: true`, resolved route shown; (4) HTTP status 1 → fetch → unpack → ack → status 0;
      (5) WebSocket: three set-up calls, live delivery on, Alice sends again, Bob receives, ack,
      session `Find`; (6) operator actions (`IMediationStore` console, queue `RemoveAsync` /
      `PurgeExpiredAsync` / `RevokeAndPurgeAsync`, `PurgeRecipientAsync`) and teardown with the
      session's `Closed` token; (7) `mediate-deny` via an allow-list policy. Smoke test pins each
      section's markers and the wallet project's reference boundary.
- [ ] **B — sample 04** (`samples/04-MediatorAgent/**`, `MediatorAgentSmokeTests.cs`).
- [ ] **C1 — cookbook CC + DD** (+ `Section_Z` `TransportRequest` properties) and the
      `CookbookMediatorHost` helper if CC/DD want one.
- [ ] **C2 — cookbook EE + FF.**
- [ ] **D — packaging**: D1 target + nuspec check, unused-grant verification, D2 tests,
      `RELEASING.md` row, `CONTRIBUTING.md:7` link.
- [ ] Each engineer runs both DX gates in its worktree and hands back the list of allowlist
      entries its work covers (no JSON edits), plus its transcript markers and README text.

### 2. Integration (orchestrator)
- [ ] Merge D, B, C1, C2, A. Delete every allowlist entry waiting on #97 (421 coverage, 366
      execution). Run both gates; close residual gaps in the owning sample (never with entries,
      except the owner-approved library-constructed constructors of item 8).
- [ ] Cookbook smoke markers and README merged.
- [ ] `IMessageStore.PurgeRecipientAsync` remark fixed.

### 3. QA (sub-agents)
- [ ] `dotnet run` for 04, 11 and the cookbook exits 0 with no network (loopback only, OS-assigned
      ports, no fixed sleeps).
- [ ] Smoke tests 04, 11, cookbook green; repeated runs and a run under parallel load (L-065).
- [ ] DX gate green with no Mediator entries; negative control: delete one sample call → the gate
      names the member.
- [ ] D2 negative controls; nuspec check negative control (`-p:DidCommSkipFriendPin=true`);
      packed nuspecs show `[1.6.0]` on exactly the friend edges; package validation negative
      control (a public type made internal in a scratch copy → `CP0001`).
- [ ] D1 graph check: a consumer project mixing versions gets NU1608 / NU1107 at restore.

### 4. Adversarial (~30 min)
- [ ] Copy-risk review of the samples: unauthenticated return route, SSRF guard narrowness,
      logging of DIDs / keys / wire text, unbounded stores or directories, the deny policy's
      correctness (single pass, no early exit, DID subjects), D1 ranges (broken graphs).
      Each finding: severity, fixed or declined with a reason.

### 5. Docs
- [ ] PRD §14.2 CC–FF snippets, §14.3 rows 02/04/11, §14.4 Mediation row (and the test's matrix
      row), §12 Phase 7 status.
- [ ] `docs/codebase-architecture.md`: extend the #90–#96 sections with "Demonstrated by", the
      packaging rule, "Phase 7: still to build".
- [ ] README: §12 wording, DX paragraph and numbers, sample 11 row, mediator package rows checked.
- [ ] CHANGELOG `[Unreleased]`: consumer impact of D1 first, then the Samples entry.

### 6. Verify and ship
- [ ] `dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release
      --no-build` · `dotnet pack DidComm.sln -c Release --no-build` (under `caffeinate -dimsu`).
- [ ] PR against `main` ("Closes #97", "Part of #88"); CI green on the head (ubuntu + windows).

## Review
(filled in as the work lands)
