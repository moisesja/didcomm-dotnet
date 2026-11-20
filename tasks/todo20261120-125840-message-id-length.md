# Core hardening: bound message and thread id lengths (epic #88, tracked privately)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Branch `fix/message-id-length` from `origin/main` (`9e14b71`). Impact and history: see the private advisory.

## Owner decisions (2026-11-20, quoted)

- **"Private fix: length limit"**: "Unpack refuses a message whose id, thid or pthid is longer than a fixed limit (no new setting). Private advisory and a separate small fix right after #138, before #100; #88 gains one terse line. A peer sending longer ids is refused (none known)."
- **"512 characters"**: "Same as the replay guard's existing id cap. Every id we know of fits. Worst case about 10 MB per store instead of ~20 GB."
- **"As suggested"**: "#138 goes ahead now, and the private fix closes A2 before 1.6.0 (no release in between). A3 rides in the same private fix, logging only a bounded form of the id."

Everything below marked **(ours)** is our implementation choice, not the owner's.

## Spec (DIDComm v2.1 snapshot `c78bcc4`, `docs/spec-files-v2.1-snapshot/threading.md`)

- L9: "The value of `id` is a short (&lt;=32 bytes) string consisting entirely of [unreserved URI characters] ... use of [UUIDs (RFC 4122)] is recommended. ... it SHOULD be written in lower case but MUST be compared case-insensitively."
- L15: "The value of `thid` MUST conform to the same constraints as the value of `id`."
- L21: "The value of the child's `pthid` header MUST obey the same constraints as `thid` and `id` values."

Anything of at most 32 bytes stays accepted. The owner accepted 33–512 characters, because a UUID is 36 characters as written. So everything the limit refuses is already invalid under the spec. No stop.

`problems.md` L99 (for the A3 log helper only): "Problem codes are lower kebab-case. They are structured as a sequence of tokens delimited by the dot character `.`".

## Design calls (ours, with reasons)

1. **Where the check lives: `Message.Validate()`, before every other per-field check** (after the `id` required check). Every path already goes through it: unpack (`EnvelopeReader`, right after the plaintext is deserialized and before consistency checks, the snapshot, the replay guard, the dispatcher or any store), pack (`EnvelopeWriter`), `MessageBuilder.Build()`, and out of band. So we never send an id our own receivers would refuse (L-074). The length goes first because the character checks quote the value (that is open issue #116, left there), so an over-long value is never copied into an exception text.
2. **The constant**: `IdentifierLimits.MaxMessageIdLength = 512` (internal). `ReplayProtectionOptions.MaxMessageIdLength`'s default now refers to it (the value is the same, so the API doesn't change), and so does `PickupSizing.MaxThreadIdLength` (same value). Lengths are counted in UTF-16 code units, as the `to`/`from` bounds already are. The unreserved set is ASCII, so for an accepted id characters and bytes are the same.
3. **`ThreadIds.ResponseThreadId` also refuses a declared `thid` over 512**, with the same text as today. This keeps #138's rule ("a declared invalid `thid` is refused, not ignored") whole for hand-built requests.
4. **`please_ack` / `ack` entries get no limit.** No store or log keeps them (`AckLoopGuard` checks only whether they are present), and the owner's decision names `id`, `thid` and `pthid` only.
5. **No check inside the stores.** Adding a throw to the public `IThreadStateStore` / `CascadeBudgetStore` methods would change public behaviour. Their docs state the bound for keys that came through unpack.
6. **A3**:
   - The three `ProblemReportHandler` lines write `LogValues.Id(pthid)`. The code is written through a new internal `LogValues.ProblemCode` (as received when it is at most 128 characters of lower kebab-case and dots, else a placeholder).
   - Each line is rate-limited with `RateLimitedLog` (first and every 1000th at its own level, the rest at Debug), the repo's rule for a line any stranger can cause once per message.
7. **The ping-response size (A2)** needs no change of its own. Its only request-controlled field is `thid`, now at most 512.

## Sweep (src/, wire strings a stranger controls)

Verdicts: **(a)** closed by the 512 limit; **(b)** already bounded; **(c)** folded into this fix; **(d)** left to an open issue.

