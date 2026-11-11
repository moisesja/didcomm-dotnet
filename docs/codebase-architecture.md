# Codebase Architecture

This document is the map of what is actually built in didcomm-dotnet: which packages exist, what
lives where, and how a message moves through the code on the way in and on the way out. The target
the implementation is measured against is the PRD, [`didcomm-dotnet_PRD.md`](didcomm-dotnet_PRD.md);
when the two disagree, the PRD is the target and this file is the stale one to fix. Everything
listed here names a folder or type that exists in `src/`, `tests/`, `tools/` or `samples/` today.

## Packages

| Package (`src/`) | What it holds |
| --- | --- |
| `DidComm.Core` | The library proper. Folders: `Messages` (plaintext model), `Jose` and `Crypto` (JWS/JWE primitives), `Composition` (envelope reader/writer, `ForwardWrapper`), `Consistency`, `Facade` (`DidCommClient`, `DidCommOptions`, `PackEncryptedOptions`, `UnpackResult`), `Resolution` (`IServiceEndpointResolver` and DID key services), `Secrets`, `Threading`, `Profiles`, `Diagnostics`, `Exceptions`, `Json`, `Protocols`, `Transports`. |
| `DidComm.Core/Protocols` | `ProtocolDispatcher`, `ProtocolHandlerRegistry`, `IProtocolHandler`, `IProtocolHandlerRoles`, `IProtocolObserver`, `ObserverDelivery`, `IInboundCorrelator`, `ProtocolContext`, `MessageTypeUri`; built-in protocols under `DiscoverFeatures`, `Empty`, `OutOfBand`, `ProblemReport`, `Rotation` (`from_prior`), `Routing` (`ForwardMessage`, `ForwardProcessor` / `ForwardProcessorOptions` / `ForwardProcessingResult`, `ForwardHandler`, `IForwardSink` / `NullForwardSink`, `IForwardRecipientRegistry` / `InMemoryForwardRecipientRegistry`, `IRecipientRoutingKeyProvider` / `StaticRecipientRoutingKeyProvider`), `Trace`, `TrustPing`. |
| `DidComm.Core/Transports` | `IDidCommTransport`, `ITransportRouter` / `TransportRouter`, `TransportRequest` / `TransportResult`, `SendOptions` / `SendResult`, `OutboundEndpointGuard` + `OutboundEndpointPolicy`, `Stomp` (`StompFrame`, `StompFrameCodec`), and `ReturnRoute` — public: `ReturnRouteMode`, `ReturnRouteHeader`, `ReturnRouteTransport`, `IReturnRouteSession`, `IReturnRouteSessionRegistry` / `InMemoryReturnRouteSessionRegistry`, `SessionRegistration` / `SessionRegistrationOutcome`, `ReturnRouteSessionEventArgs` / `SessionCloseReason`, `ReturnRouteSessionOptions`, `IInboundReplayGuard` / `InMemoryReplayGuard`, `ReplayAdmission`, `ReplayProtectionOptions`, `MediatorServiceProtocols`, `QueueTransport`; internal: `ReturnRouteEligibility`, `ReturnRouteAddressing`. |
| `DidComm.Adapters.NetDid` | `NetDidKeyStoreSecretsResolver`: optional bridge from a NetDid key store to `ISecretsResolver`. |
| `DidComm.AspNetCore` | Minimal-API receive side: `DidCommEndpointRouteBuilderExtensions` (`MapDidCommEndpoint` — callback, registry-aware and session-aware callback overloads — `MapDidCommWebSocket`, `MapDidCommOobEndpoint`), `DidCommReceiveOptions`, `DidCommMediaTypes`, `StompServerSession`; internal return-route receive side: `InboundAdmission` (with `InboundAdmissionEndpoint`, `InboundAdmissionDefaults`, `SessionOutbound`, `SafeLog`), `HttpReturnRouteSession`, `WebSocketReturnRouteSession`. |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` (`DidCommServiceCollectionExtensions`; also registers the return-route session registry and replay guard) and `DidCommBuilder` (`UseNetDidResolver`, `UseSecretsResolver`, `AddBuiltInProtocols`, `AddRoutingMediator`, ...); internal `DependencyCycleGuard`. |
| `DidComm.Transports.Http` | `HttpDidCommTransport` (HTTPS sender), `HttpTransportOptions`, `HttpResiliencePipelineFactory`, `UseHttpTransport()`. |
| `DidComm.Transports.WebSocket` | `WebSocketDidCommTransport` (WebSocket sender), `WebSocketTransportOptions`, `WebSocketLifecycleEventArgs`, `UseWebSocketTransport()`. |

## Tests and tools

- `tests/DidComm.Core.Tests`: unit tests, organised by the same folder names as `DidComm.Core`
  (Envelopes, Facade, Protocols, Resolution, Rotation, Secrets, Threading, Transports, ...).
- `tests/DidComm.InteropTests`: fixture-driven conformance (`fixtures/` with `manifest`, `packed`,
  `payloads`, `diddocs`, `secrets`, `schema`, `spec`; `FixtureCatalog`, `Runner/FixtureDispatcher`),
  plus the `AspNetCore`, `Routing`, `Transports`, `Protocols` and `Facade` suites, `DxCoverage`
  (public-API coverage and quickstart drift checks) and `Samples` (one smoke test per sample).
- `tests/DidComm.TestSupport`: shared helpers (`InMemorySecretsResolver`, `MultiRecipientPacker`,
  `EnvelopeReaderTestRunner`). Not shipped as a package.
- `tools/FixtureGen`: generates didcomm-dotnet's own packed vectors into the interop fixtures
  tree.
- `tools/InteropCli`: command-line peer used by the live interop runs.
- `tools/interop-live`: cross-implementation legs (`python/`, `jvm/`) driven by `run-all.sh`.
- `samples/01-Quickstart` through `samples/10-ProfilesAndI18n`, with `samples/_shared` (`Narrator`,
  `PeerIdentityFactory`) as the common helper project.

## Inbound flow

1. A packed envelope arrives at `MapDidCommEndpoint` (HTTP POST) or `MapDidCommWebSocket` (one
   message per binary frame, optional STOMP session via `StompServerSession`).
2. The endpoint checks the content type (415 on mismatch) and caps the body at
   `DidCommOptions.MaxReceiveBytes` (413 on HTTP, close code 1009 on WebSocket).
3. `DidCommClient.UnpackAsync` opens the envelope; every unpack failure collapses to one opaque
   rejection so a peer cannot distinguish the cause.
4. Return-route admission (the registry-aware endpoints and the session-aware HTTP callback):
   `InboundAdmission.Evaluate` reads `return_route`, decides FR-RR-03 session eligibility and runs
   the FR-RR-08 replay guard for in-scope messages. A refused message is dropped — HTTP `202`,
   nothing on a WebSocket — with no dispatch and no session. Otherwise the endpoint opens the
   session (HTTP: request-scoped; WebSocket with `AllowSameSocketReplies`: registered) or updates
   the connection's existing one. See "Return-route receive side" below.
5. With the dispatcher overloads, `ProtocolDispatcher.DispatchAsync` routes the `UnpackResult` by
   message type to the `IProtocolHandler` registered for it, with `ProtocolContext.Session` (the
   session, or `null`) and `ProtocolContext.ReturnRoute` (this message's parsed header); a handler
   may return a reply or push through the session.
6. A reply with a session becomes the HTTP `200` body or a WebSocket push; without one it is
   logged, not sent (a reply to anyone else travels out of band, FR-TRN-10).
7. `IProtocolObserver` instances get a read-only, best-effort copy off the reply path for every
   outcome (`ObserverDelivery` runs the bounded background pumps). The legacy `onReceive`-callback
   overloads skip steps 4–6 and hand the `UnpackResult` straight to the application; the
   session-aware HTTP callback gets each admitted message with its session and returns the reply.

## Outbound flow

1. The application calls `DidCommClient.SendAsync(message, SendOptions)`.
2. The facade packs via `PackEncryptedAsync`; unless the caller overrides the service endpoint it
   packs with `Forward = true`, so `IServiceEndpointResolver` resolves the recipient's
   DIDCommMessaging service and `ForwardWrapper` applies the Routing 2.0 forward layers.
3. The resulting endpoint URI is validated by `OutboundEndpointGuard` against
   `DidCommOptions.OutboundEndpointPolicy` before any network activity.
4. `ITransportRouter.SendAsync` picks the `IDidCommTransport` whose `Scheme` matches the endpoint
   (`HttpDidCommTransport` or `WebSocketDidCommTransport`).
5. The transport delivers one packed envelope and returns a `TransportResult`, surfaced to the
   caller as `SendResult`.

## Mediator routing core (Phase 7, #90)

`DidComm.Core/Protocols/Routing` holds the mediator-side core: `forward` is an ordinary registered
protocol handler, and everything that depends on who the recipient is runs detached from the
request. There is no `DidComm.Mediator` package yet (see "Phase 7: not built yet").

Wiring: `DidCommBuilder.AddRoutingMediator(ForwardProcessorOptions?)`, not part of
`AddBuiltInProtocols`. It validates the options, `TryAdd`s `ForwardProcessorOptions`,
`IRecipientRoutingKeyProvider` (`StaticRecipientRoutingKeyProvider` over
`ExtraRecipientRoutingKeys`), `InMemoryForwardRecipientRegistry` as `IForwardRecipientRegistry`,
`NullForwardSink` as `IForwardSink` and the `ForwardProcessor` singleton, then
`AddProtocol<ForwardHandler>()`. Every registration is `TryAdd`, so a host — or `DidComm.Mediator`
(#95) — replaces the sink, the registry or the provider by registering its own before the call.
Once registered, Discover Features discloses `routing/2.0` with `roles: ["mediator"]`:
`ProtocolFeatureProvider` copies `IProtocolHandlerRoles.Roles` onto `FeatureDisclosure.Roles`.

Inbound forward:

1. The envelope reaches `MapDidCommEndpoint` / `MapDidCommWebSocket` and `DidCommClient.UnpackAsync`
   opens it (steps 1–3 of the inbound flow). A JWE the mediator cannot decrypt fails here with the
   uniform rejection, before anything knows it was a forward. Return-route admission (step 4)
   lets a forward through untouched: Routing 2.0 is replay-exempt and never gets a session, even
   when it carries `return_route` (FR-RR-02).
2. `ProtocolDispatcher.DispatchAsync` routes `https://didcomm.org/routing/2.0/forward` to
   `ForwardHandler.HandleAsync`.
