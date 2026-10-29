# PR #83 Review Plan

PR: `feat(pack): encrypt to every keyAgreement key of each recipient (FR-ENC-19, #81)`

## Scope and constraints

- [x] Identify the newly opened PR and refresh `origin/main` so the review uses the actual merge base.
- [x] Read `tasks/lessons.md`, the PRD, the PR title/body/commits/file list, and CI state.
- [x] Preserve unrelated worktree files and make no source/test/documentation edits during review.
- [ ] Distinguish PR regressions from disclosed or pre-existing gaps by comparing behavior with `origin/main` where needed.

## Review

- [ ] Inspect every changed production path, concentrating on recipient ordering and `apv`, common-curve negotiation, DID-URL pins, cross-DID kid shadowing, envelope-wide deduplication, forward onion ordering, and mediator rewrap behavior.
- [ ] Audit the critical-path risks: attacker-controlled DID documents, foreign/duplicate/kidless/oversized kids, key and forward-layer limits, resolution amplification, cancellation, error taxonomy, untrusted-value quoting, logging, possession/timing oracles, and API/binary compatibility.
- [ ] Inspect tests for happy paths and adversarial permutations, including Appendix C values, last-device decryption, pins, mixed curves, duplicate DIDs/kids, cap boundaries, malicious foreign kids, routing depth, resolution counts, and failure-category consistency.
- [ ] Check that PRD, changelog, samples, and live-harness claims match the code and do not present FR-ENC-04 or the disclosed receive-side limitations as solved.
- [ ] Evaluate scope size and commit history; record weak or unclear commits separately from correctness blockers.

## Validation

- [ ] Run focused FR-ENC-19, recipient-selection, routing, and spec-vector tests.
- [ ] Run the full build and test suites with warnings as errors in a configuration proportionate to CI.
- [ ] Run package validation or packing if the repository workflow supports it locally without external mutation.
- [ ] Compare any suspected regression with `origin/main` in an isolated worktree or by base-source inspection.
- [ ] Perform an independent adversarial pass against the final revision and reconcile all findings.

## Disposition

- [ ] If a concrete concern remains, post a specific GitHub review explaining the affected path, observable impact or exploit, required change, and missing regression test.
- [ ] Otherwise, post a concise approval that names the critical paths and validation actually checked.
- [ ] Add the final findings, commands/results, and published review URL to the Review section below.

## Review

Pending.
