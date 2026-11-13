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
| `DidComm.Core/Transports` | `IDidCommTransport`, `ITransportRouter` / `TransportRouter`, `TransportRequest` / `TransportResult`, `SendOptions` / `SendResult`, `OutboundEndpointGuard` + `OutboundEndpointPolicy`, `Stomp` (`StompFrame`, `StompFrameCodec`), and `ReturnRoute` — public: `ReturnRouteMode`, `ReturnRouteHeader`, `ReturnRouteTransport`, `IReturnRouteSession`, `IReturnRouteSessionRegistry` / `InMemoryReturnRouteSessionRegistry`, `SessionRegistration` / `SessionRegistrationOutcome`, `ReturnRouteSessionEventArgs` / `SessionCloseReason`, `ReturnRouteSessionOptions`, `IInboundReplayGuard` / `InMemoryReplayGuard`, `ReplayAdmission`, `ReplayProtectionOptions`, `MediatorServiceProtocols`, `QueueTransport`, `IInboundFrameConsumer` / `InboundFrame` (the client's inbound seam); internal: `ReturnRouteEligibility`, `ReturnRouteAddressing`, `ReturnRouteHeaderWriter`, `OutgoingCreatedTime`, `ReturnRouteIdentity`, `InboundReplayGate`. |
| `DidComm.Adapters.NetDid` | `NetDidKeyStoreSecretsResolver`: optional bridge from a NetDid key store to `ISecretsResolver`. |
| `DidComm.AspNetCore` | Minimal-API receive side: `DidCommEndpointRouteBuilderExtensions` (`MapDidCommEndpoint` — callback, registry-aware and session-aware callback overloads — `MapDidCommWebSocket`, `MapDidCommOobEndpoint`), `DidCommReceiveOptions`, `DidCommMediaTypes`, `StompServerSession`; internal return-route receive side: `InboundAdmission` (with `InboundAdmissionEndpoint`, `InboundAdmissionDefaults`, `SessionOutbound`, `SafeLog`), `HttpReturnRouteSession`, `WebSocketReturnRouteSession`. |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` (`DidCommServiceCollectionExtensions`; also registers the return-route session registry and replay guard) and `DidCommBuilder` (`UseNetDidResolver`, `UseSecretsResolver`, `AddBuiltInProtocols`, `AddRoutingMediator`, `UseInboundDispatch`, ...); internal `DependencyCycleGuard` and `InboundDispatchConsumer` (the built-in `IInboundFrameConsumer`). |
| `DidComm.Transports.Http` | `HttpDidCommTransport` (HTTPS sender; reads a capped, time-bounded `200` DIDComm response body only for a return-route send), `HttpTransportOptions`, `HttpResiliencePipelineFactory`, `UseHttpTransport()`. |
| `DidComm.Transports.WebSocket` | `WebSocketDidCommTransport` (WebSocket sender: one pooled socket per endpoint, never read, plus return-route session sockets per (endpoint, local DID, peer DID), each read by its own loop), `WebSocketTransportOptions` (including `ReceiveInbound`), `WebSocketLifecycleEventArgs`, `UseWebSocketTransport()`. |
| `DidComm.Mediator` | The mediator service (Phase 7). So far two mediator roles. **Coordinate Mediation 3.0**, namespace `DidComm.Mediator.Coordination` — public: `CoordinateMediation` (type URIs, `Create…` builders, `TryParse…` parsers), `RecipientUpdate` / `RecipientUpdateResult` / `RecipientQueryPaging` / `RecipientPagination` and their enums, `IMediationStore` / `InMemoryMediationStore`, `MediationGrant`, `RecipientPage`, `RecipientStoreOutcome`, `IMediationPolicy` / `DefaultMediationPolicy` / `MediationDecision`, `IMediatorIdentity`, `CoordinateMediationOptions`, `CoordinateMediationHandler`, `AddCoordinateMediation()`; internal: `MediationBodies` (body codecs), `MediationCoordinator` (the role's logic without the wire format), `MediationForwardRecipientRegistry`, `BareDid`, `ConfiguredMediatorIdentity`. **Message Pickup 3.0**, namespace `DidComm.Mediator.Pickup` — public: `MessagePickup` (type URIs, `Create…` builders, `TryParse…` parsers), `PickupStatus`, `DeliveredMessage`, `IMessageStore` / `InMemoryMessageStore` with `QueuedMessage`, `MessageLease`, `MessageLeaseRequest`, `QueueStatus`, `EnqueueOutcome`, `MessagePickupOptions`, `MessagePickupQueue` with `PickupDeliveryOptions` / `PickupDeliveryResult` / `PickupDeliveryOutcome`, `MessagePickupHandler`, `AddMessagePickup()`; internal: `PickupBodies` (body and attachment codecs), `PickupCoordinator` (the role's logic without the wire format), `PickupSessionTracker` (live flag and lease owner per live session), `PickupSizing` (the delivery byte budget). Shared by both roles, namespace `DidComm.Mediator.Internal`: `MediatorPeer` (the authcrypt peer rule), `MediatorLog` (safe logging, bounded ids, the fault-boundary cancellation rule), `WireJson` (strict body readers). References `DidComm.Core` and `DidComm.Extensions.DependencyInjection`; no ASP.NET Core. |

## Tests and tools

- `tests/DidComm.Core.Tests`: unit tests, organised by the same folder names as `DidComm.Core`
  (Envelopes, Facade, Protocols, Resolution, Rotation, Secrets, Threading, Transports, ...).
- `tests/DidComm.InteropTests`: fixture-driven conformance (`fixtures/` with `manifest`, `packed`,
  `payloads`, `diddocs`, `secrets`, `schema`, `spec`; `FixtureCatalog`, `Runner/FixtureDispatcher`),
  plus the `AspNetCore`, `Routing`, `Transports`, `Protocols` and `Facade` suites, `DxCoverage`
  (public-API coverage and quickstart drift checks) and `Samples` (one smoke test per sample).
- `tests/DidComm.Mediator.Tests`: the `DidComm.Mediator` suites (`Coordination`: fixtures, parsers,
  builders, store, handler, DI, adversarial; `Pickup`: wire format, store, sizing, handler decision
  table, identities, faults, logs, live delivery, DI) with `fixtures/coordinate-mediation-3.0` and
  `fixtures/messagepickup-3.0` (the didcomm.org page examples normalized to valid JSON). The cases
  that need a real endpoint are in `tests/DidComm.InteropTests/AspNetCore`:
  `CoordinateMediationHttpTests.cs` (FR-MED-03), `MessagePickupHttpTests.cs` and
  `MessagePickupWebSocketTests.cs` (FR-PICK over HTTP and WebSocket), and
  `ReturnRouteSessionTests.BoundedSend.cs` (the size-bounded session send).
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

Messages a peer pushes back on a return-route session socket this agent opened as a client do not
come through these endpoints: the WebSocket transport's receive loop hands them to the
`IInboundFrameConsumer` (see "Return-route client side" below).

## Outbound flow

1. The application calls `DidCommClient.SendAsync(message, SendOptions)`.
2. Shape checks before any I/O: recipients, a wired router, `SendOptions.ReturnRoute` /
   `ReturnRouteThread`, and a queue-URI override (FR-RR-06).
3. The outgoing message: when `SendOptions.ReturnRoute` is set, or the message has no
   `created_time` and falls under the FR-RR-09 scope, the facade sends a snapshot with the header
   written and `created_time` stamped from `DidCommOptions.Clock`; otherwise the caller's instance.
   The caller's message is never modified. The effective `return_route` header is read from what
   is sent; with `all` / `thread`, recipients naming more than one DID are refused.
4. The facade packs (the instrumented `PackEncryptedAsync` path). Unless the caller overrides the
   service endpoint it packs with `Forward = true`: right after the recipient's keys,
   `IServiceEndpointResolver` reads its DIDCommMessaging service entry once; a return-route send
   whose selected entry needs a forward layer is refused there, before packing; after the pack,
   `MediatorEndpointExpander` expands that same entry and `ForwardWrapper` applies the Routing 2.0
   forward layers.
5. A resolved endpoint URI is checked against the queue URI and validated by
   `OutboundEndpointGuard` against `DidCommOptions.OutboundEndpointPolicy` before any network
   activity (an override is the caller's and skips the guard).
6. The `TransportRequest` carries the effective header and, for an authcrypt send delivered
   directly to one DID, `LocalDid` / `PeerDid` (connection affinity) and, when it asks for a return
   route, `MaxResponseBytes`.
7. `ITransportRouter.SendAsync` picks the `IDidCommTransport` whose `Scheme` matches the endpoint
   (`HttpDidCommTransport` or `WebSocketDidCommTransport`), which delivers one packed envelope —
   over WebSocket on the endpoint's pooled socket or on a return-route session socket — and returns
   a `TransportResult`.
8. The facade surfaces it as `SendResult`; for a return-route send over HTTP it first turns the
   response body into `SendResult.Returned`, or a `TransportException` when the body was over the
   cap, late or unreadable. See "Return-route client side" below. `DidCommClient.SendPackedAsync`
   delivers bytes already packed: no steps 3–4 or 6, the guard always, no response read.

## Mediator routing core (Phase 7, #90)

`DidComm.Core/Protocols/Routing` holds the mediator-side core: `forward` is an ordinary registered
protocol handler, and everything that depends on who the recipient is runs detached from the
request. The roles a recipient talks to live in `DidComm.Mediator` (Coordinate Mediation, Message Pickup; sections below); the pipeline that joins them to this handler is #95 (see "Phase 7: not built yet").

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
#92 wraps `AddDidComm`'s `DidCommClient` factory the same way and gives `UseWebSocketTransport`'s
factory a copy of the check (see "Return-route client side").

## Return-route receive side (Phase 7, #91)

A peer that authcrypts a message carrying `return_route: all` can receive messages back on the
connection it used (PRD §8a): as the HTTP `200` body (FR-RR-04, no switch) or as pushes on its
WebSocket (FR-RR-05, only with `DidCommReceiveOptions.AllowSameSocketReplies`, default off, and
never under STOMP). `DidComm.Core/Transports/ReturnRoute` holds the header model, the session
contract and the two bounded stores; `DidComm.AspNetCore` holds the admission pipeline and the two
session types. The client side — asking for a return route, `SendResult.Returned`, session
sockets and the inbound consumer, and `created_time` stamping — is "Return-route client side
(Phase 7, #92)" below.

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
   logged with the id and the outcome (rate-limited per outcome). Steps 3 and 4 are
   `InboundReplayGate.Run` (internal, Core), whose outcome `Evaluate` maps onto its results and log
   slots; the client's inbound consumer runs the same gate (#92).
5. **Session.** HTTP: `SessionEligible` → a new `HttpReturnRouteSession`. WebSocket: below.
6. **Dispatch.** `ProtocolDispatcher.DispatchAsync(…, acknowledged, session, ct)` sets
   `ProtocolContext.Session` and re-reads the header into `ProtocolContext.ReturnRoute`. The
   dispatcher admits nothing itself — admission is a one-shot check-and-insert, so a second run
   would refuse its own message — and any other caller must run steps 1–4 before dispatching: the
   client's built-in inbound consumer (`InboundDispatchConsumer`, #92) runs the same
   `InboundReplayGate` and dispatches with no session; `DidComm.Mediator` (#95) will do the same.
7. **Reply.** A handler reply goes through `session.TrySendAsync`; without a session it is logged,
   not sent.

Outbound half, shared by both session types (`SessionOutbound`): snapshot the message (serialize →
deserialize, so a caller that keeps mutating it cannot race the check), apply
`ReturnRouteAddressing.Check` — `from` the same DID subject as `LocalDid`, `to` exactly one entry
with the subject of `PeerDid`; refusals are enum values (`NoFrom`, `FromNotLocalDid`,
`ToNotExactlyPeer`) so no log line carries a DID — then `SessionOutbound.TryPackAsync` stamps
`created_time` from `DidCommOptions.Clock` when the snapshot falls under the FR-RR-09 scope (a
mediator-service type, or `return_route` `all` / `thread`) and has none (`OutgoingCreatedTime`,
#92: a client's FR-RR-08 admission refuses such a reply or push without it) and calls
`PackEncryptedAsync(snapshot, Recipients: [PeerDid], From: LocalDid, Forward: false)`.

HTTP session (`HttpReturnRouteSession`; request-scoped and never registered, so it raises no
registry events and does not count against the bounds):

- Created for each admitted, eligible request: `Transport = Http`, `IsLive = false`, `Mode = All`
  until it closes, `Thread = null`.
- One slot. A sender claims it under the lock before packing, so concurrent senders pack at most
  once; a failed or cancelled pack releases it; a successful pack fills it. Every later
  `TrySendAsync` returns `false`: a handler that pushed and also returned a reply sends the push.
- Size-bounded send (#94): `TrySendAsync(message, maxPackedBytes, ct)` is the real method and the
  two-argument one calls it with `int.MaxValue`. The packed bytes are compared with the bound
  right after the pack; over it the answer is `TooLarge`, nothing is stored and the claim is
  released like a failed pack, so a smaller message can still take the slot.
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
  The size-bounded overload (#94) compares the packed frame with the caller's bound before the
  write: over it the answer is `TooLarge`, nothing is queued and the slot goes back.
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
- Loop exit (`finally`): `Unregister` (it never runs or waits for a subscriber — the registry's
  notifier delivers `SessionClosed(Unregistered)` later — so no subscriber delays this path; a
  no-op after an eviction), then `StopAsync` — waits up
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
- Notifications (best-effort; never on the caller's thread): one notifier per registry. Inside
  the lock, right after the state change, a `Register` / `Unregister` that changed something
  `TryWrite`s one item — that call's events, `SessionClosed(Evicted)` then `SessionOpened` for a
  registration, with the subscribers captured then — to a bounded `Channel`
  (`MaxPendingNotifications`, default 1024; `DropOldest`; one reader; no synchronous
  continuations, so the notifier never resumes inside the lock) and returns. Queue order is
  therefore state-change order. One consumer loop, started in the constructor under
  `ExecutionContext.SuppressFlow` (no request's ambient state is kept for the registry's
  lifetime, and none flows from the writers), delivers the items one at a time, each subscriber in
  its own `try`/`catch` (type name logged), so subscriber calls never overlap and a throwing one
  never ends the loop. A subscriber that blocks holds the notifier's one pool thread and stalls
  that registry's later notifications only — never the registration, the endpoint's
  `CloseEvicted` and backstop, `Find` or the receive loop's `finally`. A write that finds the queue
  full drops the oldest item — superseded by the newer ones, and its session references go with
  it — counted in `DroppedNotifications` by the channel's drop callback (inside the write: count
  only); outside the lock the call logs a Warning with the running total for the first and every
  1000th drop. `IReturnRouteSession.Closed` is the authoritative per-session signal.
- `Dispose` (the container disposes `AddDidComm`'s registry): under the lock, marks the registry
  disposed and completes the channel, then returns without waiting. The notifier checks the flag
  before every subscriber call, stops, and drops (counting) what is still queued; a later write
  fails and is counted. `Register` / `Unregister` / `Find` keep working. The endpoints'
  per-application fallback registry (`InboundAdmissionDefaults`) is never disposed and has no
  subscribers, so its notifier stays a parked asynchronous loop holding no thread.
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

## Return-route client side (Phase 7, #92)

The sending half of the return-route extension (PRD §8a, FR-RR-07, FR-RR-09): `DidCommClient.SendAsync`
can ask a directly connected peer to send messages back over the connection it used, timestamps
what the receiver's replay protection covers, and takes back what returns — the HTTP response as
`SendResult.Returned`, WebSocket pushes through an `IInboundFrameConsumer`.
`DidComm.Core/Transports/ReturnRoute` holds the public consumer seam (`IInboundFrameConsumer`,
`InboundFrame`) and the shared internal rules (`ReturnRouteHeaderWriter`, `OutgoingCreatedTime`,
`ReturnRouteIdentity`, `InboundReplayGate`); `DidComm.Core/Facade` the send pipeline; the two
transports the I/O; `DidComm.Extensions.DependencyInjection` the built-in consumer.

Wiring:

- `SendOptions.ReturnRoute` / `ReturnRouteThread` need no registration, and `HttpDidCommTransport`
  reads a response whenever a request carries `MaxResponseBytes`.
- `UseWebSocketTransport` registers `WebSocketDidCommTransport` through an explicit factory (not
  type activation, which would pick the four-parameter constructor only when everything it takes
  happened to be registered): the bound options, the logger and `IOptions<DidCommOptions>` when
  registered (the inherited SSRF policy and the `MaxReceiveBytes` receive cap), and the registered
  `IInboundFrameConsumer`, if any. `WebSocketTransportOptions.ReceiveInbound` is read once at
  construction; on without a consumer, construction throws `InvalidOperationException`.
- `DidCommBuilder.UseInboundDispatch()` `TryAdd`s the internal `InboundDispatchConsumer`, whose
  constructor takes only the provider and a logger, so a consumer the host registered first wins.
  The pair is `UseWebSocketTransport(o => o.ReceiveInbound = true)` + `UseInboundDispatch()`.
  Nothing is hosted: each session socket's receive loop calls the consumer.
- Dependency walk: `DidCommClient` → `ITransportRouter` → transports → `WebSocketDidCommTransport`
  → `IInboundFrameConsumer` → (provider, logger); the consumer resolves the facade, the dispatcher,
  the replay guard and the options per message. `AddDidComm`'s `DidCommClient` factory is wrapped by
  `DependencyCycleGuard`, and `UseWebSocketTransport`'s factory carries a copy of the same re-entry
  check (`DependencyCycleGuard` is internal to the DI assembly). So a dependency cycle through the
  facade or the WebSocket transport factory (for example an `IInboundFrameConsumer` that takes
  `DidCommClient`, a transport or the router in its constructor) fails first resolution with
  `InvalidOperationException` instead of deadlocking it. A WebSocket transport the host builds in
  its own factory has no such guard.

`SendAsync`, in order (`SendCoreAsync`):

1. Recipients non-empty; a router wired in.
2. Shape checks, before any I/O: an undefined `SendOptions.ReturnRoute` → `ArgumentOutOfRangeException`;
   `ReturnRouteHeaderWriter.Validate(mode, ReturnRouteThread)` → `ArgumentException` (also a thread
   without `Thread`), the value never echoed. Then the queue-URI check on an override (FR-RR-06).
3. Snapshot, override, stamp (`PrepareOutgoing`, JC-15). When `ReturnRoute` is set, or the message
   has no `created_time` and `OutgoingCreatedTime.IsInScope` (header `all` / `thread`, or
   `MediatorServiceProtocols.IsMediatorServiceType(type)`): serialize → deserialize with
   `DidCommJson.Default` (a failure → `ArgumentException`), `ReturnRouteHeaderWriter.Write` when the
   mode is set, then `OutgoingCreatedTime.StampIfInScope(snapshot, DidCommOptions.Now())`,
   re-evaluated on the overridden header; a caller's `created_time` is never replaced. Otherwise the
   caller's instance goes out; it is never mutated.
4. Effective header, read from what is sent: absent → `null`; present → `ReturnRouteHeader.Read`
   (malformed → `None`). `asksReturn` = `All` or `Thread`.
5. One-DID check: with `asksReturn`, the recipient entries are parsed (no I/O) and must all name
   one DID, else `ArgumentException`.
6. Pack (`PackEncryptedInstrumentedAsync` with `refuseForwardLayers: asksReturn`; `Forward` unless
   overridden). With `Forward`, the recipient's service entry is read once, right after its keys;
   with `asksReturn`, a selected entry whose `MediatorEndpointExpander.ForwardLayerCount` (routing
   keys, plus one for a mediator DID as the `uri`) is above zero → `InvalidOperationException`
   (`RoutedReturnRouteRefusal`, names no DID), before the sender's keys, the signer, the pack and
   any mediator or routing-key resolution. Then sender keys, signer, inner pack and
   `MediatorEndpointExpander.ExpandAsync` over that same list; the pack reports how many forward
   layers it wrapped. An empty service list still fails in `ExpandAsync`, after the pack.
7. Unchanged: the queue-URI check on the resolved endpoint, endpoint resolution and
   `OutboundEndpointGuard` for a resolved endpoint.
8. Identities (JC-18): with no forward layer, `From` set and entries naming one DID,
   `LocalDid = DidSubjectOf(From)` and `PeerDid = DidSubjectOf(entries[0])`, set only when both are
   at most `IdentifierLimits.MaxDidLength` (2048) characters. `MaxResponseBytes = MaxReceiveBytes`
   when `asksReturn` and `LocalDid` is set; otherwise `null`.
9. `ITransportRouter.SendAsync(new TransportRequest(…) { ReturnRoute = effective, LocalDid, PeerDid, MaxResponseBytes })`.
10. `Returned` (`EvaluateReturnedAsync`), only when `MaxResponseBytes` was set: `ResponseDiscarded`
    → `TransportException` (the reported status, `200`); no or empty payload → `null`; an
    `UnpackAsync` failure other than the caller's cancellation → `TransportException` wrapping the
    typed exception (FR-API-07); `ReturnRouteIdentity.IsFromPeerToLocal` fails → `LogReturnedDiscarded`
    (Warning, EventId 6: the sent message's id and the refusal name) and `null`; otherwise the
    unpack result, neither dispatched nor replay-admitted. A payload on any other send is ignored.

HTTP response read (`HttpDidCommTransport.ReadCappedAsync`):

- Only for a final `200` (after a `307`, the last hop's) whose `Content-Type` matches one of the
  three `MediaTypes` constants, and only when the request carries `MaxResponseBytes`; otherwise the
  body is never read. `ResponseMediaType` is the matching canonical constant.
- A declared `Content-Length` over the cap → `OverCap` without reading a byte; `0` → no payload; at
  most 64 KiB (`ExactSizeThreshold`) → a buffer of exactly that size, a short body → `ReadFailed`.
  A larger declared length, or none (chunked), is read in 16 KiB pooled chunks into a `MemoryStream`
  whose capacity grows only as bytes arrive, never past the declared length or the cap: a total
  over the cap → `OverCap`, a total that differs from the declared length → `ReadFailed` (ADV-04).
  Peak memory is about twice the cap plus one chunk, whatever a peer declares.
- Budget: a linked token that cancels after `HttpTransportOptions.RequestTimeout` (clamped to a
  legal wait), because the resilience pipeline's per-attempt timeout ends with the headers
  (`ResponseHeadersRead`). The read runs outside the pipeline, so it is never retried: the request
  was delivered. Our timeout → `ReadTimeout`; anything else the stream throws (an `IOException`, an
  `HttpRequestException`, a decompressing handler's fault) → `ReadFailed`; the caller's
  cancellation propagates.
- A discard returns `TransportResult(true, 200) { ResponseDiscarded = true }` and writes one guarded
  Warning (the reason, the limit and, for `ReadFailed`, the exception type name; never the URL or a
  body byte). Success returns the exact-size bytes in `ResponsePayload`.

The `Returned` rule (`ReturnRouteIdentity.IsFromPeerToLocal`, shared with the inbound consumer):

- `ReturnRouteEligibility.TryGetPeer` (the receive side's identity step): encrypted and
  authcrypted (a non-empty `SenderKid` whose DID subject parses), a `SignerKid`, if any, naming the
  same subject, a `RecipientKid` that is a DID, not Routing 2.0. Failure → `NotAuthcrypt` or
  `Forward`.
- The expected peer and local DIDs are reduced to subjects too; both ordinal compares always run,
  combined with a non-short-circuiting `&` → `PeerMismatch`, `LocalMismatch` or
  `PeerAndLocalMismatch`.
- The decrypting subject comes from the unpack's `RecipientKid`, the first recipient key of the
  envelope the client holds, in envelope order (ADV-05, below).
- Passing proves authcrypt by the addressed peer to the sending DID only: it is not replay
  admission and does not bind the reply to the request (ADV-03, below).

Session sockets (`WebSocketDidCommTransport`):

- Key: `PoolKey(endpoint) + "\n" + LocalDid + "\n" + PeerDid`, only when both identities are set and
  STOMP is off. An identity must be a `did:` value of at most 2048 characters with no character at
  or below U+0020 (`IsSessionDid`); only a request that would open a session socket refuses an
  invalid one (`ArgumentException`, value not repeated), any other request treats it as absent.
  `_sessions` maps a key to its active `SessionSocket`; `_liveSessionSockets` holds every socket
  whose loop has not ended, retiring ones included. Session keys share `_sendLocks` with pool keys
  (a key lives in exactly one of `_pool` / `_sessions`), so dispose's drain covers them; each gate
  is minted once and kept until dispose.
- Routing, decided inside every reconnect-pipeline attempt (JC-10): `opens` = `ReturnRoute` `All` /
  `Thread`, `retires` = `None`. With a key and (`opens` or an S in `_sessions`) the attempt runs
  `SendOnSessionAsync` under the key's gate: re-check `_disposed`; an S that is not `Open` (the
  peer's close not yet handled, a dead connection) is removed, disposed and its loop stopped
  (`Disconnected` once); no S and not `opens` → nothing sent, and the same attempt falls through to
  `SendOnPoolAsync`; no S and `opens` → `OpenSessionAsync` (factory and connect within
  `ConnectTimeout`, no STOMP; published into `_liveSessionSockets` and `_sessions` before the seal
  is read, refused with `ObjectDisposedException` when dispose sealed first; the receive loop
  started under `ExecutionContext.SuppressFlow`). The send itself is bounded by `SendTimeout`. A
  failure removes, disposes and stops S (`Disconnected` once), records `SendFailed` and rethrows,
  so the retry re-routes. Lifecycle events and loop stops run after the gate is released.
- Retire (`RetireAsync`, under the gate, after the `none` was sent): remove S from `_sessions`, send
  `CloseOutputAsync(NormalClosure, "return-route session ended")` bounded by the close grace (a
  failure is logged at Debug by type name), arm the backstop. The loop keeps surfacing what was
  already on its way until the peer's close frame. An `all` queued behind it opens a fresh S.
- Receive loop (`ReceiveLoopAsync`, one per socket, an 8 KiB pooled read buffer): reassemble one
  message (FR-TRN-09); a close frame ends the loop. A total over `MaxReceiveBytes` stops buffering
  at once and runs `CloseOversizedAsync`: discard mode, removal from `_sessions`, the key's gate
  (bounded by `DrainBudget` and the loop token, skipped once dispose began),
  `CloseOutputAsync(MessageTooBig, "payload too large")` bounded by the close grace, the backstop,
  one rate-limited Warning. With `ReceiveInbound` off, or in discard mode, a message is dropped
  (counted, not logged). Otherwise `ConsumeAsync(new InboundFrame(E, L, P, bytes), loopToken)` is
  awaited inline (JC-8); cancellation by the loop token ends the loop; any other exception is a
  rate-limited Warning by type name and the loop goes on. Socket faults end the loop (Debug, type
  name). The loop sends nothing but close frames and never throws.
- Loop end (`EndReceiveLoopAsync`): remove S from `_sessions`; if dispose has not begun and the
  peer started the close (`CloseReceived`), answer it under the gate (bounded by `DrainBudget`) with
  `CloseOutputAsync(NormalClosure)` bounded by the close grace; dispose the socket; `Disconnected`
  once (`SessionSocket.TryEnd`), naming the endpoint; remove from `_liveSessionSockets`;
  `MarkLoopDone`, which also disposes the backstop timer.
- Close grace versus dispose budget: `CloseGrace` is `SendTimeout` when finite and positive
  (clamped), else 30 seconds, and bounds the retire close, the `1009` close, the close answer and
  the backstop — an infinite `SendTimeout` must not stop a retiring loop at once (QA-F01).
  `CloseBudget` (`SendTimeout`, infinite or negative → 0) is for dispose only.
- Backstop: `SessionSocket.ArmBackstop` starts a `Timer` of its own (no flowed `ExecutionContext`)
  whose callback calls `StopLoop`, never `CancellationTokenSource.CancelAfter`: the loop token is the
  consumer's, and the source's own timer would rethrow a throwing registered callback on a pool
  thread and end the process (ADV-01). `StopLoop` — called by the backstop, by a send that failed on
  S or found it no longer open, and by dispose — swallows what callbacks throw. The loop's
  `CancellationTokenSource` is never disposed, because any of those may cancel it at any time.
- Dispose: set `_disposed`; `CloseEndpointAsync` concurrently for every key in
  `_sendLocks ∪ _pool ∪ _sessions` — for a session key, inside the gate it removes S, sends
  `CloseOutputAsync(NormalClosure, "transport disposed")` bounded by `CloseBudget` while `Open` or
  `CloseReceived`, disposes S and stops its loop after the release; seal; sweep `_pool`, `_sessions`
  and `_liveSessionSockets` (stop, dispose, `Disconnected` once each); await every loop with
  `WaitAsync(CloseBudget)` (a loop held by a consumer that ignores cancellation is abandoned, its
  socket already disposed); dispose idle gates. Bound: `DrainBudget + 2 × CloseBudget`. No open
  socket survives — pooled, session or retiring — and a loop never waits on a gate once `_disposed`
  is set.
- Guarantee, from the caller's side: a send that starts after an `all` / `thread` send to (E, L, P)
  returned uses S while S exists; one that starts after a `none` send returned uses the pool;
  concurrent sends are not ordered. Pooled sockets are never read. Under STOMP an `all` / `thread`
  send uses the pool (one Information line).

Inbound consumer and admission (`InboundDispatchConsumer.ConsumeAsync`, one frame at a time per
socket):

1. Resolve `DidCommClient`, `ProtocolDispatcher`, `IInboundReplayGuard` and
   `IOptions<DidCommOptions>` from the provider; a failure (a disposed container, for example)
   drops the frame.
2. `UnpackAsync` of the UTF-8 payload; a failure drops it, logged without the message id (JC-12).
3. `ReturnRouteIdentity.IsFromPeerToLocal(unpacked, frame.PeerDid, frame.LocalDid)`; a refusal drops
   it, logged by refusal name without the id. Anoncrypt, signed-only, `anoncrypt(sign)`, plaintext,
   `forward`, another sender and another local DID end here, before admission.
4. `ReturnRouteHeader.TryRead`; a malformed header is logged with the id and treated as `none`.
5. `InboundReplayGate.Run` — the same FR-RR-08 rule `InboundAdmission.Evaluate` applies (steps 3–4
   of the receive side above), against the DI `IInboundReplayGuard` and `DidCommOptions.Now()`;
   anything but `OutOfScope` / `Admitted` drops the frame (logged with the id and the outcome).
6. `ProtocolDispatcher.DispatchAsync(unpacked, client, options, Task.CompletedTask, session: null, ct)`;
   a handler exception drops the frame (id and type name); cancellation by `ct` propagates.
7. `DispatchResult.ReplyProduced` → a Debug line; the reply is never sent (JC-9). Handlers never
   re-check replay.

Every drop kind is rate-limited (the first and every 1000th at Warning with the count, Debug
otherwise) and every line is written through a guarded call.

`SendPackedAsync(packed, endpoint, mediaType, ct)` (JC-20): `ArgumentNullException` for the endpoint
or media type; `ArgumentException` for an empty payload, a relative endpoint or a media type that
is not one of the three DIDComm types (normalized to the canonical constant); no router →
`InvalidOperationException`; the queue URI → `TransportException`; `OutboundEndpointGuard` always.
It sends a plain `TransportRequest` (no return-route fields: no affinity, no body read) under the
same `SendActivity` span and `LogSendFailed` line as `SendAsync`, and returns the `TransportResult`.

Server side (a one-line change to #91's code): `SessionOutbound.TryPackAsync` stamps
`created_time` on its snapshot with `OutgoingCreatedTime.StampIfInScope` before packing, so a
mediator's in-scope replies and pushes pass the client's admission.

Threat model (the CHANGELOG's #92 Security entry is the full list):

- Consumers are registered by the host at startup only. A consumer sees the bytes a peer pushed on
  a socket the client opened, with the transport's own (E, L, P) attribution, and holds no socket.
- Trust comes from authcrypt by P to L (`ReturnRouteIdentity`), never from the socket or the
  plaintext `from` / `to`; everything else is dropped before admission and consumes no replay
  budget. A push is dispatched with no session and its handler reply is never sent (no reflector).
- Bodies are read only when asked, never buffered past the cap and time-bounded; pushes are capped
  (`1009`); each session socket backpressures only itself, so a flood on one session neither drops
  nor delays another.
- `SendPackedAsync` always applies the SSRF guard; the routed-recipient refusal precedes any
  mediator or routing-key resolution.
- The new lines log message ids (inbound: only after the identity rule) and enum or exception type
  names only, rate-limited where a peer can repeat them, through guarded calls.

Known limitations:

- `Returned` is authcrypt by the addressed DID to the sending DID, nothing more: not FR-RR-08
  replay-admitted and not bound to the request, so callers correlate on `thid` (DD-11) and handle
  it idempotently (ADV-03, won't fix: PRD FR-RR-07 specifies identity only, and admitting it would
  drop the unstamped in-scope replies of third-party mediators).
- A multi-recipient envelope whose first held key belongs to another local DID is discarded, even
  when a later key is the sending DID's (ADV-05); a receiver's return-route replies always have
  exactly one recipient.
- Unpacking a hostile response or push resolves the DID its sender key id names before the identity
  check, as every receive endpoint does (ADV-06, a pre-existing class), bounded by FR-API-09 and
  `MaxReceiveBytes`.
- A send racing the peer's close may be accepted and lost (WebSocket has no acknowledgement).
- A consumer or handler that waits for another inbound on the same session deadlocks it until the
  wait's own timeout.
- One send gate per (endpoint, local DID, peer DID) is kept until dispose.
- A DID over 2048 characters gets no session socket and no `Returned`.
- Clock skew: FR-RR-08 admission, on the receiver and now in the client's inbound consumer, refuses
  `created_time > now + ExpiresClockSkew` in whole seconds, and `DidCommOptions.ExpiresClockSkew`
  defaults to zero, so a message stamped by a host whose clock runs even slightly ahead can be
  refused as `FutureDated`. Hosts that talk across machines should set `ExpiresClockSkew` to their
  expected clock offset.

## Coordinate Mediation 3.0, mediator role (Phase 7, #93)

The enrolment protocol of the mediator service (PRD §8b, FR-MED-01..06 and -08;
https://didcomm.org/coordinate-mediation/3.0/): a recipient asks for mediation, gets the mediator's
routing DIDs, and registers the DIDs it wants forwards accepted for. It lives in the new package
`DidComm.Mediator`, namespace `DidComm.Mediator.Coordination`, and is a protocol handler like any
other: it is reached through `ProtocolDispatcher` and knows nothing about transports.

Wiring: `AddCoordinateMediation(configure?)` on `DidCommBuilder` registers, each as a `TryAdd` so a
host registration made first wins:

- `CoordinateMediationHandler` for `https://didcomm.org/coordinate-mediation/3.0` (through
  `AddProtocol`), with `Roles = ["mediator"]`, so Discover Features discloses the PIURI with that
  role;
