# Dependency refresh to latest stable + release 1.5.0 (#77)

## Context

Issue #77 asks to move every pin in [Directory.Packages.props](../Directory.Packages.props) to its
latest stable release, record why for any that stay behind, and cut a release that lists the new
dependency floors.

**The issue's table is partly wrong** (checked against nuget.org on 2026-10-28):

- "Already current: NetDid.\* 3.1.0, DataProofsDotnet.Jose 1.3.0" is wrong. The table rows
  "NetDid 3.1.0 → 4.0.0" and "DataProofsDotnet 1.3.0 → 1.5.0" mean those exact packages: all five
  `NetDid.*` packages are at **4.0.0**, and `DataProofsDotnet.Jose` is at **1.5.0**. We do not
  reference packages named `NetDid` or `DataProofsDotnet`.
- Polly's latest is now **8.9.0**. 8.8.0 was the latest when the issue was written.

**This reverses an earlier decision on purpose.** The 1.4.x work held the test stack back on
purpose (the comment in `Directory.Packages.props`, CHANGELOG 1.4.1 and 1.4.0). #77 reopens that
decision. net-did 4.0.0 (released 2026-10-28) has made the same move: AwesomeAssertions 9.6.0,
xunit.runner.visualstudio 4.0.1, Test.Sdk 18.11.0, NSubstitute 6.2.0, coverlet 10.1.0.

**Security finding on `main` today.** `dotnet list package --vulnerable --include-transitive`
reports `Microsoft.Build.Tasks.Git` 10.0.301 ([GHSA-23fw-v26w-5fgq], moderate, information
disclosure). It comes in through SourceLink 10.0.301, is build-time only, and runs whenever
`release.yml` packs. SourceLink 10.0.403 clears it.

[GHSA-23fw-v26w-5fgq]: https://github.com/advisories/GHSA-23fw-v26w-5fgq

## Trial results (throwaway worktree off `main`, nothing committed to the repo)

| Step | `build -warnaserror` | Core tests | Interop tests (fixtures on) | Other |
|---|---|---|---|---|
| Runtime + build bumps | clean | 799/799 | 279/279 | pack vs **1.4.1** baseline clean |
| + Test.Sdk 18 / runner 4 / NSubstitute 6 / coverlet 10 / BDN 0.15 | clean | 799/799 | 279/279 | trx + `XPlat Code Coverage` emit as in CI; benchmarks `--job dry` run all 4 |
| + FluentAssertions 7.0.0 → AwesomeAssertions 9.6.0 | 1 error → clean after one rename | 799/799 | 279/279 | — |

After every bump, `--vulnerable` reports nothing. `--deprecated` reports only xunit 2.9.3 (see D3).

## Decisions

Each one has a recommendation. D1 and D2 need your answer; D3 to D5 go ahead as recommended
unless you say otherwise.

**Approved 2026-10-28:** D1 (a) AwesomeAssertions, D2 1.5.0, D3 to D5 as recommended. The
user asked for NetDid and DataProofs at their latest on NuGet; that is still NetDid.* 4.0.0 and
DataProofsDotnet.Jose 1.5.0 (re-checked on approval).

- **D1. Assertion library.** FluentAssertions 8 is published under the Xceed license, which
  requires a paid license for commercial use.
  - **(a) AwesomeAssertions 9.6.0 (recommended).** The Apache-2.0 community fork, and net-did's
    choice. It costs one mechanical rename plus the audit in step 4.
  - (b) Stay on FluentAssertions 7.x, moved to **7.2.2** (the latest 7.x, still Apache-2.0), and
    record why. Nothing in the suite changes, but the test stack stays on a dead line.
  - (c) FluentAssertions 8.11.0. Not recommended: it puts a commercial license on every
    contributor's test dependencies.
- **D2. Release number: 1.5.0 (recommended).** A NetDid major as a didcomm minor has a
  precedent: 1.4.0 took NetDid 2.3 → 3.0. NetDid 4.0's break is the did:webvh witness file
  format, which didcomm does not use. No didcomm public API changes. The floors do narrow what
  input is accepted (see step 5), and the CHANGELOG says so.
