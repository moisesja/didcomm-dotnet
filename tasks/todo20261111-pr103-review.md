# PR #103 review

Review authorization: the user requested inspection and publication of a specific,
honest review. Lessons L-033 and L-047 apply; this task does not edit implementation,
tests, PRD, architecture, or changelog.

Target: `moisesja/didcomm-dotnet#103`, head
`b1114cff5d228c4194a1edbf5bf7dde0983a4133`, base
`1fbcc31dbb6cfb19760543bb8a9224e3dce34f21`.

- [x] Identify the latest open PR; read title, description, commits, and change inventory.
- [x] Read relevant requirements, architecture, diff, and supporting code across the review team.
- [x] Architect review of core rules, replay, registry, and scalability.
- [x] Independent adversarial review of receive admission and session lifecycles.
- [x] QA review of test coverage, DI changes, build, and tests.
- [x] Verify findings against base behavior and exact head; distinguish bugs from accepted scope.
- [x] Recheck PR revision and post a specific review comment with findings or approval.
- [x] Record validation and review link below.

## Review results

Published review: https://github.com/moisesja/didcomm-dotnet/pull/103#pullrequestreview-5862569342

Disposition: COMMENT with recommendation to change before merge; two P2 inline findings:

1. WebSocket push capacity is checked after snapshot and awaited packing. With a blocked
   writer and two queued frames, 20 excess sends entered the gated key resolver and stayed
   pending; all returned false after resolution resumed. Reserve capacity before expensive work.
2. Registry lifecycle subscribers run synchronously before Register returns. A blocked
   SessionClosed(Evicted) callback prevents the endpoint from cancelling/closing the evicted
   session and arming its timeout. This contradicts the published fire-and-forget guarantee.

Scratch reproductions (outside repository source/tests):
- /private/tmp/pr103-adversarial-repro/Program.cs
- /private/tmp/pr103-qa-events/Program.cs

Validation at the reviewed head:
- `dotnet build DidComm.sln -c Release /warnaserror --no-restore --disable-build-servers -m:1`:
  0 warnings, 0 errors.
- `dotnet test DidComm.sln -c Release --no-build --no-restore --disable-build-servers -m:1`:
  1490 Core + 484 Interop passed; zero failures/skips.
- `git diff --check 1fbcc31...b1114cf`: passed.
- GitHub CI run 43037841047: Linux and Windows build/test passed; Linux pack passed.
- A suspected DispatchAsync default-literal overload ambiguity was disproved by actual-DLL
  consumer compilation and was excluded from the review.

No implementation, test, PRD, architecture, or changelog edits. The pre-existing modification
to tasks/todo20261110-pr102-review.md was preserved. GitHub connector review creation lacked
integration permission (403); the authorized GitHub CLI submission succeeded.

## Re-review requested by user

Target: `6de169bf8856515a839d521bf24a5cbeff9a3c07`; prior reviewed head `b1114cf`.
Review authorization continues under L-033/L-047; no implementation changes planned.

- [x] Inspect follow-up diff and requirements/documentation reconciliation.
- [x] Independently verify push reservations and asynchronous event lifecycle behavior.
- [x] Build, run tests, and check new regression coverage and CI.
- [x] Recheck remote head and publish updated review disposition.
- [x] Record findings and validation below.

Re-review: https://github.com/moisesja/didcomm-dotnet/pull/103#pullrequestreview-5863219455

The two original reproductions are addressed. Pushes reserve capacity before work;
the full-queue scratch reproduction now reports 0 blocked sends and 0 extra resolver
calls for 20 excess producers. Concurrent empty-queue admission with bound 2 admits
exactly 2 to the gated resolver, refuses 18 immediately, and cancellation restores both
slots. Async registry callbacks remove the direct eviction deadlock, covered by new
integration tests. Requirements/architecture reconciliation is accurate. The author's
decision to retain commit history is acknowledged and not treated as a blocker.

One new P2 finding: QueueNotification submits one global ThreadPool item per registry
change without a backlog/concurrency limit. An isolated scratch process with 4 workers,
MaxSessions=MaxSessionsPerPeer=1 and a gated subscriber demonstrated 40 registrations,
Count=1, 4 blocked callbacks and 36 queued notifications; unrelated work could not run
until the gate opened. Root independently reran the reproduction (exit 0). This requires
a slow/blocking host subscriber; no unauthenticated remote exploit is claimed.
Reproduction: /private/tmp/pr103-architect-compat/Class1.cs.

Validation at 6de169bf:
- Release build with /warnaserror, --no-restore --disable-build-servers -m:1: 0 warnings/errors.
- Full Release suite with --no-build --no-restore --disable-build-servers -m:1:
  1492 Core + 490 Interop passed, zero failures/skips.
- git diff --check 1fbcc31..HEAD: passed.
- GitHub CI run 43070800081: success.
- Remote head rechecked immediately before submission; unchanged.

No implementation/test/product-documentation changes made during re-review.

## Additional re-review request: unchanged revision

- Verified GitHub PR metadata through both connector and authenticated GitHub CLI:
  head remains `6de169bf8856515a839d521bf24a5cbeff9a3c07` (no new commits).
- Local HEAD matches; no source/test changes are present.
- Reran the isolated notification-backlog reproduction: Count=1, 4 blocked callbacks,
  36 queued notifications; unrelated work runs only after the gate is released (exit 0).
- Existing review 5863219455 remains applicable. No duplicate review posted and no
  full build/test rerun, since the reviewed revision is unchanged.