- `IMediationStore` → `InMemoryMediationStore`; `IMediationPolicy` → `DefaultMediationPolicy`;
  `IMediatorIdentity` → the list in `CoordinateMediationOptions.RoutingDids`;
- `CoordinateMediationOptions` through the Options pattern (repeated calls compose; the delegate
  also runs once on a scratch instance so bad settings fail at registration);
- `IForwardRecipientRegistry` → `MediationForwardRecipientRegistry`, an adapter that answers the
  forward handler's "is `next` registered?" with one `IMediationStore.FindGrantByRecipientDidAsync`.
  It must be the directory the container hands out: call `AddCoordinateMediation()` before
  `AddRoutingMediator()`. A directory already registered throws at registration; one registered
  afterwards throws when the handler is first built. `UseAsForwardRecipientRegistry = false` opts
  out.

How a request is handled (`CoordinateMediationHandler.HandleAsync` → `MediationCoordinator`):

1. **Peer.** `ReturnRouteEligibility.TryGetPeer` — the same rule that opens a return-route session —
   gives the peer (DID subject of the authcrypt sender key) and the local DID (DID subject of the
   decrypting key). Without an authcrypt layer (plaintext, anoncrypt, signed-only,
   `anoncrypt(sign)`) the message is dropped before its body is read: nothing applied, no reply, one
   rate-limited log line with the id. FR-RR-08 replay admission has already run at the endpoint;
   the handler does not repeat it.
