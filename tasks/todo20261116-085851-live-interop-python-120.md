# todo20261116-085851 — CI: restore the nightly Live interop job (#120)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Issue: #120 (epic #88). Base: `703ab92` (= `origin/main`, the PR #121 merge). Branch: `fix/live-interop-python-120` in worktree `.claude/worktrees/orch-120` (L-019: re-verify the branch before every commit).
Binding inputs: #120's scope / out-of-scope / acceptance; #88 comments 6894883036 (owner decision creating #120) and 6918389034 ("#120" notes: floating runner images, Node 20 deprecation of the pinned action majors).

Stops (hand back, don't patch): a FAIL cell that traces to our code (a regression since 2026-11-07); a counterpart-side FAIL; N-A cells that differ from the last green run; the pinned didcomm-python 0.3.2 stack not installing/importing on CPython 3.12 on Ubuntu 26.04 (would force a pin move or the image fallback); anything that changes #88's plan.

## Diagnosis (done before writing this plan)

- **Cause.** `ubuntu-latest` moved to Ubuntu 26.04 (runner notice: "The ubuntu-latest label will migrate to Ubuntu 26 beginning October 19, 2026", actions/runner-images#14748). `actions/setup-python` has no CPython 3.9 build for 26.04 (3.9 is end-of-life), so the job fails in `Setup Python` before any test runs.
- **First failure is 2026-11-08, not 2026-11-10.** Runs 42471799819 (11-08) and 42611473329 (11-09) fail with the same error on image `ubuntu26/20261101.186`. The last green run, 42363748989 (2026-11-07, `d8a100c`), ran on `ubuntu-24.04` with CPython 3.9.25. Latest failure: 43859419746 (2026-11-16, image `ubuntu26/20261111.82`).
- **CPython 3.12 exists for 26.04.** `actions/python-versions` `versions-manifest.json` lists linux-x64 26.04 builds for 3.12.13–3.12.16 (also 3.10.20–22 and 3.11.15–18; **none for 3.9**).
- **The pinned stack has cp312 Linux wheels.** `pip download --only-binary=:all: --platform manylinux… --python-version 3.12` per pin: every pin resolves to a wheel (`cffi` cp312, `cryptography` cp311-abi3, `pycryptodomex` cp37-abi3, `pydantic 1.9.2` py3-none-any, the rest pure) **except `varint==1.0.2`, which is sdist-only** and is built at install time (on a 3.12 venv, via a PEP 517 isolated build with the current setuptools from PyPI). Local runs on CPython 3.12.7 (macOS arm64, 2026-10-29 and 2026-11-13) already built it.
- **Last green per-cell result (the N-A baseline):** python leg executed 62 · passed 62 · n/a 0; jvm leg executed 59 · passed 59 · n/a 3 (outbound + inbound `signed ES256K` and vector `signed-es256k`: JDK secp256k1 removal). #95 later added one cell per leg ("authcrypt via a running dotnet mediator"), so the expected totals now are python 63 · n/a 0 and jvm 60 · n/a 3 (README, 2026-11-13 local run).
- **PRD / architecture doc** name neither the CI interpreter nor the runner image: no change there.

## Checklist

### 1. Interpreter fix
- [x] `interop-live.yml`: `python-version: '3.9'` → `'3.12'`, comment says why 3.12 (newest in the 3.9–3.12 window the harness accepts; upstream support to 2028-10) and that 3.13+ cannot work (pydantic v1).
- [x] `interop-live.yml`: give the setup step an `id` and pass its `python-path` output to the harness step as `PYTHON` (step-level `env:`), so the leg seeds its venv from exactly the interpreter the workflow installed, not whatever `python3` is first on `PATH` (Ubuntu 26.04 ships a newer system Python; the guard would stop it with exit 2, but the binding should be explicit). Architect: keep. Nothing else in the harness calls `python3` (the jvm leg has no Python; the peer and pip run from `$VENV/bin`).
- [x] `run-python-leg.sh`: print the venv interpreter's version next to the existing `didcomm-python:` stderr line, so the leg's log names the CPython patch it ran on (evidence only; the guard is unchanged). QA trap (architect): `${PYTHON:-python3}` treats an empty value as unset, so a mistyped step id would silently fall back to `python3`. Check the step's `env:` block in the log shows `/opt/hostedtoolcache/Python/3.12.*/x64/bin/python`.
- [x] YAML comment + README: 3.12 is a stopgap. The same failure returns when `ubuntu-latest` moves to an image with no 3.12 build (3.12 is end-of-life 2028-10); the lasting fix is a counterpart stack off pydantic v1 (not in #120's scope).
- [x] Harness guard (`run-python-leg.sh`, 3.9–3.12) unchanged.
- [x] (after the green live run, citing its id — architect) `tools/interop-live/python/requirements.txt` header: "Validated on CPython 3.12 (macOS arm64 + Ubuntu 26.04 x86_64 in CI); imports on 3.9–3.12".
- [x] `tools/interop-live/README.md`: Pinned versions row ("CPython 3.12 on CI") and CI cadence note (ubuntu-latest = 26.04, CPython 3.12, Temurin 17; the "remaining unproven claim" sentence updated with the run that proves it).
- [x] No pin moves unless the live run forces one (stop and report first).

### 2. Node 20 sweep (same shape: a pinned version the runners are dropping)
Verified with `gh api` (tag ref exists; `runs.using` in `action.yml` at that tag):

| action | today | node | target | node | breaking notes relevant to our inputs |
|---|---|---|---|---|---|
| `actions/checkout` | v4 | node20 | **v7** (v7.0.1) | node24 | v6: credentials persisted to a separate file under `$RUNNER_TEMP` (so no upload may widen to `runner.temp`); v7: refuses fork-PR checkouts on `pull_request_target`/`workflow_run` (we use neither). `submodules: true` unchanged. |
| `actions/setup-dotnet` | v4 | node20 | **v6** (v6.1.0) | node24 | v5: old .NET versions dropped from the installer (we install the `global.json` SDK, 10.0.x). |
| `actions/setup-python` | v5 | node20 | **v7** (v7.1.0) | node24 | v7: `pip-install` input removed, EOL Pythons removed (we use neither). |
| `actions/setup-java` | v4 | node20 | **v6** (v6.3.0) | node24 | v4 itself warns "deprecated … migrate to v5"; v6 changes Zulu/Maven paths, renames `jdkFile`→`jdk-file` (unused), and added a javac problem matcher that v6.3.0 made opt-in (so no `InteropPeer.java` annotations expected). We use Temurin, no cache. |
| `actions/upload-artifact` | v4 | node20 | **v7** (v7.0.2) | node24 | v5 still node20; v6 node24; v7 adds opt-in `archive: false` (default unchanged: zipped). |

- [x] `interop-live.yml`: all five bumped.
- [x] `ci.yml`: `checkout`, `setup-dotnet`, `upload-artifact` bumped (the PR's own CI run proves them on ubuntu and windows).
- [x] `release.yml`: **no change** (only a real release proves it). Recommendation listed below.

### 3. QA
- [x] Negative control: `PYTHON=python3.14 bash tools/interop-live/run-python-leg.sh` exits 2 before installing anything; `PYTHON=/nonexistent` exits 2 ("not runnable").
- [x] YAML check: both workflows parse (Ruby `YAML.load_file`; steps, ids, `uses:` and env listed as intended); `setup-python@v7` declares the `python-path` output. No `actionlint` installed locally; the dispatched run is the end-to-end check.
- [x] First full harness run on 26.04 (the failing runs stopped before Setup Java): both legs ran; jvm leg 60 executed / 3 n/a; `fetch-deps.sh` SHA-256 checks pass under 26.04's coreutils.
- [x] Live run from the branch: `gh workflow run interop-live.yml --ref fix/live-interop-python-120`; record run URL, image, CPython patch, per-cell summary; compare against the 2026-11-07 baseline (every FAIL is a finding; N-A must match).
- [x] Log of the live run shows no "Node.js 20 is deprecated" annotation.
- [x] Ordinary CI green on the PR head (ubuntu + windows), no Node 20 annotation. The first run on `2658f31` (43885384456) was red on windows: a test defect, diagnosed and fixed (see Review). The run on `75a3ce2` (43888426496) was green on both: Core 2,548, Mediator 4,272 + 8 skipped, Interop 1,380 + 1 skipped. Its only annotation was the `ubuntu-latest` notice.

### 4. Adversarial pass (one short review of the workflow diff)
- [x] Supply chain: each `uses:` ref is an existing tag of the right repo (re-verify with `gh api`), and the same tag style as before.
- [x] `permissions`: unchanged (`contents: read`), nothing new requested.
- [x] `release.yml`'s publish path: byte-unchanged.
- [x] The `PYTHON` env binding cannot be steered by untrusted input (it is an action output, not event data).
- [x] The artifact upload path stays `${{ runner.temp }}/interop-summary/` (checkout v6+ writes its credentials file under `$RUNNER_TEMP`).

### 5. Docs, PR
- [x] CHANGELOG entry under `[Unreleased]`.
- [x] Commits `2f886e8` (ci), `2658f31` (docs), `75a3ce2` (test fix) and this plan update; PR #122 against `main` (`Closes #120`, `Part of #88`).

## Sweep list (step 4 of #120) — fixed vs listed

Fixed in this PR:
1. `interop-live.yml` `python-version: '3.9'` — the bug.
2. Node 20 action majors in `interop-live.yml` and `ci.yml` (table above).

Listed for the owner, not changed:
3. **`release.yml` action majors** (`checkout@v4`, `setup-dotnet@v4` ×2, `upload-artifact@v4`, `download-artifact@v4`). They run today, forced onto Node 24 with a warning: release v1.5.1 (run 41564679011, 2026-11-03, on `ubuntu-24.04`) passed with exactly that warning on both jobs. Its `pack` job's steps also run in `ci.yml`, green on `ubuntu-26.04` at `703ab92` (run 43880029542); the `publish` job has not run on 26.04 yet. Recommendation: bump them to the same majors (`download-artifact` → v8, node24) in a separate change proven by the next release (or a throwaway pre-release tag the owner approves). The `pack` job's three steps are the same as `ci.yml`'s, which this PR proves; the `publish` job's `setup-dotnet` + `download-artifact` are not proven by anything short of a release. `NuGet/login@v1` is already node24.
4. **Floating runner images** — `ci.yml` (`ubuntu-latest`, `windows-latest`), `release.yml` (`ubuntu-latest` ×2), `interop-live.yml` (`ubuntu-latest`). Recommendation: keep floating. The SDK is pinned by `global.json`, Java and Python are downloaded at the version the workflow names, and the nightly is the early warning that caught this. Pinning images trades a loud failure for a silent one when the pinned image is retired. (If the owner prefers pins: note that `ci.yml`'s pack step tests `matrix.os == 'ubuntu-latest'` and must change with it.)
5. **`release.yml` publish job's `dotnet-version: 10.0.x`** — floats within 10.0, independent of `global.json` (the job does not check out the repo). Not image-dependent (installed by the action); only runs `dotnet nuget push`. No action.
6. **Temurin 17** in `interop-live.yml` — downloaded from Adoptium, not taken from the image; Temurin 17 is an LTS line supported into 2027. No action now.
7. **`varint==1.0.2` builds from source** with whatever setuptools pip fetches at install time (unpinned build dependency). Same shape, smaller: a future setuptools could break the build. Recommendation: leave it; the nightly would show it at once, and pinning build tools adds upkeep for a one-line `setup.py`. Revisit if it ever breaks.
8. **`persist-credentials`** (architect, different defect): checkout keeps the job's read-only token on disk for later steps; `interop-live.yml` then runs third-party code (the `varint` sdist build, Maven jars) and `ci.yml` runs NuGet build logic. None of them needs git credentials after checkout. Recommendation: `persist-credentials: false` on every checkout, as its own change. The token is `contents: read` on a public repo, so the exposure is small, and checkout v6+ does not widen it (it moves the file, not the access).
9. **3.12 is a stopgap.** It runs until `ubuntu-latest` moves to an image with no 3.12 build (3.12 is end-of-life 2028-10). The lasting fix is moving the python counterpart off pydantic v1, which means a newer didcomm-python stack: out of #120's scope; for #98 or a follow-up.
10. **Action refs are major tags, not commit SHAs.** Unchanged style. Recommendation for the owner: SHA-pinning is the stronger supply-chain posture but needs a bump bot; out of #120's scope.

## Review

**Result.** Live interop run **43882021940** from `2f886e8` on this branch is green:
https://github.com/moisesja/didcomm-dotnet/actions/runs/43882021940

- **Image and tools.** Ubuntu 26.04 (`ubuntu26/20261111.196`), CPython **3.12.16** (from the tool cache), Temurin **17.0.21**, SDK 10.0.404.
- **Interpreter binding.** The harness step's env shows `PYTHON: /opt/hostedtoolcache/Python/3.12.16/x64/bin/python`, and the leg logs `python: CPython 3.12.16 from /opt/hostedtoolcache/Python/3.12.16/x64`, so the venv came from the interpreter the workflow installed, not a `python3` fallback.
- **python leg:** 63 executed · 63 passed · 0 failed · 0 n/a.
- **jvm leg:** 60 executed · 60 passed · 0 failed · 3 n/a.
- **Annotations.** Only the `ubuntu-latest` migration notice. No "Node.js 20 is deprecated" warning, and no javac problem-matcher annotations.

**Compared with the last green run** (42363748989, 2026-11-07, `d8a100c`, Ubuntu 24.04, CPython 3.9.25), I diffed `summary.md` below its header:
- Every cell has the same result.
- The N-A cells are the same three on the JVM side: outbound and inbound `signed ES256K` and vector `signed-es256k`.
- The only additions are one row per leg, "authcrypt via a running dotnet mediator (pickup)", PASS. #95 added them after 11-07.
- So there is no FAIL cell and no regression since 2026-11-07, which covers #95, #96, #113 and #119.

**Negative control.** The interpreter guard is unchanged and still bites. Each case exits **2** before creating a venv (the work dir stays empty):
- `PYTHON=python3.14`;
- `PYTHON=/nonexistent/python` ("not runnable");
- an empty `PYTHON`, which falls back to `python3`, 3.14 on this Mac. This is the architect's trap: a mistyped step id would fall back the same way. The run log's env line above shows that did not happen.

**First failure date.** The issue says 2026-11-10, but the first failing nightly is 2026-11-08 (42471799819), then 11-09 (42611473329), all with the same "3.9 … not found for Ubuntu 26.04" error.

**Adversarial pass** (one reviewer, read-only): no security or correctness blockers.
- **Supply chain.** Every ref is in `actions/*`, in the existing floating-major style, and is the newest release of its major: checkout v7.0.1 `3d3c42e`, setup-dotnet v6.1.0 `0f4a354`, setup-python v7.1.0 `6d4fd22`, setup-java v6.3.0 `d1374f8`, upload-artifact v7.0.2 `cf430e0`. Each declares node24.
- **Permissions.** `permissions` is unchanged.
- **Credentials file.** In the run, checkout's credentials file sits at `_temp/git-credentials-<uuid>.config`, a sibling of the uploaded `_temp/interop-summary/`, so the upload cannot pick it up.
- **Injection.** `PYTHON` comes from an action output, not from event data.
- **`release.yml`.** Byte-unchanged.
- **The new echo line** cannot abort the leg under `set -euo pipefail`.
- **Its only hold was the docs and CHANGELOG** (done in the docs commit).
- **One note for the owner.** A `release: published` live run uses the workflow at the released tag. A tag cut before this merge (for example 1.6.0, if it were cut from an older commit) would still ask for 3.9 and fail. Cut releases after this merges.

**CI finding on the PR (windows), diagnosed and fixed: a test defect, not a product defect.**

- **The failure.** Run 43885384456 on `2658f31` (ubuntu green, windows red) failed one test: `DeliveredUnpackAdv4Tests.Host_code_that_throws_on_its_thread_after_the_shutdown_is_observed_and_gives_its_slot_back` (Mediator.Tests, #96 unpacker). The message was "Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown", after 10 s.
- **Not caused by this PR.** The image (`windows-2025-vs2026`), SDK 10.0.404 and runtime 10.0.15 are the same as main's green windows run 43880029542, and no code changed. It is this test's first failure in the CI history searched (all CI runs since 11-13).
- **Mechanism.** The test ends the shutdown with `await shutdown.CancelAsync()`, which runs the token's callbacks (`GuardedDeadline` → `OnBudgetEnded`) on a thread-pool work item. The host code under test blocks two pool threads with `gate.Wait(RealTime)` (10 s) and then throws. If the pool reaches the CancelAsync item more than 10 s late, the gates time out first, both unpacks fail normally, `Complete` closes the delivery with outcomes, and `OnBudgetEnded` later finds it closed. The batch never throws. Production cancels synchronously (`MediatorClientChannel.Dispose` → `GuardedDeadline.CancelQuietly(_shutdown)` → `Cancel()`), so the test's stimulus differed from the product's (L-065 rule 7: real time decided the outcome).
- **Forced** (scratch branch `diag/120-win-flake`, local, unpushed; `Diag120ForcingTests`). Before the cancel, 400 pool work items blocking for 15 s are queued:
  - `CancelAsync` + starved pool: **FAIL**, with the exact CI message.
  - `Cancel()` + starved pool: pass (13 ms).
  - Both pass on an idle pool.
- **Fix** (test only). `shutdown.Cancel()` in place of `await shutdown.CancelAsync()`, with a comment.
  - Mutation: `OnBudgetEnded` with `shutDown = false` (the unpacker ignores the shutdown). The fixed test fails deterministically (47 ms).
  - Slow but correct: it passes under the forced starvation.
  - Mediator.Tests: 4,272 passed, 8 skipped (unchanged).
- **Sweep** (L-039): all 41 `CancelAsync()` calls in `tests/` were classified by one read-only agent: 4 are before start, 35 are hang guards only, and 2 were suspected of the same shape.
  - The second suspect is `MediatorClientTimeoutRaceTests.When_the_deadline_fired_and_the_caller_cancelled…:230`. Under the same forced starvation it **passes** (24 ms): at that moment the caller's token has no callbacks for CancelAsync to defer. Left unchanged.
  - `DeliveredUnpackFix3Tests:119` is a hang guard only (its host code waits on a TCS or on its token, on a manual clock). Left unchanged.

**Windows leg of `ci.yml`.** The new majors ran on `windows-2025-vs2026` in both PR CI runs. The checkout and its post-job credential cleanup worked, and so did the submodule and the artifact upload. Upstream, checkout v7.0.1 includes the fix for v7.0.0's Windows credential-path cleanup bug (actions/checkout#2528).

**Live interop on the code head `75a3ce2`.** Run 43888459643 is green. Its per-cell `summary.md`, below the header, is byte-identical to 43882021940's. A first dispatch on `2658f31` (43883317735) stayed "queued" with no jobs for over 30 minutes, during a GitHub incident; `gh run cancel` answered that it was already completed. It never ran, so it is not evidence either way.

### PR review round 1 (comment 6919371539, on `2658f31`)

The reviewer found no defect in the workflow or harness diff. They held approval only on the red windows CI run, and asked for:
- a diagnosis;
- the comparison with the base;
- a green windows run;
- a deterministic fix that does not disable the assertion and does not rely on a passing retry.

The test-only fix stays in #122; we chose that over a separate issue. It is small, deterministic and in a CI-reliability PR, and splitting it out would leave a known flake on `main`.

**Addressed the reviewer's point that `gate.Wait(RealTime)`'s return value was ignored:**
- The host code now counts a gate that timed out.
- The test asserts, right after the synchronous `Cancel()`, that none had: the shutdown was decided while both unpacks were still blocked.

**Proof:**
- **Forced starvation** (`Diag120ForcingTests` with the same check):
  - the old `CancelAsync()` version now fails at the new check, "found 2": both hang guards had fired. That proves the ordering the CI log could not show;
  - the fixed version passes in 13 ms;
  - both pass on an idle pool.
- **Mutation:** with `OnBudgetEnded` set to `shutDown = false`, the strengthened test still fails (47 ms, at the `OperationCanceledException` assertion).
- **Mediator.Tests:** 4,272 passed, 8 skipped; build with `/warnaserror` clean.

