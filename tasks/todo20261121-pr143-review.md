# PR #143 review

Read-only review authorized by the user's explicit request (lessons L-033/L-047). No source, test, PRD, or product documentation changes.

- [x] Identify PR, title, description, immutable base/head, and changed files.
- [x] Inspect requirements, diff, commit history, existing discussion, and CI.
- [x] Architect review: client negotiation, acknowledgement, correlation, and resource lifetime.
- [x] QA review: codec, handlers, registration, fixtures, and missing tests.
- [x] Adversarial review: mailbox leases, live push, input validation, and scalability.
- [x] Run proportionate validation and independently confirm concrete findings against base and intended decisions.
- [x] Recheck remote head, then publish the requested specific review comment.
- [x] Record review outcome and validation limits here.

## Review

Base `0b3a3e40c2508726af7643e5ef75c870b1ab729b`; head `ef24a301363585ea1186f53f71e753c6cc118e1e` (rechecked before posting).

Published COMMENT review recommending changes before merge:
https://github.com/moisesja/didcomm-dotnet/pull/143#pullrequestreview-6005610073

Two P2 findings:
1. PickupCoordinator.DeliverAsync's exceptional exit releases by lease ID even after session closure, losing the new returned-live-push mark when owner cleanup is delayed. An isolated reproduction against head assemblies reports Queued / one ID release / next push Sent / older socket receives one message. Same close with a normal Sent return reports Queued / zero ID releases / next push Empty. Scratch reproduction: `/private/tmp/pr143-mailbox-repro/Program.cs` (not committed).
2. The new 4.0 AckAsync one-way send can use a pooled socket while its confirming status uses another session socket, allowing the status to precede acknowledgement. Existing QA-2 test explicitly reproduces this. Negotiated versions cached by mediator DID expose HTTP-to-WebSocket and lost-session paths too, beyond the documented pinned-V4 case.

Validation: freshly rebuilt mediator suite 5,423 passed / 11 skipped; focused interoperability suite 126 passed / zero skipped; diff whitespace and shell syntax checks passed. Head CI Ubuntu/Windows build+test jobs green; Ubuntu pack checks green. External Docker interop not rerun. No source, test, product-documentation, or CHANGELOG edits: this task reviews another branch rather than shipping an implementation change.

Publishing: GitHub connector denied both review and conversation-comment writes (403 integration permission). Authenticated local GitHub CLI successfully posted the anchored review and inline comments. No duplicate review was created by the failed connector calls.