3. Inline, on the request, the handler screens: `ForwardMessage.TryParse` (missing `next` or
   attachments, or a `next` over the FR-API-09 cap); posture — `UnpackResult.Encrypted` and a
   non-empty `RecipientKid`; relay-time expiry of the forward's own `expires_time` on
   `DidCommOptions.Clock` with `ExpiresClockSkew`; `DidSubjectOf(next)` and
   `IForwardRecipientRegistry.IsRegisteredAsync`. Every failure is a drop: a Warning carrying the
   message id only, a `null` reply, no problem report. `please_ack` is never read.
4. An admitted forward reserves its place on a bounded channel inline (`DefaultCapacity` 1024
   outstanding and `DefaultByteBudget` 64 MiB of their plaintext UTF-8 bytes, the exact size the
   unpack path recorded in `InboundMessageSnapshot`; the newest is dropped beyond either bound,
   decided on the request) and the handler returns `null`. The item is published to the pump only
   once `ProtocolContext.Acknowledged` completes: the registry-aware `MapDidCommEndpoint` creates
   it per request, completes it from `Response.OnCompleted` (the 202 is committed) and cancels it
   on `RequestAborted` or a post-handler 400, passing it through the
   `ProtocolDispatcher.DispatchAsync(…, Task acknowledged, …)` overload — so neither the processor
   nor the sink runs before the sender holds the 202. The WebSocket endpoint and a direct
   dispatcher caller pass `Task.CompletedTask` and publish immediately. A cancelled or faulted
   acknowledgement releases the reservation and drops the forward with a warning (id only). The
   clock was read once, at step 3: the same instant fixes the `delay_milli` schedule
   (`ExtractDelay` → `NotBefore`) carried in the queued item. The item is the dispatcher's
   `ProtocolContext` plus that schedule; the request token is not stored.
