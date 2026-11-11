# PR #101 review

Review target: `76f05ba8f4d995325be9aac4cd3b92a23e08734a`, base `0716fc12bc5923002cd421975c5e2b0b15dd83bb`.

Scope: review and post feedback as requested; no source, test, PRD, or changelog changes.

- [x] Identify the newest open PR; read title, description, changes, commit history, and originating issues.
- [x] Verify the new requirements against existing contracts and primary protocol sources.
- [x] Independently review protocol semantics, architecture accuracy, and adversarial security cases.
- [x] Check changed-document integrity and distinguish missing implementation tests from appropriate documentation acceptance requirements.
- [x] Consolidate evidence-backed findings, verify the PR head remains unchanged, and post the review comment.
- [x] Record the review result and validation limits here.

## Review

Posted: https://github.com/moisesja/didcomm-dotnet/pull/101#issuecomment-6782073370

Requested changes on four requirement gaps: replay freshness/global cache bounds; quota accounting for retained live deliveries; encoded pickup-response byte limits; identity-aware WebSocket connection selection. These are design findings, not demonstrated vulnerabilities in shipped code.

Validation: reviewed all five changed files, originating issues #88/#89, the single commit, protocol sources, and affected existing transport paths. Three independent reviewers checked protocol, architecture, and adversarial cases. Local links in AGENTS.md and the architecture document resolve; all 30 new RR/MED/PICK rows have valid table structure; CLAUDE.md is the matching symlink; git diff --check passes. CI run 42919812325 succeeded. No runtime tests run locally for this documentation-only diff. Verified head remained 76f05ba before posting.

GitHub connector review and conversation-comment writes both returned 403. Posted the authorized review as a conversation comment using the local GitHub CLI. No source, tests, PRD, or changelog modified.

## Re-review of 58efd808

- [x] Read updated PR metadata, discussion, and complete delta from 76f05ba.
- [x] Recheck all four findings and interactions introduced by their fixes with independent reviewers.
- [x] Validate document structure and current CI; recheck remote head and post follow-up.
- [x] Record findings and verification limits.

Follow-up posted: https://github.com/moisesja/didcomm-dotnet/pull/101#issuecomment-6782697815

Replay-cache bounds, live-retention quotas, and count-only batching findings are addressed. Two P2 integration gaps remain: require relevant client requests to emit the timestamps now mandatory at receive and clarify replay-policy scope; preserve identity-specific socket affinity for omitted and explicit None headers after All establishes a session. Three independent reviewers rechecked the fixes. CI run 42924722437 passed, all 30 new requirement rows remain structurally valid with unique IDs, and git diff --check passes. No runtime tests run locally; no implementation or normative-document edits. Remote head rechecked as 58efd808 before posting.

## Re-review of 99010950

- [x] Read current PR metadata and full delta from 58efd808, including changelog and task notes.
- [x] Independently verify timestamp scope/emission and socket affinity fixes; no remaining blockers identified.
- [x] Verify CI and document structure, and recheck remote head.
- [x] Post approving comment and verify it.

FR-RR-09 defines clock-based timestamp stamping, preserves caller values, and covers default-client/default-server flows. FR-RR-08 explicitly exempts ordinary one-way application traffic. Nullable return-route options distinguish omission from explicit None; both preserve established connection affinity, with None retiring the same socket after downgrade. All three reviewers found the contract adequate. CI run 42928549099 passed; git diff --check passes; 31 RR/MED/PICK rows have unique IDs and valid table structure; checked local links resolve. No runtime tests run locally for this documentation-only change.

Approving conversation comment posted and verified: https://github.com/moisesja/didcomm-dotnet/pull/101#issuecomment-6783717576 . One nonblocking wording cleanup noted for Pickup 4.0 acknowledgement response wording. No formal GitHub approval or merge performed.
