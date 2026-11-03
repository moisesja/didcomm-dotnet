# Upgrade DataProofsDotnet.Jose 1.5.0 → 2.0.0

## Context

`DataProofsDotnet.Jose` 2.0.0 is the latest release on nuget.org (published 2026-11-02, listed, no
deprecation, no advisory recorded against it). We reference only Jose directly; it brings
`DataProofsDotnet.Core` 2.0.0.

**Upstream's major does not land on anything didcomm uses.** Checked between the `v1.5.0` and
`v2.0.0` tags in dataproofs-dotnet:

- `src/DataProofsDotnet.Jose`: no source change at all.
- `src/DataProofsDotnet.Core`: adds `ProofVerificationOptions.KnownContexts` and its check. The check
  sits behind `options.KnownContexts is { }`, so it does nothing unless a caller sets it.
- Dependency pins are unchanged (NetCrypto 1.7.1, NetCid 1.6.0, IdentityModel 8.19.1).
- The breaking changes (removed constructors, changed signed bytes, the GHSA-6fxh-mc39-94vc proof
  binding fix) are in `DataProofsDotnet.Rdfc` and `.Legacy`. Neither is in didcomm's graph: 0 of 23
  `project.assets.json` files contain them.

**So this is not a security fix for didcomm.** The advisory affects RDFC and bbs-2023. didcomm has
neither, and `NetDid.Method.WebVh` uses the JCS suite, which upstream states was not affected. The
CHANGELOG must not present this as a security update.

**The one real risk is in the graph, not our code.** `NetDid.Method.WebVh` 4.0.0 is compiled against
Core 1.4.0. No didcomm project references it directly, but `DidComm.Extensions.DependencyInjection`
→ `NetDid.Extensions.DependencyInjection` → `NetDid.Method.WebVh` puts it in 17 of 23 projects and
in consumers' graphs. After this bump it runs on Core 2.0.0, and our test suite never calls it.
net-did has no newer release to move to (4.0.0 is its latest). The trial below tested this directly.

## Trial results (disposable clone in the scratchpad, nothing committed)

CI's sequence, run on `main` and then with only the pin changed:

| Gate | `main` (1.5.0) | Bump (2.0.0) |
|---|---|---|
| `dotnet build /warnaserror` | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `DidComm.Core.Tests` | 1121 passed | 1121 passed |
| `DidComm.InteropTests` | 290 passed | 290 passed |
| Vulnerable packages, including transitive | not run | none, all 23 projects |

Beyond the suite:

| Check | Result |
|---|---|
| Built `DidComm.Core.dll` binds Jose 2.0.0 | 21 types, 43 members, 0 unresolved |
| Packed `DidComm.Core.nuspec` declares Jose | 2.0.0 |
| Package validation, 1.5.1 vs baseline 1.5.0, 6 packages | clean |
| Same gate after hiding a public type (negative control) | fails `CP0001`, against 1.5.0 and 1.4.1 |
| Published DidComm.Core and Adapters.NetDid 1.5.0 bind Jose/Core 2.0.0 | 0 unresolved |
| Published WebVh 4.0.0 binds Core 2.0.0 | 10 types, 32 members, 0 unresolved |
| Same checker on ZcapLd.Core 5.0.0 → Legacy 2.0.0 (negative control) | finds the 2 replaced constructors |
| Samples 01 and 09 run on 2.0.0 | exit 0 |

**Published WebVh 4.0.0 at run time** (compiled against Core 1.4.0, loaded Core 2.0.0, confirmed from
the loaded assemblies; same results on Core 1.5.0 as the control):

- a stored `eddsa-jcs-2022` proof from net-did's upgrade fixture verifies;
- the tampered copy is refused with `invalidDidLog`, so verification really runs;
- a malformed `proofValue` is reported as a resolution error, not a raw fault;
- create then resolve round-trips.

**Not run:** CI's Windows job, and the nightly `interop-live` harness against didcomm-python and
didcomm-jvm. Jose has no source change, so I don't expect either to differ. PR CI covers Windows, and
the nightly picks up `main` after merge.

