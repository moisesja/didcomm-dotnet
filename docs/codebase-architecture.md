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
| `DidComm.Core/Protocols` | `ProtocolDispatcher`, `ProtocolHandlerRegistry`, `IProtocolHandler`, `IProtocolObserver`, `ObserverDelivery`, `IInboundCorrelator`, `ProtocolContext`, `MessageTypeUri`; built-in protocols under `DiscoverFeatures`, `Empty`, `OutOfBand`, `ProblemReport`, `Rotation` (`from_prior`), `Routing` (`ForwardMessage`, `ForwardProcessor`), `Trace`, `TrustPing`. |
| `DidComm.Core/Transports` | `IDidCommTransport`, `ITransportRouter` / `TransportRouter`, `TransportRequest` / `TransportResult`, `SendOptions` / `SendResult`, `OutboundEndpointGuard` + `OutboundEndpointPolicy`, and `Stomp` (`StompFrame`, `StompFrameCodec`). |
| `DidComm.Adapters.NetDid` | `NetDidKeyStoreSecretsResolver`: optional bridge from a NetDid key store to `ISecretsResolver`. |
| `DidComm.AspNetCore` | Minimal-API receive side: `DidCommEndpointRouteBuilderExtensions` (`MapDidCommEndpoint`, `MapDidCommWebSocket`, `MapDidCommOobEndpoint`), `DidCommReceiveOptions`, `DidCommMediaTypes`, `StompServerSession`. |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` (`DidCommServiceCollectionExtensions`) and `DidCommBuilder` (`UseNetDidResolver`, `UseSecretsResolver`, `AddBuiltInProtocols`, ...). |
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

## Mediator pipeline (planned, Phase 7)

Nothing of the mediator service is built yet. Today `DidComm.Core/Protocols/Routing` holds only the
mediator-side building block `ForwardProcessor` (FR-ROUTE-05..07): it unwraps one `forward` and
reports where the onward bytes should go. There is no `DidComm.Mediator` package, no live-session
registry, no message queue and no pickup protocol.

The requirements live in the PRD: §8a (Return-Route and Queue Transport), §8b (Coordinate
Mediation 3.0, with 2.0 compatibility) and §8c (Message Pickup 3.0, 4.0 as a follow-up), scheduled as
§12 Phase 7 under epic #88. This section is to be filled in by #95 and #97 once the pipeline exists
(`DidComm.Mediator`: `forward` -> live session -> queue -> direct push; Coordinate Mediation 3.0/2.0;
Pickup 3.0/4.0).