5. The pump calls the internal `ForwardProcessor.ProcessAsync(UnpackResult, delay, notBefore)`
   overload with the schedule fixed at admission (the public overloads resolve it when called):
   parse, posture, attachment count, attachment decode, then pass-through (verbatim, or one extra wrap per key the
   `IRecipientRoutingKeyProvider` returns for the DID subject of `next`) or rewrap
   (`RewrapMode.ReanoncryptToNext`: a fresh forward anoncrypted to every keyAgreement key of the
   next hop; the provider is not consulted). The result carries `NextHop`, `OnwardPacked`,
   `ExpiresTime`, `Delay` and `NotBefore` (receipt time plus `delay_milli`, for a queue to store;
   the handler never sleeps on it).
6. Expiry is re-checked with `ForwardProcessingResult.IsExpired` for the time spent queued, then
   `IForwardSink.AcceptAsync(result, context, shutdownToken)` runs. A processor or sink fault is
   logged with the message id and the exception type name(s) only — the exception object never
   reaches the logger, because a `DidResolutionException` or a sink fault can carry the next-hop
   DID in its message — and swallowed.

Shutdown: `ForwardHandler` is `IDisposable` / `IAsyncDisposable` and the container disposes it with
the provider. Dispose stops admission, cancels the pump, discards queued forwards and unblocks the
forwards still waiting for their acknowledgement (each releases its reservation and counts as
discarded; a later acknowledgement publishes nothing) — fail closed; `DisposeAsync` additionally
waits for the pump and those pending forwards, up to 5 s. `FlushAsync` (test seam) waits for
pending acknowledgements within its timeout before fencing the pump.

