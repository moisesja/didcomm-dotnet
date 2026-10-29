# PR #83 Review Plan

PR: `feat(pack): encrypt to every keyAgreement key of each recipient (FR-ENC-19, #81)`

## Scope and constraints

- [x] Identify the newly opened PR and refresh `origin/main` so the review uses the actual merge base.
- [x] Read `tasks/lessons.md`, the PRD, the PR title/body/commits/file list, and CI state.
- [x] Preserve unrelated worktree files and make no source/test/documentation edits during review.
- [x] Distinguish PR regressions from disclosed or pre-existing gaps by comparing behavior with `origin/main` where needed.

## Review

- [x] Inspect every changed production path, concentrating on recipient ordering and `apv`, common-curve negotiation, DID-URL pins, cross-DID kid shadowing, envelope-wide deduplication, forward onion ordering, and mediator rewrap behavior.
- [x] Audit the critical-path risks: attacker-controlled DID documents, foreign/duplicate/kidless/oversized kids, key and forward-layer limits, resolution amplification, cancellation, error taxonomy, untrusted-value quoting, logging, possession/timing oracles, and API/binary compatibility.
- [x] Inspect tests for happy paths and adversarial permutations, including Appendix C values, last-device decryption, pins, mixed curves, duplicate DIDs/kids, cap boundaries, malicious foreign kids, routing depth, resolution counts, and failure-category consistency.
- [x] Check that PRD, changelog, samples, and live-harness claims match the code and do not present FR-ENC-04 or the disclosed receive-side limitations as solved.
- [x] Evaluate scope size and commit history; record weak or unclear commits separately from correctness blockers.

## Validation

- [x] Run focused FR-ENC-19, recipient-selection, routing, and spec-vector tests.
- [x] Run the full build and test suites with warnings as errors in a configuration proportionate to CI.
- [x] Run package validation or packing if the repository workflow supports it locally without external mutation.
- [x] Compare any suspected regression with `origin/main` in an isolated worktree or by base-source inspection.
- [x] Perform an independent adversarial pass against the final revision and reconcile all findings.

## Disposition

- [x] If a concrete concern remains, post a specific GitHub review explaining the affected path, observable impact or exploit, required change, and missing regression test.
- [x] Otherwise, post a concise approval that names the critical paths and validation actually checked. *(Not applicable: concerns remained.)*
- [x] Add the final findings, commands/results, and published review URL to the Review section below.

## Review

Review posted on https://github.com/moisesja/didcomm-dotnet/pull/83 with status `COMMENTED`
(the authenticated account owns the PR, so GitHub does not permit a self-authored
`REQUEST_CHANGES` review).

Findings requiring changes:

1. `SelectRecipientKeys` truncates a bare DID before envelope-wide deduplication. With 65
   same-curve keys and `[bob#x1, bob]`, the pin effectively consumes one of the 64 automatically
   selected slots, so `x65` is lost despite the public contract saying pins do not count.
2. The PRD/CHANGELOG say “at most 64 keys per DID,” while XML docs and implementation exempt
   explicit pins. The normative contract must be made consistent before implementation can be
   judged correct.
3. PRD/CHANGELOG/PR text overclaim that an over-depth route is refused “before any resolution.”
   Recipient key and service resolution already happened; only mediator/routing-key resolution is
   avoided.
4. The head advanced during review with `f22d7d8 Review Plans`, which imports three unrelated
   review-plan files for PRs #78/#82. A follow-up review comment asks that scope pollution be
   removed and calls out the vague commit subject.

Validation:

- Targeted Core: 116 passed, 0 failed/skipped.
- Targeted spec/routing interop: 11 passed, 0 failed/skipped.
- Full suites: Core 1,115/1,115; Interop 290/290; 0 skipped.
- Release `/warnaserror` build: clean; package creation: all six shipping packages produced both
  `.nupkg` and `.snupkg`.
- New JVM multi-key cells passed both directions. Seven unrelated macOS P-256 provider failures
  prevented an all-green local JVM run; the Python leg could not run because the host CPython 3.14
  is intentionally outside the peer stack's supported 3.9-3.12 window.
