# Full mediator service — GitHub issue set and plan of attack

Goal: open a set of GitHub issues that, taken together, specify everything didcomm-dotnet
needs to become a complete, spec-conformant DIDComm v2.1 **mediator service** (plus the
recipient-side client needed to use one), with enough detail that coding agents can execute
each issue independently.

## Plan (this task)

- [x] Research normative sources (spec routing/transports, return_route extension,
      coordinate-mediation 2.0/3.0, messagepickup 3.0/4.0, discover-features).
- [x] Audit the codebase for extension points (file:line) and gaps.
- [x] Identify ecosystem interop targets (Identus mediator, Blocktrust mediator, Affinidi).
- [x] Draft the epic + 10 issues below.
- [x] **User approved drafts as written** (2026-11-10), decisions D1–D7 unchanged.
- [x] Created label `mediator` (`#7B61FF`).
- [x] Opened #88 (epic) and #89–#100 in dependency order; cross-references and the
      epic checklist back-filled with real numbers and verified.
- [x] Review section below.

## Decisions baked into the drafts (confirm or override)

| # | Decision | Why |
|---|---|---|
| D1 | New package **`DidComm.Mediator`** holds coordinate-mediation, message-pickup, the delivery pipeline, and stores. Routing 2.0 fixes and the return-route extension land in the existing `DidComm.Core` / `DidComm.AspNetCore` / `DidComm.Transports.*` packages. | PRD §2.2 already says "ship as a separate package later". Routing 2.0 and transports are in the core spec, so they belong in Core. |
| D2 | Protocol versions: **coordinate-mediation 3.0** primary, **2.0** as a compatibility handler (separate issue). **messagepickup 3.0** first; **message-pickup 4.0** as a follow-up issue (it changes live-mode framing and the empty-queue response, and uses a different PIURI, so both can coexist). | 3.0/3.0 are the current didcomm.org versions. The two external mediators that exist today (Identus, Blocktrust) speak CM **2.0** + pickup **3.0**, so 2.0 is what live interop will actually exercise. |
| D3 | `return_route` semantics: HTTP → at most **one** message returned in the HTTP response body (200 + `application/didcomm-encrypted+json`), otherwise 202 empty; WebSocket → connection is a session, multiple pushes. `return_route` is honored **only** on envelopes whose sender is authenticated (authcrypt, FR-CONSIST-05), and the session is bound to that sender DID. | Extension text ("HTTP transports will only be able to receive at most one message at a time"); spec: "trust … MUST be associated with DIDComm encryption or signing, not from the socket connection itself." |
| D4 | A `forward` whose `next` is not a registered recipient is **dropped and logged**, never answered with a problem report, and the HTTP status is 202 either way, with constant-work lookup. | Routing says mediators send only destward; a report would be a registration oracle. Matches memory `[[feedback_no_early_exit_on_identity_checks]]`. |
| D5 | `recipient-update` has no proof-of-DID-ownership in the spec. Default policy: a `recipient_did` can be registered by **one** mediation grant only (first come, then `client_error` for others); a pluggable `IMediationPolicy` lets hosts require stronger proof. | Spec endnote acknowledges squatting; this is the least-surprising safe default. |
| D6 | Delivery modes for a routed message, in priority order: (1) live return-route session for the recipient, (2) queue for pickup, (3) optional direct push to a host-registered endpoint (what sample 04 does today). Modes 1–2 are the spec path; 3 stays as an explicit host option. | Keeps the sample's behavior available without making it the default. |
| D7 | PRD gets new areas **FR-MED-** (coordinate mediation), **FR-PICK-** (pickup), **FR-RR-** (return route), new rows FR-ROUTE-09..11, amendments to FR-TRN-03/10, a §2.2 rewrite, and a **Phase 7** plan. PRD issue goes first because the PRD is the target. | CLAUDE.md: "The PRD is the target." |

## Dependency graph / lanes

```
#89 (PRD)  ──────────────────────────────────────────────────────┐
#90 (routing core fixes) ───────────────┐                        │
#91 (return-route, server) ───┐       │                        │
#92 (return-route, client) ───┼──┐    │                        │
#93 (coord-mediation 3.0) ────┤  │    │   (needs #89 for FRs; handlers testable via dispatcher alone)
#94 (pickup 3.0) ─────────────┤  │    │
                               ▼  │    ▼
#95 (mediator package + pipeline) ◄── #90, #91, #93, #94
                                  ▼
#96 (recipient-side client) ◄── #92, #93, #94
                                  ▼
#97 (sample 11 + cookbook + docs) ◄── #95, #96
                                  ▼
#98 (interop + security gate) ◄── #97
#99 (coord-mediation 2.0 compat) ◄── #93   (parallel, low priority)
#100 (pickup 4.0) ◄── #94                  (parallel, low priority)
```

Parallel lanes once #89 is merged: **Lane A** #90 · **Lane B** #91 then #92 · **Lane C** #93 and
#94 (two agents) · then #95 · then #96 · then #97 · then #98. #99/#100 any time
after their parent.

Per-issue agent protocol (CLAUDE.md §2): Plan agent writes `tasks/todo<ts>-<slug>.md` and stops
for approval → Staff-engineer agent implements on `feat/<slug>` → QA agents (happy path +
permutations) → adversarial agents → CHANGELOG `[Unreleased]` entry → PRD/FR sync → PR
referencing the issue. Definition of done is in each issue.

---

<!-- issue:EPIC -->
## [EPIC] Full DIDComm v2.1 mediator service

**Labels:** enhancement, mediator

### What this is

didcomm-dotnet already implements everything the DIDComm v2.1 spec itself says about mediators
(Routing Protocol 2.0: sender-side forward wrapping with reverse-order `routingKeys`,
mediator-as-DID endpoints, the mediator-side `ForwardProcessor` with pass-through / rewrap /
extra-routing-keys, `please_ack` ignored on forwards). What it does **not** have is anything
needed to run a mediator as a *service* that unknown recipients can enroll with and fetch from:

- the **coordinate-mediation** protocol (enrollment, `routing_did`, recipient DID registry),
- the **message-pickup** protocol (queue, status, delivery, acknowledgement, live mode),
- the **return-route** transport extension that both of the above REQUIRE,
- the delivery pipeline tying `forward` → registered recipient → session / queue,
- the recipient-side client that enrolls, builds its DID document from the grant, and picks up,
- a sample, PRD coverage, and an interop/security gate.

