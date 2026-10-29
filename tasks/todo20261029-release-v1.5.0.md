# Release v1.5.0

Cut and publish `DidComm.*` 1.5.0 to NuGet.org per [RELEASING.md](../RELEASING.md).

## Preconditions (verified 2026-10-29)

- [x] nuget.org tops out at **1.4.1** for all six package ids; 1.5.0 is free.
- [x] `DidCommVersion` = 1.5.0; `PackageValidationBaselineVersion` = 1.4.1 (the previous release — correct).
- [x] Local `dotnet pack -p:DidCommVersion=1.5.0` creates all six packages; package validation is clean
      against 1.4.1 (nothing removed or reshaped).
- [x] Public-signature diff v1.4.1..HEAD: no new public members (only an `internal` ctor, internal
      constants, doc comments). The `[Unreleased]` lead's "public API does not change" holds.
- [x] `NUGET_API_KEY` exists (repo-level secret, created 2026-06-01, used for 1.4.1).
- [ ] CI green on `main` HEAD `42a49be` (ubuntu green; windows running at time of writing).

## Findings that need a decision

1. **The publish gate is not set up.** `nuget-release` has **no protection rules**, so a tag push
   publishes immediately with no approval pause. RELEASING.md claims the one-time setup is done.
2. **Live interop hasn't run since 2026-10-06.** The workflow is `disabled_inactivity`. Its last
   failures were the NU1902 restore break that #78 fixed, not interop regressions. But the
   FR-ENC-19 wire change (#81) has never been round-tripped live against didcomm-python/jvm in CI.

## Plan

- [x] 1. *(decision 1: approved)* Added `moisesja` as required reviewer on `nuget-release`.
- [ ] 2. *(decision 2: approved; run 40973472436 dispatched)* Re-enable `interop-live.yml` and `workflow_dispatch` it on `main`; tag only
        once it is green.
- [x] 3. Branch `docs/release-v1.5.0` off `main`.
- [x] 4. CHANGELOG: `## [Unreleased]` → `## [1.5.0] - 2026-10-29`; fresh empty `## [Unreleased]`
        above; footer `[Unreleased]` → `compare/v1.5.0...HEAD`, add
        `[1.5.0]: .../compare/v1.4.1...v1.5.0`.
- [x] 5. RELEASING.md: status block adds `1.4.1`; correct the "setup is done" claim to match
        whatever step 1 decides; replace the stale `<DidCommVersion>0.1.0</DidCommVersion>` literal.
- [ ] 6. PR → CI green → merge.
- [ ] 7. Annotated tag `v1.5.0` on the merge commit (summary message, like `v1.4.1`) → push.
- [ ] 8. Watch the Release run; approve the deployment if step 1 added a gate.
- [ ] 9. Verify six packages at 1.5.0 on nuget.org; smoke-restore `DidComm.Core` 1.5.0 in a scratch project.
- [ ] 10. GitHub Release from the tag, notes = the CHANGELOG 1.5.0 section (this also fires
         `interop-live` via `release: published`).
- [ ] 11. Follow-up (separate PR, after publish): `PackageValidationBaselineVersion` 1.4.1 → 1.5.0,
         and advance the `DidCommVersion` dev default.

## Review

_(filled in after the release)_