2. **Parse.** `CoordinateMediation.TryParse…` over `MediationBodies`. A malformed body from a peer
   throws `MalformedMessageException` out of dispatch (the endpoint's 400); the text names the
   member and the rule, never a value. A DID member over 2048 characters is refused by one length
   comparison before anything parses it. More than `MaxUpdatesPerMessage` entries is malformed,
   and that is decided from the list's length before the body is re-read or any entry decoded
   (`MediationBodies.RequireUpdatesWithin`, then again in `ReadUpdates` before it allocates).
3. **Act.**
   - `mediate-request`: a peer that holds a grant gets the same grant again (the policy is not
     asked). Otherwise `IMediationPolicy.OnMediateRequestAsync` decides; a grant is recorded with
     the routing DIDs of `IMediatorIdentity`, or, when none are configured, the DID that decrypted
     the request. A full store answers `mediate-deny`.
   - `recipient-update`: one store read for the grant (none → every entry `client_error`), and in
     the same fault boundary the list of the mediator's own DIDs (the decrypting DID, the grant's
     routing DIDs, the currently configured ones, `OwnIdentifiers`). If either cannot be read,
     every entry is `server_error` and nothing is applied: the own-DID guard fails closed. Then per
     entry: not a bare DID → `client_error` (a DID URL is never reduced to its DID); one of the
     mediator's own DIDs → `client_error`; `IMediationPolicy.MayRegisterAsync` for an `add`. What
     remains goes to `IMediationStore.UpdateRecipientsAsync` in one call, and the store's
     `RecipientStoreOutcome` per entry is mapped to the wire `result`. Results are in input order.
   - `recipient-query`: `limit` clamped to `MaxPageSize`, one `ListRecipientsAsync`, and a
     `recipient` reply that always carries `pagination`.
