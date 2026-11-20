# Core hardening: bound message and thread id lengths (epic #88, tracked privately)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Branch `fix/message-id-length` from `origin/main` (`9e14b71`). Impact and history: see the private advisory.

## Owner decisions (2026-11-20, quoted; the impact figures they carried are in the private advisory)

- **"Private fix: length limit"**: "Unpack refuses a message whose id, thid or pthid is longer than a fixed limit (no new setting). Private advisory and a separate small fix right after #138, before #100; #88 gains one terse line. A peer sending longer ids is refused (none known)."
- **"512 characters"**: "Same as the replay guard's existing id cap. Every id we know of fits. Worst case about 10 MB per store […]."
- **"As suggested"**: "#138 goes ahead now, and the private fix closes [the ping-response size item] before 1.6.0 (no release in between). [The problem-report log item] rides in the same private fix, logging only a bounded form of the id."

Everything below marked **(ours)** is our implementation choice, not the owner's.

## Spec (DIDComm v2.1 snapshot `c78bcc4`, `docs/spec-files-v2.1-snapshot/threading.md`)

- L9: "The value of `id` is a short (&lt;=32 bytes) string consisting entirely of [unreserved URI characters] ... use of [UUIDs (RFC 4122)] is recommended. ... it SHOULD be written in lower case but MUST be compared case-insensitively."
- L15: "The value of `thid` MUST conform to the same constraints as the value of `id`."
- L21: "The value of the child's `pthid` header MUST obey the same constraints as `thid` and `id` values."

Anything of at most 32 bytes stays accepted. The owner accepted 33–512 characters, because a UUID is 36 characters as written. So everything the limit refuses is already invalid under the spec. No stop.

`problems.md` L99 (for the problem-report log helper only): "Problem codes are lower kebab-case. They are structured as a sequence of tokens delimited by the dot character `.`".

## Design calls (ours, with reasons)

1. **Where the check lives: `Message.Validate()`, before every other per-field check** (after the `id` required check). Every path already goes through it: unpack (`EnvelopeReader`, right after the plaintext is deserialized and before consistency checks, the snapshot, the replay guard, the dispatcher or any store), pack (`EnvelopeWriter`), `MessageBuilder.Build()`, and out of band. So we never send an id our own receivers would refuse (L-074). The length goes first because the character checks quote the value (that is open issue #116, left there), so an over-long value is never copied into an exception text.
2. **The constant**: `IdentifierLimits.MaxMessageIdLength = 512` (internal). `ReplayProtectionOptions.MaxMessageIdLength`'s default now refers to it (the value is the same, so the API doesn't change), and so does `PickupSizing.MaxThreadIdLength` (same value). Lengths are counted in UTF-16 code units, as the `to`/`from` bounds already are. The unreserved set is ASCII, so for an accepted id characters and bytes are the same.
3. **`ThreadIds.ResponseThreadId` also refuses a declared `thid` over 512**, with the same text as today. This keeps #138's rule ("a declared invalid `thid` is refused, not ignored") whole for hand-built requests.
4. **`please_ack` / `ack` entries get no limit.** No store or log keeps them (`AckLoopGuard` checks only whether they are present), and the owner's decision names `id`, `thid` and `pthid` only.
5. **No check inside the stores.** Adding a throw to the public `IThreadStateStore` / `CascadeBudgetStore` methods would change public behaviour. Their docs state the bound for keys that came through unpack.
6. **Problem-report log lines**:
   - The three `ProblemReportHandler` lines write `LogValues.Id(pthid)`. The code is written through a new internal `LogValues.ProblemCode` (as received when it is at most 128 characters of lower kebab-case and dots, else a placeholder).
   - Each line is rate-limited with `RateLimitedLog` (first and every 1000th at its own level, the rest at Debug), the repo's rule for a line any stranger can cause once per message.
7. **The ping-response size** needs no change of its own. Its only request-controlled field is `thid`, now at most 512.

## Sweep (src/, wire strings a stranger controls)

Verdicts: **(a)** closed by the 512 limit; **(b)** already bounded; **(c)** folded into this fix; **(d)** left to an open issue.

