# PR #109 re-review

Authorized by the explicit re-review request (L-033/L-047). Prior head `bbb1fee407e9e403e4ad52524838625c78092be0`; current remote head `755c497cbc7e7c685d5595df72a4852692632c4d`.

- [x] Read current PR metadata and implementer response.
- [x] Inspect incremental changes and independently verify both prior findings.
- [x] Review new concurrency, cancellation, leasing and resource-bound behavior; run proportionate validation.
- [x] Recheck remote head and publish follow-up disposition.

## Results

Both original findings are fixed. Follow-up review requests changes for two newly reproduced P2 defects in the redesign:

1. Expired waiting jobs still count against dispatcher quotas after the store frees their records. Real-endpoint quota-2 reproduction: first live push briefly gated, second record expires, third accepted but refused a push as Saturated. It remains pickup-only after the gate releases. Previous-head control delivers the third live. No timeout, cooldown, recipient action or session failure involved.
2. Timed-out live calls that ignore cancellation escape concurrency/accounting after Finish clears them. Repeated timeout, cooldown, session replacement and pickup/ack produced six actual pending sends with PushConcurrency=1, Outstanding=0 and an empty store. Requires a noncooperative asynchronous collaborator; not claimed against default cooperative resolution.

- Independent architecture, QA and adversarial passes completed.
- CI 43518089627 on current head: Linux and Windows each 7,313 passed, zero failures/skips; clean builds and Linux package compatibility.
- Scratch Release build clean with archive-only SourceLink/SCM metadata disabled; 113 targeted tests passed, zero failures/skips.
- Original checkout source/tests/docs untouched; scratch extraction `/private/tmp/pr109-rereview`. Reproductions `/private/tmp/pr109-rereview-architect/Program.cs`, prior control `/private/tmp/pr109-control-architect/Program.cs`, `/private/tmp/pr109-orphans/Program.cs`.
- Review body `/private/tmp/pr109-rereview-body.md`; posted through GitHub CLI (connector write permission was unavailable in prior review). Remote head unchanged immediately before publication.
- CHANGELOG unchanged: review only, no implementation change.
- Publication verified: https://github.com/moisesja/didcomm-dotnet/pull/109#pullrequestreview-5899449894 .