4. **Reply.** From the local DID to the peer, `thid` = the request's `thid`, else its `id`. The
   handler returns the reply whether or not the inbound opened a session; the endpoint decides how
   it travels (200 body, WebSocket push, or nothing).

The store (`InMemoryMediationStore`): one lock for every change, so "is this DID free?" and
"register it" are one step; a concurrent index from recipient DID to its grant for the forward
lookup, read without the lock; registration order kept per grant for stable paging; three bounds
(`MaxGrants` 1 000, `MaxRecipientsPerGrant` 256, `MaxRecipients` 10 000 by default — modest, because
the in-memory store is for samples, tests and small hosts; about 45 MB of keys in the worst case).
Ownership (one grant per recipient DID) is
the store's rule, not the policy's: a custom policy can only add refusals.

Faults: a store, policy or identity that throws, or returns something unusable, never leaves the
handler — `server_error` for the affected `recipient-update` entries, no reply for a
`mediate-request` or `recipient-query` (the protocol has no error shape for them). When the dispatch
token is cancelled, only `OperationCanceledException` leaves.

Documented limits:

- **No proof of DID ownership.** The protocol asks for none; the first peer to register a DID gets
  it. `MayRegisterAsync` is where a host adds proof.
- **A registration oracle toward enrolled peers.** `client_error` on an `add` tells a peer with a
  grant that another grant holds the DID. Unauthenticated callers learn nothing.
