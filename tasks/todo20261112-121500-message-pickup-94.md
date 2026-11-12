# todo20261112-121500 — `DidComm.Mediator`: Message Pickup 3.0, mediator role (#94)

Issue: #94 (epic #88; builds on #89 PR #101, #90 PR #102, #91 PR #103, #92 PR #104, #93 PR #106 merge `0d64ec9`). Branch: `feat/message-pickup-94` (L-019: re-verify before every commit).
Normative source: https://didcomm.org/messagepickup/3.0/ (Production). PRD rows (the target): §8c FR-PICK-01..10 (FR-PICK-11 is #96's, FR-PICK-12 is #100's). D1–D7 unchanged.
Binding refinements: the "#94" paragraphs of the five "Planning update" comments on #88 override the issue body where they differ. In short: a request without an authcrypt layer is neither applied nor answered; `limit: 0` → `status`, negative / non-integer → malformed; `max_delivery_bytes` byte-budgeted batches with post-pack verification and an admission bound at enqueue; the store budget counts queued + leased + live-unacknowledged and a live push is "admit to the store, then push under a lease" with no second copy; HTTP leases end by timeout only, WebSocket leases also when the session closes; `max_ack_ids` bound; live mode resets on the **session's `Closed` token** (not the registry's best-effort notifications); live pushes go through `registry.Find(peer)`; `TrySendAsync` true = queued, not delivered; a `recipient_did` is authorised through `IMediationStore.FindGrantByRecipientDidAsync` with `grant.PeerDid` = the authenticated peer; quotas are keyed by `MediationGrant.PeerDid`; handlers neither stamp `created_time` nor re-check replay, and never await another inbound message on the same session; L-054 / L-055.
Owner decision relayed by the coordinator on 2026-11-12 (decided, not open): queued messages of a removed DID / revoked grant — registration is checked at delivery time (nothing is delivered to a DID that no longer authorises the peer) and the queue store has purge operations a host and the #95 pipeline call; no change notification on `IMediationStore`.
The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`.

## Files this change touches

- NEW `src/DidComm.Mediator/Pickup/`: `MessagePickup.cs` (constants, builders, strict parsers), `PickupRecords.cs` (`PickupStatus`, `DeliveredMessage`), `PickupBodies.cs` (internal, version-neutral codecs), `IMessageStore.cs` (+ `QueuedMessage`, `MessageLease`, `MessageLeaseRequest`, `QueueStatus`, `EnqueueOutcome`), `InMemoryMessageStore.cs`, `MessagePickupOptions.cs`, `PickupSizing.cs` (internal), `PickupSessionTracker.cs` (internal), `PickupCoordinator.cs` (internal, version-neutral role logic), `MessagePickupQueue.cs` (public facade for the pipeline and the host, + `PickupDeliveryOptions` / `PickupDeliveryResult` / `PickupDeliveryOutcome`), `MessagePickupHandler.cs`, `MessagePickupBuilderExtensions.cs`.
- NEW `src/DidComm.Mediator/Internal/`: `MediatorPeer.cs`, `MediatorLog.cs`, `WireJson.cs` — the peer gate, the safe log / `LogId` / cancellation helper and the JSON readers lifted out of #93's `MediationCoordinator` / `MediationBodies` / `CoordinateMediation`, which now call them (no behaviour change; the #93 tests are the proof).
- EDIT (Core, AspNetCore — the one change outside the package): `IReturnRouteSession.TrySendAsync(Message, int maxPackedBytes, CancellationToken)` returning the new `ReturnRouteSendResult { NotSent, Sent, TooLarge }`, implemented by `HttpReturnRouteSession` and `WebSocketReturnRouteSession`; four test fakes; four `<see cref>` disambiguations. The interface is unreleased (introduced after `v1.5.1`), so the member is required, not a default implementation. Reason: JC-2.
- NEW tests: `tests/DidComm.Mediator.Tests/Pickup/*`, `tests/DidComm.Mediator.Tests/fixtures/messagepickup-3.0/*.json`, `tests/DidComm.InteropTests/AspNetCore/MessagePickupHttpTests.cs`, `MessagePickupWebSocketTests.cs`, `ReturnRouteSessionTests.BoundedSend.cs`.
- EDIT both DX allowlists (`TODO(#97)` entries), `src/DidComm.Mediator/DidComm.Mediator.csproj` (description).
- Docs: `CHANGELOG.md`, `docs/codebase-architecture.md`, `docs/didcomm-dotnet_PRD.md` (rows listed in H), this file.
- NOT touched: `ForwardHandler`, `ProtocolDispatcher`, transports, samples (sample 11 is #97's), the recipient role (#96), the forward → queue wiring (#95), Pickup 4.0 (#100).

## Public surface (`DidComm.Mediator.Pickup`; L-006: every referenced type is public; L-035: every optional parameter is there from the start)

- `MessagePickup` (static): `ProtocolUri`, six MTURI constants, `MediatorRole` / `RecipientRole`, `LiveModeNotSupportedCode`, `LiveModeNotSupportedComment`; `CreateStatusRequest(from, mediator, recipientDid?, idGenerator?)`, `CreateStatus(from, to, PickupStatus, thid?, idGenerator?)`, `CreateDeliveryRequest(from, mediator, limit, recipientDid?, idGenerator?)`, `CreateDelivery(from, to, IReadOnlyList<DeliveredMessage>, thid?, recipientDid?, idGenerator?)`, `CreateMessagesReceived(from, mediator, messageIds, idGenerator?)`, `CreateLiveDeliveryChange(from, mediator, liveDelivery, idGenerator?)`, `CreateLiveModeNotSupported(from, to, pthid)`, `ResponseThreadId(request)`; `TryParseStatusRequest`, `TryParseStatus`, `TryParseDeliveryRequest`, `TryParseDelivery`, `TryParseMessagesReceived`, `TryParseLiveDeliveryChange`. Parsers return `false` for another type and throw a value-free `MalformedMessageException` for the right type with a bad shape; types match as the registry matches them.
- `PickupStatus(MessageCount, RecipientDid?, LongestWaitedSeconds?, NewestReceivedTime?, OldestReceivedTime?, TotalBytes?, LiveDelivery?)`, `DeliveredMessage(Id, Payload)`.
- `IMessageStore`: `EnqueueAsync(message, lease?, now)` → `EnqueueOutcome`; `LeaseAsync(MessageLeaseRequest)`; `AcknowledgeAsync(grantPeerDid, ids)`; `ReleaseAsync(grantPeerDid, ids)`; `ReleaseLeasesAsync(leaseOwner)`; `GetStatusAsync(grantPeerDid, recipientDid?, now)`; `PurgeRecipientAsync(grantPeerDid, recipientDid)`; `PurgeGrantAsync(grantPeerDid)`; `PurgeExpiredAsync(now)`. Records `QueuedMessage(Id, GrantPeerDid, RecipientDid, Payload, ReceivedAt, NotBefore?, ExpiresAt?)`, `MessageLease(Owner?, ExpiresAt)`, `MessageLeaseRequest(GrantPeerDid, RecipientDid?, MaxMessages, MaxPayloadBytes, Now, Lease)`, `QueueStatus(MessageCount, TotalBytes, OldestReceivedAt?, NewestReceivedAt?)`; enum `EnqueueOutcome { Enqueued, GrantMessageLimitReached, GrantByteLimitReached, StoreLimitReached, DuplicateId }`. `InMemoryMessageStore` (+ `MessageCount`, `TotalBytes`).
- `MessagePickupOptions`: `MaxDeliveryBatch` (10), `MaxDeliveryBytes` (null = `DidCommOptions.MaxReceiveBytes`), `MaxAckIds` (null = batch × 10), `LeaseTimeout` (60 s), `MaxQueueMessages` (1000 per grant), `MaxQueueBytes` (32 MiB per grant), `MaxStoreMessages` (100 000), `MaxStoreBytes` (1 GiB), `Validate()`.
- `MessagePickupQueue` (singleton; what #95's sink and a host call): `MaxAdmissiblePayloadBytes`, `DeliverAsync(recipientDid, payload, PickupDeliveryOptions?, ct)` → `PickupDeliveryResult(Outcome, MessageId?, GrantPeerDid?)` with `PickupDeliveryOutcome { PushedLive, Queued, NotRegistered, Expired, PayloadTooLarge, QueueFull, Faulted }`, `RemoveAsync(grantPeerDid, messageId, ct)`, `PurgeExpiredAsync(ct)`. `PickupDeliveryOptions(NotBefore?, ExpiresTime?, AllowLive = true)`.
- `MessagePickupHandler : IProtocolHandler, IProtocolHandlerRoles`; `MessagePickupBuilderExtensions.AddMessagePickup(this DidCommBuilder, Action<MessagePickupOptions>? = null)`.
- Core: `ReturnRouteSendResult`, `IReturnRouteSession.TrySendAsync(Message, int, CancellationToken)`.
- Internal, for #100: `PickupBodies` (codecs parameterised by member names and time unit), `PickupCoordinator` (the role without the wire format: authorisation, status, lease → verify → push, acknowledgement, live flag, the pipeline's deliver), `PickupSessionTracker`, `PickupSizing`. A 4.0 handler is a second thin class over the same `MessagePickupQueue` singleton, so it shares store, leases, live flags and counters.

## Threat model (L-031)

- **Who talks to it.** Anyone who can reach the endpoint; only an authcrypt sender is a peer (`MediatorPeer.TryGet` over `ReturnRouteEligibility.TryGetPeer`, the rule that opens a session, L-041), never the plaintext `from`. Unauthenticated shapes are dropped before the type or body is read: nothing applied, no reply, one rate-limited line with the id. Senders of forwards reach the queue through #95's sink without authentication. Host code calls `MessagePickupQueue` and implements `IMessageStore`.
- **What a peer can do.** Count, lease, acknowledge and toggle live mode under its own authenticated DID only: every store operation is keyed by the peer's DID, and a message id means something only under that key, so another grant's ids are plain misses (never reported, never echoed, same work). Pinning its own queue under leases, or publishing a DID document so large that its envelopes exceed the budget, harms only itself.
- **`recipient_did`.** Authorised by `FindGrantByRecipientDidAsync` + `grant.PeerDid == peer`, computed without a data-dependent branch. "Not registered", "registered under another grant" and "not a bare DID" make the same store calls and get the same answer (`status` echoing the value with `message_count: 0`): no registration oracle here, a smaller surface than #93's accepted `client_error`. Over 2048 characters is malformed before anything parses it (FR-API-09).
- **Registration at delivery time (owner decision).** A filtered request is authorised before the store is touched. In an unfiltered batch every distinct recipient DID of the leased records is checked again; records of a DID that no longer authorises the peer are not delivered and are purged (they can never be delivered, and would otherwise block the head of the queue). If the check cannot be made, the leases are released and nothing is delivered (L-054). Leftovers of a revoked grant are never delivered (no grant → empty `status`); removing them is the host's `PurgeGrantAsync`.
- **Floods.** A sender can fill one recipient's queue to the per-grant budget and no further; all grants together are bounded by the store-wide budget (open enrolment can exhaust it with self-made grants — a stated availability limit, like W-2). Queued, leased, live-unacknowledged and not-yet-due payloads all count; nothing is ever copied on release. Enqueue outcomes never reach the forward's sender (#95 answers 202 first).
- **Work per message, each bound applied before the work (L-055).** `limit` is clamped and the byte budget applied inside the lease call; `message_id_list` is refused from its length before any entry is read; a payload over the admissible size is refused before any lookup; one mediation-store read per request plus at most `MaxDeliveryBatch` for the delivery-time check.
- **Faults (L-054).** Mediation store, message store (each method), id generator, session registry `Find`, session send and logger each fail closed on every path that reads them: no reply, nothing half-applied, leases taken are released. When the dispatch token is cancelled only `OperationCanceledException` leaves. What a store returns is copied and validated inside the boundary (a leased record must carry the caller's grant key).
- **Logs and errors.** Message id through `LogId` (128-character cap), exception type names, enum names, counts. No DID, `recipient_did`, attachment id or thread id at any level. `MalformedMessageException` text names the member and the rule only.

## Judgement calls (architect design reconciled with mine; each has a reason)

- JC-1. **Outcomes are enums, not `QueueFullException`** (the issue's draft). A full queue is an expected state any sender can cause; with enums a throw means "store fault" and nothing else (#93's `RecipientStoreOutcome` precedent).
- JC-2. **The byte budget is verified by the session, on the bytes it hands to the transport.** The handler holds plaintext; `SessionOutbound` packs. Options weighed: a trial pack in the handler (double encryption, two key resolutions, duplicated pack options, no cover for a pipeline push), an analytic size (couples to the JWE serialisation), a cached per-pair overhead (wrong exactly when a DID document changes). Chosen: `IReturnRouteSession.TrySendAsync(message, maxPackedBytes, ct)` → `Sent | NotSent | TooLarge`. The batch is chosen by arithmetic (`PickupSizing`), the `delivery` is pushed through the bounded send, and on `TooLarge` the last attachment's lease is released and the message re-sent until it fits (the PRD's trim-and-repack). The handler returns `null` after pushing a `delivery`: over HTTP the pushed message is the 200 body.
- JC-3. **Sizing.** Constants: envelope allowance 32 KiB (protected header, `recipients[]`, iv, tag; measured ≈ 1.1 KiB for one X25519 key), `delivery` skeleton allowance 8 KiB (three DIDs at 2048 + a 512-character thread id + fixed text), 96 bytes per attachment (descriptor + a 64-character id + rounding). `BatchPayloadBudget` = ¾ × (largest plaintext whose base64url ciphertext fits − skeleton − batch × 96); 564 527 bytes (≈ 551 KiB) at the 1 MiB default. The same number is the admission bound (`MaxAdmissiblePayloadBytes` = min(`MaxReceiveBytes`, budget)) and the per-lease byte budget, so an admitted payload always fits alone whatever `limit` is. PRD said "≈ 576 KiB"; the row now gives the computed number. A budget below 1 fails `Validate()` / construction.
- JC-4. **Store key = `(grantPeerDid, id)`; `recipient_did` is a filter inside the grant.** The issue's `PeekAsync(recipientDid, …)` / `AckAsync(recipientDid, ids)` carry no grant key and "peek" that mutates is misnamed → `LeaseAsync` / `AcknowledgeAsync(grantPeerDid, ids)`.
- JC-5. **A lease is state on the stored record** (owner token + expiry instant). It ends by acknowledgement, release (by ids or by owner) or lazily by timeout; no timers, nothing re-inserted, position kept. The owner token is an opaque string minted per live session (never on the wire); HTTP leases have none.
- JC-6. **A live push is one store step**: `EnqueueAsync(message, lease)` inserts the record already leased to the target session, so no `delivery-request` can take it in between.
- JC-7. **`status` counts** records that are due and unexpired, leased ones included (FR-PICK-04: the count drops on acknowledgement). Not-yet-due records are retained and count against the budget but are not reported until due (a client looping on the count would otherwise spin). Members emitted: `message_count`, `total_bytes`, `live_delivery` always; `recipient_did` when the request had one; the three time members when the count is above zero.
- JC-8. **Thread ids (DD-11).** `status`, `delivery` and the problem report's `pthid` use the request's `thid` if present, else its `id`; a live `delivery` has no `thid`. Putting `thid` on a 3.0 `status` is an addition (the page shows none; 4.0 requires it; harmless and what #96 correlates on). A request thread id over 512 characters is malformed (bounds the echo; the replay guard's `MaxMessageIdLength` default).
- JC-9. **No session.** A `delivery-request` dispatched without a return-route session leases nothing and is answered `status` (the library endpoints log such a reply and do not send it; nothing could carry the batch and a lease would only lock the queue). `messages-received` is still applied. A session bound to another (peer, local) pair is treated as absent.
- JC-10. **Problem report only for `live_delivery: true`** on a connection that cannot do live delivery (HTTP, or no session) — the spec's wording; `false` there is answered `status{live_delivery: false}`.
- JC-11. **Attachment data is base64url without padding** on emit (DIDComm v2.1 attachments; what `ForwardProcessor` reads) and decoded leniently on receive (padding and the standard alphabet accepted). `TryParseDelivery` checks structure and decodes; it does not inspect the payload (the mediator relays bytes; per-attachment unpack failures are #96's).
- JC-12. **Ids** are minted by the coordinator through `IMessageIdGenerator` (default UUID v4), must be unreserved characters and at most 64 long, else the delivery is `Faulted`; the store reports `DuplicateId`. Wire ids (`message_id_list`, attachment ids on parse) are capped at 2048 (FR-API-09).
- JC-13. **Several live sessions for one peer**: one target, the newest session with the flag on. A lease has one owner; pushing to every session would duplicate.
- JC-14. **A not-yet-due payload is never pushed live**; it is queued and picked up once due.
- JC-15. **Store faults → no reply** (the #93 precedent; pickup has no error shape for them).
- JC-16. **Acknowledgement needs no grant lookup**: it is confined to the caller's own key. **Toggling live mode needs no grant**: it is per-session state bounded by the session registry, and pushes only ever follow a grant.
- JC-17. **`AddMessagePickup()` is order-independent** with respect to `AddCoordinateMediation()`; a missing `IMediationStore` fails when the queue is first built, with a message naming the fix.
- JC-18. **Lease selection is a FIFO prefix**: it stops at the first eligible record that would pass the byte budget (never skips to a smaller one), except that the first eligible record is always returned, so a record admitted under an older, larger budget still reaches the bounded send instead of blocking silently.
- JC-19. **A payload this peer's envelope can never carry** (its own DID document inflates `recipients[]` past the allowance) is released and answered `status`; it stays queued until it expires or is purged. Only that peer is affected. Stated in the docs.

## A. Core: bounded session send
- [x] A1. `ReturnRouteSendResult`, the interface member, both sessions, fakes, 32 tests, DX entries (independent engineer agent in a worktree; commit `4b4f803`; diff reviewed).

## B. Shared internals
- [ ] B1. `Internal/MediatorPeer.cs`, `MediatorLog.cs`, `WireJson.cs`; #93 classes forward to them; #93 tests unchanged and green.

## C. Messages
- [ ] C1. `MessagePickup` constants, records, builders.
- [ ] C2. `PickupBodies` codecs + strict parsers (FR-API-09 first; `limit` rules; `message_id_list` bound from the length before any entry is read).

## D. Store
- [ ] D1. `IMessageStore` + records + contract remarks.
- [ ] D2. `InMemoryMessageStore` (one lock; per-grant arrival list + id index + counters; owner index; lazy expiry).
- [ ] D3. `MessagePickupOptions` + `Validate()`; `PickupSizing`.

## E. Role logic, handler, DI
- [ ] E1. `PickupSessionTracker` (live flag + owner token per live session, reset on `Closed`).
- [ ] E2. `PickupCoordinator` (authorise, status, deliver-request, acknowledge, live change, pipeline deliver).
- [ ] E3. `MessagePickupQueue`, `MessagePickupHandler`, `AddMessagePickup()`.
- [ ] E4. DX allowlist entries (`TODO(#97)`), csproj description.

## F. Tests
- [ ] F1. Fixtures (six page examples + `status` with only `message_count`), builders, parser rejection tables.
- [ ] F2. Store matrix (FIFO, not-before, expiry, leases, release, budgets, isolation, purges, concurrency invariants).
- [ ] F3. Sizing (formula, boundaries, real packs stay under the estimate).
- [ ] F4. Handler decision table, unauthenticated matrix, faults, logs, poison beyond the bounds, forged-but-consistent identities.
- [ ] F5. Live mode (enable → push → ack; close → flag off and leases released; races).
- [ ] F6. DI / Discover Features; HTTP and WebSocket end to end over the TestServer (incl. 5 × 300 KiB under 1 MiB).

## G. QA pass and adversarial pass (independent agents; findings verified before fixing)
- [ ] G1. QA slices.
- [ ] G2. Adversarial slices.

## H. Docs
- [ ] H1. CHANGELOG `[Unreleased]`.
- [ ] H2. `docs/codebase-architecture.md` (new "Message Pickup 3.0, mediator role (Phase 7, #94)" section; return-route sections gain the bounded send).
- [ ] H3. PRD: FR-PICK-01..10 as built (JC-3, -7, -8, -9, -10, -11, -18, -19; the owner decision), FR-RR-04/05 (bounded send), FR-PICK-12 (what 4.0 still changes).
- [ ] H4. Review section below.

## Review

(to be written)