- **D3. xUnit v3: out of scope**, as the issue itself says. xunit.runner.visualstudio 4.0.1
  still runs v2 tests ("Capable of running xUnit.net v1, v2, and v3 tests", per its nuspec), and
  this is confirmed by the trial. xunit 2.9.3 is the latest v2 and is marked *Legacy*
  (deprecated) on NuGet. I can file a follow-up issue for the move to v3 using the
  `xunit.v3.mtp-off` route net-did took.
- **D4. `global.json`: unchanged (recommended).** SourceLink 10.0.403 builds and packs on SDK
  10.0.100. CI already rolls forward to the newest SDK on the runner: the last run used
  10.0.302. Raising the floor would make contributors install a newer SDK and gain nothing.
- **D5. Microsoft.Extensions floors: move all five to 10.0.14.** This is not really optional:
  NetDid.Core 4.0.0 itself depends on `Caching.Memory` and `Logging.Abstractions` 10.0.14, so
  those floors reach consumers regardless. Moving `DependencyInjection`,
  `DependencyInjection.Abstractions` and `Http` too keeps one servicing line.
  `AspNetCore.TestHost` is test-only.

## Version matrix — [Directory.Packages.props](../Directory.Packages.props)

| Package | From | To | Reaches consumers? |
|---|---|---|---|
| NetDid.Core / .Method.Key / .Method.Peer / .Method.WebVh / .Extensions.DependencyInjection | 3.1.0 | 4.0.0 | yes, as the new floor |
| NetCrypto | 1.5.0 | 1.7.1 | yes (also forced by NetDid 4.0.0 and DataProofs 1.5.0) |
| DataProofsDotnet.Jose | 1.3.0 | 1.5.0 | yes (brings DataProofsDotnet.Core 1.5.0) |
| Microsoft.Extensions.{Logging.Abstractions, DependencyInjection, DependencyInjection.Abstractions, Http, Caching.Memory} | 10.0.10 | 10.0.14 | yes |
| Polly | 8.7.0 | 8.9.0 | yes |
| Microsoft.SourceLink.GitHub | 10.0.301 | 10.0.403 | no (build-only); clears the advisory |
| Microsoft.AspNetCore.TestHost | 10.0.10 | 10.0.14 | no |
| Microsoft.NET.Test.Sdk | 17.12.0 | 18.11.0 | no |
| xunit.runner.visualstudio | 2.8.2 | 4.0.1 | no |
| FluentAssertions → AwesomeAssertions (D1a) | 7.0.0 | 9.6.0 | no |
| NSubstitute | 5.3.0 | 6.2.0 | no |
| coverlet.collector | 6.0.3 | 10.1.0 | no |
| BenchmarkDotNet | 0.14.0 | 0.15.8 | no |
| xunit / Mono.Cecil | 2.9.3 / 0.11.6 | unchanged | already latest stable |

## Steps