`ForwardProcessor.ProcessAsync(string)` stays as a convenience: it unpacks with the mediator's
client and follows the same path, so a plaintext or signed-only forward is refused with
`ConsistencyException`. `samples/04-MediatorAgent` still drives the processor from the `onReceive`
callback, handing it the endpoint's `UnpackResult`; the handler-based version of the sample is #97.

Rule for sinks (lesson L-017): an `IForwardSink` must not take `ProtocolHandlerRegistry` or
`ProtocolDispatcher` in its constructor — the registry factory builds `ForwardHandler`, which
takes the sink — so a sink that needs either injects `IServiceProvider` and resolves at call time.
`AddRoutingMediator` enforces this for the sink, the registry and the routing-key provider when
they are registered by implementation type: a public constructor naming the registry, the
dispatcher or a protocol handler throws `InvalidOperationException` at registration. A cycle it
cannot inspect — a factory registration, or one closed through a further dependency — fails the
first resolution instead of hanging it: the `ProtocolHandlerRegistry`, `ProtocolDispatcher` and
`AddProtocol<T>()` handler factories are wrapped by `DependencyCycleGuard`
(`DidComm.Extensions.DependencyInjection`), which throws `InvalidOperationException` naming the
service when its factory is re-entered on the resolving thread for the same provider (the
container cannot see inside a factory and would otherwise deadlock). The same rule binds a
host-registered `IReturnRouteSessionRegistry` or `IInboundReplayGuard` that a handler depends on.

