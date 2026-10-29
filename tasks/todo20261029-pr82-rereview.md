# PR #82 Re-review Plan — Head 35ed5f5

## Baseline

- [x] Read the prior review, relevant lessons, current PR body, commits, files, checks, and review history.
- [x] Isolate the delta from rejected head `d9ae685` to current head `35ed5f5`.
- [x] Obtain a Software Architect assessment of the remediation and re-review risks.
- [x] Validate the exact PR head in an isolated worktree, preserving unrelated stacked local changes.

## Prior findings

- [x] Re-run the exact long-alias-first differential scenario that succeeded on `main` and failed at the prior PR head.
- [x] Confirm an over-limit recipient kid never reaches `ISecretsResolver`, while a valid same-material alias still decrypts.
- [x] Confirm FR-CONSIST-08 remains fail-closed for swapped/forged labels, including post-encryption tampering and a skipped entry beside the swap.
- [x] Confirm PR body, PRD, CHANGELOG, source docs/comments, and task records describe only signer kids the verifier actually looks up. The release-facing artifacts are aligned; the historical task record has a nonblocking stale summary noted below.

## Remediation review

- [x] Prove reordering the unprotected `recipients` array preserves protected-header AAD, `apv`, encrypted-key/header pairing, and authcrypt/anoncrypt semantics.
- [x] Exercise long aliases first, middle, and multiple; kept entries retain stable envelope order; same-material aliases; held, stranger, extractable, and opaque-key paths.
- [x] Confirm `AllRecipientKids` preserves the original wire order even when the parser consumes a reordered copy.
- [x] Check flattened/general structure behavior, nested envelopes, malformed JSON exception contracts, logging redaction, concurrency, and allocation/scalability impact.
- [x] Inspect the added tests for false positives and mutation strength; ensure each critical guard/reorder step is necessary to pass.

## Validation and disposition

- [x] Have independent Staff QA and adversarial reviewers inspect and test the updated head.
- [x] Run `git diff --check`, focused alias/identifier/security tests, full Core tests, and Interop tests as proportionate; compare with current green Linux/Windows CI.
- [x] Publish a specific concern if any regression or overclaim remains; otherwise post a concise approving review comment.
- [x] Record commands, results, disposition, and GitHub review URL below. Do not modify product code, tests, PRD, or CHANGELOG during review.

## Re-review results

- Reviewed exact base/head `a71e9a5...35ed5f51` in `/tmp/didcomm-pr82-rereview.Cpo2to`; unrelated stacked working-tree changes were not used or modified.
- The remediation delta is one focused commit across four files (+166/-9). It stable-partitions whole JWE recipient entries from envelope content alone, keeps all allowed entries first in original order, leaves the FR-CONSIST-08 equality check unchanged, and reports `AllRecipientKids` from the original peek.
- The prior long-alias-first regression now passes. Additional independent probes passed anoncrypt, authcrypt, opaque-key, middle-position and multiple dropped aliases, reversed resolver order, post-encryption label tampering, holder/stranger equivalence, and original metadata order. Over-limit kids never reached `ISecretsResolver`.
- JOSE invariants hold: whole recipient entries move together; protected-header AAD, `apu`, `apv`, ciphertext, tag, and optional `aad` strings are unchanged; DataProofs commits `apv` to the sorted kid list.
- Resource probe: eight concurrent unpacks with an approximately 700k-character skipped alias completed in about 20 ms and allocated about 80 MiB total. The extra work remains linear and bounded by `MaxReceiveBytes`.
- Validation: `git diff --check` clean; focused local suite 235/235; full Core 1011/1011; full Interop 283/283 after initializing the pinned fixture submodule; independent warn-as-error solution build exited 0; current Ubuntu and Windows CI green.
- Minor nonblocking hygiene: `tasks/todo20261028-keyid-cap-80.md:224-225` retains the old absolute “every wire key id” wording and line 231 reports 1008/1008 instead of 1011/1011. PR body, PRD, and CHANGELOG are accurate.
- Disposition: approve. GitHub forbids formal self-approval, so the approval was posted as a `COMMENTED` review: https://github.com/moisesja/didcomm-dotnet/pull/82#pullrequestreview-5705849309