**Three false passes along the way**, recorded so nobody re-reads them as evidence: the first trial's
build failed on SourceLink (the clone's `origin` was a local path), so it was redone with the real
URL. The first `baseline=1.5.0` pack ran with `--no-build`, never fetched the baseline and logged
nothing, so it was redone with a restore. The first negative control (ZcapLd → Rdfc) did not fail,
because ZcapLd doesn't call the removed Rdfc constructors, so it was replaced with Legacy, which does.

## Plan

Branch `feat/dataproofs-2.0.0` off `main`. Stage only the files below: the uncommitted L-053 entry
in `tasks/lessons.md` predates this work and stays out of the commit.

- [x] **`Directory.Packages.props:71`** — pin to 2.0.0.
- [x] **`Directory.Packages.props:70`** — extend the version history in the comment:

      ```
      1.5.0 changes only RDFC canonicalization and bbs-2023, which didcomm does not use.
      2.0.0's breaking changes are in DataProofsDotnet.Rdfc and .Legacy, which are not in didcomm's
      graph. Jose has no source change from 1.5.0, and Core only adds
      ProofVerificationOptions.KnownContexts, which does nothing unless set. NetDid.Method.WebVh
      4.0.0 (here through NetDid.Extensions.DependencyInjection) is compiled against Core 1.4.0 and
      runs unchanged on 2.0.0: every member it imports still binds. -->
      ```

- [x] **`Directory.Packages.props:44`** — "NetDid 4.0.0 and DataProofsDotnet 1.5.0" → 2.0.0. Both
      still depend on NetCrypto 1.7.1.
- [x] **`docs/didcomm-dotnet_PRD.md:132`** — `DataProofsDotnet.Jose 1.1.1` → `2.0.0`. In the same
      cell, also correct `NetCrypto 1.4.0` → `1.7.1` and `NetDid.Core 3.0.x` → `4.0.x`. Both are
      stale from #77, and line 164 of the PRD already says 4.0.x, so the document disagrees with
      itself today.
- [x] **`CHANGELOG.md` `[Unreleased]`** — add:

      ```markdown
      ### Changed

      - **`DataProofsDotnet.Jose` 1.5.0 → 2.0.0** (brings `DataProofsDotnet.Core` 2.0.0). didcomm's
        public API and wire output do not change, and package validation is clean against 1.5.0.
        `DataProofsDotnet.Jose` has no source change in this release, and `DataProofsDotnet.Core` only
        adds an option that does nothing unless set. The advisory fixed in 2.0.0
        (GHSA-6fxh-mc39-94vc) affects RDFC and bbs-2023 proofs, which didcomm does not use, so this
        is not a security update for didcomm.

      Upgrade notes:

      - **DataProofsDotnet: move every package you reference together, then rebuild.** The 2.0.0
        major is in `DataProofsDotnet.Rdfc` and `DataProofsDotnet.Legacy`, which didcomm does not
        reference. If your application does, this update does not move them for you. Moving them to
        2.0.0 removes constructors, so an assembly built against 1.x throws
        `MissingMethodException` until it is rebuilt, and it changes signed bytes for some RDFC
        documents. Read upstream's 2.0.0 notes first.
      - **did:webvh.** `NetDid.Method.WebVh` 4.0.0 arrives through `DidComm.Extensions.DependencyInjection`
        and is built against `DataProofsDotnet.Core` 1.4.0. It runs unchanged on 2.0.0.
      ```

- [ ] **Verify on the real branch:** restore, `build /warnaserror`, full test suite, `pack`. Expect
      the counts above. Then open the PR.

## Deliberately not changing

- **`DidCommVersion`.** This is not a release. Whether and when to cut one is separate.
- **`PackageValidationBaselineVersion`** stays 1.4.1. It should have moved to 1.5.0 when 1.5.0
  shipped, per the comment in `Directory.Build.props`. That slip predates this work, and the bump
  was validated against 1.5.0 directly, so it belongs with the next release.
- **`NetDid.Method.WebVh` pin.** It stays, and it stays unreferenced directly. It is 4.0.0 and
  already satisfied.
- **No new test.** The WebVh probe is a one-off for this crossing. A permanent one would add a direct
  WebVh reference and a fixture to pin behavior net-did already tests.
- **No QA or adversarial agent pass.** Those rules target generated code, and this change generates
  none. Its risk is the dependency graph, and the checks above went at that directly.

## Cross-repo note (zcap-dotnet, not this repo)

`ZcapLd.Core` 5.0.0, its latest, imports the two Legacy constructors that 2.0.0 replaced. For an
application using didcomm and ZcapLd together on Core 2.0.0:

| Legacy version | Binding result |
|---|---|
| 1.5.0, with Rdfc 1.5.0 | binds |
| 1.3.0, ZcapLd's floor | fails on `DataIntegrityProof.Type`'s setter. Not new: the same failure exists on today's `main`, from Core 1.4.0's `init`→`set` change |
| 2.0.0 | fails on the two replaced constructors until zcap-dotnet publishes a rebuilt release |

These are metadata binding checks, not runs. Nothing here depends on ZcapLd, and the fix belongs in
zcap-dotnet.

## Review

_(filled in after implementation)_