## Return-route receive side (Phase 7, #91)

A peer that authcrypts a message carrying `return_route: all` can receive messages back on the
connection it used (PRD §8a): as the HTTP `200` body (FR-RR-04, no switch) or as pushes on its
WebSocket (FR-RR-05, only with `DidCommReceiveOptions.AllowSameSocketReplies`, default off, and
never under STOMP). `DidComm.Core/Transports/ReturnRoute` holds the header model, the session
contract and the two bounded stores; `DidComm.AspNetCore` holds the admission pipeline and the two
session types. The client side — `SendOptions.ReturnRoute`, `SendResult.Returned`, the WebSocket
inbound loop and `created_time` stamping — is #92.

Wiring: `AddDidComm` registers `IReturnRouteSessionRegistry` → `InMemoryReturnRouteSessionRegistry`
and `IInboundReplayGuard` → `InMemoryReplayGuard` as singletons with `TryAdd`, plus
`ReturnRouteSessionOptions` and `ReplayProtectionOptions` (configure with
`services.AddOptions<T>().Configure(...)`; both stores validate them at construction). Neither
store depends on the handler registry, the dispatcher, the facade or a handler, so handlers may
inject them. When a host maps the registry-aware endpoints without registering them,
`InboundAdmissionDefaults` builds one registry and one guard per application — keyed weakly by
`IEndpointRouteBuilder.ServiceProvider` and shared by every registry-aware endpoint of that
application, so a request admitted over HTTP is a duplicate on the WebSocket endpoint — and logs
that once at Information. Each `Map*` call's `InboundAdmissionEndpoint` keeps only the log rate
limiters. The legacy callback overloads (`MapDidCommEndpoint(pattern, Func<UnpackResult,
CancellationToken, Task>)`, `MapDidCommWebSocket(pattern, Func<…>)`) run none of this.

Admission, per inbound after a successful unpack (`InboundAdmission.Evaluate`, shared by the
registry-aware `MapDidCommEndpoint` / `MapDidCommWebSocket` and the session-aware callback
`MapDidCommEndpoint(pattern, Func<UnpackResult, IReturnRouteSession?, CancellationToken,
Task<Message?>>)`):

1. **Header.** `ReturnRouteHeader.TryRead`: absent, `none` or malformed → `None`; `all` → `All`;
   `thread` with a valid sibling `return_route_thread` (1–2048 RFC 3986 unreserved characters) →
   `Thread`. A malformed value is logged by message id only, rate-limited (the first and every
   1000th at Warning). `HeaderPresent` records whether the member exists at all (the WebSocket
   session changes mode only for such a message).
2. **Identity.** `ReturnRouteEligibility.TryGetPeer` (internal, Core): `Encrypted &&
   Authenticated`; a non-empty `SenderKid` whose DID subject parses (a verified JWS also sets
   `Authenticated`, so the key id is the test for an authcrypt layer); a `SignerKid`, if present,
   naming the same subject; a `RecipientKid` that is a DID; not a Routing 2.0 message. Success gives
   `PeerDid` (sender subject) and `LocalDid` (decrypting subject); `SessionEligible` additionally
   needs mode `All` and admission.