- [x] 1. Branch `chore/deps-refresh-77` off `main`.
- [x] 2. **Pins.** Apply the matrix. Rewrite the comments in `Directory.Packages.props` to match:
      the NetDid 4.0.0 rationale and the stricter-input note, NetCrypto 1.7.1 (1.6/1.7 add only
      key-store APIs that didcomm does not use), DataProofs 1.5.0 (1.5.0 changes only RDFC/BBS,
      which didcomm does not use; 1.4.0's JOSE input strictness is covered in step 5). Replace the
      "held deliberately" testing comment with the new rationale.
- [x] 3. **Assertion swap (D1a).** `Directory.Packages.props`, the two test `.csproj` files, the
      112 `using FluentAssertions;` lines, `BeGreaterOrEqualTo` → `BeGreaterThanOrEqualTo` (the
      only API that no longer compiles), and the FluentAssertions mention in
      `MessageJsonTests.cs:44`.
- [x] 4. **Find assertions that now accept more.** A green suite does not prove every assertion
      is as strict as before: since FluentAssertions 8, `OnlyContain` passes on an empty
      collection (net-did found exactly this in its #144). A QA agent diffs the FluentAssertions
      7 → AwesomeAssertions 9 semantics of every assertion verb the suite uses. That is 7
      `OnlyContain` sites, `AllSatisfy`, 9 `BeEquivalentTo`, `ContainSingle`, `BeEmpty` and so on.
      Where a test could now pass vacuously, add the missing precondition at that site.
      `RedactionAuditTests.cs:68` is the clearest case: its own message says the audit "is
      meaningless without real material". Add one guard test proving a vacuous input now fails.
- [x] 5. **Input that the new floors reject, pinned at didcomm's boundary.** Both `EnvelopeReader`
      branches already end in a catch-all guard (FR-API-07), so no new upstream exception type
      can leak out untyped. Regression tests record what changes for didcomm callers:
      - a JWE or JWS protected header with an unpaired-surrogate escape in an extension member
        (for example `"x":"\uD800"`). 1.4.1 accepted it. DataProofs 1.4.0 rejects it, so
        unpacking it should raise `MalformedMessageException`. This is a narrowing and goes in the
        CHANGELOG;
      - anoncrypt with a small-order X25519 `epk` raises `CryptoException`, the same uniform
        failure whether or not the recipient key is held. That is the constant-work property, now
        enforced upstream too.
      If either test shows something other than this, stop and re-plan; do not adjust the test
      to fit.
- [x] 6. **Package validation + version.** In `Directory.Build.props`, move
      `PackageValidationBaselineVersion` 1.4.0 → 1.4.1 (1.4.1 is on NuGet and tagged) and
      `DidCommVersion` 1.4.1 → 1.5.0 (the local-build default only; the tag sets the released
      version).
- [x] 7. **Docs.** README.md:74 (NetDid 3.1.0 → 4.0.0); PRD §3 diagram line 164 ("NetDid
      3.0.x" → "4.0.x") and §13 test-tooling row line 645 (FluentAssertions → AwesomeAssertions);
      CONTRIBUTING.md:51. There is no `codebase-architecture.md` in the repo, so nothing to sync
      there.
- [x] 8. **CHANGELOG `[Unreleased]`.**
      - *Security*: the SourceLink advisory.
      - *Changed — dependency refresh*: a table of the new minimum versions consumers get.
      - *Inherited input changes*: NetDid 4.0.0's stricter DID, DID URL, did:peer:2 and JWK
        validation, and the JOSE unpaired-surrogate narrowing.
      - *Test stack*: what moved and why it is no longer held.
- [x] 9. **Verify, as CI runs it:** `restore`; `build -c Release /warnaserror`; full `test` with
      the fixtures submodule, `--logger trx` and `--collect "XPlat Code Coverage"`; `pack` (package
      validation against 1.4.1); `dotnet list package --vulnerable / --deprecated --include-transitive`;
      benchmarks `--job dry`. Test counts must be at least 799 + 279, plus the new tests.
- [x] 10. **Review.** One adversarial agent reviews only steps 4 and 5: did any assertion get
      looser, and does any new upstream exception path change the uniform-failure property?
      Low-severity findings are batched into this PR, not filed as follow-ups.
- [x] 11. **PR** from `chore/deps-refresh-77`, closing #77. Watch CI on ubuntu and windows.
- [ ] 12. **Release (needs your go).** After merge: a release PR that renames `[Unreleased]` to
      `[1.5.0] - <date>` (RELEASING.md step 2, as in #76). Then the `v1.5.0` tag, which publishes
      to NuGet. I will not push the tag unless you say so.
- [x] 13. Update the memory note that says the test stack is held, and add a lesson if anything
      in steps 4 and 5 surprises us.

## Out of scope (recorded here, per the issue's "or this issue records why not")

- **xUnit v3**: see D3.
- **Packages we only get transitively** and do not pin: `NBitcoin.Secp256k1` 4.0.0 → 4.0.4 and
  `Microsoft.Bcl.Cryptography` 10.0.2 → 10.0.14 come in through NetCrypto, so moving them belongs
  in **crypto-dotnet**, not here. `Castle.Core` comes through NSubstitute; Roslyn and Perfolizer
  come through BenchmarkDotNet. Pinning them here would override upstream's tested graph.
- Prereleases (Microsoft.Extensions 11.0.0-rc, BenchmarkDotNet 0.16.0-preview): stable only.

## Review

**Results (HEAD 5dd465d, clean `bin`/`obj`):** restore, `build -c Release /warnaserror` (0 warnings),
and `test` with trx and coverage all pass: Core **805** (799 + 5 boundary + 1 guard), Interop
**279**. `pack` is clean, with package validation against 1.4.1 and the nuspecs showing the new
floors. `--vulnerable` reports nothing. `--deprecated` reports only xunit 2.x (D3). Benchmarks
`--job dry` run all 4 from a clean checkout. Inside the repo folder, BenchmarkDotNet trips on
extra project copies in the old `.claude/worktrees/*` checkouts, which is unrelated to this change.

**Things that weren't known when planning:**
- `dotnet restore` of `main` (a0ed554) **fails today**: NuGet audit raises `NU1902` for
  Microsoft.Build.Tasks.Git 10.0.301, and `TreatWarningsAsErrors` makes it an error. Every new CI
  run on `main` is red until this lands. This is recorded under CHANGELOG *Security*.
- Step 4: only `OnlyContain` and `AllSatisfy` got looser (they pass on an empty collection; FA 8
  #2350 / #2321). Of the 8 call sites, one could pass vacuously:
  `SnapshotRegistrationTests.AssertOneRegistrationPerResult`. It now has a non-empty guard and a
  guard test. `RedactionAuditTests:68`, which the plan flagged, scans a fixed 5-element array,
  so it is not vacuous.
- Step 5: the old vs new run proves the surrogate-escape narrowing is real. Both tests fail on
  the 1.4.1 dependency set and pass on the new one. The small-order X25519 case is *not* a
  narrowing: didcomm already raised the same uniform `CryptoException`, and only
  `InnerException` changed. The pin comment and the CHANGELOG were corrected to say so.
- NetDid 4.0.0's `ParseDidUrl` still returns `null` rather than throwing. A stricter grammar
  therefore only widens the set of values the DID-subject checks treat as unparseable. It opens
  no new exception path.

**Adversarial review (step 10):**
- **F1 (medium, predates the branch):** an unpaired-surrogate escape in a member *name* threw a raw
  `InvalidOperationException` from `EnvelopeDetector`, at every layer and before any
  authentication. String values of plaintext extension headers had the same problem, which shows
  up later in `TraceObserver` and `OutOfBand.ReadWebRedirect`. **User decision 2026-10-28: fold the
  fix into #78.** The detector now maps the parse's `InvalidOperationException` and
  `ArgumentException` to `MalformedMessageException`. On a plaintext layer it also refuses any
  string value that does not decode. `ForwardMessage.Create` maps the same input to its documented
  `ArgumentException`. 14 tests pin this; before the fix, 9 of them failed with a raw exception and
  3 unpacked.
- **F2 (low, comes with NetCrypto 1.6+):** on keystore-backed keys, a small-order `epk` costs the
  holder about 3 µs more. This falls inside the timing caveat FR-SEC-06 already documents, and HTTP
  is covered by the #35 floor. Recorded in the CHANGELOG; no code change.
- **F3 (low):** the surrogate-pair control was a raw emoji (see L-049), now fixed. The claim that the
  tests "pin" the small-order case was reworded. Nothing in Core guards the decoy's *timing*; that
  predates the branch, and timing assertions are flaky (L-044), so no test was added.
- **F4 (info, positive):** a held key with a wrong-length `d` no longer fails differently from a
  non-holder.
- **NetDid 4.0.0 DID-URL regexes:** measured linear across 37 adversarial shapes up to 1 MiB.
  The worst case per call (about 37 ms at 1 MiB) is no worse than 3.1.0's. One pre-existing item:
  a 1 MiB kid allocates about 60–95 MB of regex backtracking stack. A length cap on kids before
  `ParseDidUrl` would bound it. This is a follow-up candidate, not in this PR.

Final: Core **819**, Interop **279**, 0 warnings, pack clean against 1.4.1.
