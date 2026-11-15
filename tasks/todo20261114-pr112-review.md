# PR #112 review — 2026-11-14

This is a read-only review of PR #112 at `69a96f5807888cf9810212c62ca4a253877d30da`, against `61f78983b34031750cc467ff1b24baa9b1520b72`. The user's direct request authorizes review and posting its outcome (lesson L-047). No source, test, PRD, architecture or changelog changes are proposed or authorized by this plan.

- [x] Identify the newly opened PR and pin base/head commits.
- [x] Read title, description, diff, relevant requirements and prior owner decisions across the review team.
- [x] Review mediation/version negotiation, request correlation and transport integration for identity, timeout, concurrency and resource failures.
- [x] Independently review pickup acknowledgement, deduplication, rewrapping and live polling paths, including adversarial inputs.
- [x] Examine test coverage, changed test expectations, commit clarity and scope; run suitable existing tests and isolated repros where useful.
- [x] Confirm each proposed finding against the pinned head, distinguish approved #111 deferrals, and identify exact paths/lines and corrective tests.
- [x] Post the authorized GitHub review comment with concrete concerns or a brief approval if no material concerns remain.

## Review results

Posted and verified a COMMENT review on PR #112, requesting a fix for one P2 defect: LiveDeliveryStream.cs:313 schedules the next cycle before awaiting its successful live-on response; when response latency is at least LivePollInterval, NextAsync repeatedly rearms and never drains or yields. Isolated reproduction in /tmp/pr112-live-review: interval 1s, successful responses 2s each, 20 live-on requests and zero drains. This differs from deferred ADV4-1.

Existing Release artifacts: 130 focused Core tests passed; 698 mediator-client tests passed with 7 documented skips; 112 interop-client tests passed with 1 documented skip. Independent focused runs overlap these counts. Exact-head Windows/Ubuntu CI passed. Fresh local build stalled and cancellation was requested; no fresh-build success claimed.

Commit descriptions are clear; scope is large (141 files, +28,830/-85, 104 commits) but cohesive. ADV1-1's edited no-loss assertion retains its separate genuine-delivery assertion. No additional proven authentication, acknowledgement-loss or negotiation bug was reported. No source, tests, PRD, architecture or changelog edits were made. Connector writes returned 403; authenticated GitHub CLI posted the requested review successfully, verified through the connector's review listing.

## Round 2 — scheduling fix re-review

Review head `c4a04239d57fcfd6a83d1e8e0d127d6c7a88db8a` against previously reviewed `69a96f5807888cf9810212c62ca4a253877d30da`. Read-only review and requested review disposition remain authorized under L-047; no implementation changes are planned.

- [x] Pin the new head and enumerate the scheduling/backoff delta.
- [x] Compare the new state transitions with PRD, architecture and recorded owner decisions.
- [x] Assess new regression tests and modified expectations for meaningful coverage of the original defect and follow-on races.
- [x] Independently attempt live scheduling failures, excluding accepted ADV5-3/4 deferrals to #111 from new findings.
- [x] Run proportionate validation and report exact new findings, or confirm the reviewed fix with remaining limitations.
- [x] Publish and verify the requested follow-up review disposition.

### Round 2 results

Previous P2 confirmed fixed: the independent latency reproduction reaches one drain after one live-on response at 2 simulated seconds with a 1s interval. No new actionable defect found. Clean Release solution build passed with zero warnings/errors using --no-incremental --disable-build-servers -p:UseSharedCompilation=false -m:1 /warnaserror. Full freshly built test suite: Core 2548 passed; Mediator 4251 passed/8 skipped; Interop 1370 passed/1 skipped (8169 passed, 9 documented #111 skips). Exact-head CI green. No tracked product or test files changed.

Posted and verified approving COMMENT review at https://github.com/moisesja/didcomm-dotnet/pull/112#pullrequestreview-5908515454, anchored to c4a04239d57fcfd6a83d1e8e0d127d6c7a88db8a. Non-blocking request: refresh stale PR description's polling behavior, ADV4-1 deferral and test totals.

Architect inspection: scheduling/backoff implementation agrees with the revised PRD, architecture, XML and recorded owner decision. Changed old test expectations reflect the new contract, while delivery/acknowledgement assertions remain. No new runtime defect confirmed. A possible count-after-clock-advance test rendezvous race was not reproduced: a scratch harness invoked the three relevant unmodified scheduling tests 400 times with eight concurrent workers, with zero failures; it is not a review finding. Scratch harness and results: `/tmp/pr112-scheduling-test-stress`.
