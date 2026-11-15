# todo20261114-214112 — Split the recipient-side client into `DidComm.Mediator.Client` (#113)

Issue: #113 (epic #88; owner decision R-9, 2026-11-14, comment 6887757919). Base: `origin/main` at `5477971` (PR #112, #96).
Branch: `feat/client-package-split-113` in worktree `.claude/worktrees/orch-113` (L-019: re-verify the branch before every commit).
The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.
D1 on #88 (comment 6881144022): trusted publishing already matches `DidComm.*` with "Push new packages and package versions", so the new id needs no setup. The RELEASING setup-table fix stays with #97.

Scope: packaging only. No behaviour change, no public API change, no renames, no namespace change, no DI-registration change, no clean-ups.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`. Interop suite runs with `git submodule update --init` (fixtures 149–150 files).
Baseline at `5477971`: Core 2,548; Mediator 4,251 + 8 skipped; Interop 1,370 + 1 skipped.

## Judgement calls (mine, after the architect pass; each with a reason)

- J-1. **Cut line: 41 files move whole, two files split.** Moving whole (`git mv`, path prefix only):
  all 32 files of `src/DidComm.Mediator/Client/**`; `Coordination/{CoordinateMediation,CoordinateMediation20,MediationBodies,RecipientUpdate}.cs`;
  `Pickup/{MessagePickup,PickupBodies,PickupRecords}.cs`; `Internal/{WireJson,MediatorLog}.cs`.
  Reason: the client uses each of them, or a moved public signature exposes it (L-006: `CoordinateMediation`
  exposes the `RecipientUpdate` family; `MessagePickup` exposes `PickupStatus` and `DeliveredMessage`). Every public
  signature in the moving set reaches only moved, Core, DI or BCL types, so no API change is needed.
- J-2. **`Coordination/MediationGrant.cs` is split.** `MediationGrant` moves (the client returns it, `ForMediation` takes it);
  `RecipientPage` and `RecipientStoreOutcome` stay in `DidComm.Mediator` (store-contract types: `IMediationStore`
  returns/uses them; `RecipientPage`'s doc crefs `IMediationStore`). The server half keeps the history (`git mv` to
  `RecipientPage.cs`); the client gets a verbatim copy of the `MediationGrant` declaration. Reason: a wallet should not
  carry store-contract types, and moving the whole file would need a cref edit anyway.
- J-3. **Internal `BareDid` (in `Coordination/CoordinateMediationOptions.cs`) moves to the client as its own file**
  `Coordination/BareDid.cs`, verbatim; `CoordinateMediationOptions` stays. Reason: the client and the server both use it;
  it must be declared exactly once (a linked copy would be CS0436 under IVT).
- J-4. **`MediatorLog` moves whole, not split.** It holds four helper methods and two consts: no `[LoggerMessage]`, no
  EventId, no template. The server reaches it through the client's IVT. Reason: no behaviour can change, smallest diff.
- J-5. **Two XML-doc crefs become `<c>`** (doc-only): `CoordinateMediation.cs:21` `CoordinateMediationHandler`,
  `MessagePickup.cs:21` `MessagePickupHandler`. Reason: the target stays in `DidComm.Mediator`; CS1574 under `/warnaserror`.
- J-6. **InternalsVisibleTo** (every grant, why):
  | Grant | Status | Needed by |
  |---|---|---|
  | Core → `DidComm.Mediator.Client` | new | the moved code's Core internals: `IInboundCorrelator`, `InboundMessageSnapshot` (+ `Pthid`, `Verified`, `DeserializeMessage`, `TryGetFor`), `ReturnRouteEligibility.TryGetPeer`, `DidCommClient.UnpackDeliveredAsync` / `ResolveDirectEndpointAsync` / `ReceivesPushesOn` / `DeliveredUnpack`, `DidSubject`, `IdentifierLimits`, `LogValues`, `UnreservedUriChars`, `Base64Url` |
  | Core → `DidComm.Mediator` | kept | server code still uses `DidSubject`, `ForwardMetrics`, `ForwardOutcomes`, `IdentifierLimits`, `ReturnRouteEligibility`, `UnreservedUriChars` |
  | Client → `DidComm.Mediator` | new | server use of moved internals: `MediatorLog`, `BareDid`, `PickupBodies.MaxIdLength`, `CoordinateMediation.TryParseRecipientUpdate(Message, int, out …)`, `MessagePickup.TryParseMessagesReceived(Message, int, out …)` |
  | Client → `DidComm.Mediator.Tests` | new | tests of moved internals (channel, codecs, bodies, …) |
  | Client → `DidComm.InteropTests` | new | interop tests of moved internals (channel, CM 2.0 codec, …) |
  | Client → `DidComm.Mediator.AspNetCore` | **not granted** | it touches no moved internal |
  | `DidComm.Mediator` → Tests, InteropTests, Mediator.AspNetCore | unchanged | |
  Net effect: the moved internals were visible to `DidComm.Mediator`, Mediator.Tests, InteropTests and Mediator.AspNetCore;
  now to the first three only (narrower). Core's list gains one name, behind which is the same code that had the access.
- J-7. **Client references:** `DidComm.Core`, `DidComm.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`. No explicit `Microsoft.Extensions.Options` (it arrives through Core → NetDid.Core
  and through the DI project, as for `DidComm.Transports.WebSocket`; losing it would be a compile error, not silent).
  `DidComm.Mediator` keeps all its references and gains a ProjectReference to the client.
- J-8. **Folder layout mirrors the old paths** (`Client/`, `Client/Internal/`, `Coordination/`, `Pickup/`, `Internal/`) with
  `RootNamespace=DidComm.Mediator`. Reason: pure-prefix renames (100% similarity), folders still match namespaces.
- J-9. **Tests stay in `DidComm.Mediator.Tests`**; the two test projects get an explicit ProjectReference to the client.
  Reason: no test churn, counts trivially comparable; the tests compile against client internals.
- J-10. **The dependency pin test** lives in `tests/DidComm.Mediator.Tests/Client/ClientPackageReferencesTests.cs` (that host runs on
  `Microsoft.NETCore.App` only, so .NET 10 package pruning does not hide Hosting.Abstractions from its deps.json, unlike InteropTests):
  (a) the client's non-BCL metadata references pinned to an exact list; (b) the cut line: named types in the right assembly and
  no `DidComm.*` type in both; (c) a walk of the test host's deps.json from the `DidComm.Mediator.Client` node: no
  `DidComm.Mediator`, `DidComm.Mediator.AspNetCore`, `DidComm.AspNetCore`, `Microsoft.Extensions.Hosting*`, `Microsoft.AspNetCore.*`,
  with positive controls (the `DidComm.Mediator` closure does contain Hosting.Abstractions and the client) and a precondition that the
  host's frameworks are `Microsoft.NETCore.App` only; (d) a self-test of the walk over an in-memory deps document where the
  client depends on `DidComm.Mediator` (the permanent stand-in for the issue's literal negative control, which cannot build: NU1108 cycle).
- J-11. **DX allowlists: 111 coverage / 108 execution entries change prefix, not only the 43/42 tagged `#96`.** The issue text names the
  43/42 client entries; the #93/#94-tagged entries of the moved shared types (`CoordinateMediation`, `MessagePickup`, `MediationGrant`,
  the `RecipientUpdate` family, `PickupStatus`, `DeliveredMessage`) also carry the assembly in their id and must follow, or the gate
  reports them stale and their members uncovered. Totals unchanged (433 / 366); the 43/42 `TODO(#97)` client entries now name
  `DidComm.Mediator.Client`. Rewrite by script (L-049), exact outer-type match (not prefix: `CoordinateMediationOptions` etc. stay).
- J-12. **Docs:** PRD §3.1 (+ the scope-table row that names the package, L-045), architecture doc, README, RELEASING, CHANGELOG
  (new entry + the Unreleased #93/#94/#96 lines that place these types in `DidComm.Mediator`), and the three Core source comments that
  name `DidComm.Mediator` as the user of a Core internal. No runtime text changes. `Directory.Build.targets`' stale "six" comment is
  left alone (pre-existing, no clean-ups).

## Threat model (a move)

- **No internals wider than before** — J-6; the adversarial pass checks every grant.
- **No new dependency in the client's restore graph** — the client's graph must be a subset of the old `DidComm.Mediator` graph, without
  `Microsoft.Extensions.Hosting.Abstractions`; checked on `project.assets.json` (QA) and pinned by J-10 (CI).
- **No behaviour change** — moved files are byte-identical apart from the two crefs; the two splits copy declarations verbatim;
  logger categories (type full names) and namespaces unchanged; no EventIds, Meters or ActivitySources involved.
- **No DI registration change** — `MediatorBuilderExtensions.cs` untouched; `MediatorClientBuilderExtensions.cs` a pure rename.
- **No type-identity split** — every type declared once (J-10 b); the DX execution gate loads only shipped assemblies into its isolated
  load context, so the client must be in `ShippedAssemblies` or it would load beside an isolated Core.
- **No public API change** — mechanical comparison of full type + member names between `5477971` and head, ignoring the assembly (QA).

## Checklist

### A. The move
- [x] A1 `git mv` the 41 files (J-1, J-8); split `MediationGrant.cs` (J-2) and `BareDid` (J-3); the two cref edits (J-5).
- [x] A2 New `src/DidComm.Mediator.Client/DidComm.Mediator.Client.csproj` (J-7, `EnablePackageValidation=false`, lockstep version, IVTs J-6).
- [x] A3 `DidComm.Mediator.csproj` gains the client reference; `DidComm.Core.csproj` gains the IVT with a comment.
- [x] A4 Solution entry under the `src` folder; test projects reference the client (J-9).
- [x] A5 Build green under `/warnaserror`.

### B. Gates and tests
- [x] B1 DX gate: `ShippedAssemblies` + "nine"; `ExtensionMemberSurfaceTests` prefix and dll; allowlist rewrite by script (J-11), counts recorded.
- [x] B2 Dependency pin test (J-10), red first to read the real reference list, then green.
- [ ] B3 Full test run; counts against the baseline.
- [x] B4 Pack: 9 `.nupkg` + 9 `.snupkg`; nuspec dependencies per package.

### C. Negative controls (run once, record, revert; re-diff csproj after, L-023)
- [x] C1 NC-1 unused Hosting.Abstractions PackageReference on the client → deps walk red.
- [x] C2 NC-2 NC-1 + a use of `IHostedService` → metadata pin red too.
- [x] C3 NC-3 `DidComm.AspNetCore` ProjectReference on the client → walk/precondition red.
- [x] C4 NC-5 `DidComm.Mediator` ProjectReference on the client → restore fails NU1108.
- [ ] C5 API-equivalence diff fails when one moved public member is renamed.
- [x] C6 DX gate fails when one allowlist entry is dropped.
- [x] C7 Interop fixtures populated (149–150 files).
- [x] C8 Pack validation still bites on a released package (CP0002 on a renamed released member).

### D. Docs
- [x] D1 PRD §3.1 package rows (+ scope-table row).
- [x] D2 Architecture doc: Packages rows, "nine shipped assemblies", Coordination/Pickup/client section locations, tests list.
- [x] D3 README: package tables, tree, "nine shipped assemblies".
- [x] D4 RELEASING: table, "nine" ×2, lockstep IVT paragraph, New-package-ids paragraph (three ids).
- [x] D5 CHANGELOG (Unreleased): what a wallet references, what a mediator references; older Unreleased lines aligned.
- [x] D6 Source comments that name the package.

### E. Independent passes
- [ ] E1 QA pass: API equivalence, restore graph + wallet-only consumer enrols and picks up against the #95 test server, counts, DX gate, pack/nuspecs.
- [ ] E2 Adversarial review of the diff (public API, IVT width, type identity, DI, load/trim, dependency leak).
- [ ] E3 Fix what they confirm; per-component defect tally (L-063).

### F. PR
- [ ] F1 PR opened (`Closes #113`, `Part of #88`); CI checked once; hand back.

## Results

### Implementation (orchestrator, directly)
- Commits: `b5c6c5c` (move), `122e958` (DX gate + pin test), `f4f9e3d` (docs).
- Build `/warnaserror` green first time after the move (no fix beyond J-5 needed).
- Pin test: the client's non-BCL references are exactly `DidComm.Core`, `DidComm.Extensions.DependencyInjection`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`.
- Allowlist rewrite: coverage 111 entries changed (client 111 / server 165 / total 433; 43 of 43 `#96`-tagged now client);
  execution 108 changed (client 108 / server 165 / total 366; 42 of 42).
- First full run: Core 2,548 passed; Interop 1,370 passed + 1 skipped; Mediator 4,257 passed + 1 failed + 8 skipped
  (4,251 + 7 new). The failure (`MediatorClientTimeoutRaceTests.When_the_deadline_and_the_callers_cancellation_are_both_set_the_cancellation_wins`)
  is a pre-existing flake: 12 full Mediator runs each on `5477971` and on the branch failed 2/12 and 3/12, in the same
  three timing tests (that one, `PickupClientLiveAdv5Tests.A_fifty_round_drain_slower_than_the_interval_is_never_pre_empted…`,
  `LivePushTests.A_live_push_that_ignores_its_deadline_keeps_its_grant_waiting…`); 0/30 in isolation. Follow-up, not this PR.
- Pack: 9 `.nupkg` + 9 `.snupkg`. Client nuspec: Core, DI package, DI.Abstractions, Logging.Abstractions (all 1.5.1 / 10.0.14);
  Mediator nuspec adds `DidComm.Mediator.Client`; README packed in the client package.

### Negative controls (each reverted; `git status` clean after)
- NC-1 unused `Microsoft.Extensions.Hosting.Abstractions` PackageReference on the client: the graph walk fails, the metadata pin passes (as designed).
- NC-2 NC-1 + `typeof(IHostedService)` in the client: the metadata pin fails too.
- NC-3 `DidComm.AspNetCore` ProjectReference on the client: the graph walk and the base-framework precondition fail.
- NC-5 `DidComm.Mediator` ProjectReference on the client: restore fails, `MSB4006: There is a circular dependency in the target
  dependency graph involving target "_GenerateRestoreProjectPathWalk"` (the self-test is the permanent stand-in).
- DX: dropping one client entry (coverage) fails `EveryPublicMember_IsReferencedByASampleAssembly`; dropping one shared-type
  entry (execution) fails `EveryExecutableMember_IsExecutedByASampleRun`; leaving one moved entry on the old prefix fails the coverage gate.
- Interop fixtures: 150 files.
- Pack validation: renaming `Message.Empty()` in Core fails pack with `CP0002` against the 1.5.0 baseline.
