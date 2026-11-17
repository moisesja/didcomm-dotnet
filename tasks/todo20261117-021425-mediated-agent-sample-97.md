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
- [x] Scaffold commit: `samples/_shared/LoopbackPort.cs`; cookbook csproj references to the three
      mediator packages; `Section_CC…FF` stubs (banner only) + `Program.cs` calls; CC–FF banners in
      `CookbookSmokeTests`; this plan.

### 1. Implementation (one engineer per lane, disjoint files)
- [x] **A — sample 11** (`samples/11-MediatedAgent/**`, `MediatedAgentSmokeTests.cs`, `DidComm.sln`,
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
- [x] **B — sample 04** (`samples/04-MediatorAgent/**`, `MediatorAgentSmokeTests.cs`).
- [x] **C1 — cookbook CC + DD** (+ `Section_Z` `TransportRequest` properties) and the
      `CookbookMediatorHost` helper if CC/DD want one.
- [x] **C2 — cookbook EE + FF.**
- [x] **D — packaging**: D1 target + nuspec check, unused-grant verification, D2 tests,
      `RELEASING.md` row, `CONTRIBUTING.md:7` link.
- [x] Each engineer runs both DX gates in its worktree and hands back the list of allowlist
      entries its work covers (no JSON edits), plus its transcript markers and README text.

### 2. Integration (orchestrator)
- [x] Merge D, B, C1, C2, A. Delete every allowlist entry waiting on #97 (421 coverage, 366
      execution). Run both gates; close residual gaps in the owning sample (never with entries,
      except the owner-approved library-constructed constructors of item 8).
- [x] Cookbook smoke markers and README merged.
- [x] `IMessageStore.PurgeRecipientAsync` remark fixed.

### 3. QA (sub-agents)
- [x] `dotnet run` for 04, 11 and the cookbook exits 0 with no network (loopback only, OS-assigned
      ports, no fixed sleeps).
- [x] Smoke tests 04, 11, cookbook green; repeated runs and a run under parallel load (L-065).
- [x] DX gate green with no Mediator entries; negative control: delete one sample call → the gate
      names the member.
- [x] D2 negative controls; nuspec check negative control (`-p:DidCommSkipFriendPin=true`);
      packed nuspecs show `[1.6.0]` on exactly the friend edges; package validation negative
      control (a public type made internal in a scratch copy → `CP0001`).
- [x] D1 graph check: a consumer project mixing versions gets NU1608 / NU1107 at restore.

### 4. Adversarial (~30 min)
- [x] Copy-risk review of the samples: unauthenticated return route, SSRF guard narrowness,
      logging of DIDs / keys / wire text, unbounded stores or directories, the deny policy's
      correctness (single pass, no early exit, DID subjects), D1 ranges (broken graphs).
      Each finding: severity, fixed or declined with a reason.

### 5. Docs
- [x] PRD §14.2 CC–FF snippets, §14.3 rows 02/04/11, §14.4 Mediation row (and the test's matrix
      row), §12 Phase 7 status.
- [x] `docs/codebase-architecture.md`: extend the #90–#96 sections with "Demonstrated by", the
      packaging rule, "Phase 7: still to build".
- [x] README: §12 wording, DX paragraph and numbers, sample 11 row, mediator package rows checked.
- [x] CHANGELOG `[Unreleased]`: consumer impact of D1 first, then the Samples entry.

### 6. Verify and ship
- [x] `dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release
      --no-build` · `dotnet pack DidComm.sln -c Release --no-build` (under `caffeinate -dimsu`).
- [x] PR against `main` ("Closes #97", "Part of #88"); CI green on the head (ubuntu + windows): PR #123, head `2a4382f`, run 44111807996 (ubuntu + windows build/test, pack).

## Review

