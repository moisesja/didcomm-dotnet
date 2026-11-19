# PR #130 re-review

Requested re-review authorizes analysis, validation and the GitHub review comment
(L-033/L-047). No source, tests or product documentation edits planned.

- [x] Pin current head 5d3fbba, previous reviewed head fec76c3, base a6b1ebe.
- [x] Recheck all three previous findings and regression coverage.
- [x] Review the additional framing, wire-shape and advisory-addressing changes.
- [x] Run proportionate tests/probes; inspect exact-head CI.
- [x] Recheck head and publish evidence-backed disposition.

## Review

Parallel reviewers covered HTTP lifetimes, client correlation and harness/WebSocket
framing; primary reviewer covered routing payload validation and integration.

Published: https://github.com/moisesja/didcomm-dotnet/pull/130#issuecomment-7018451785

HTTP P1 and reusable-directory P2 are resolved, independently reproduced. WebSocket
P2 remains across calls: after AckAsync abandons a chunk and completes on a later
threaded status, its delayed threadless reply can answer a subsequent GetStatusAsync.
Confirmed with actual channel/crypto snapshots and a manual clock against main's
corresponding code. Also noted stale binary-frame diagnostics as nonblocking.

Fresh Release solution build with warnings as errors: zero warnings/errors.
Targeted tests: Core 610 passed/1 opt-in skip; Mediator client 798 passed/8 existing
skips; Interop 66 passed. Total 1,474 passed, 9 skipped. Exact-head CI 44575818477
passed. Full Docker leg not rerun. Head unchanged before posting. No tracked product
source/tests/docs edited; scratch probes reside under /tmp.
