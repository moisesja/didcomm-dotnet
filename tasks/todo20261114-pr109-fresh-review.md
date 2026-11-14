# PR #109 — fresh review

Authorized by the explicit request for a review from scratch. Review all current changes against main, not only prior findings. No implementation changes are planned.

Head: `2477fd27b026171a9977206fe31ae93279ee52f6`; base: `add80ab3dd0f4e57820eb113f016346457508221`.

- [x] Read current PR title, description, scope decisions and discussion; pin base/head.
- [x] Read requirements and full production diff with callers, tests and documentation.
- [x] Independently assess routing/security, hosting/store/push design, cancellation and resource bounds.
- [x] Build and test current head; reproduce any actionable defects and compare with base.
- [x] Recheck remote head, post a fresh review disposition and verify publication.

## Results

Fresh review against main, not just the previous review's fixes. Covered routing and restricted
self-addressed peeling, store and live delivery, lease cleanup, public API changes, DI and startup,
endpoint mapping, expiry/delay, transport guards, logging, documentation, tests and commit scope.
Direct push's removal is the explicit owner decision tracked in #110.

Independent Release build with `/warnaserror`: 0 warnings, 0 errors. Full current-head suite:
Core 2,448 + Interop 1,256 + Mediator 3,475 = 7,179 passed, 0 failed, 0 skipped.
Logs: `/private/tmp/pr109-2477-build.log`, `/private/tmp/pr109-2477-test.log`.
GitHub CI run 43561069320 is successful on Windows and Ubuntu; Ubuntu package/API compatibility
validation also passed. Remote head rechecked and unchanged at 2477fd2.

Security and hosting passes found no actionable defect. The pending-wake collection has no separate
capacity limit, but the stock replay guard bounds distinct peer admissions and DID strings are
shared; this is not evidence of the earlier unbounded payload-retention defect. Do not present it
as a demonstrated vulnerability. The documented limits around stalled collaborators and best-effort
live retries remain relevant. `git diff --check` found one trailing-space-only line in an existing
design task file (line 182); this is not a merge blocker.

Architecture pass also found no actionable defect. Independently reran the prior expiry/saturation
reproduction through real HTTP/WebSocket endpoints: three forwards returned 202; first and third
pushed live, second expired; the expired record no longer displaced the newer live push.
Scratch reproduction: `/private/tmp/pr109-fresh-architect/Program.cs`.

Posted an approving review comment (GitHub state COMMENTED) on the reviewed head and verified it
through the GitHub API:
https://github.com/moisesja/didcomm-dotnet/pull/109#pullrequestreview-5904640123
No implementation files edited. No CHANGELOG change for a review-only task.