### What landed (branch `feat/mediated-agent-sample-97`)
- Lanes merged: D packaging (`52873f1`), B sample 04 (`09253f2`), C1 cookbook CC/DD/Z (`095c64e`),
  C2 cookbook EE/FF (`13963c6`), A sample 11 (`d6d8c41`); integration `88c063f`; fix batch
  `4107477`..`7a1a76e`; docs commit on top.
- DX gate: 421 + 366 `#97` entries deleted; 7 owner-approved library-constructed constructors added
  (reference gate only — they execute, the library builds them); execution allowlist empty;
  994/1021 executed, 27 not measurable; §14.4 Mediation row added to the matrix test.
- D1: 8 exact edges, derived from the grants; Core → Adapters.NetDid grant proven unused (82/20/4/50/4/2/4/2
  CS errors for the used ones; none for the adapter, identical IL) and removed; pack fails on a
  non-exact friend dependency. Published packages whose Core dependency changes: AspNetCore,
  Extensions.DependencyInjection, Transports.WebSocket.
- D2: `ShippedAssembliesListTests` (7 tests) with negative controls.

### QA (two passes) — all permutations PASS
- QA1 (samples): 10 sequential + 36 concurrent runs exit 0; lsof + a macOS sandbox denying
  non-loopback traffic show loopback only; every wait is on the event the next line reads; smoke
  tests green 10×, under 14 CPU burners, and beside Mediator.Tests; 7 mutations each fail their
  smoke test, 6 slow-but-correct variants pass; the LoopbackPort retry path forced and proven.
  Findings: L1 wallet boundary test missed unused references → fixed (restore-graph check, proven
  to bite); L2 sample 04 §8 line true by construction → removed; L3 FF.1 Carol note unconditional →
  fixed; nit `Task.WhenAny` hang guards → `WaitAsync`.
- QA2 (gates): every negative control bites — DX static (3 members) and execution (wallet assembly
  instrumented), each of the 7 entries, redundant/stale hygiene, the matrix row, D2 (fake project,
  new grant, attribute grant), the pack check (skip-pin fails 6 packages, no nupkg left),
  package validation (`CP0001` on a type made internal), the release workflow runs the same targets,
  no network needed. Consumer graph: NU1608 / NU1107 as designed. Findings: M1 CHANGELOG pending →
  written; M2 detection only among packages packed with the rule → CHANGELOG + RELEASING say so;
  L1 `symbols.nupkg` format broke the check → package selected by exact name; L3 strong-name grant
  text → compared before the comma (pin + test); L2 (failed solution pack leaves other packages
  unchecked: release uses a fresh dir and stops) and L4 (negative-control tests start from the live
  repo, noisy on real drift) declined as harmless; L5 (`PackageValidationBaselineVersion` still
  1.5.0 though 1.5.1 is published) pre-existing → for the owner.

### Adversarial (one pass) — no High
- ADV-1 Medium, sample 04 relay had no per-relay bound on the single forward pump → 5 s bound per
  relay, failures recorded, known limit stated (L-064). Library side noted for a follow-up: the HTTP
  transport's circuit breaker is transport-wide.
- ADV-2 Medium, FF.2(b) dispatched without FR-RR-08 admission → the sample now runs `TryAdmit` with
  the library's scope rule rebuilt from public APIs and shows a replay refused; handler docs point at
  the rule. Classified sample-only (FR-RR-08 binds receive endpoints; `ProtocolDispatcher` documents
  that a receive path of your own must admit). Follow-up idea: make the scope helper public.
- ADV-3 Medium, sample 04 passed a DID-derived URL as `ServiceEndpointOverride` → the mediator DID
  carries its own service; override dropped; Section Z says a network transport must run the guard.
- ADV-4 Medium, the wallet's invitation fetch bypassed the guard → `SocketsHttpHandler` with the
  guard's `ConnectAsync`, no redirects, 64 KiB cap, scheme check.