- **Open enrolment fills the store.** The default policy grants any authcrypt sender and grants do
  not expire on their own (owner decision: the protocol has no message that tells a recipient its
  grant lapsed; a host ends a grant with `RevokeAsync`). Self-made DIDs can exhaust `MaxGrants` and
  `MaxRecipients`; existing grants keep working. A public mediator needs an allow-list or rate
  limit in its policy.
- **Forward lookup.** One probe either way; a hit compares one key more than a miss (the same as
  `InMemoryForwardRecipientRegistry`).
- **Revocation.** `RevokeAsync` stops new forwards for the grant's DIDs. A forward already admitted
  by `ForwardHandler` (waiting on `delay_milli`) still reaches the sink; the delivery pipeline (#95)
  has to check again before it queues or pushes.
- **Not here yet:** the recipient role (#96), the 2.0 handler (#99, which reuses `MediationBodies`
  and `MediationCoordinator`), and startup validation of routing DIDs against their DID documents
  (FR-MED-07, #95).

## Message Pickup 3.0, mediator role (Phase 7, #94)

Source: https://didcomm.org/messagepickup/3.0/ ; PRD §8c FR-PICK-01..10. It lives in
`DidComm.Mediator`, namespace `DidComm.Mediator.Pickup`, beside Coordinate Mediation, and is a
protocol handler like any other: no transport code, no ASP.NET Core.

Wiring: `DidCommBuilder.AddMessagePickup(configure?)` (`MessagePickupBuilderExtensions`):

- Options through `AddOptions<MessagePickupOptions>().Configure(...)`; the delegate is also run once
  on a scratch instance, so bad settings fail at registration.
- `TryAddSingleton` for `IMessageStore` (`InMemoryMessageStore`), `MessagePickupQueue` and
  `MessagePickupHandler`, then `AddProtocol<MessagePickupHandler>()`. Discover Features discloses
  `https://didcomm.org/messagepickup/3.0` with `roles: ["mediator"]`.
- The queue's factory needs an `IMediationStore` (`AddCoordinateMediation()` in either order, or
  the host's own) and throws `InvalidOperationException` naming the fix when there is none. It
  takes the session registry `AddDidComm` registers. Nothing in the graph takes the dispatcher, the
  handler registry, a handler or `DidCommClient`, so a forward sink may take `MessagePickupQueue`.

The pieces:

- `MessagePickup`: type URIs, six builders, six strict parsers (both roles). Bodies and attachments
  go through `PickupBodies`, which is version-neutral: the limit's member name and the time unit
  are parameters, so Pickup 4.0 (#100) reuses it.
- `IMessageStore` / `InMemoryMessageStore`: records keyed by (grant peer DID, message id), in
  arrival order per grant. A lease is a mark on the record (lease id, owner token, expiry instant);
  nothing is moved or copied when a lease ends. Time is passed in; the store reads no clock.
- `PickupCoordinator` (internal): the role without the wire format — authorisation, status,
  lease → check → push → trim, acknowledgement, the live flag, and the pipeline's `DeliverAsync`.
  One instance, owned by `MessagePickupQueue` and shared with every handler over it, so a 4.0
  handler shares leases, live flags and log counters.
- `PickupSessionTracker` (internal): one `PickupSessionState` per live session, held in a
  `ConditionalWeakTable` keyed by the session object (no bound of its own; it cannot outlive the
  session). It carries the live-delivery framing (set by the handler that turned live mode on), a
  random owner token for the session's leases, and a hook on `IReturnRouteSession.Closed`.
- `PickupSizing` (internal): the byte arithmetic of `max_delivery_bytes`.
- `MessagePickupQueue` (public): `DeliverAsync`, `RemoveAsync`, `PurgeExpiredAsync`,
  `MaxAdmissiblePayloadBytes` — what #95's sink and a host call.

`MessagePickupHandler.HandleAsync`, in order:

1. **Peer.** `MediatorPeer.TryGet` (the rule of `ReturnRouteEligibility.TryGetPeer`): no authcrypt
   layer → dropped before the type or body is read, one rate-limited line with the id.
2. **Session.** `context.Session` is used only when it is bound to exactly this (peer, local) pair;
   a session that throws when asked is treated as absent.
3. **Parse** by type; the `message_id_list` bound is part of the parse (counted before any entry
   is read). A request thread id over 512 characters is malformed. Types the mediator does not
   act on are ignored.
4. **Authorise.** No filter: the peer needs a grant (`GetGrantAsync`). With `recipient_did`: it must
   be a bare DID and `FindGrantByRecipientDidAsync(did).PeerDid` must equal the peer. Anything else
   is answered as an empty queue and the message store is not read.
5. **Act**, then reply with `status` (returned as the handler's reply), or push a `delivery`
   through the session and return nothing, or return the live-mode problem report.

A `delivery-request`, step by step (`PickupCoordinator.LeaseAndPushAsync`):

1. `limit` is clamped to `MaxDeliveryBatch`; 0, an unowned scope or no session → `status`, nothing
   leased.
2. `IMessageStore.LeaseAsync`: the FIFO prefix of deliverable records (due, not expired, not under
   a live lease), at most the clamped count and at most `BatchPayloadBudget` payload bytes, marked
   with a new lease — owned by the session's token over a WebSocket, by nobody over HTTP.
3. What the store returned is copied and checked (the peer's grant key, the count, the filter,
   usable ids); one wrong record and nothing is delivered.
4. Unfiltered batches: every distinct recipient DID is looked up again; the leased records of a
   DID no longer registered to the peer are removed by id (`AcknowledgeAsync` — exactly the
   records this check looked at) and leave the batch. A registry answer that names no usable peer
   is a fault of the check: nothing is removed.
5. `session.TrySendAsync(delivery, MaxDeliveryBytes, ct)`. `Sent` → done (over HTTP the pushed
   message is the 200 body). `TooLarge` with more than one message → the last one is released and
   the rest sent again. `TooLarge` with one message → `IMessageStore.PassOverAsync`: the record
   gives up the lease and is left out of every lease until `now + LeaseTimeout × 2^PassOvers`
   (the store keeps the count on the record). Nothing is removed. `NotSent` → no reply.
6. A batch that came to nothing (its head was passed over, or all of it was unregistered
   leftovers) → the request leases again and goes on with what is queued behind; `status` is the
   answer only when a lease round returns nothing. One request does at most 2 × `MaxDeliveryBatch`
   lease rounds and 2 × `MaxDeliveryBatch` sends.
7. On every exit, including a fault or cancellation, whatever is still held under this request's
   lease is released (`ReleaseAsync` names the lease id, with `CancellationToken.None`). Records
   passed over in step 5 are under no lease any more.

Why the pass-over mark is the record's and not a lease: the work bound of step 6 restarts with
every request, so whatever a request learned must outlive it — and its lease, and its connection —
or a queue with more unfit records than one request can try would meet every later request with the
same records first. The doubling makes the periods outgrow any polling interval.

A new message (`MessagePickupQueue.DeliverAsync`, the pipeline's call): size bound first
(`MaxAdmissiblePayloadBytes`, before any lookup) → expiry → `FindGrantByRecipientDidAsync` → the
live target, if any → `EnqueueAsync`, already leased to that session when there is one → the push
(a `delivery` with one attachment and no `thid`, size-bounded). Pushed → `PushedLive`; not taken →
the lease is released at once and the outcome is `Queued`. The live target is the newest session
`IReturnRouteSessionRegistry.Find(peer)` returns that has the flag on, `Mode == All`, and whose own
`PeerDid` is the peer.

The byte budget: a payload of `n` bytes is base64url text in the `delivery` JSON and that JSON is
base64url again as the envelope's ciphertext, so the packed size is about 16/9 of the payload.
`PickupSizing` sets aside 32 KiB for the envelope, 8 KiB for the `delivery` without attachments and
96 bytes per attachment, and derives one number, `BatchPayloadBudget` (564 525 bytes at 1 MiB with
a batch of 10). It is the byte budget of every lease and, capped by `MaxReceiveBytes`, the largest
payload admitted — so an admitted payload always fits alone. The estimate only picks the batch;
the guarantee is the session measuring the packed bytes.

Session lifecycle: `PickupSessionState` hooks `Closed` once (`UnsafeRegister`). When it fires the
state is marked closed and live delivery is off. If the session ever took a lease
(`MarkLeasing`, set before the store call that records one), its owner token joins the tracker's
clean-up backlog: one worker, started on the thread pool without the closing thread's execution
context, calls `ReleaseLeasesAsync(ownerToken)` for one token at a time. The backlog holds at most
`PickupSessionTracker.MaxPendingReleases` (1 024) tokens, the one in flight included; a close that
finds it full is dropped (counted, logged) and its leases end by timeout. So neither the number of
store calls in flight (one) nor the number waiting is set by how fast connections come and go, and
a peer that only opens connections and toggles live mode costs the store nothing. Every path that
takes a session-owned lease checks the closed mark again afterwards, so a lease recorded after the
release ran is released too. HTTP sessions are never tracked: their leases end by time only.

Faults: a message store, mediation store, registry, session or id generator that throws, or
returns something outside its contract, never leaves the role — a request gets no reply, a
delivery reports `Faulted` (or `Queued` when the message was already stored). When the caller's
token is cancelled, only `OperationCanceledException` leaves.

Documented limits:

- **At-least-once.** A lease that ran out, or a connection that closed mid-delivery, makes a
  message deliverable again; recipients acknowledge by id.
- **Live deliveries must be acknowledged.** They stay stored and counted until `messages-received`;
  a recipient that never acknowledges fills its quota.
- **Many-key recipients.** An envelope larger than the allowance means trimmed batches. A message
  that does not fit alone is never delivered and never removed by the role: it is passed over,
  tried again at intervals that double, and stays stored and counted until it expires or is
  purged — so `message_count` does not reach zero while it exists.
- **Not-yet-due messages** count against the quota but are not reported by `status`; a sender
  asking for long delays can use a recipient's quota up unseen. The pipeline (#95) must bound the
  delay it accepts.
- **A live session whose peer stops reading** can hold up to `MaxPendingPushes` packed deliveries.
- **Revoked grants.** Their leftovers are not delivered while the peer has no grant, but stay
  stored until the host calls `IMessageStore.PurgeGrantAsync` (the mediation store raises no
  change notification) — and are delivered again if the same peer is granted mediation again.
- **Lazy expiry.** Memory of expired records comes back when their grant is next touched, when the
  store runs full, or on `PurgeExpiredAsync`.
- **Open enrolment.** Self-made grants can use up the store-wide bounds; what is held stays
  deliverable.
- **No session, no delivery.** Over the library endpoints a request without `return_route: all`
  gets nothing; over a WebSocket the endpoint needs `AllowSameSocketReplies`.

## Phase 7: not built yet

Phase 7 issues under epic #88 still open: the separate replay clock skew (#105); the delivery
pipeline — a forward sink that calls `MessagePickupQueue.DeliverAsync`, the optional direct push,
`AddMediator()` and, in a new `DidComm.Mediator.AspNetCore` package, `MapDidCommMediator` (#95);
the recipient-side client (#96); sample 11 with the cookbook sections (#97); Coordinate Mediation
2.0 compatibility (#99); the interop and adversarial gate (#98); and Message Pickup 4.0 (#100),
a second thin handler over the same `MessagePickupQueue`. Until #95 the default sink drops every
forward, so nothing reaches the pickup queue except through `DeliverAsync`.