| # | Where | Key / field / line | Bound before | Stranger reaches it | Verdict |
|---|---|---|---|---|---|
| 1 | `Threading/InMemoryThreadStateStore` (filled at `ProtocolDispatcher` for pure ACKs and before a handler) | `thid ?? id` | 10,000 entries, no key length | yes | (a), test |
| 2 | `ProblemReport/CascadeBudgetStore` (filled by `ProblemReportHandler`) | `pthid` | 10,000 entries, no key length | yes | (a), test |
| 3 | `ProblemReportHandler` Information ×2, Warning ×1 | raw `pthid`, raw `code` | none; not rate-limited | yes | (c), A3 |
| 4 | `ProblemReportHandler` cascade stop | echoes `pthid` | none | yes | (a) |
| 5 | `TrustPing.CreateResponse`, `DiscoverFeaturesHandler`, the mediation handlers via `ResponseThreadId` | echoes `thid` | none | Trust Ping / Discover Features yes | (a), covers A2 |
| 6 | `MessagePickupHandler.ThreadOf` | echoes `thid` | 512 (`PickupSizing`) | authcrypt | (b), now (a) too |
| 7 | AspNetCore return-route push queue | packed replies | 16 frames | authenticated session | (a) for frame size (A2) |
| 8 | `InMemoryReplayGuard` | sender subject + id | id ≤ `MaxMessageIdLength` (512), DID ≤ 2048 | in-scope messages | (b); unpack now refuses > 512 first |
| 9 | `InMemoryReturnRouteSessionRegistry`, `IReturnRouteSession.Thread` | peer DID, `return_route` thread | 4096 sessions / 8 per peer; thread ≤ 2048 (FR-RR-01) | authcrypt | (b); a thread of 513–2048 now matches no `thid` and is inert. The public 2048 const stays. |
| 10 | `ObserverDelivery` | snapshots | 64 per observer | yes | (b) |
| 11 | `ForwardHandler` / `HeldForward` | held thread id, charged | forward byte budget | yes | (b); now ≤ 512. The comment saying no bound caps it is corrected. |
| 12 | `DiscoverFeaturesClient` pending queries | our own query ids | ours | — | (b) |
| 13 | `DiscoverFeaturesClient.LogRejected` | raw DIDs and kid | rate-limited | needs a pending query | (d) #115 |
| 14 | `MediatorClientChannel` | our own request ids | ≤ 64 | — | (b) |
| 15 | `DuplicateFilter` | 128-bit hashes | capacity | after unpack | (b) |
| 16 | `LiveDeliveryStream`, `DeliveryReader` | attachment ids from our mediator | ≤ 2048 | no | (b) |
| 17 | `NegotiatedVersionCache` | mediator DIDs we configure | ours | no | (b) |
| 18 | `InMemoryMessageStore` | grant DIDs; ids the mediator mints | MaxStore*/MaxQueue* | authcrypt grant | (b). A forward's inner message is opaque and never keyed. |
| 19 | `InMemoryMediationStore`, `PushDispatcher`, `PickupSessionTracker` | DIDs, owner tokens | FR-MED-08 bounds | authcrypt | (b) |
| 20 | `InMemoryForwardRecipientRegistry` | registered DIDs; `next` lookup | registration bounds; `next` ≤ 4096 | lookup only | (b) |
| 21 | `IOobInvitationStore`, `MediatorInvitationStore` | `_oobid` | written by the local sender only | lookup only | (b); the invitation id as `pthid` is now ≤ 512 |
| 22 | `ProtocolHandlerRegistry`, `TraceOptions` | `type` lookup; allowlisted trace URI | config | — | (b) |
| 23 | `ProtocolDispatcher` and `InboundAdmission` log lines | `LogValues.Id` / exception type names | 128 chars; rate-limited | yes | (b) |
| 24 | `Extensions.DependencyInjection/InboundDispatchConsumer` (4 lines) | **raw** message id | rate-limited | authenticated session peer | (c): `LogValues.Id` |
| 25 | `AspNetCore/StompServerSession` unsupported-command Warning | **raw** STOMP command (≤ 32 bytes; ESC possible) | one per connection | yes (opt-in STOMP) | (c): drop the value from the line |
| 26 | `WebSocketDidCommTransport` | our endpoints and DIDs | ours | no | (b); pushed frames go through unpack, so (a) |
| 27 | `DidCommClient` pack-time log | our own id | ≤ 512 now | no | (b) |
| 28 | `please_ack`/`ack`, `accept-lang`, attachment ids, problem-report `comment`/`args`, Discover Features queries, DIDs/kids | — | not stored or logged; transient, or 2048/4096 | yes | (b) |
| 29 | Exception texts that quote wire values (`Message.Validate` character checks, `ProblemCode.Parse`, ...) | — | — | — | (d) #116 |

Nothing needs a new setting, a public API change or a limit the owner has not chosen.

## Stores' bound (estimate; measured in QA)

With 512-character keys and the default 10,000 entries:
- the thread store holds about 1.16 KB per entry, about 11–12 MB in all, plus a small overshoot while an eviction pass runs;
- the cascade budget holds about 1.1 KB per entry, about 11 MB in all.

## Items

- [x] Advisory drafted (private; id kept out of public text)
- [x] Spec quoted; architect pass reconciled; sweep table
- [ ] `IdentifierLimits.MaxMessageIdLength`; `Message.Validate` length checks first; `ThreadIds.ResponseThreadId`; `ReplayProtectionOptions` / `PickupSizing` refer to the constant
- [ ] A3: `LogValues.ProblemCode`; `ProblemReportHandler` lines bounded and rate-limited
- [ ] Sweep folds: `InboundDispatchConsumer` ids via `LogValues.Id`; STOMP command not written; `ForwardHandler`/`HeldForward` comment
- [ ] Regression tests (unit, facade, end to end); fix existing tests that used ids over 512
- [ ] QA pass (permutations, memory measured, mutations, interop vectors)
- [ ] Adversarial pass (~20 min)
- [ ] Docs: CHANGELOG (leads with the behaviour change), PRD FR-MSG-02/04/11, FR-API-09, FR-RR-01/08, FR-PROTO-10, architecture doc, SECURITY.md, XML docs with the measured bound
- [ ] Build/test/pack gate; package validation negative control; DX gate (no new public members)
- [ ] Live interop run (normal)
- [ ] PR; CI green

## Review

(filled in at hand-back and after each review round)