- ADV-5..10 Low and the Info items: per-client limiters in the copyable hosts, comments elsewhere;
  relay failure records for every failure; the sample's raw-id log line dropped; comments on the
  narrated guard messages; EE frames checked by DID subject; "the allow-list gates only the first
  grant"; replay budget back to defaults in sample 11; "this host (on every port)"; host directory
  filled by the operator only; `NoticeHandler` registration swap made safe. Declined: printing the
  sanitized `DisclosedProtocols`/`ProblemCode` in CC (it is the member being demonstrated); reusing
  a secrets resolver across LoopbackPort attempts (harmless); sample `Main` printing the exception
  (every sample does).

### Library observations (recorded, not patched)
- `HttpDidCommTransport` connect failures reach `ITransportRouter.SendAsync` callers as
  `HttpRequestException`, not the documented `TransportException`; reproduced by the skipped
  `HttpTransportConnectFailureContractTests` (fails when un-skipped). Facade callers are unaffected.
- The HTTP transport's Polly circuit breaker is shared by all hosts.

### Owner decision during the run
- 2026-11-17: the seven library-constructed constructors are permanent reference-gate entries
  (option a).

### Review round 1 (PR #123, review 5945111006, reviewer notes `tasks/todo20261117-pr123-review.md`)

Three P2 findings, all in this PR's own code; fixed in one round, each with a regression test or CI
step that fails on the submitted head:
- **Proxy bypass in the wallet's invitation fetch** (`Wallet/RecipientWallet.cs`): the guarded
  `SocketsHttpHandler` kept the default `UseProxy`, so with a configured proxy the guard vetted the
  proxy's address and the proxy received `CONNECT` for the forbidden host. Fix: `UseProxy = false`,
  with the reason in a comment and the README. Regression: `MediatedAgentWalletProxyTests` (a
  stand-in proxy on loopback through `HttpClient.DefaultProxy`, in a non-parallel collection): the
  fetch of `https://10.0.0.1/…` is refused by the guard and the proxy accepts no connection.
  Mutation (`UseProxy` removed): fails at once.
- **Pack check vs build metadata** (`Directory.Build.targets`): the verifier looked for
  `$(PackageId).$(PackageVersion).nupkg`, but NuGet names the file by the normalized version (no
  build metadata). Fix: take the package pack reports writing (`@(_OutputPackItems)`, `.nupkg`, not
  `.symbols.nupkg`). Proven: `-p:DidCommVersion=1.5.1+review` packs (deps `[1.5.1]`) with the new
  targets and fails with the old ones; the ordinary version, `SymbolPackageFormat=symbols.nupkg`
  and the skip-pin negative control (also with metadata) behave as before. Regression: a CI step
  packs `DidComm.Mediator.AspNetCore` at `1.0.0+ci.<run>` after the ordinary pack.
- **Live iterator disposed mid-read** (cookbook EE `NextLiveAsync`, sample 11
  `ReceiveNextAsync` + `LiveConnection.DisposeAsync`): on the hang-guard path the stream was disposed
  while `MoveNextAsync` was still running. Fix: one helper, `samples/_shared/GuardedStreamReader<T>`,
  used by both: on a timeout it cancels the read and waits for it to end before reporting the
  timeout; disposing with a read running does the same before disposing the stream; with no read
  running, disposing is a normal end (no cancellation, so the live stream still acknowledges and
  turns live delivery off). Regression: `GuardedStreamReaderTests` on the manual clock, with a stream
  whose clean-up is held by a gate (3 tests). Mutation (no wait for the running read): the two
  timing tests fail.

Recorded, not changed here (**owner decision**, brought to the coordinator): the shipped HTTP and
WebSocket transports build their SSRF-pinned `SocketsHttpHandler` with the default `UseProxy` too.
Probe at this head through `ITransportRouter` with a loopback stand-in proxy: with the default policy
the guard refuses the proxy's own address (so a proxy on a private address breaks every send); with
the proxy's address allowed (as a public proxy would be), the transport sent `CONNECT 10.0.0.1:443`
to it. The facade's pre-send check still stops literal private IPs; DNS names and direct router
callers are exposed. Pre-existing in released packages.