| # | Where | Key / field / line | Bound before | Stranger reaches it | Verdict |
|---|---|---|---|---|---|
| 1 | `Threading/InMemoryThreadStateStore` (filled at `ProtocolDispatcher` for pure ACKs and before a handler) | `thid ?? id` | 10,000 entries, no key length | yes | (a), test |
| 2 | `ProblemReport/CascadeBudgetStore` (filled by `ProblemReportHandler`) | `pthid` | 10,000 entries, no key length | yes | (a), test |
| 3 | `ProblemReportHandler` Information ×2, Warning ×1 | raw `pthid`, raw `code` | none; not rate-limited | yes | (c), see call 6 |
| 4 | `ProblemReportHandler` cascade stop | echoes `pthid` | none | yes | (a) |
| 5 | `TrustPing.CreateResponse`, `DiscoverFeaturesHandler`, the mediation handlers via `ResponseThreadId` | echoes `thid` | none | Trust Ping / Discover Features yes | (a), covers the ping-response size |
| 6 | `MessagePickupHandler.ThreadOf` | echoes `thid` | 512 (`PickupSizing`) | authcrypt | (b), now (a) too |
| 7 | AspNetCore return-route push queue | packed replies | 16 frames | authenticated session | (a) for frame size |
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
| 27 | `DidCommClient` pack-signed "no `to`" warning | the caller's own id, written before `Validate` runs | none (QA found it) | no | (c): `LogValues.Id` |
| 27a | `ObserverDelivery` drop warning | raw message id (≤ 512 after unpack; unbounded from a host-built `UnpackResult`) | rate-limited | yes | (c): `LogValues.Id` (adversarial pass, Low) |
| 28 | `please_ack`/`ack`, `accept-lang`, attachment ids, problem-report `comment`/`args`, Discover Features queries, DIDs/kids | — | not stored or logged; transient, or 2048/4096 | yes | (b) |
| 29 | Exception texts that quote wire values (`Message.Validate` character checks, `ProblemCode.Parse`, ...) | — | — | — | (d) #116 |

Nothing needs a new setting, a public API change or a limit the owner has not chosen.

## Stores' bound (measured)