The spec explicitly puts coordination out of its own scope ("That coordination is out of the
scope of this spec", §Routing / Mediator Process), which is why PRD §2.2 lists it as deferred.
The protocols live on didcomm.org instead. This epic brings them in.

### Normative sources

| Piece | Source | Status |
|---|---|---|
| Routing 2.0, service endpoints, transports | DIDComm Messaging v2.1 (`decentralized-identity/didcomm-messaging`, `docs/spec-files/{routing,transports,message_structure}.md`) | spec |
| `return_route` | `didcomm-messaging/extensions/return_route/main.md` — **unversioned, DRAFT**; there is no `didcomm.org/return-route/2.0` page | extension |
| Coordinate Mediation 3.0 | https://didcomm.org/coordinate-mediation/3.0/ | Production |
| Coordinate Mediation 2.0 | https://didcomm.org/coordinate-mediation/2.0/ | Production (what Identus/Blocktrust speak) |
| Message Pickup 3.0 | https://didcomm.org/messagepickup/3.0/ | Production |
| Message Pickup 4.0 | https://didcomm.org/message-pickup/4.0/ (note the renamed PIURI) | Production |
| Discover Features 2.0 | already implemented; mediator advertises the PIURIs above | spec |

### Ecosystem interop targets (for the gate issue)

- Hyperledger **Identus mediator** (Scala): CM 2.0 done, CM 3.0 TODO, pickup 3.0 done, trust-ping, report-problem.
- **Blocktrust mediator** (C#, `bsandmann/blocktrust.Mediator`): routing 2.0, CM 2.0, pickup 3.0, discover-features 2.0, return-route; hosted at `mediator.blocktrust.dev`.
- **Affinidi** DIDComm mediator (Rust, "DIDComm v2.1"): protocol versions to be verified.

### Issues (dependency order)

- [ ] PRD: mediator requirements, return-route amendment, Phase 7 — #89
- [ ] Core: `ForwardProcessor` as a dispatchable handler; `UnpackResult` overload; relay-time `expires_time`/`delay_milli`; DI + feature advertisement — #90
- [ ] Transports: `return_route` extension, receive side (HTTP response body, WebSocket sessions, `ProtocolContext` reply channel) — #91
- [ ] Transports: `return_route` extension, client side (read HTTP response, WebSocket inbound loop, `SendOptions.ReturnRoute`) — #92
- [ ] `DidComm.Mediator`: Coordinate Mediation 3.0, mediator role — #93
- [ ] `DidComm.Mediator`: Message Pickup 3.0, mediator role — #94
- [ ] `DidComm.Mediator`: package, identity, delivery pipeline, `AddMediator()` — #95
- [ ] Recipient-side client: `MediationClient`, `PickupClient`, DID-document builder from `mediate-grant` — #96
- [ ] Sample 11 `MediatedAgent`, sample 04 refresh, cookbook sections, docs — #97
- [ ] Gate: live interop against an external mediator + adversarial security review — #98
- [ ] Compat: Coordinate Mediation 2.0 handler — #99
- [ ] Follow-up: Message Pickup 4.0 — #100

### Design decisions (fixed for the epic unless an issue says otherwise)

1. **Packaging.** New `DidComm.Mediator` package (coordination, pickup, pipeline, stores). Return-route and Routing 2.0 fixes go in the existing packages because they are in the core spec. Store interfaces ship with in-memory implementations only; persistent stores are a host concern (document the contract).
2. **Versions.** CM 3.0 primary, CM 2.0 compat handler; pickup 3.0 first, 4.0 follow-up.
3. **Return-route.** HTTP returns at most one message in the response body; WebSocket is a session. Only honored for authenticated senders; the session is bound to the authenticated sender DID; never honored on a `forward`.
4. **Unknown `next`.** Drop + log, 202 regardless, constant-work lookup. No problem report sourceward.
5. **Ownership.** One grant per `recipient_did`; pluggable `IMediationPolicy`.
6. **Delivery order.** live session → pickup queue → optional host-registered direct push.
7. **Security posture carried over:** no early exit on identity comparisons, no error-text echo of wire values, FR-API-09 identifier caps apply to every new wire field that carries a DID/kid (`recipient_did`, `routing_did`, `next`, attachment ids), `max_receive_bytes` applies to queued bytes too.

### Plan of attack for coding agents

Lanes after the PRD issue merges: **A** routing core · **B** return-route server then client ·
**C** CM 3.0 and pickup 3.0 in parallel (handler-level, tested through `ProtocolDispatcher`
without transports) · then pipeline · then client · then sample · then gate. Every issue runs
the repo's agent protocol (plan → approval → implement → QA permutations → adversarial →
CHANGELOG → PRD sync → PR). One `feat/<slug>` branch per issue, never on `main`.
<!-- /issue -->

<!-- issue:P -->
## PRD: add mediator-service requirements (FR-MED-*, FR-PICK-*, FR-RR-*), amend one-way-transport rules, add Phase 7

**Labels:** documentation, spec-compliance, mediator

Part of the mediator epic (#88). **Do this first** — the PRD is the target the rest is measured
against (CLAUDE.md), and today it has nothing for coordination, pickup, or return-route.

### Current state

- `docs/didcomm-dotnet_PRD.md:114` (§2.2) lists "Coordinate-mediation protocol" as out of scope: *"Ship as a separate package later."* No mention of message-pickup or return-route anywhere in the PRD.
- FR-TRN-03 (`:412`) and FR-TRN-10 (`:431`) state transports are one-way / delivery-only. The `return_route` extension contradicts that between directly connected agents, and coordinate-mediation 3.0 and pickup 3.0 both say *"The `return_route` extension must be supported by both agents."*
- FR-ROUTE-01..08 exist (`:393-398`); the mediator's relay-time behavior (expiry while queued, delay, unknown `next`, forward addressed to someone else) is unspecified.
- §12 ends at Phase 6 (`:620`); §14.3 sample table row 04 (`:1042`) describes the current mediator sample.
- Convention: `FR-<AREA>-NN` rows `| ID | Keyword | Requirement | Acceptance |`; letter suffixes for insertions.
- Repo-instruction drift to fix in the same PR: `AGENTS.md:13` links `docs/didcomm-dotnet-prd.md` (file is `didcomm-dotnet_PRD.md`) and `AGENTS.md:17-18` require `codebase-architecture.md`, which does not exist in `docs/`. Either create a minimal `docs/codebase-architecture.md` (package map + the mediator pipeline once built) or drop the rule; pick one and say which.

### Scope

1. **§2.2 rewrite.** Remove coordinate-mediation from out-of-scope. Add a short "Mediator service (separate package `DidComm.Mediator`)" row to §2.1 scope naming CM 3.0 (+2.0 compat), pickup 3.0 (+4.0 later), return-route. Keep out of scope: persistent store implementations, shorten-url 1.0, swarm 1.0, DIDComm v1 pickup forms.
2. **§8 additions** (after FR-ROUTE-08):
   - FR-ROUTE-09 MUST — relay-time expiry: a forward (or a queued payload) whose `expires_time` has passed is discarded, never delivered; discard is logged with message id only.
   - FR-ROUTE-10 MAY — `delay_milli` honored as a scheduling hint; negative = uniform random in `[0, |n|]`; a mediator that does not honor it MAY send a problem report but is not required to (spec text).
   - FR-ROUTE-11 MUST — forward handling as a registered protocol handler: a `forward` is dispatched like any PIURI; a forward whose decrypting key is not the mediator's, or whose `next` is not a registered recipient, is dropped with constant-work lookup and no sourceward report; HTTP always 202.
3. **New §8a Return-Route extension** — FR-RR-01..06 covering: header name/values (`none` default, `all`, `thread` + `return_route_thread`); valid only between directly connected agents, never on a `forward`; only honored when the sender is authenticated (authcrypt) and the session is bound to that DID; HTTP: at most one returned message in the response body, 200 + `application/didcomm-encrypted+json`, else 202 empty; WebSocket: session for the connection lifetime, `all` re-asserted per connection; `didcomm:transport/queue` as a recognized `serviceEndpoint.uri` meaning "hold at sender for pickup" (parse-and-recognize only). Amend FR-TRN-03 and FR-TRN-10 to say "one-way *unless* the return-route extension (§8a) is in effect".
4. **New §8b Coordinate Mediation** — FR-MED-01..NN: PIURI `https://didcomm.org/coordinate-mediation/3.0`; message pairs and bodies exactly as the protocol page (`mediate-request`→`mediate-grant{routing_did: [..]}`/`mediate-deny`; `recipient-update{updates:[{recipient_did, action: add|remove}]}`→`recipient-update-response{updated:[{recipient_did, action, result: client_error|server_error|no_change|success}]}`; `recipient-query{paginate?{limit, offset}}`→`recipient{dids:[{recipient_did}], pagination?{count, offset, remaining}}`); `return_route: all` required on recipient requests; threading: responses set `thid` = request `id` (CM 3.0 is silent; pickup 4.0 does this; state it); ownership policy (D5); pagination bounds; `routing_did` MUST be a DID whose document has a `DIDCommMessaging` service with a plain transport `uri` (no recursion, FR-ROUTE-04); 2.0 compat row (`keylist-*` names, `routing_did` string).
5. **New §8c Message Pickup** — FR-PICK-01..NN: PIURI `https://didcomm.org/messagepickup/3.0`; `status-request{recipient_did?}`→`status{recipient_did?, message_count (REQUIRED), longest_waited_seconds?, newest_received_time?, oldest_received_time?, total_bytes?, live_delivery?}`; `delivery-request{limit (REQUIRED), recipient_did?}`→`delivery{thid=request id, recipient_did?, attachments:[{id, data:{base64}}]}` or `status` immediately if empty; `messages-received{message_id_list}` → remove, then SHOULD `status`; `live-delivery-change{live_delivery}` → `status`, or problem report `e.m.live-mode-not-supported` on a non-persistent transport; status is never queued; per-recipient independent queues for multi-recipient messages; live mode off by default, reset on reconnect, queue unaffected by live mode; delivered-not-acked messages are never deleted; attachment `id` is mediator-opaque and unique; limits: per-recipient `max_queue_bytes`/`max_queue_messages` (host-configurable), `max_receive_bytes` honored; 4.0 follow-up row.
6. **§12 Phase 7 — Mediator service** with the issue list and kickoff prompt, mirroring Phase 4's format.
7. **§14.3** add sample 11 row; update row 04 description to "routing relay only; enrollment and pickup in 11".
8. **§13.5 matrix** add "Mediation" dimension: enroll · pickup HTTP · pickup WS live · CM 2.0 peer.

### Acceptance

- Every new FR row has a testable Acceptance cell.
- FR-TRN-03/10 no longer contradict §8a.
- `grep -c "FR-MED-\|FR-PICK-\|FR-RR-" docs/didcomm-dotnet_PRD.md` > 0 and each ID is referenced by at least one later issue.
- AGENTS.md references resolve.
- CHANGELOG `[Unreleased]` → "Docs" entry.

### Out of scope

No code. No sample changes.
<!-- /issue -->

<!-- issue:R -->
## Core: make `ForwardProcessor` a dispatchable handler, accept `UnpackResult`, enforce relay-time `expires_time`, surface `delay_milli` as a schedule

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #89 (FR-ROUTE-09..11). Lane A; can start as soon as the FR text is agreed.

### Current state (verified)

- `src/DidComm.Core/Protocols/Routing/ForwardProcessor.cs:76` — `ProcessAsync(string packed, …)` calls `_client.UnpackAsync(packed)` itself (`:80`). It is **not** an `IProtocolHandler`, not registered in DI, and therefore not advertised by `ProtocolFeatureProvider` (`Protocols/DiscoverFeatures/ProtocolFeatureProvider.cs:39-47`), so a mediator does not disclose `https://didcomm.org/routing/2.0` today.
- `src/DidComm.AspNetCore/DidCommEndpointRouteBuilderExtensions.cs:48-141` hands the receive callback an `UnpackResult`. The sample has to re-pack as plaintext and unpack again: `samples/04-MediatorAgent/Program.cs:132-134`. That loses envelope metadata (`Encrypted`, `Authenticated`, `RecipientKid`), re-runs size/expiry checks, and builds a new `ForwardProcessor` per request.
- `ForwardProcessingResult.cs:11-15` carries `(NextHop, OnwardPacked, ExpiresTime, Delay)`; nothing enforces either. `UnpackAsync` rejects an already-expired inbound (`Facade/DidCommClient.cs:669-678`), but nothing re-checks when the payload is relayed or dequeued later.
- `ForwardProcessor.ProcessAsync` does not check that the decrypting kid belongs to the mediator, nor whether the envelope was anoncrypt (spec: forwards are anoncrypted to the routing key; an authcrypt forward is legal but unusual — accept, don't require).
- `ForwardProcessorOptions.ExtraRecipientRoutingKeys` (`ForwardProcessorOptions.cs:10-24`) is one global list; FR-ROUTE-05 says the keys are *per recipient* ("if recipient pre-registered extra routing keys").
- FR-ROUTE-07 (ignore `please_ack`) is already implemented; keep its test.

### Scope

1. **`UnpackResult` overload.** `ProcessAsync(UnpackResult unpacked, CancellationToken)` becomes the primary path; the `string` overload delegates to it. Validate: `unpacked.Message.Type` is the forward MTURI; `unpacked.Encrypted == true` (a plaintext/signed-only forward is refused with `ConsistencyException`); `RecipientKid` is non-null (it decrypted with a key the processor's client owns); exactly one attachment; attachment decodes to a JWE/JWS/JWM the recipient re-parses (keep the lenient base64 pass-through per lesson L-0xx in `tasks/lessons.md` — do not tighten).
2. **`ForwardHandler : IProtocolHandler`** (`ProtocolUri = ForwardConstants.ProtocolUri`) in `Protocols/Routing/`. `HandleAsync` runs the processor and hands the result to an injected `IForwardSink` (new, `Task AcceptAsync(ForwardProcessingResult, ProtocolContext, CancellationToken)`); returns `null` (never a reply — FR-ROUTE-07 and "mediators send only destward"). Default sink in Core: `NullForwardSink` that logs and drops; `DidComm.Mediator` (#95) supplies the real one. Routing-key lookup for `ExtraRecipientRoutingKeys` goes through a new `IRecipientRoutingKeyProvider` (`ValueTask<IReadOnlyList<Jwk>> GetAsync(string next, ct)`), default = the static list from options, so FR-ROUTE-05's per-recipient keys become possible.
3. **Relay-time expiry.** `ForwardProcessingResult` gains `bool IsExpired(DateTimeOffset now, TimeSpan skew)`; the handler drops expired forwards (FR-ROUTE-09) using `DidCommOptions.Clock`/`ExpiresClockSkew`. The *inner* payload's own `expires_time` is invisible to the mediator (encrypted) — document that only the forward's header is enforceable.
4. **Delay.** Keep `Delay` as resolved `TimeSpan?` (negative randomized, already done). Add `DateTimeOffset? NotBefore` computed at receipt so a queue can store an absolute time. The handler does not sleep; sinks schedule.
5. **DI.** `DidCommBuilder.AddRoutingMediator(Action<ForwardProcessorOptions>?)` registers `ForwardProcessor` (singleton), `ForwardHandler` via `AddProtocol<T>`, `IForwardSink`/`IRecipientRoutingKeyProvider` defaults with `TryAdd`. Not part of `AddBuiltInProtocols()` (an ordinary agent is not a mediator). Once registered, `routing/2.0` is advertised automatically with role `mediator`.
6. **Registry-aware endpoints already dispatch** (`MapDidCommEndpoint(pattern)` at `:159`); with the handler registered a mediator no longer needs a custom callback. Keep the callback overload working.
7. **Sample 04** switches to the overload (drop the re-pack) — minimal edit; the real rewrite is #97.

### Acceptance

- Unit (`tests/DidComm.Core.Tests/Protocols/Routing/`): overload equivalence with the string path on the existing fixtures; plaintext forward refused; signed-only forward refused; forward decrypted by a non-owned kid impossible by construction (document); two attachments refused; expired forward dropped at the handler, not before (so an expired forward never reaches the sink); negative `delay_milli` yields `NotBefore` within `[now, now+|n|]`; `please_ack` still ignored; sink receives exactly one call per forward; sink exception does not escape the dispatcher and does not produce a reply.
- Dispatch (`tests/DidComm.InteropTests/Protocols/`): `AddRoutingMediator()` + `MapDidCommEndpoint("/didcomm")` relays a real Alice→Mediator→Bob onion (reuse `Routing/AliceMediatorBobRoundTripTests.cs` actors) with the sink capturing `OnwardPacked`; `discover-features` query for `protocol` match `https://didcomm.org/routing/*` discloses `routing/2.0` with roles `["mediator"]` only when `AddRoutingMediator()` is on.
- Live harness `tools/InteropCli` `unwrap-forward` (`Program.cs:352-375`) still passes both legs.
- Public-API: additive only; package validation green.
- CHANGELOG + PRD acceptance cells for FR-ROUTE-09..11 filled.

### Out of scope

Queues, sessions, coordination (#95, #91, #93).
<!-- /issue -->

<!-- issue:RRs -->
## Transports: `return_route` extension — receive side (HTTP response body, WebSocket sessions, reply channel on `ProtocolContext`)

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #89 (FR-RR-*). Lane B, step 1. Required by coordinate-mediation 3.0 and message-pickup 3.0 ("The `return_route` extension must be supported by both agents").

### Spec (extension `return_route`, DRAFT, unversioned)

- Header `return_route`; values `none` (default when omitted), `all` ("Send all messages for this DID over the connection"), `thread` (+ `return_route_thread`, placement unspecified → read it as a sibling top-level header, document the choice).
- "only to be used between the immediate sending agent and the agent it connects directly to … not valid when routing keys are in use."
- "For HTTP transports … the receiving agent MAY hold onto the connection and use it to return messages … HTTP transports will only be able to receive at most one message at a time. Websocket transports are capable of receiving multiple messages over a single connection."
- Core spec: "The trust of each message MUST be associated with DIDComm encryption or signing, not from the socket connection itself."
- CM 3.0: `return_route: all` "must be set each time the communication channel is established: once per established websocket, and every message for an HTTP POST."

### Current state (verified)

- Zero `return_route` hits in src/tests/samples/docs. `Message.AdditionalHeaders` (`Messages/Message.cs:137`) is where it lands today.
- `ProtocolContext` (`Protocols/ProtocolContext.cs:19-24`) = `(Received, Thread, Client, Options, Threads)`; no connection/session/reply sink. Handlers can only return one `Message`.
- HTTP registry-aware endpoint logs the reply and returns 202 (`DidCommEndpointRouteBuilderExtensions.cs:206-211`); callback overload cannot touch `HttpContext` (`:48-141`), always 202 (`:139`).
- WebSocket registry-aware endpoint can send a reply on the same socket only when `DidCommReceiveOptions.AllowSameSocketReplies` and STOMP is off (`:462-465`, `TrySendReplyOnSocketAsync` `:475-517`), gated by `TryRouteSameSocketReply` (`:532-609`): inbound must be encrypted+authenticated, `reply.from` == decrypting DID, `reply.to` == `[peer]`. **This is the seed of the session model; generalize it rather than adding a parallel path.**
- `StompServerSession.cs` has no SUBSCRIBE/MESSAGE → no server push under STOMP (`DidCommReceiveOptions.cs:43-46`).
- `DispatchOutcome` (`Protocols/DispatchOutcome.cs:30-33`) carries a single `Reply`.
- PRD FR-TRN-03 (`:412`), FR-TRN-10 (`:431`) say one-way — amended by #89.

### Scope

1. **Model** (`DidComm.Core/Transports/ReturnRoute/`):
   - `ReturnRouteMode { None, All, Thread }`; `ReturnRoute.TryRead(Message, out mode, out thread)` reads `return_route` / `return_route_thread` from `AdditionalHeaders`; unknown value → `None` + log (never throw: a bad header must not break delivery). `MessageBuilder.WithReturnRoute(mode, thread?)`.
   - `IReturnRouteSession` — `string PeerDid` (the **authenticated** sender DID), `ReturnRouteMode Mode`, `string? Thread`, `TransportKind` (`Http`|`WebSocket`), `bool IsLive` (true for WS), `ValueTask<bool> TrySendAsync(ReadOnlyMemory<byte> packed, string mediaType, ct)` (HTTP: succeeds at most once, then false; WS: queued to the socket writer), `CancellationToken Closed`.
   - `IReturnRouteSessionRegistry` — `Register/Unregister(session)`, `IReadOnlyList<IReturnRouteSession> Find(string did)`; in-memory default, bounded (max sessions per DID, total), LRU-evicting.
   - `ProtocolContext` gains `IReturnRouteSession? Session` (null when no return-route or sender unauthenticated). Handlers may call `Session.TrySendAsync` for *additional* messages; the single returned `Message` keeps working and is sent over the session when present.
2. **Eligibility (security).** A session is created only when: outermost unpack is encrypted **and** authenticated (`UnpackResult.Authenticated`, sender binding verified — FR-CONSIST-05/06); the message is **not** a `forward` (extension: invalid when routing keys are in use); `return_route != none`. `PeerDid` = `DidSubject.DidSubjectOf(SenderKid)`. Anything sent on the session MUST be packed with `to = [PeerDid]`; the pack is done by the endpoint, not the handler (handlers return plaintext `Message`s, as today), authcrypt from the decrypting DID — exactly the existing `TryRouteSameSocketReply` rule, now the one shared rule for HTTP and WS.
3. **HTTP.** `MapDidCommEndpoint` (both overloads): when a session exists and a handler produced a message, respond **200** with body = packed message, `Content-Type: application/didcomm-encrypted+json`; otherwise **202** empty. Never more than one. The 415/413/400 paths and the constant-time rejection floor (`DelayToRejectionFloorAsync` `:691`) are untouched. The callback overload gets a new `Func<UnpackResult, IReturnRouteSession?, CancellationToken, Task<Message?>>` variant.
4. **WebSocket.** `MapDidCommWebSocket` registers a session per socket at first eligible message; `Mode` is re-set by each subsequent message (CM 3.0: asserted once per socket is enough, but a later `none` must downgrade). Unregister on close. Writer is single-consumer (one `Channel<T>` per socket) so handler pushes and replies never interleave frames. `AllowSameSocketReplies` becomes the on/off switch for return-route on WS (keep the name, update the doc), default **off** as today. STOMP: unsupported → session never created, log once; document (`DidCommReceiveOptions.UseStomp` doc).
5. **Live-delivery hook.** `IReturnRouteSessionRegistry` raises `SessionOpened/SessionClosed` so pickup (#94) can flip live mode off on disconnect ("a new inbound connection starts with Live Mode disabled").
6. **Dispatcher.** `ProtocolDispatcher.DispatchAsync` takes an optional `IReturnRouteSession?` and places it on the context; `DispatchOutcome` unchanged.
7. **`didcomm:transport/queue`.** `ServiceEndpointParser` recognizes the URI (no transport); `TransportRouter` refuses to send to it with a clear `TransportException` ("recipient holds messages for pickup; use return-route"). Parse-level only.

### Acceptance

- Unit: header parse table (absent/none/all/thread/garbage/non-string); eligibility matrix (anoncrypt → no session; signed-only → no session; authcrypt → session; authcrypt `forward` → no session; `OwnIdentifiers` mismatch → no session); registry bounds + eviction; HTTP session `TrySendAsync` second call returns false.
- Integration (`tests/DidComm.InteropTests/AspNetCore/`): HTTP POST with `return_route: all` + trust-ping → 200 with an authcrypt `ping-response` to the sender, media type exact; without the header → 202 empty (existing tests keep passing); WS: two pushes from a handler arrive on the same socket in order; close → `SessionClosed` fired and registry empty; a second socket for the same DID → two sessions; STOMP → 202-equivalent behavior, no push.
- Adversarial: a message authcrypted by Mallory with `return_route: all` cannot receive Bob's traffic (session keyed by Mallory's DID only); a replayed message cannot open a session for someone else; oversize push respects `max_receive_bytes` of the peer? (No — sender side; but respect socket frame limits.) Timing: 202 vs 200 is observable by design (the sender authenticated) — document.
- Existing `SameSocketReplyRoutingTests.cs` migrate to the shared rule without weakening.
- PRD FR-RR-* acceptance cells filled; FR-TRN-03/10 wording landed; CHANGELOG.

### Out of scope

Client side (#92), pickup/live-mode semantics (#94), STOMP push.
<!-- /issue -->

<!-- issue:RRc -->
## Transports: `return_route` extension — client side (read HTTP response, WebSocket inbound loop, `SendOptions.ReturnRoute`)

**Labels:** enhancement, mediator

Part of #88. Blocked by #91 (shared model) and #89. Lane B, step 2. Needed by the recipient-side client (#96) and by every test that drives a mediator end-to-end.

### Current state (verified)

- `IDidCommTransport` (`Transports/IDidCommTransport.cs:13-39`) is delivery-only; `TransportResult` (`:10`) = `(Accepted, HttpStatusCode?)` — no body.
- `HttpDidCommTransport.SendAsync` (`Transports.Http/HttpDidCommTransport.cs:65`) discards the response body (`:98-101`).
- `WebSocketDidCommTransport` (`Transports.WebSocket/WebSocketDidCommTransport.cs:28`) never reads DIDComm frames; only the STOMP handshake receives (`:475`, `:515-540`).
- `SendOptions` (`Transports/SendOptions.cs:21-27`) has no return-route field; `DidCommClient.SendAsync` (`Facade/DidCommClient.cs:493`, core `:522-573`) always uses the encrypted media type and has no inbound path.
- `DidCommClient` has no "send already-packed bytes" API; relays use `ITransportRouter` directly.

### Scope

1. **`TransportResult`** gains `ReadOnlyMemory<byte>? ResponsePayload` and `string? ResponseMediaType`. HTTP transport: when the response is 200 with a DIDComm media type (`application/didcomm-*+json`), read the body **capped at `DidCommOptions.MaxReceiveBytes`** (SSRF/response-bomb guard; an over-cap body is discarded and `Accepted` stays true with a logged warning), else `null`. 202 → `null`. Keep `Accepted` semantics unchanged.
2. **`SendOptions.ReturnRoute`** (`ReturnRouteMode`, default `None`) and `ReturnRouteThread`; `DidCommClient.SendAsync` stamps the header (via `MessageBuilder.WithReturnRoute`) before packing. **Refuse** `ReturnRoute != None` together with `Forward: true`/a routed recipient (extension: invalid when routing keys are in use) with `ArgumentException` — at the facade, before any resolution.
3. **`SendResult`** gains `UnpackResult? Returned`: when `ResponsePayload` is present, the facade unpacks it with the same client and returns it (no dispatch — the caller asked for it). Unpack failure → `TransportException` wrapping, not a crash; the original send is still reported accepted.
4. **WebSocket inbound.** `WebSocketDidCommTransport` gets an opt-in receive loop per pooled socket (`WebSocketTransportOptions.ReceiveInbound = false` default): frames are read, size-capped, and surfaced on `IInboundMessageSource` (new, `ChannelReader<ReceivedFrame>`), which the DI layer wires to `ProtocolDispatcher` through a new `DidCommBuilder.UseInboundDispatch()` so returned/pushed messages hit registered handlers (e.g. pickup `delivery`). Backpressure: bounded channel, drop-newest with the same rate-limited log pattern as `ObserverDelivery`.
5. **`DidCommClient.SendPackedAsync(ReadOnlyMemory<byte>, Uri, string mediaType, ct)`** — a thin public wrapper over the router + `OutboundEndpointGuard` for relays and acks; the mediator pipeline (#95) and sample 04 stop reaching for `ITransportRouter`.

### Acceptance

- Unit: `ResponsePayload` present on 200+DIDComm type, absent on 202, absent/logged on over-cap, absent on non-DIDComm content type; `Forward`+`ReturnRoute` refused before resolution (no resolver call observed via NSubstitute); header stamped exactly `"return_route":"all"`.
- Integration: client `SendAsync(ping, ReturnRoute: All)` against the #91 TestServer gets `Returned` = authenticated `ping-response`; WS: `UseInboundDispatch()` dispatches a server push to a registered test handler; socket drop → loop ends, no unobserved exception; reconnect policy unchanged.
- Adversarial: a 200 response with a 100 MB body from a hostile mediator is cut at the cap without allocating it; a response claiming DIDComm type but containing garbage yields a wrapped `TransportException`, not an unhandled parser exception (FR-API-07 parity).
- CHANGELOG; PRD FR-RR client rows.
<!-- /issue -->

<!-- issue:CM3 -->
## `DidComm.Mediator`: Coordinate Mediation 3.0 — mediator role (`mediate-request`/`grant`/`deny`, `recipient-update`, `recipient-query`, registry, policy)

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #89 (FR-MED-*). Lane C. Handler-level work: testable through `ProtocolDispatcher` with no transport; the return-route requirement is satisfied by #91 at the endpoint, and the handler only needs `context.Session` to exist (or not) — do not couple to transports.

### Spec (https://didcomm.org/coordinate-mediation/3.0/, Production)

- PIURI `https://didcomm.org/coordinate-mediation/3.0`; roles `mediator`, `recipient`; "No protocol specific errors exist."; "The `return_route` extension must be supported by both agents"; recipient sets `return_route: all` on every HTTP request / once per WS.
- `mediate-request` — empty body.
- `mediate-grant` — `body.routing_did: [string]` (array in 3.0; string in 2.0). "DID of the mediator where forwarded messages should be sent. The `recipient` may use this DID as an endpoint". "After receiving a `mediate-grant` … the `recipient` should … add DIDs. In order for the `mediator` to start accepting Forward Message for those DIDs."
- `mediate-deny` — empty body.
- `recipient-update` — `body.updates: [{recipient_did, action: "add"|"remove"}]`.
- `recipient-update-response` — `body.updated: [{recipient_did, action, result: "client_error"|"server_error"|"no_change"|"success"}]`.
- `recipient-query` — `body.paginate?: {limit, offset}` ("if present must include `limit` and `offset`").
- `recipient` — `body.dids: [{recipient_did}]`, `body.pagination?: {count, offset, remaining}`.
- Threading: not stated. **Decision:** every response sets `thid` = request `id` (pickup 4.0 does this; harmless for peers that ignore it).
- Endnotes: single endpoint per grant; no proof of DID ownership (squatting risk acknowledged).

### Current state

- Nothing exists (zero hits for `coordinate-mediation`, `routing_did`, `recipient_did`). Sample 04 fakes the registry with a `Dictionary<string, Uri>` (`samples/04-MediatorAgent/Program.cs:72`, `:217` "the coordinate-mediation stand-in").
- Handler pattern to copy: `Protocols/DiscoverFeatures/DiscoverFeaturesHandler.cs:30-89` (ctor-injected providers), `TrustPingHandler.cs:34-37` (reply `from` = decrypting DID via `DidSubject.DidSubjectOf(context.Received.RecipientKid)`).
- `DidCommOptions.OwnIdentifiers` is frozen at client construction (`DidCommClient.cs:140`) — the mediator's own DIDs are static; recipients' DIDs must **not** go there (they are not "us").
- FR-API-09 caps: `recipient_did`/`routing_did` are wire DIDs → 2048 UTF-16 units, linear scan, never echoed.

### Scope (new project `src/DidComm.Mediator`, namespace `DidComm.Mediator.Coordination`)

1. **Messages.** `CoordinateMediation` static class: PIURI/MTURI constants (3.0), `CreateMediateRequest`, `CreateGrant(routingDids, thid)`, `CreateDeny(thid)`, `CreateRecipientUpdate(updates)`, `CreateRecipientUpdateResponse`, `CreateRecipientQuery(paginate?)`, `CreateRecipient(dids, pagination?)`; strict parsers (`TryParse*`) that reject unknown `action`/`result` values, non-array `updates`, missing `limit`/`offset` when `paginate` present, and over-cap DIDs — all mapped to the existing `MalformedMessageException`/`ProtocolException` types, never raw JSON exceptions.
2. **Store.** `IMediationStore` (async, ct everywhere): `GetGrantAsync(peerDid)`, `GrantAsync(peerDid, MediationGrant)`, `RevokeAsync(peerDid)`, `UpdateRecipientsAsync(peerDid, updates) → results`, `ListRecipientsAsync(peerDid, offset, limit) → page`, `FindGrantByRecipientDidAsync(recipientDid)` (used by the pipeline to resolve `next`; **constant-work with respect to registration state** — e.g. always a dictionary lookup, no early return on the result). `InMemoryMediationStore` with bounds: `MaxGrants`, `MaxRecipientsPerGrant`, and the pipeline's quotas keyed by grant.
3. **Policy.** `IMediationPolicy`: `ValueTask<MediationDecision> OnMediateRequestAsync(peerDid, ProtocolContext)` (Grant/Deny; default Grant — hosts override for allowlists), `ValueTask<bool> MayRegisterAsync(peerDid, recipientDid, …)` (default: `recipientDid` not already owned by another grant, else `client_error`; identical DID → `no_change`). The peer's DID is **always** the authenticated sender (`SenderKid`); an anoncrypt or signed-only request is answered with `mediate-deny` / all-`client_error`, never granted.
4. **Handler** `CoordinateMediationHandler : IProtocolHandler` (3.0), registered via `AddProtocol<T>`. `mediate-request` → policy → `grant` with `routing_did` from `MediatorIdentity` (#95 provides it; here an injected `IMediatorIdentity { IReadOnlyList<string> RoutingDids }`) or `deny`; `recipient-update` → per-entry results in **input order** (spec example preserves order), `server_error` for store faults (caught, logged, never thrown through dispatch); `recipient-query` → paged, `limit` clamped to `MaxPageSize` (option, default 100), `offset` beyond end → empty page with `remaining: 0`. Replies' `from` = decrypting DID, `to = [peerDid]`, `thid` = request id. If `context.Session` is null (no return-route) the reply is still produced — the endpoint decides how to deliver (the spec *requires* return-route, but a missing header is the recipient's bug and must not crash the mediator).
5. **Feature advertisement:** automatic via `ProtocolFeatureProvider` once the handler is registered; add `roles: ["mediator"]`.
6. **DI:** `DidCommBuilder.AddCoordinateMediation(Action<CoordinateMediationOptions>?)` (extension method in the Mediator package, same pattern as `HttpDidCommBuilderExtensions.cs:30`); `TryAdd` for store/policy so hosts can replace them.
7. **Package plumbing:** csproj with `EnablePackageValidation=false` until first release (no baseline on NuGet), add to `DidComm.sln` under `src`, add to `tests/DidComm.InteropTests` references and `DxCoverage/CoverageComputation.cs:14-22` shipped-assembly list (expect DX-coverage failures until #97 lands — add to `coverage-allowlist.json` with a `TODO(#97)` marker rather than weakening the gate).

### Acceptance

- Unit (`tests/DidComm.Core.Tests` or new `tests/DidComm.Mediator.Tests`): message round-trips byte-exact against the didcomm.org examples (checked into `tests/.../fixtures/coordinate-mediation-3.0/*.json`); parser rejection table (unknown action, `routing_did` string vs array — **accept both on parse**, emit array); results order; `no_change`/`client_error`/`server_error`/`success` each produced; pagination edge cases (limit 0, offset > count, limit > max); over-cap DID rejected without echo; anoncrypt `mediate-request` → `deny`.
- Dispatch tests: full `request→grant→update→query` sequence through `ProtocolDispatcher` with `AddCoordinateMediation()`; `discover-features` discloses the PIURI with `roles: ["mediator"]`.
- Adversarial: Mallory cannot `remove` Bob's DID (different grant → `client_error`); Mallory cannot `add` a DID Bob already registered (→ `client_error`); 10k `recipient-update` adds hit `MaxRecipientsPerGrant` → `client_error`, memory bounded; store lookup timing for registered vs unregistered DIDs is statistically indistinguishable (same harness style as the #35/#44 timing tests).
- CHANGELOG; PRD FR-MED-* acceptance cells.

### Out of scope

2.0 (`keylist-*`) — #99. The recipient role — #96. Delivery — #95.
<!-- /issue -->

<!-- issue:PK3 -->
## `DidComm.Mediator`: Message Pickup 3.0 — mediator role (queue store, `status`, `delivery`, `messages-received`, live mode)

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #89 (FR-PICK-*); live mode needs #91 sessions. Lane C, parallel to #93 (share the project; agree the `IMessageStore` surface up front).

### Spec (https://didcomm.org/messagepickup/3.0/, Production)

- PIURI `https://didcomm.org/messagepickup/3.0`; roles `mediator`, `recipient`; `return_route: all` on every recipient request.
- `status-request` — `body.recipient_did?`; if present the mediator **MUST** only return status for that DID.
- `status` — `body: {recipient_did?, message_count (REQUIRED), longest_waited_seconds?, newest_received_time?, oldest_received_time?, total_bytes?, live_delivery?}`; times are UTC epoch **seconds**; "a status message **MUST NOT** be put on the pending message queue and **MUST** only be sent when the `recipient` is actively connected"; echo `recipient_did` when the request had one.
- `delivery-request` — `body.limit` (REQUIRED, "SHOULD deliver up to the limit"), `recipient_did?`. "If no messages are available … a `status` message **MUST** be sent immediately." "Delivered messages **MUST NOT** be deleted until delivery is acknowledged by a messages-received message."
- `delivery` — `thid` = `delivery-request` id; `body.recipient_did?` (only when the request had one); `attachments: [{id, data: {base64}}]` — `id` "opaque … unique to the mediator", "The ONLY valid type of attachment … is a DIDComm v2 Message in encrypted form."
- `messages-received` — `body.message_id_list: [id]`; mediator removes them and SHOULD send `status`.
- Multi-recipient: queued "for each `recipient` independently", removed per recipient.
- Live mode: "disabled by default and only activated by the `recipient`"; messages arriving while off MUST be queued; on a broken connection "a new inbound connection starts with Live Mode disabled"; queued messages are unaffected by live mode; "MUST only be enabled when a persistent transport is used, such as WebSockets".
- `live-delivery-change` — `body.live_delivery: bool`; mediator MUST respond with `status`; on a non-persistent transport SHOULD send `problem-report` with `pthid` = thread, `code: "e.m.live-mode-not-supported"`, `comment: "Connection does not support Live Delivery"`.
- 3.0 does **not** define how a live-delivered message is framed. **Decision:** push it as a `delivery` message with one attachment and expect `messages-received` (the 4.0 rule) — document in the PRD as the 3.0 ambiguity resolution; #100 formalizes.

### Current state

- Nothing (zero hits). Thread store (`Threading/InMemoryThreadStateStore.cs`) is fixed-shape and must not be abused for queues. `ProblemReport` helpers exist (`Protocols/ProblemReport/`) for `e.m.live-mode-not-supported`. `MessageBuilder.WithThid` `:87`, `Attachment.Base64` exists (`Messages/Attachment.cs`).

### Scope (namespace `DidComm.Mediator.Pickup`)

1. **Store.** `IMessageStore`: `EnqueueAsync(recipientDid, QueuedMessage{Id, Payload, MediaType, ReceivedAt, NotBefore?, ExpiresAt?, Bytes})`, `PeekAsync(recipientDid, limit, now) → IReadOnlyList<QueuedMessage>` (skips `NotBefore > now`, never returns expired, marks returned items *delivered-pending-ack* with a lease), `AckAsync(recipientDid, ids)` (delete), `ReleaseLeaseAsync` (on disconnect/timeout so unacked messages become deliverable again), `StatusAsync(recipientDid?) → QueueStatus{Count, TotalBytes, Oldest, Newest, LongestWaitedSeconds}`, `PurgeExpiredAsync(now)`. Scope = the *grant* (peer DID) plus optional `recipient_did` filter: the queue key is `(grantPeerDid, recipientDid)` so a recipient with several DIDs can query all or one. `InMemoryMessageStore` with per-grant `MaxMessages`/`MaxBytes` (enqueue beyond quota → `QueueFullException` the pipeline turns into drop+log), lease timeout option.
2. **Handler** `MessagePickupHandler : IProtocolHandler` (3.0). Peer identity = authenticated sender; anoncrypt requests → no reply, log. `status-request` → `status` (never queued: the reply is only ever written to the current session; if `context.Session` is null, the endpoint's single-response path still carries it over HTTP). `delivery-request` → clamp `limit` to `MaxDeliveryBatch` (option, default 10) and to the session transport (HTTP: the *response* can carry exactly one DIDComm message, and that message is one `delivery` whose attachments hold up to `limit` payloads — so HTTP works); empty → `status`; `messages-received` → ack + `status`; `live-delivery-change` → if `Session?.IsLive != true` → problem report `e.m.live-mode-not-supported` (built with the existing ProblemReport helpers, `pthid` = request `thid ?? id`), else flip the per-session flag and reply `status{live_delivery}`.
3. **Live delivery.** `LiveDeliveryCoordinator` subscribes to `IReturnRouteSessionRegistry.SessionClosed` (#91) to clear live flags and release leases; exposes `TryDeliverLiveAsync(recipientDid, QueuedMessage)` for the pipeline (#95): if a live session exists for the grant → push a `delivery` (one attachment) over `Session.TrySendAsync` and keep the lease until `messages-received`; else return false (→ queue). Unacked live deliveries return to the queue on lease expiry/disconnect (the 4.0 rule; harmless under 3.0).
4. **Attachment ids** = store-generated opaque ids (`IMessageIdGenerator` exists — reuse), never the inner message id (the mediator cannot read it).
5. **DI:** `AddMessagePickup(Action<MessagePickupOptions>?)`; `TryAdd` store/coordinator; advertised with `roles: ["mediator"]`.

### Acceptance

- Fixtures: byte-exact message shapes vs the didcomm.org examples (`tests/.../fixtures/messagepickup-3.0/*.json`), including `status` with only `message_count`.
- Unit: FIFO per recipient; `limit` clamp; `NotBefore` honored; expired never delivered and purged; delivered-unacked survives a second `delivery-request` **only after** lease release (no double-delivery within the lease, no loss after); `messages-received` with unknown ids → ignored, `status` still sent; multi-recipient enqueue = N independent copies, each acked separately; quota → `QueueFullException`; `recipient_did` filter scoping (Bob's grant cannot see Carol's queue even if both registered the same `recipient_did` string — impossible by #93 policy, but assert it).
- Dispatch: HTTP (`return_route: all`) `delivery-request{limit: 3}` → 200 with one `delivery` carrying 3 attachments; WS live: `live-delivery-change{true}` → `status{live_delivery: true}`, then a pipeline enqueue arrives as a `delivery` push, `messages-received` clears it; socket close → live off, next connection `status{live_delivery: false}`; HTTP `live-delivery-change{true}` → problem report with the exact code/comment strings.
- Adversarial: a recipient cannot ack another grant's ids (ids are scoped per grant); a flood of 100 MB enqueue attempts for one grant stays within `MaxBytes`; `limit: 2^31-1` and `limit: -1` and `limit: "10"` are rejected/clamped without allocation; base64 attachment of a non-JWE is rejected at parse on the *recipient* side only (mediator relays bytes — lesson in `tasks/lessons.md` on not tightening pass-through).
- CHANGELOG; PRD FR-PICK-* cells.

### Out of scope

Pickup 4.0 (#100), the recipient role (#96), the forward→queue wiring (#95).
<!-- /issue -->

<!-- issue:PIPE -->
## `DidComm.Mediator`: mediator identity, delivery pipeline (`forward` → session / queue / direct), `AddMediator()` composition

**Labels:** enhancement, mediator

Part of #88. Blocked by #90, #91, #93, #94. This is where the pieces become a service.

### What a mediator must do end-to-end (spec §Mediator Process)

> 1. Receive `forward` message. 2. Retrieve service endpoint pre-configured by recipient (`next` attribute). 3. Transmit `payload` message to service endpoint in the manner specified in the transports section.

plus the optional once-per-routing-key rewrap between 2 and 3 (FR-ROUTE-05), optional rewrapping (FR-ROUTE-06), optional `delay_milli`, `expires_time`, `please_ack` ignored (FR-ROUTE-07), and — from pickup — "Messages that arrive when Live Mode is off MUST be stored in the queue".

### Current state

- Sample 04 does steps 1–3 by hand with a `Dictionary<string, Uri>` and `ITransportRouter.SendAsync` (`samples/04-MediatorAgent/Program.cs:118-153`); `ExpiresTime`/`Delay` only printed (`:140-141`).
- The mediator's own DID is minted ad hoc in the sample (`_shared/PeerIdentityFactory.cs:49-87`); there is no library helper to build a `DIDCommMessaging` service entry, and `ServiceEndpointParser` is internal/parse-only.
- `ForwardProcessorOptions.ExtraRecipientRoutingKeys` is global, not per recipient (`ForwardProcessorOptions.cs:10-24`).
- `ForwardHandler`/`IForwardSink` arrive with #90.

### Scope (namespace `DidComm.Mediator`)

1. **`IMediatorIdentity` / `MediatorIdentity`.** `RoutingDids` (what `mediate-grant` returns; usually one DID), and a documented requirement that each routing DID's document has a `DIDCommMessaging` service with a **plain transport `uri`** and the mediator's `keyAgreement` keys (FR-ROUTE-04: senders prepend those keys; "A DID representing a mediator SHOULD NOT use alternative endpoints in its own DID Document"). Provide `DidCommServiceBuilder` (public, Core or Mediator — Core if #96 also needs it; it does) that emits the spec object form `{id, type: "DIDCommMessaging", serviceEndpoint: {uri, accept: ["didcomm/v2"], routingKeys?}}` for use with net-did's `IDidManager` — lifting `samples/_shared/PeerIdentityFactory.cs:96-113` into the library. Secrets stay in the host's `ISecretsResolver`.
2. **`MediatorForwardSink : IForwardSink`** — the pipeline:
   1. `result.IsExpired(now)` → drop + log id (FR-ROUTE-09).
   2. Resolve `next`: strip fragment if it is a DID URL (spec: `next` "may also be a key, for the last hop"); `IMediationStore.FindGrantByRecipientDidAsync(next)` constant-work; unknown → drop + log, **no** report (D4).
   3. Per-grant extra routing keys (`IRecipientRoutingKeyProvider` backed by the grant) → rewrap once per key (FR-ROUTE-05) — already in `ForwardProcessor` once #90 routes the lookup through the provider.
   4. `NotBefore` from `delay_milli` → store on the queued message; live push is also deferred until then (`PeriodicTimer`-driven scheduler in the coordinator; keep it simple, no Quartz).
   5. Delivery order (D6): `LiveDeliveryCoordinator.TryDeliverLiveAsync` → `IMessageStore.EnqueueAsync` → if the host registered an `IRecipientEndpointDirectory` (optional, `Uri? Find(grantPeerDid)`) and `MediatorOptions.DirectPush == true`, push via `DidCommClient.SendPackedAsync` (#92) with the outbound SSRF guard and treat a transport failure as "queue instead" (never lose a message that was accepted).
   6. Quota exceeded (`QueueFullException`) → drop + log; HTTP still 202 (the sender gets no oracle; the recipient sees `status.total_bytes` at cap — document). Consider FR-TRN constraint `max_receive_bytes` disclosure already present.
3. **`MediatorOptions`**: `DirectPush` (false), `MaxQueueBytesPerGrant`, `MaxQueueMessagesPerGrant`, `LeaseTimeout`, `MaxDeliveryBatch`, `MaxPageSize`, `PurgeInterval`, `RewrapMode` (pass-through default). One options object; CM/pickup options nest or alias it — avoid three overlapping option types.
4. **`DidCommBuilder.AddMediator(Action<MediatorOptions>?)`** = `AddRoutingMediator()` + `AddCoordinateMediation()` + `AddMessagePickup()` + sink + coordinator + identity + hosted `PurgeExpiredAsync` loop (`IHostedService`, only when hosting abstractions are present — the Mediator package may reference `Microsoft.Extensions.Hosting.Abstractions`; keep Core free of it). Also registers a `trust-ping` responder (already built-in) so health checks work.
5. **Endpoint composition:** `IEndpointRouteBuilder.MapDidCommMediator(pattern)` in `DidComm.AspNetCore` (or in the Mediator package referencing AspNetCore — prefer the Mediator package to keep AspNetCore's dependency list unchanged): maps the HTTP and WS registry-aware endpoints plus the existing OOB invitation endpoint (`DidCommEndpointRouteBuilderExtensions.cs:255`) so a recipient can discover the mediator's DID from `/oob`.
6. **Observability:** structured logs with message id / grant / outcome (`Delivered-Live`, `Queued`, `Pushed`, `Dropped-Expired`, `Dropped-Unknown`, `Dropped-Quota`), rate-limited like `ObserverDelivery`; `System.Diagnostics.Metrics` counters with the same names. No DID values in log messages at Information or above.

### Acceptance

- Integration (`tests/DidComm.InteropTests/Mediator/`): one TestServer running `AddMediator()`+`MapDidCommMediator("/")`: Bob (via #96 or hand-rolled messages) enrolls, registers `did:peer` B, builds his DID doc with `uri = routing_did`; Alice `SendAsync(Forward: true)` resolves B, wraps for the mediator (FR-ROUTE-04 path), posts; mediator queues; Bob `delivery-request` over HTTP gets it; Bob acks; `status.message_count == 0`. Same with Bob on a live WS session → push, no queue growth. Same with `DirectPush` and an `IRecipientEndpointDirectory` → delivered to Bob's inbox (sample 04 parity). Two-mediator route (Bob's `routingKeys` names a second mediator DID) → first mediator relays to the second via the queue/push, second delivers — reuse `SpecActorRegistry` mediator1/mediator2.
- Expiry: a forward with `expires_time` in the past → `Dropped-Expired`, nothing queued; a queued payload whose forward expiry passes before pickup → purged, `status` reflects it.
- Delay: `delay_milli: 500` → not deliverable before 500 ms (clock injected via `DidCommOptions.Clock`, no real sleeps in tests).
- Unknown `next` → 202, nothing queued, no outbound message; timing equal to the known-recipient path within the existing timing-test tolerance.
- Rewrap mode and per-grant extra routing keys produce onions the recipient unwraps (reuse `ForwardProcessorTests` vectors).
- Live harness: extend `tools/InteropCli` with a `mediator-serve` verb (or keep CLI stateless and add a TestServer-based cell) so the python/jvm legs can send a forward *through* a running didcomm-dotnet mediator and the dotnet side picks it up — at least one cell per leg.
- CHANGELOG; PRD Phase 7 rows for the pipeline (FR-ROUTE-09..11 acceptance filled).
<!-- /issue -->

<!-- issue:CLIENT -->
## Recipient-side client: `MediationClient` (enroll, manage recipient DIDs), `PickupClient` (status/delivery/ack/live), DID-document service from `mediate-grant`

**Labels:** enhancement, mediator

Part of #88. Blocked by #92, #93, #94 (message types/parsers). This is what an edge agent (wallet) uses; without it the mediator is only testable by hand-built messages.

### Current state

- `DiscoverFeaturesClient` (`Protocols/DiscoverFeatures/DiscoverFeaturesClient.cs:41`, `QueryFeaturesAsync` `:148`) is the only request/await client; it works as an `IInboundCorrelator` (internal, `Protocols/IInboundCorrelator.cs:21-26`). The Mediator package needs `InternalsVisibleTo` in `DidComm.Core.csproj`, or — better — promote `IInboundCorrelator`/`InboundMessageSnapshot` to public with the same contract (decide in the plan; promotion is additive).
- No helper builds a `DIDCommMessaging` service (#95 adds `DidCommServiceBuilder`).
- Receivers must "expect double-packaged messages" (rewrapping) — `UnpackAsync` already peels a forward addressed to us? Verify: if the inner payload after the mediator's rewrap is a `forward` to Bob, Bob's unpack must recognize and unwrap it (FR-ROUTE-06 receiver side). Add if missing.

### Scope (namespace `DidComm.Mediator.Client`)

1. **`MediationClient`** (`DidCommClient`, options, correlator): `RequestMediationAsync(mediatorDid, from, ct) → MediationGrant | MediationDenied` (sends `mediate-request` with `ReturnRoute: All`; over HTTP the grant arrives in `SendResult.Returned`; over WS via the inbound dispatch — the client handles both: first look at `Returned`, else await the correlator with a timeout option); `UpdateRecipientsAsync(mediatorDid, from, updates) → results`; `QueryRecipientsAsync(…, paginate?)` with an async-enumerable page walker.
2. **`DidCommServiceBuilder.ForMediation(MediationGrant grant)`** → the service object `{uri: grant.RoutingDids[0], accept: ["didcomm/v2"]}` (spec: "The `recipient` may use this DID as an endpoint"; with FR-ROUTE-04 senders will resolve the mediator DID and prepend its keys — no explicit `routingKeys` needed for the one-mediator case). Also `.WithRoutingKeys(...)` for the two-mediator case.
3. **`PickupClient`**: `GetStatusAsync(mediatorDid, from, recipientDid?)`, `FetchAsync(mediatorDid, from, limit, recipientDid?) → IReadOnlyList<(id, UnpackResult)>` — sends `delivery-request`, unpacks each attachment with the recipient's client, and **acks only the ones that unpacked** (a failed unpack is reported, left on the queue, and surfaced to the caller — never silently acked); `AckAsync(ids)`; `SetLiveDeliveryAsync(bool)` (WS only; throws `InvalidOperationException` over HTTP before sending, mirroring the mediator's problem report); an `IAsyncEnumerable<UnpackResult> ReceiveLiveAsync(ct)` that acks each item after the consumer's `MoveNextAsync` returns.
4. **Threading/correlation:** responses correlate on `thid` = request id (#93/#94 decision) with a fallback on message type + mediator DID for peers that omit `thid` (Identus/Blocktrust may); bounded pending-map with timeout.
5. **Errors:** `mediate-deny` → `MediationDenied` result (not exception); problem reports in-thread (`e.m.live-mode-not-supported`) → `MediationProtocolException` with the code; transport failures propagate as `TransportException`.

### Acceptance

- Against the #95 TestServer: full enroll → DID doc → Alice send → fetch → ack over HTTP; same over WS with live mode; deny path; partial-unpack path leaves the bad message queued and returns the good ones; correlation timeout surfaces as `TimeoutException` with the request id (not the DID).
- Double-packaged (rewrapped) delivery unwraps at Bob.
- CM 2.0 peer (fixture-driven fake handler, until #99 and the live gate) with string `routing_did` and `keylist-*` names: the client either speaks 2.0 when the mediator discloses only 2.0 (`discover-features` first) or fails with a clear error — pick "negotiate via discover-features, prefer 3.0" and test both.
- DX coverage: every public member exercised by sample 11 (#97) — until then, allowlist with a `TODO(#97)` marker.
- CHANGELOG; PRD FR-MED/FR-PICK recipient-role cells.
<!-- /issue -->

<!-- issue:SAMPLE -->
## Sample 11 `MediatedAgent` (enroll → send → pickup HTTP → live WS), refresh sample 04, cookbook sections, docs

**Labels:** documentation, enhancement, mediator

Part of #88. Blocked by #95, #96. Also closes the DX-coverage allowlist `TODO(#97)` markers.

### Current state

- `samples/04-MediatorAgent` demonstrates routing relay with a hard-coded delivery dictionary (`Program.cs:72`, `:217`) and the SSRF guard; smoke test `tests/DidComm.InteropTests/Samples/MediatorAgentSmokeTests.cs` asserts on the transcript. Cookbook section O (`samples/02-Cookbook/Sections/Section_O_RoutingViaMediator.cs`) covers forward wrapping.
- PRD §14.3 row 04 (`docs/didcomm-dotnet_PRD.md:1042`); cookbook tasks run A–BB; DX gate `tests/DidComm.InteropTests/DxCoverage/` requires every public member of shipped assemblies to be referenced by a sample.
- Sample comments must be consumer-facing plain language with PRD IDs only as parenthetical refs (memory `[[feedback_sample_comments]]`).

### Scope

1. **`samples/11-MediatedAgent`** (narrated, `RunAsync(TextWriter?)` seam like 04): (1) start the mediator with `AddMediator()` + `MapDidCommMediator("/")`, mint its `did:peer:2` with `DidCommServiceBuilder` pointing at its own URL; (2) Bob discovers the mediator from `/oob`, `RequestMediationAsync`, gets `routing_did`, registers his DID, mints his DID doc with `ForMediation(grant)`; (3) Alice sends to Bob with `Forward: true` — show the resolved route (mediator-as-DID, FR-ROUTE-04); (4) Bob `GetStatusAsync` → 1 message, `FetchAsync` over HTTP → unpack → ack → status 0; (5) Bob opens a WS, `SetLiveDeliveryAsync(true)`, Alice sends again, Bob receives the push, ack; (6) teardown; (7) show a `mediate-deny` via a policy that allowlists DIDs. Smoke test asserting each section's transcript markers.
2. **Sample 04** becomes "routing relay only": use `AddRoutingMediator()` + the `UnpackResult` overload (#90), `DirectPush` + `IRecipientEndpointDirectory` to keep its Alice→Mediator→Bob-inbox story; drop the hand-rolled relay. Update its README and the smoke test.
3. **Cookbook** sections: `CC` "Enroll with a mediator", `DD` "Pick up queued messages", `EE` "Live delivery over WebSocket", `FF` "Run a minimal mediator in 20 lines"; `02-Cookbook/Program.cs` menu and `CookbookSmokeTests.cs`.
4. **Docs:** PRD §14.3 rows 04/11 and §14.4 matrix; `README.md` one line in the package table for `DidComm.Mediator` (thin router rule — no runbook); `docs/codebase-architecture.md` mediator pipeline section if #89 chose to create that file; package `README.md` for `DidComm.Mediator` (packed by `Directory.Build.targets`).
5. **DX gate:** remove the `TODO(#97)` allowlist entries; `PublicApiCoverageTests` green with `DidComm.Mediator` in `CoverageComputation.ShippedAssemblies`.

### Acceptance

- `dotnet run --project samples/11-MediatedAgent` exits 0 on a clean machine with no network (loopback only, dynamic ports, no fixed timing — FR-DX-02).
- Smoke tests for 04 and 11 green on ubuntu and windows CI.
- DX coverage gate green without allowlist entries for Mediator types.
- CHANGELOG "Samples" entry.
<!-- /issue -->

<!-- issue:GATE -->
## Gate: live interop against an external DIDComm v2 mediator + adversarial security review of the mediator service

**Labels:** enhancement, security, mediator

Part of #88. Blocked by #97 (and #99 for the external-mediator cells, since the available mediators speak CM 2.0). The epic is not done until this is green.

### Why a separate gate

Self-round-trip tests do not prove interop (`tasks/lessons.md` L-005); every protocol here was first implemented by someone else, and the mediator exposes new multi-tenant state to unauthenticated senders.

### Interop targets

| Target | Protocols | How to run |
|---|---|---|
| Hyperledger Identus mediator (Scala) | CM 2.0, pickup 3.0, trust-ping, report-problem | Docker image from the repo; pin a tag + digest |
| Blocktrust mediator (C#) | routing 2.0, CM 2.0, pickup 3.0, discover-features 2.0, return-route; hosted `https://mediator.blocktrust.dev` (OOB at `/oob_url`) | hosted (on-demand only, never in nightly), or build from `bsandmann/blocktrust.Mediator` |
| Affinidi mediator (Rust) | "DIDComm v2.1" — verify versions first | Docker, if versions match |

### Scope

1. **Harness** (`tools/interop-live/mediator/`): a new leg `run-mediator-leg.sh` that (a) starts the target mediator in Docker, (b) uses the didcomm-dotnet client (#96) as the **recipient**: discover-features → enroll (2.0 via #99) → register → build DID doc → have didcomm-python (the existing leg's peer) send a forward through the external mediator → fetch over HTTP → ack → status 0; then WS live mode if the target supports it; (c) the reverse: the external mediator's *client* is not available as a library, so the reverse cell uses didcomm-python as the **sender** through the didcomm-dotnet mediator (#95 acceptance already covers it) and a hand-rolled 2.0 `mediate-request` from the python peer. Cells added to `summary.md`, `interop-live.yml` runs the leg nightly with `continue-on-error: false` only for targets pinned by digest.
2. **Adversarial review** (agents, written report in `tasks/` and a GitHub comment on this issue), at minimum:
   - **Registration squatting / takeover:** register a DID another tenant owns; remove another tenant's DID; re-grant after revoke; grant for an anoncrypt request; `recipient_did` as DID URL vs bare DID (normalization must be exact, no fragment stripping that merges two keys of one DID into one tenant unexpectedly).
   - **Oracles:** unknown vs known `next` timing and status; quota-full vs ok; expired vs valid; return-route 200/202 is by design (authenticated) — confirm it leaks nothing about *other* tenants.
   - **Resource exhaustion:** grants, recipients per grant, queue bytes/messages, sessions per DID, WS frame floods, `delivery-request` with huge `limit`, `messages-received` with 10⁶ ids, attachments at `max_receive_bytes`, slow-loris on a held HTTP return-route response (confirm Kestrel timeouts apply).
   - **Session confusion:** push to the wrong socket after reconnect; two sockets for one DID; session reuse after `return_route: none`; forward with `return_route: all` (must be ignored).
   - **Wire-identifier caps (FR-API-09)** on `recipient_did`, `routing_did`, attachment ids, `message_id_list` entries — no echo in error text, rejection before any resolver call.
   - **Rewrap correctness:** double-packaged payload tamper (swap inner attachment between two forwards) is detected at Bob, not relayed as valid.
   - **Both post-sign tampering and self-consistent forged documents** whose identity fields disagree (CLAUDE.md §4).
3. **Fix-forward:** findings are filed as issues with the existing `security`/`severity:*` labels and fixed before the epic closes; `SECURITY.md` gets a mediator threat-model paragraph.

### Acceptance

- At least one external-mediator leg green in nightly CI with a pinned image digest; results table in `tools/interop-live/README.md`.
- Adversarial report posted; every High/Medium finding closed; Low/Info triaged with issue links.
- `tasks/lessons.md` updated with anything that surprised us.
<!-- /issue -->

<!-- issue:CM2 -->
## Compat: Coordinate Mediation 2.0 handler (`keylist-*`, string `routing_did`) for interop with Identus and Blocktrust mediators

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #93. Low priority for the library's own semantics, **high priority for the interop gate** (#98): the external mediators that exist today speak 2.0, not 3.0.

### Spec differences (https://didcomm.org/coordinate-mediation/2.0/)

- PIURI `https://didcomm.org/coordinate-mediation/2.0`.
- `mediate-grant.body.routing_did` is a **string**.
- `keylist-update` / `keylist-update-response` / `keylist-query` / `keylist` replace `recipient-update` / `recipient-update-response` / `recipient-query` / `recipient`.
- `keylist.body.keys` (not `dids`); inner fields identical (`recipient_did`, `action`, `result`).
- 2.0 `mediate-request` example carries `return_route: "all"` on the request itself.

### Scope

- `CoordinateMediation2Handler : IProtocolHandler` sharing the 3.0 store/policy (one registry: a DID registered via 2.0 is the same tenant). `ProtocolHandlerRegistry` already keys by PIURI and resolves by major version (`ProtocolHandlerRegistry.cs:44-74`), so 2.0 and 3.0 coexist.
- Parsers/creators for the 2.0 names; map to the same internal records.
- `MediationClient` (#96) negotiates: disclose-first, prefer 3.0, fall back to 2.0; `MediationClientOptions.PreferredVersion`.
- `AddCoordinateMediation(o => o.EnableV2 = true)` — default **true** (interop first, memory `[[feedback_spec_over_local_rules]]`).

### Acceptance

- Fixtures from the 2.0 page round-trip; a 2.0 `keylist-update` and a 3.0 `recipient-update` from the same peer mutate one tenant; discover-features discloses both PIURIs; #98 cells against Identus/Blocktrust use this path.
<!-- /issue -->

<!-- issue:PK4 -->
## Follow-up: Message Pickup 4.0 (`message-pickup/4.0`) — live-mode framing, `message_count_limit`, millisecond times

**Labels:** enhancement, spec-compliance, mediator

Part of #88. Blocked by #94. Can ship after the epic's first release.

### Differences vs 3.0 (https://didcomm.org/message-pickup/4.0/, Production)

- PIURI **`https://didcomm.org/message-pickup/4.0`** (renamed protocol; both can be registered).
- Live mode: messages "**MUST** be delivered via a `delivery` message and the `recipient` **MUST** send … `messages-received`. If a message is not acknowledged, the message **MUST** be added to the queue" — #94 already behaves this way under 3.0; 4.0 makes it normative.
- Empty `delivery-request` → "a `delivery` message with an empty attachments array **MUST** be sent immediately" (3.0: `status`).
- `status` gains `thid` (= `status-request` id); `delivery.thid` optional (absent for live deliveries); `messages-received` no longer carries `return_route: all`.
- `longest_waited_seconds` removed; `limit` → `message_count_limit`; "number of attached messages **MUST NOT** exceed the `message_count_limit`"; times in UTC epoch **milliseconds**.
- Problem report type `https://didcomm.org/message-pickup/4.0/problem-report`, same code `e.m.live-mode-not-supported`.
- Adds a DIDComm v1 form (out of scope: v1 is not implemented).

### Scope

- `MessagePickup4Handler` sharing `IMessageStore`/`LiveDeliveryCoordinator`; version-specific message shapes behind one internal model (`PickupVersion` enum on the session: a recipient's version is whatever it last spoke).
- `PickupClient.Version` option, disclose-first negotiation preferring 4.0.
- Fixtures from the 4.0 page; both versions in the discover-features disclosure.

### Acceptance

- Byte-exact fixtures; empty-queue → empty `delivery` (4.0) vs `status` (3.0) on the same store; a 3.0 and a 4.0 recipient for different grants interleave on one mediator without cross-talk; PRD FR-PICK rows for 4.0; CHANGELOG.
<!-- /issue -->

## Review

- Opened 2026-11-10: epic #88; issues #89 (PRD), #90 (routing core), #91 (return-route
  server), #92 (return-route client), #93 (CM 3.0), #94 (pickup 3.0), #95 (pipeline),
  #96 (recipient client), #97 (sample 11), #98 (interop + security gate), #99 (CM 2.0
  compat), #100 (pickup 4.0). All carry the `mediator` label.
- Research basis: spec source files from `decentralized-identity/didcomm-messaging` (the
  rendered v2.1 site returned 403), the `return_route` extension (DRAFT, unversioned — no
  `didcomm.org/return-route/2.0` exists), didcomm.org pages for CM 2.0/3.0 and pickup
  3.0/4.0, plus a file:line audit of the codebase.
- External interop reality: Identus and Blocktrust mediators speak CM **2.0** + pickup 3.0,
  which is why #99 is a hard prerequisite for #98 despite being "compat".
- Not filed (mentioned to the user instead): the `AGENTS.md` drift (wrong PRD filename,
  missing `codebase-architecture.md`) is folded into #89 rather than a separate issue.
- Nothing committed; this file is untracked on `main` until the user decides.
