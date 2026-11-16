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
- [ ] `interop-live.yml`: `python-version: '3.9'` → `'3.12'`, comment says why 3.12 (newest in the 3.9–3.12 window the harness accepts; upstream support to 2028-10) and that 3.13+ cannot work (pydantic v1).
- [ ] `interop-live.yml`: give the setup step an `id` and pass its `python-path` output to the harness step as `PYTHON` (step-level `env:`), so the leg seeds its venv from exactly the interpreter the workflow installed, not whatever `python3` is first on `PATH` (Ubuntu 26.04 ships a newer system Python; the guard would stop it with exit 2, but the binding should be explicit). Architect: keep. Nothing else in the harness calls `python3` (the jvm leg has no Python; the peer and pip run from `$VENV/bin`).
- [ ] `run-python-leg.sh`: print the venv interpreter's version next to the existing `didcomm-python:` stderr line, so the leg's log names the CPython patch it ran on (evidence only; the guard is unchanged). QA trap (architect): `${PYTHON:-python3}` treats an empty value as unset, so a mistyped step id would silently fall back to `python3`. Check the step's `env:` block in the log shows `/opt/hostedtoolcache/Python/3.12.*/x64/bin/python`.
- [ ] YAML comment + README: 3.12 is a stopgap. The same failure returns when `ubuntu-latest` moves to an image with no 3.12 build (3.12 is end-of-life 2028-10); the lasting fix is a counterpart stack off pydantic v1 (not in #120's scope).
- [ ] Harness guard (`run-python-leg.sh`, 3.9–3.12) unchanged.
- [ ] (after the green live run, citing its id — architect) `tools/interop-live/python/requirements.txt` header: "Validated on CPython 3.12 (macOS arm64 + Ubuntu 26.04 x86_64 in CI); imports on 3.9–3.12".
- [ ] `tools/interop-live/README.md`: Pinned versions row ("CPython 3.12 on CI") and CI cadence note (ubuntu-latest = 26.04, CPython 3.12, Temurin 17; the "remaining unproven claim" sentence updated with the run that proves it).
- [ ] No pin moves unless the live run forces one (stop and report first).

### 2. Node 20 sweep (same shape: a pinned version the runners are dropping)
Verified with `gh api` (tag ref exists; `runs.using` in `action.yml` at that tag):

| action | today | node | target | node | breaking notes relevant to our inputs |
|---|---|---|---|---|---|
| `actions/checkout` | v4 | node20 | **v7** (v7.0.1) | node24 | v6: credentials persisted to a separate file under `$RUNNER_TEMP` (so no upload may widen to `runner.temp`); v7: refuses fork-PR checkouts on `pull_request_target`/`workflow_run` (we use neither). `submodules: true` unchanged. |
| `actions/setup-dotnet` | v4 | node20 | **v6** (v6.1.0) | node24 | v5: old .NET versions dropped from the installer (we install the `global.json` SDK, 10.0.x). |
| `actions/setup-python` | v5 | node20 | **v7** (v7.1.0) | node24 | v7: `pip-install` input removed, EOL Pythons removed (we use neither). |
| `actions/setup-java` | v4 | node20 | **v6** (v6.3.0) | node24 | v4 itself warns "deprecated … migrate to v5"; v6 changes Zulu/Maven paths, renames `jdkFile`→`jdk-file` (unused), and added a javac problem matcher that v6.3.0 made opt-in (so no `InteropPeer.java` annotations expected). We use Temurin, no cache. |
| `actions/upload-artifact` | v4 | node20 | **v7** (v7.0.2) | node24 | v5 still node20; v6 node24; v7 adds opt-in `archive: false` (default unchanged: zipped). |

- [ ] `interop-live.yml`: all five bumped.
- [ ] `ci.yml`: `checkout`, `setup-dotnet`, `upload-artifact` bumped (the PR's own CI run proves them on ubuntu and windows).
- [ ] `release.yml`: **no change** (only a real release proves it). Recommendation listed below.

### 3. QA
- [ ] Negative control: `PYTHON=python3.14 bash tools/interop-live/run-python-leg.sh` exits 2 before installing anything; `PYTHON=/nonexistent` exits 2 ("not runnable").
- [ ] `actionlint`-style check of the YAML (parse + `gh workflow view` after push).
- [ ] First full harness run on 26.04 (the failing runs stopped before Setup Java): both legs ran; jvm leg 60 executed / 3 n/a; `fetch-deps.sh` SHA-256 checks pass under 26.04's coreutils.
- [ ] Live run from the branch: `gh workflow run interop-live.yml --ref fix/live-interop-python-120`; record run URL, image, CPython patch, per-cell summary; compare against the 2026-11-07 baseline (every FAIL is a finding; N-A must match).
- [ ] Log of the live run shows no "Node.js 20 is deprecated" annotation.
- [ ] Ordinary CI green on the PR head (ubuntu + windows), no Node 20 annotation.

### 4. Adversarial pass (one short review of the workflow diff)
- [ ] Supply chain: each `uses:` ref is an existing tag of the right repo (re-verify with `gh api`), and the same tag style as before.
- [ ] `permissions`: unchanged (`contents: read`), nothing new requested.
- [ ] `release.yml`'s publish path: byte-unchanged.
- [ ] The `PYTHON` env binding cannot be steered by untrusted input (it is an action output, not event data).
- [ ] The artifact upload path stays `${{ runner.temp }}/interop-summary/` (checkout v6+ writes its credentials file under `$RUNNER_TEMP`).

### 5. Docs, PR
- [ ] CHANGELOG entry under `[Unreleased]`.
- [ ] Commit(s) `ci(interop): … (#120)`; push; PR against `main` (`Closes #120`, `Part of #88`), summary, live-run URL + per-cell summary, N-A comparison, sweep list.

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

(filled in at the end)