With 512-character keys and the default 10,000 entries, measured as heap size minus fragmentation after full compacting GCs (`GC.GetTotalMemory` reads about 2x under .NET 10's region GC, so it is not used):
- the thread store holds 11.8 MB (1,180 B per entry), plus about 1.2 KB per insert that completes during an eviction pass. An end-to-end flood of 12,000 pings, measured by QA with the Interop flood test run alone, left 9,977 entries and 11.7 MB. In the full Interop run the same heap difference read 13.2–13.9 MB, because earlier tests' memory is still being released while it runs (consecutive readings with nothing changed fell 1–3 MB each, and 28 MB over 3 s). So the end-to-end test now checks the store's contents (filled to its cap, every key one of the flood's 512-character ids). On PR CI (run 44805245728, ubuntu) the Core store probe then read 3.16 MB for 9,990 entries, less than their keys alone: memory freed elsewhere in the process during the measurement. As with the forward retention probe (#98), the heap probe is now opt-in (`DIDCOMM_ADV98_PROBES=1`). The deterministic tests are the regression (an id over 512 never reaches a store; the flood holds only 512-character keys). Locally, with the switch: 11.79 MB and 11.29 MB, unchanged;
- the cascade budget holds 11.3 MB (1,129 B per entry);
- the negative control, 2048-character keys set directly (no unpack), holds 42 MB.

## Items

- [x] Advisory drafted (private; id kept out of public text)
- [x] Spec quoted; architect pass reconciled; sweep table
- [x] `IdentifierLimits.MaxMessageIdLength`; `Message.Validate` length checks first; `ThreadIds.ResponseThreadId`; `ReplayProtectionOptions` / `PickupSizing` refer to the constant (`7b0cb76`)
- [x] Problem-report log lines: `LogValues.ProblemCode`; `ProblemReportHandler` lines bounded and rate-limited (`7b0cb76`)
- [x] Sweep folds: `InboundDispatchConsumer` ids via `LogValues.Id`; STOMP command not written; `HeldForward` comment (`7b0cb76`)
- [x] Regression tests (unit, facade, end to end); existing tests that used ids over 512 now use 512, or set the long value after unpack where they test a handler's own bound (`7b0cb76`)
- [x] QA pass: no product defect. Tests in `91f8880`, `996b53b`. One low doubt fixed: the pack-signed warning's id (`9377736`)
- [x] Adversarial pass (~24 min): no way around the limit. One Low fixed: the observer-drop id (`58cc68d`); the rest overlap #115 / #116 (see Review)
- [x] Docs: CHANGELOG (leads with the behaviour change), PRD FR-MSG-02/11, FR-API-09, FR-RR-01/08, FR-PICK-03, FR-PROTO-10, architecture doc, SECURITY.md, XML docs with the measured bound
- [x] Build/test/pack gate (Core 2875 + 4 skipped, Mediator 4725 + 8, Interop 2112 + 5); package validation passes vs 1.5.0, negative control CP0002; DX gate (no new public members, no allowlist change)
- [x] Live interop run (normal): 44803851719 on `2394f73`, green; python and jvm tables byte-identical to nightly 44730996966; mediator leg 10/10
- [x] PR #142; CI 44806474087 green on `e3038ff` (44805245728 on `2394f73` red on ubuntu in the store heap probe, diagnosed; the probe is now opt-in)

## Review

### QA (sub-agent, branch `qa/message-id-length`, merged fast-forward)

- 46 end-to-end tests (`MessageIdLengthEndpointTests`): each field × 4 envelopes × HTTP and WebSocket at 512 and 513; refusal before the thread store (spy), the cascade budget, the replay guard and the `RecordingHandler`; L-055 poison (512 valid characters + `/`) refused for its length in all 4 envelopes.
- Flood (`MessageIdLengthFloodTests`): 12,000 pings at 512 → 9,977 entries, 11.7 MB (run alone); at 513 → 0 entries. At the gate, its heap measurement failed in the full Interop run (13.2 MB against 12.98 MB), from other tests' memory being freed during the measurement, not from the store; diagnosed with repeated readings and changed to a contents check (see "Stores' bound").
- Ping-response size: 1,648 B on a UUID thread, 2,288 B on a 512-character thread, socket and HTTP alike; at 513 nothing is pushed.
- Real console (`ProblemReportRealConsoleTests`, JSON and plain console): no marker, injected line or ESC reaches the console.
- Pack paths and Out-of-Band (`MessageIdLengthPackPathTests`).
- Mutations M0–M10 (the fix removed whole; each field's check; order; `ResponseThreadId`; each log line raw; rate limit off; consumer id; STOMP command; off by one): each red, then green after revert and rebuild. Tests that cannot fail under any mutation, by design: the three store-retention tests (they set keys directly; their negative control shows sensitivity) and three controls.
- Low doubt, fixed in `9377736`: `DidCommClient.PackSignedAsync`'s "no `to`" warning wrote the caller's raw id before `Validate`. Now through `LogValues.Id`, with a mutation-proven test.

### Adversarial (sub-agent, ~24 min, probes not merged)

| Severity | Finding | Decision |
|---|---|---|
| Low | Observer-drop warning wrote the inbound id raw (`ObserverDelivery`) | Fixed (`58cc68d`), mutation-proven test |
| Info | `please_ack` / `ack` entries and attachment ids have no length bound, and their character-check errors quote them | Left to open issue #116 (exception texts quoting wire values). Nothing stores them. A 512 limit on them is not part of the owner's decision |
| Info | `DiscoverFeaturesClient.LogRejected` writes the received id raw (now ≤ 512) | Left to open issue #115 |
| Info | The WebSocket endpoint's per-frame unpack-rejection Warning is not rate-limited | Pre-existing and documented as one line per message (architecture doc); not changed |

Held: JSON escapes in values and member names; near-miss names (`THID`, `~thread`) do not bind and nothing reads them as thread ids; duplicate keys refused; surrogate pairs; nothing touches the id between deserialization and `Validate`; every receive path goes through `UnpackAsync` → `Validate`; other wire-keyed stores bounded; no new timing or log oracle. A host-built `UnpackResult` passed to `ProtocolDispatcher.DispatchAsync` skips `Validate` (host code; the store docs say so).

### Seen locally, not this change

`ObserverDeliveryTests.Enqueue_flush_and_shutdown_races_finish_without_leaking_reservations` failed 4 of 15 local runs (`InvalidOperationException`, "flush barrier could not be admitted"). That is open issue #136, unchanged by this PR.
