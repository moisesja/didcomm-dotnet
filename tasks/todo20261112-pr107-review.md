# PR #107 review

Review authorized by the user's explicit request; follow lessons L-047. No implementation changes.

- [x] Identify the newest open PR; read title, description, base/head and commit messages.
- [x] Record review coverage with independent architecture/wire, queue/store and coordinator/security reviewers.
- [x] Read the complete production diff and assess relevant tests, requirements and known limitations.
- [x] Run proportionate existing tests and reproduce suspected defects without modifying the PR.
- [x] Validate findings against the exact PR head, distinguishing new defects from accepted limits and pre-existing behavior.
- [x] Post one specific review summarizing verified concerns, or a brief approving comment if none are found.

Base: `0d64ec9c66553c53601dbcce29bc986985085aee`.
Head: `4af9383ec3da3712a5f3d75b0eca90a93c865aaa`.

## Review coverage

Inspect queue bounds, isolation, acknowledgement and lease identity; live-session lifecycle and races; wire parsing and protocol compliance; bounded HTTP/WebSocket sends; shared coordination refactor; DI; test quality, commit clarity and scope. Existing uncommitted task/lesson files are unrelated and will be preserved.

## Results

`dotnet test DidComm.sln -c Release --no-restore --verbosity minimal`: 5,588 passed (Core 1,812; Mediator 3,005; Interop 771), zero failures/skips. GitHub CI run 43302101939 succeeded. `git diff --check` passed. GitHub head rechecked unchanged.

Two P2 findings validated independently:

1. PickupCoordinator resets its bounded scan/send budget on every request, but pass-over state lasts only one lease. With MaxDeliveryBatch=1, two individually TooLarge messages followed by a deliverable one, five polls 61 seconds apart all return status; an immediate control poll delivers the third message. Existing bounded-work tests do not advance the clock; slow-poller tests have only one blocker. Scratch harness: `/private/tmp/pr107-store-review`.
2. PickupSessionTracker starts detached lease cleanup without an active-operation or backlog bound. Public-handler repro with real empty mediation store and session registry capped at one session: 1,000 sequential session closures, zero grants or enqueues, 1,000 pending asynchronous store cleanup calls. All finish after releasing the test store gate. Root independently reran it. Scratch harness: `/private/tmp/pr107-release-repro-direct`.

Commit messages are descriptive. The 105-file scope is large but predominantly tests; the bounded-send API and documented default changes have stated reasons. No independent blocker found in wire codecs, the shared coordination refactor, DI or bounded return-route send implementation. No source, tests, PRD or changelog changed by this review.

Posted both findings and requested changes: https://github.com/moisesja/didcomm-dotnet/pull/107#issuecomment-6836723120 . The connector denied review/comment writes with HTTP 403; the authenticated local GitHub CLI posted the conversation comment successfully. Review body is preserved at `/private/tmp/pr107-review-comment.md`.

Validation limit: an additional full HTTP starvation repro stalled during fixture startup; the published finding accurately reports the successful dispatcher/store repro with a scripted bounded-send session, not a successful full HTTP reproduction.