3. **Replay scope.** In scope when the message has an authcrypt layer (`Authenticated` and a
   non-empty `SenderKid`), is not Routing 2.0 (`MediatorServiceProtocols.IsReplayExempt`), and
   either carries a mode other than `None` or has a mediator-service type
   (`MediatorServiceProtocols.IsMediatorServiceType`: Coordinate Mediation 2.0 / 3.0, Message
   Pickup 3.0 / 4.0, any minor). Plaintext, anoncrypt and signed-only messages pass through to
   dispatch; the mediator handlers (#93, #94, not built yet) are to refuse unauthenticated requests
   themselves (FR-MED-05, FR-PICK-01).
4. **Replay admission.** In scope → `IInboundReplayGuard.TryAdmit(DidSubjectOf(SenderKid), id,
   created_time, DidCommOptions.Now())`. Anything but `Admitted` — or a sender key id with no DID
   subject, or a guard that throws — is not admitted: nothing is dispatched, no session is opened
   or changed, HTTP answers `202` with an empty body and the WebSocket writes nothing. The drop is
   logged with the id and the outcome (rate-limited per outcome).
5. **Session.** HTTP: `SessionEligible` → a new `HttpReturnRouteSession`. WebSocket: below.
6. **Dispatch.** `ProtocolDispatcher.DispatchAsync(…, acknowledged, session, ct)` sets
   `ProtocolContext.Session` and re-reads the header into `ProtocolContext.ReturnRoute`. The
   dispatcher admits nothing itself — admission is a one-shot check-and-insert, so a second run
   would refuse its own message — and a transport of its own (#92's inbound loop, #95) must run
   steps 1–4 before dispatching.
7. **Reply.** A handler reply goes through `session.TrySendAsync`; without a session it is logged,
   not sent.

Outbound half, shared by both session types (`SessionOutbound`): snapshot the message (serialize →
deserialize, so a caller that keeps mutating it cannot race the check), apply
`ReturnRouteAddressing.Check` — `from` the same DID subject as `LocalDid`, `to` exactly one entry
with the subject of `PeerDid`; refusals are enum values (`NoFrom`, `FromNotLocalDid`,
`ToNotExactlyPeer`) so no log line carries a DID — then `PackEncryptedAsync(snapshot, Recipients:
[PeerDid], From: LocalDid, Forward: false)`.

HTTP session (`HttpReturnRouteSession`; request-scoped and never registered, so it raises no
registry events and does not count against the bounds):

- Created for each admitted, eligible request: `Transport = Http`, `IsLive = false`, `Mode = All`
  until it closes, `Thread = null`.
- One slot. A sender claims it under the lock before packing, so concurrent senders pack at most
  once; a failed or cancelled pack releases it; a successful pack fills it. Every later
  `TrySendAsync` returns `false`: a handler that pushed and also returned a reply sends the push.
- The endpoint's last step, `TakeResponseAndClose()`, closes the session, cancels `Closed` and
  takes the packed bytes in one transition; a pack that completes afterwards returns `false` and
  is never sent.
- Response: bytes → `200` with `Content-Type: application/didcomm-encrypted+json`; none → `202`.
  The abort path and the post-handler uniform `400` also close the session and discard the slot.
  `ProtocolContext.Acknowledged` completes once the `200` or `202` is committed; a forward never
  has a session, so `ForwardHandler` always waits for a `202`.

WebSocket session (`WebSocketReturnRouteSession`; at most one per connection):

- Opened only when `AllowSameSocketReplies` is on, STOMP is off and the message is
  `SessionEligible`. `InboundAdmission.TryOpen` registers it — `RefusedTotalBound` or a throwing
  registry means the message is dispatched without a session — and `CloseEvicted` closes whatever
  the per-peer bound evicted, right after `Register` returns (no subscriber runs inside it); the
  writer task starts once registered. Under STOMP no session ever opens; one Information line per
  connection, at its first eligible message, says so.
- The socket's (peer, local) pair is fixed by its first eligible message, even when the registry
  refused that session; only that pair may retry. The pair comparison always runs both ordinal
  compares (no early exit on an identity check).
- A later message from the same pair with `HeaderPresent` → `ApplyHeader`: `all` → `Mode = All`,
  thread cleared; `thread` → `Mode = None`, thread surfaced on `Session.Thread` (JC-20); `none` or
  malformed → `Mode = None`. No header → mode unchanged. A message from any other pair is
  dispatched with `Session = null` and never opens, re-binds or changes the session; a message
  that is not identity-eligible never touches it.
- `TrySendAsync`: `false` without packing unless `Mode == All`; then it reserves one of
  `MaxPendingPushes` push slots (a lock-free decrement that never goes below zero) — `false` at
  once when none is free, before any snapshot, key resolution or encryption; then snapshot, check,
  pack and `TryWrite` to a bounded channel of the same depth (one reader, a second limit). Every
  path that queues nothing gives the slot back (`try`/`finally`: refused addressing, failed
  snapshot or pack, a downgrade or close during the pack, a refused write, cancellation); a queued
  frame keeps it until the writer dequeues the frame, sent or discarded. So pushes reserved plus
  frames queued never exceed `MaxPendingPushes`; the frame inside the socket send is not counted.
  It never blocks the handler. The writer task is the only code that sends on or close-outputs the
  socket, and its "may I send?" check and the start of `SendAsync` share a lock with `ApplyHeader`
  and `RequestClose`, so nothing starts after a downgrade or a close was applied (a send already
  in flight completes).
- `RequestClose(reason)` (idempotent; the first reason wins): `Mode = None`, channel completed,
  queued pushes discarded, `Closed` cancelled (its callbacks run off the caller's thread). For
  `Evicted` it also arms the backstop — the connection's token source cancels after
  `DefaultCloseGrace` (5 s) — and the writer, after any in-flight send, sends
  `CloseOutputAsync(NormalClosure, "return-route session evicted")`.
- The receive loop keeps reading in `Open` and `CloseSent` — an evicted connection dispatches,
  without a session, what the peer sent before it saw the close — until the peer's close frame or
  the backstop. A closed session is never reopened on that socket.
- Loop exit (`finally`): `Unregister` (its `SessionClosed(Unregistered)` is raised on the thread
  pool, so no subscriber delays this path; a no-op after an eviction), then `StopAsync` — waits up
  to the grace for the writer, then cancels a send stuck on a peer that stopped reading and waits
  once more; a writer that still has not stopped means the socket is aborted rather than closed —
  then the deferred close (`client closed`, or `1009` after an oversized message), itself bounded
  by the grace and aborted on timeout. A close never overlaps a send.

Registry (`InMemoryReturnRouteSessionRegistry`):

- One lock over `Dictionary<peer subject, List<session>>` (registration order = age) and a
  reference-equality `HashSet`.
- `Register`: `PeerDid` must be a bare DID subject (`ArgumentException` that does not echo it); an
  instance already present → `AlreadyRegistered`; a peer at `MaxSessionsPerPeer` (default 8) →
  its oldest is removed (`list[0]`, O(1)) and the new one registered — checked before the total,
  because replacing a peer's own session does not grow it, so a reconnecting peer is not locked
  out by a flood of other DIDs; otherwise the total at `MaxSessions` (default 4096) →
  `RefusedTotalBound`, nothing evicted; otherwise added.
- Events never run on the caller's thread. After the state change and outside the lock, a
  `Register` / `Unregister` that changed something queues one work item
  (`ThreadPool.UnsafeQueueUserWorkItem`, so no request `ExecutionContext` flows into subscribers;
  the subscribers are captured when it is queued) and returns. The work item raises that call's
  events in order — `SessionClosed(Evicted)` for an evicted session, then `SessionOpened` — each
  subscriber in its own `try`/`catch` (type name logged). A subscriber that blocks holds one pool
  thread and delays only its own call's later notifications — never the registration, the
  endpoint's `CloseEvicted` and backstop, the receive loop's `finally` or another call's
  notifications. Events of different calls are not ordered with respect to each other;
  `IReturnRouteSession.Closed` is the authoritative per-session signal.
- `Find(did)`: reduces a DID or DID URL to its subject and returns a snapshot, oldest first; empty
  for a non-DID. HTTP sessions never appear.

Replay guard (`InMemoryReplayGuard`):

- `MaxMessageIdLength` (default 512) is checked first → `MessageIdTooLong`. Then whole-second
  arithmetic, the FR-API-05 shape: `created_time < now − Window` → `TooOld`; `created_time > now +
  skew` → `FutureDated` (skew = `DidCommOptions.ExpiresClockSkew`, read per call, clamped to ≥ 0);
  expiry = `created_time + Window + skew`. No `created_time` → `MissingCreatedTime`, or with
  `RequireCreatedTime = false` expiry = `now + Window`.
- Under one lock: purge the min-heap of `(sender, id)` by expiry (entries strictly earlier than
  now; the only removal path, so heap and buckets never diverge), then `Duplicate` if the sender's
  bucket holds the id (no refresh), `Saturated` at `MaxEntries` (default 32 768) or at the bucket's
  `MaxEntriesPerPeer` (default 1024) — nothing evicted, `SaturationCount` incremented — else
  insert. A bucket keeps one copy of its sender subject, shared by its heap entries.
- Retention property: an entry lives until `created_time + Window + skew` inclusive while age
  rejection starts at `created_time < now − Window`, so there is no instant at which a captured
  request is neither remembered nor too old.
- Memory at the default budget (measured): about 6 MB with UUID ids, about 35 MB with 512-character
  ids from a few dozen senders, up to about 170 MB when every entry has its own maximal
  2048-character DID.

Queue URI (FR-RR-06): `QueueTransport.Uri` is `didcomm:transport/queue`. `ServiceEndpointParser`
accepts it as an ordinary `uri`. `DidCommClient.SendAsync` throws `TransportException` (scheme
`didcomm`, a text naming no DID) for an endpoint override before packing, and for a resolved
endpoint right after `PackEncryptedAsync` (the recipient is resolved first, because the endpoint
lives in its service entry); `TransportRouter.SendAsync` refuses it before consulting any transport.

Threat model (the CHANGELOG's #91 Security entry is the full list):

- Sessions open only through admission; the registry's public `Register` is for trusted
  in-process code.
- The session peer comes from the authcrypt sender key id — never the plaintext `from`, never the
  socket — and every send is from `LocalDid` to exactly `[PeerDid]` and authcrypted to it, so a
  hijacked socket, or one that delivered a captured envelope first, gets ciphertext it cannot open;
  the residual is availability.
- Replay admission is atomic on (sender subject, id); a replay drop looks like an accepted `202`;
  unauthenticated senders get the same answer with or without the header.
- The return-route code logs the message id and enum or exception type names only; flood-shaped
  lines are rate-limited; its lines are written through guarded calls (`SafeLog` in AspNetCore, a
  `try`/`catch` in the two stores), so a throwing logging provider cannot
  change an admission decision, a session push or the response that carries it. (The endpoints'
  pre-existing unpack-rejection and handler-fault lines still log the exception object.)

Known limitations (documented, not fixed; W-1..W-4 in the #91 plan): a server clock that jumps
forward past an entry's retention and back re-admits a request created inside the restored
window; freshly minted DIDs can saturate the global replay budget and deny in-scope traffic for up
to `Window` (#95 should rate-limit in front of the mediator); the first delivery of a captured
authcrypted `all` message owns the session socket (confidentiality holds); a handler that throws
after pushing gets the uniform `400` and its push is discarded.

## Phase 7: not built yet

Phase 7 issues under epic #88 still open: the return-route client side — `SendOptions.ReturnRoute`,
`SendResult.Returned`, the WebSocket inbound loop (which must run the same replay admission before
dispatching) and `created_time` stamping (#92); Coordinate Mediation 3.0 with 2.0 compatibility
(#93); Message Pickup (#94), whose live delivery consumes `IReturnRouteSessionRegistry.Find`, the
`SessionOpened` / `SessionClosed` events and `IReturnRouteSession.Closed`; the `DidComm.Mediator`
package with the live-session -> queue -> direct-push pipeline and `MapDidCommMediator` (#95); the
recipient-side client (#96); and sample 11 with the return-route cookbook sections (#97). The
default sink drops every forward; a real relay is #95's.
