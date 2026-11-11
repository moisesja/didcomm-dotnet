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
| `DidComm.Core/Transports` | `IDidCommTransport`, `ITransportRouter` / `TransportRouter`, `TransportRequest` / `TransportResult`, `SendOptions` / `SendResult`, `OutboundEndpointGuard` + `OutboundEndpointPolicy`, and `Stomp` (`StompFrame`, `StompFrameCodec`). |
| `DidComm.Adapters.NetDid` | `NetDidKeyStoreSecretsResolver`: optional bridge from a NetDid key store to `ISecretsResolver`. |
| `DidComm.AspNetCore` | Minimal-API receive side: `DidCommEndpointRouteBuilderExtensions` (`MapDidCommEndpoint`, `MapDidCommWebSocket`, `MapDidCommOobEndpoint`), `DidCommReceiveOptions`, `DidCommMediaTypes`, `StompServerSession`. |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` (`DidCommServiceCollectionExtensions`) and `DidCommBuilder` (`UseNetDidResolver`, `UseSecretsResolver`, `AddBuiltInProtocols`, `AddRoutingMediator`, ...). |
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
4. With the dispatcher overloads, `ProtocolDispatcher.DispatchAsync` routes the `UnpackResult` by
   message type to the `IProtocolHandler` registered for it; a handler may return a reply.
5. `IProtocolObserver` instances get a read-only, best-effort copy off the reply path for every
   outcome (`ObserverDelivery` runs the bounded background pumps); the `onReceive`-callback
   overloads hand the `UnpackResult` straight to the application instead.

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
request. There is no `DidComm.Mediator` package yet (see the closing note).

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
   uniform rejection, before anything knows it was a forward.
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
dispatcher or a protocol handler throws `InvalidOperationException` at registration.

Not built yet (Phase 7 issues under epic #88): return-route sessions and the queue transport
(#91, #92), Coordinate Mediation 3.0 with 2.0 compatibility (#93), Message Pickup (#94), the
`DidComm.Mediator` package with the live-session -> queue -> direct-push pipeline (#95), the
recipient-side client (#96) and sample 11 (#97). The default sink drops every forward; a real
relay is #95's.
