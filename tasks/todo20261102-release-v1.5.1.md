# Release v1.5.1

Cut and publish `DidComm.*` 1.5.1 to NuGet.org per [RELEASING.md](../RELEASING.md). The release holds
one change: `DataProofsDotnet.Jose` 1.5.0 → 2.0.0 (#85).

## Version

**1.5.1, by user decision** (2026-11-02). The public API does not change, so SemVer allows a patch.

I had recommended 1.6.0, because v1.2.0 shipped a pin-only bump as a minor, and because an application
that pins `DataProofsDotnet.Core` 1.5.0 directly fails `dotnet restore` with `NU1605` on this upgrade
(tested: Jose 2.0.0 beside Core 1.5.0 fails, beside Core 2.0.0 restores). The CHANGELOG lead and first
upgrade note say so up front, following the "Despite the patch number" wording in the 1.4.1 notes.

## Preconditions (verified 2026-11-02)

- [x] nuget.org tops out at **1.5.0** for all six package ids, so 1.5.1 is free.
- [x] CI is green on `main` at `8c1e29c`, on Ubuntu and Windows.
- [x] Live interop is green on `8c1e29c`: python and jvm pass, 123 cells passed, 0 failed, 4 not
      applicable.
- [x] Nothing under `src`, `tests` or `samples` changed since `v1.5.0`.
- [x] Package validation is clean for all six packages against the published 1.5.0, and hiding a
      public type fails it with `CP0001`. That ran on `c2c0ad8`; PR CI and the
      Release workflow both rerun it.
- [x] `nuget-release` requires `moisesja` as reviewer, so a tag cannot publish without your approval.
- [x] `NUGET_API_KEY` is a repo-level secret that published 1.5.0 on 2026-10-29.

## Plan

- [x] 1. Branch `docs/release-v1.5.1` off `main`.
- [x] 2. **`CHANGELOG.md`:** `## [Unreleased]` → `## [1.5.1] - <merge date>`, with a fresh empty
      `## [Unreleased]` above it. Footer: `[Unreleased]` → `compare/v1.5.1...HEAD`, and add
      `[1.5.1]: .../compare/v1.5.0...v1.5.1`. Add a lead:

      ```markdown
      > Moves `DataProofsDotnet.Jose` to 2.0.0 (#85). This is a pin bump only: didcomm's public API,
      > behavior and wire output do not change. Despite the patch number, the dependency floor crosses a
      > major, so read the upgrade notes if your application references a DataProofsDotnet package
      > directly.
      ```

      Add this upgrade note, which the tested `NU1605` failure calls for:

      ```markdown
      - **A direct pin below 2.0.0 fails restore.** If your application references
        `DataProofsDotnet.Core` or `DataProofsDotnet.Jose` directly at an older version, `dotnet restore`
        fails with `NU1605` until you raise it to 2.0.0.
      ```

- [x] 3. **`Directory.Build.props`:** `PackageValidationBaselineVersion` 1.4.1 → 1.5.0, so the gate
      compares against the last release, and `DidCommVersion` dev default 1.5.0 → 1.5.1. The `cut
      1.4.1` commit moved both the same way. This also closes the follow-up the v1.5.0 plan left open.
- [x] 4. **`RELEASING.md`:** add `1.5.0` to the published list in the status block.
- [ ] 5. Open the PR. Wait for CI. **You merge.**
- [ ] 6. Annotated tag `v1.5.1` on the merge commit, with a summary message like `v1.5.0`, then push.
      The Release workflow rebuilds, retests and packs, then pauses at `nuget-release`.
- [ ] 7. **You approve the deployment.** This is the irreversible step, and I will not approve it for
      you.
- [ ] 8. Verify all six packages at 1.5.1 on nuget.org, then restore `DidComm.Core` and
      `DidComm.Extensions.DependencyInjection` 1.5.1 in a scratch project and confirm Jose 2.0.0 and
      Core 2.0.0 resolve.
- [ ] 9. Create the GitHub Release from the tag, with the CHANGELOG 1.5.1 section as its notes. Write
      the notes file in its own call and check it exists first (L-053). Publishing fires `interop-live`;
      wait for that result.

## Deliberately not changing

- **No code, test or dependency changes.** Everything since `v1.5.0` is already on `main`.
- **`NUGET_API_KEY` stays repo-level.** RELEASING.md prefers an environment-scoped secret, but moving
  it mid-release adds risk and is a separate decision.
- **No follow-up baseline PR.** Each release PR sets the baseline to the previous release, as `cut
  1.4.1` did, so nothing is left to forget after publishing.

## Rollback

NuGet packages cannot be deleted. If 1.5.1 turns out broken: unlist or deprecate it, then ship a
patch. If something fails before step 7, nothing has been published, so the tag can be deleted and
recut.

## Review

_(filled in after the release)_
