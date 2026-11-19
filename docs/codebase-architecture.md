# Codebase Architecture

This document is the map of what is actually built in didcomm-dotnet: which packages exist, what
lives where, and how a message moves through the code on the way in and on the way out. The target
the implementation is measured against is the PRD, [`didcomm-dotnet_PRD.md`](didcomm-dotnet_PRD.md);
when the two disagree, the PRD is the target and this file is the stale one to fix. Everything
listed here names a folder or type that exists in `src/`, `tests/`, `tools/` or `samples/` today.

## Packages

| Package (`src/`) | What it holds |
| --- | --- |
| `DidComm.Core` | The library proper. Folders: `Messages` (plaintext model), `Jose` and `Crypto` (JWS/JWE primitives), `Composition` (envelope reader/writer, `ForwardWrapper`), `Consistency`, `Facade` (`DidCommClient`, `DidCommOptions`, `PackEncryptedOptions`, `UnpackResult`), `Resolution` (`IServiceEndpointResolver`, DID key services, and `DidCommServiceBuilder`, which builds the `DIDCommMessaging` service entry a DID publishes), `Secrets`, `Threading`, `Profiles`, `Diagnostics` (`DidCommDiagnostics`: the `ActivitySource` name and the meter name, `didcomm-dotnet`; internal `LogValues`: bounded message ids and exception type names for log lines, shared with `DidComm.AspNetCore` and `DidComm.Mediator.Client`, whose `MediatorLog` the mediator roles use too), `Exceptions`, `Json`, `Protocols`, `Transports`. Internal, for the recipient-side mediator client (#96; package `DidComm.Mediator.Client` since #113), in `Facade/DidCommClient.Mediated.cs`: `DidCommClient.ResolveDirectEndpointAsync` (the direct endpoint `SendAsync` would select), `ReceivesPushesOn` (whether the transport hands pushes to the inbound consumer) and `UnpackDeliveredAsync` with `DeliveredUnpack` (opens re-wrapped deliveries). |
| `DidComm.Core/Protocols` | `ProtocolDispatcher`, `ProtocolHandlerRegistry`, `IProtocolHandler`, `IProtocolHandlerRoles`, `IProtocolObserver`, `ObserverDelivery`, `IInboundCorrelator`, `ProtocolContext`, `MessageTypeUri` (internal: `IInboundCorrelator`, whose `OnInbound` reports whether it claimed the message, and `InboundMessageSnapshot`, with `Pthid` and `Verified` since #96, and `Detach` / `Detached` since #98); built-in protocols under `DiscoverFeatures`, `Empty`, `OutOfBand`, `ProblemReport`, `Rotation` (`from_prior`), `Routing` (`ForwardMessage`, `ForwardProcessor` / `ForwardProcessorOptions` / `ForwardProcessingResult`, `ForwardHandler`, `IForwardSink` / `NullForwardSink`, `IForwardRecipientRegistry` / `InMemoryForwardRecipientRegistry`, `IRecipientRoutingKeyProvider` / `StaticRecipientRoutingKeyProvider`; internal `ForwardMetrics` / `ForwardOutcomes` (the `didcomm.forward.outcomes` counter), `RateLimitedLog` and `HeldForward` (what a waiting forward keeps, #98)), `Trace`, `TrustPing`. |
| `DidComm.Core/Transports` | `IDidCommTransport`, `ITransportRouter` / `TransportRouter`, `TransportRequest` / `TransportResult`, `SendOptions` / `SendResult`, `OutboundEndpointGuard` + `OutboundEndpointPolicy`, `Stomp` (`StompFrame`, `StompFrameCodec`), and `ReturnRoute` — public: `ReturnRouteMode`, `ReturnRouteHeader`, `ReturnRouteTransport`, `IReturnRouteSession`, `IReturnRouteSessionRegistry` / `InMemoryReturnRouteSessionRegistry`, `SessionRegistration` / `SessionRegistrationOutcome`, `ReturnRouteSessionEventArgs` / `SessionCloseReason`, `ReturnRouteSessionOptions`, `IInboundReplayGuard` / `InMemoryReplayGuard`, `ReplayAdmission`, `ReplayProtectionOptions`, `MediatorServiceProtocols`, `QueueTransport`, `IInboundFrameConsumer` / `InboundFrame` (the client's inbound seam); internal: `ReturnRouteEligibility` (over an `UnpackResult` or an `InboundMessageSnapshot`, one shared core), `ReturnRouteAddressing`, `ReturnRouteHeaderWriter`, `OutgoingCreatedTime`, `ReturnRouteIdentity`, `InboundReplayGate`, `IInboundReceivingTransport` (a transport says whether pushes reach the inbound consumer; #96), and `TransportRouter.Select` (the one transport selection for a send and for questions about it). |
| `DidComm.Adapters.NetDid` | `NetDidKeyStoreSecretsResolver`: optional bridge from a NetDid key store to `ISecretsResolver`. |
| `DidComm.AspNetCore` | Minimal-API receive side: `DidCommEndpointRouteBuilderExtensions` (`MapDidCommEndpoint` — callback, registry-aware and session-aware callback overloads — `MapDidCommWebSocket`, `MapDidCommOobEndpoint`), `DidCommReceiveOptions`, `DidCommMediaTypes`, `StompServerSession`; internal return-route receive side: `InboundAdmission` (with `InboundAdmissionEndpoint`, `InboundAdmissionDefaults`, `SessionOutbound`, `SafeLog`), `HttpReturnRouteSession`, `WebSocketReturnRouteSession`; internal for `DidComm.Mediator.AspNetCore`: `MapDidCommWebSocketCore` (the registry-aware WebSocket endpoint, optionally with the mediator's receive options), `MapDidCommEndpointCore` (the registry-aware HTTP endpoint, optionally with a deadline for a returned `200`) and `DidCommReceiveOptions.ForMediatorEndpoint()`; internal response deadlines: `HttpResponseAcknowledgement`, `ResponseDeadlineLog`. |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` (`DidCommServiceCollectionExtensions`; also registers the return-route session registry and replay guard) and `DidCommBuilder` (`UseNetDidResolver`, `UseSecretsResolver`, `AddBuiltInProtocols`, `AddRoutingMediator` — which also registers the shared `ForwardMetrics` and a cycle-guarded `ForwardHandler` factory — `UseInboundDispatch`, ...); internal `DependencyCycleGuard` and `InboundDispatchConsumer` (the built-in `IInboundFrameConsumer`). |
| `DidComm.Transports.Http` | `HttpDidCommTransport` (HTTPS sender; reads a capped, time-bounded `200` DIDComm response body only for a return-route send), `HttpTransportOptions` (including `TrustedProxy`), `HttpResiliencePipelineFactory`, `UseHttpTransport()`; internal `TrustedProxyRoute` (the checked copy of the named proxy and the test that recognises its socket) and `TrustedProxySnapshot` (one reading per container). |
| `DidComm.Transports.WebSocket` | `WebSocketDidCommTransport` (WebSocket sender: one pooled socket per endpoint, never read, plus return-route session sockets per (endpoint, local DID, peer DID), each read by its own loop), `WebSocketTransportOptions` (including `ReceiveInbound` and `TrustedProxy`), `WebSocketLifecycleEventArgs`, `UseWebSocketTransport()`. The transport implements Core's internal `IInboundReceivingTransport` (`ReceiveInbound` on and STOMP off) through `InternalsVisibleTo` (#96), so it ships in lockstep with `DidComm.Core`. |
| `DidComm.Mediator.Client` | The recipient side of the mediator service and the message helpers both roles share (Phase 7; split out of `DidComm.Mediator` by #113 before either was published, namespaces unchanged). A wallet references this package only. **The recipient-side client** (#96), namespace `DidComm.Mediator.Client` — public: `MediationClient`, `PickupClient`, `MediatorClientOptions`, `CoordinateMediationVersion`, `PickupBatch`, `PickupItem`, `RecipientQueryResult`, `MediationProtocolException`, `MediationNotSupportedException`, `AddMediatorClient()` (`MediatorClientBuilderExtensions`) and `DidCommServiceBuilder.ForMediation(grant)` (`MediationServiceBuilderExtensions`, a C# 14 static extension member); internal, namespace `DidComm.Mediator.Client.Internal`: `MediatorClientChannel` (the correlator: pending map, matcher, routes, live-stream registry) behind `IMediatorExchange`, with `PendingRequest`, `MediatorAnswerTypes`, `MediatorExchangeSpec`, `MediatorRoute`, `GuardedDeadline`, `OwnDeadline`, `MediatorBusyException`, `MediatorProblemCode`, `SingleLoggerFactory`; `FeatureNegotiation`, `NegotiatedVersionCache` and `CoordinateMediationWire` (Coordinate Mediation); `DeliveryReader`, `LiveRegistration`, `LiveDeliveryStream` and `PickupLog` (pickup); `DeliveredMessageUnpacker`, `DeliveredOutcome` and `DuplicateFilter`. **Shared by both roles** — namespace `DidComm.Mediator.Coordination`, public: `CoordinateMediation` (type URIs, `Create…` builders, `TryParse…` parsers), `MediationGrant`, `RecipientUpdate` / `RecipientUpdateResult` / `RecipientQueryPaging` / `RecipientPagination` and their enums; internal: `MediationBodies` (body codecs), `BareDid`, and the 2.0 codec `CoordinateMediation20` (both directions since #99: the client's requests and the mediator's answers). Namespace `DidComm.Mediator.Pickup`, public: `MessagePickup` (type URIs, `Create…` builders, `TryParse…` parsers), `PickupStatus`, `DeliveredMessage`; internal: `PickupBodies` (body and attachment codecs, with `ReadAttachmentsEach` / `DeliveredAttachment` for the client). Namespace `DidComm.Mediator.Internal`, internal: `MediatorLog` (safe logging, bounded ids, the fault-boundary cancellation rule), `WireJson` (strict body readers). Its package depends on `DidComm.Core`, `DidComm.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` only (`Microsoft.Extensions.Options`, which `AddMediatorClient()` uses, arrives through Core and the DI package) — never `DidComm.Mediator`, `Microsoft.Extensions.Hosting.Abstractions` or ASP.NET Core (pinned by `ClientPackageReferencesTests`). Calls Core internals (`IInboundCorrelator`, `InboundMessageSnapshot`, `DidCommClient.UnpackDeliveredAsync`, …) through `InternalsVisibleTo`, and grants its own to `DidComm.Mediator` and the two test projects, so it ships in lockstep with both. |
| `DidComm.Mediator` | The mediator service (Phase 7): two mediator roles and the pipeline that joins them. **Coordinate Mediation 3.0 and 2.0**, namespace `DidComm.Mediator.Coordination` — public: `IMediationStore` / `InMemoryMediationStore`, `RecipientPage`, `RecipientStoreOutcome`, `IMediationPolicy` / `DefaultMediationPolicy` / `MediationDecision`, `IMediatorIdentity` / `MediatorIdentity`, `CoordinateMediationOptions`, `CoordinateMediationHandler` (3.0), `CoordinateMediation20Handler` (2.0, #99), `AddCoordinateMediation()`; internal: `MediationCoordinator` (the role's logic without the wire format, one instance per handler), `MediationForwardRecipientRegistry`, `ConfiguredMediatorIdentity`. **Message Pickup 3.0**, namespace `DidComm.Mediator.Pickup` — public: `IMessageStore` / `InMemoryMessageStore` with `QueuedMessage`, `MessageLease`, `MessageLeaseRequest`, `QueueStatus`, `EnqueueOutcome`, `MessagePickupOptions`, `MessagePickupQueue` with `PickupDeliveryOptions` / `PickupDeliveryResult` / `PickupDeliveryOutcome` / `GrantRevocation`, `MessagePickupHandler`, `AddMessagePickup()`; internal: `PickupCoordinator` (the role's logic without the wire format), `PickupSessionTracker` (live mode with its arrival mark, and lease owner, per live session), `PickupSizing` (the delivery byte budget), `PickupIds` (random queue ids) and `PickupIdSource` (the test seam for them). **The mediator service**, namespace `DidComm.Mediator` — public: `AddMediator()` (`MediatorBuilderExtensions`), `MediatorOptions`; internal, namespace `DidComm.Mediator.Pipeline`: `MediatorForwardSink`, `PushDispatcher` (the live-push workers: a wake-up flag and at most one operation per grant, no per-message state), `MediatorHostedService`, `RoutingDidValidator`, `MediatorMetrics` (with `PushOutcomes`), `MediatorMarker`, `MediatorInvitationHolder`, `NoRecipientRoutingKeys` (the mediator's default routing-key provider, a type of its own so start-up can tell it from a host's). Internal, namespace `DidComm.Mediator.Internal`: `MediatorPeer` (the authcrypt peer rule). The builders, parsers and wire records the roles speak, and the recipient-side client, come from `DidComm.Mediator.Client` (row above); the roles use its internals `MediatorLog`, `BareDid`, `PickupBodies.MaxIdLength` and the bounded parser overloads through `InternalsVisibleTo`. References `DidComm.Mediator.Client`, `DidComm.Core`, `DidComm.Extensions.DependencyInjection` and `Microsoft.Extensions.Hosting.Abstractions`; no ASP.NET Core. A mediator references this package and gets the client with it. |
| `DidComm.Mediator.AspNetCore` | The mediator's endpoints: `MediatorEndpointRouteBuilderExtensions` (`MapDidCommMediator`, `InvitationOobId`); internal `CompositeEndpointConventionBuilder` and `MediatorInvitationStore`. References `DidComm.Mediator` and `DidComm.AspNetCore`, and calls internals of both, so they ship in lockstep. |

**Lockstep is enforced at pack time (#97).** A package that calls another's internals depends on it
at an exact version (`[x.y.z]`) in its packed `.nuspec`; every other dependency stays a minimum.
`Directory.Build.targets` derives the pairs from the `InternalsVisibleTo` grants themselves:
`_DidCommGetFriendGrants` returns a referenced project's grants (compared by the assembly name
before any `, PublicKey=…`), `_DidCommPinFriendDependencies` (after NuGet's
`_GetProjectReferenceVersions`) rewrites `ProjectVersion` to `[%(ProjectVersion)]` for each
referenced project that grants this assembly, and `_DidCommVerifyFriendDependencies` (after
`GenerateNuspec`) reads the `.nuspec` inside the package pack reports writing (named by NuGet's
normalized version, so a version with build metadata works; never a `.symbols.nupkg`) and
fails pack, deleting the package, if a dependency on a granting package is missing or not exact.
`-p:DidCommSkipFriendPin=true` skips only the rewrite: it is the negative control, and pack must
fail with it. The edges today: `DidComm.AspNetCore`, `DidComm.Extensions.DependencyInjection`,
`DidComm.Transports.WebSocket`, `DidComm.Mediator.Client` and `DidComm.Mediator` → `DidComm.Core`;
`DidComm.Mediator` → `DidComm.Mediator.Client`; `DidComm.Mediator.AspNetCore` →
`DidComm.AspNetCore` and `DidComm.Mediator`. `DidComm.Core` no longer grants `DidComm.Adapters.NetDid`
(the adapter never used Core's internals), so the adapter and `DidComm.Transports.Http` keep minimum
dependencies. `tests/DidComm.InteropTests/DxCoverage/ShippedAssembliesListTests` pins this edge list
(a new grant is new consumer impact, so it fails until the list is updated deliberately), checks
that every grantee references its granter directly (pack pins direct references only) and that no
`[assembly: InternalsVisibleTo]` attribute hides a grant from both.

## Tests and tools

- `tests/DidComm.Core.Tests`: unit tests, organised by the same folder names as `DidComm.Core`
  (Envelopes, Facade, Protocols, Resolution, Rotation, Secrets, Threading, Transports, ...); the
  forward core's delay cap, outcome metric and DI are `Protocols/Routing/ForwardDelayCapTests`,
  `ForwardHandlerOutcomeMetricsTests` and `ForwardHandlerDiTests`; the forward addressed to the
  mediator itself is `ForwardSelfAddressedTests`, rewrap's DID subject `ForwardRewrapNextTests`, the
  QA permutations of the forward core `ForwardPipelineQaTests`, and the adversarial ones
  `ForwardPeelAdvTests`, `ForwardPeelAdv3Tests` and `ForwardHandlerPumpContextTests` (with
  `Observability/LogValuesTests` and `Transports/OutboundEndpointGuardAdv3Tests`). The Core
  internals of the recipient-side client (#96) are `Facade/UnpackDeliveredTests` (opening re-wrapped
  deliveries) and `Facade/ResolveDirectEndpointTests` (parity with `SendAsync`'s endpoint selection),
  both over `Facade/MediatedClientTestWorld`, `Transports/ReturnRouteEligibilitySnapshotParityTests`
  and `Protocols/Dispatch/ProtocolDispatcherClaimedLogLevelTests` (the no-handler line at Debug for a
  claimed message).
- `tests/DidComm.InteropTests`: fixture-driven conformance (`fixtures/` with `manifest`, `packed`,
  `payloads`, `diddocs`, `secrets`, `schema`, `spec`; `FixtureCatalog`, `Runner/FixtureDispatcher`),
  plus the `AspNetCore`, `Routing`, `Transports`, `Protocols` and `Facade` suites, `Mediator` (the
  mediator over the real endpoints, on `MediatorTestServer`: `MapDidCommMediatorTests` — the three
  routes, the 202 before the sink, pickup and live delivery over the mediator WebSocket, the
  invitation, conventions — and the acceptance suites `MediatorEndToEndTests`,
  `MediatorAcknowledgementTests`, `MediatorExpiryDelayAndAgeTests`,
  `MediatorRewrapAndRoutingKeysTests`, `MediatorStartupTests`, `MediatorTwoHopTests`,
  `MediatorUnknownNextTests`, `MediatorLiveIsolationTests` (a live recipient whose key resolution
  stalls holds up no other recipient's storage or pickup) and `MediatorCoordinateMediation20Tests`
  (#99: the real client pinned to 2.0 and negotiating against `AddMediator()`, one tenant across
  versions, replay admission of 2.0 requests, `EnableV2 = false`; with its QA suites
  `MediatorCoordinateMediation20Qa1Tests` and `Client/MediatorClientCoordinateMediation20Qa2Tests`,
  and `Client/MediatorClientThirdPartyDiscoveryTests`, the client's question answered as the
  Identus and Blocktrust mediators answer it);
  the QA permutations are the `*QaTests.cs`
  files with `QaMediatorSupport`, and the adversarial passes the `*AdvTests.cs` files
  (`MediatorPeelEndpointAdvTests`, `MediatorEgressAdvTests`, `MediatorIdentityAndLogsAdvTests`,
  `MediatorReceiveLogsAdv3Tests`, `MediatorFaultedAdv3Tests`, `MediatorLivePushAdv4Tests`); the
  timing measurements run in the `QaSerialTiming` collection, alone and after the parallel tests;
  `Transports/HttpTransportAcceptedLogTests` pins the HTTP transport's guarded 2xx line),
  `Transports/Proxy` (FR-TRN-13, both transports against `StandInProxy`, a loopback stand-in forward
  proxy that records request lines: `HttpTransportProcessProxyTests` and
  `WebSocketDirectByDefaultTests` set a process proxy through `ProcessProxyScope`, from
  `HttpClient.DefaultProxy` or each proxy variable, in the non-parallel `DefaultProxyCollection`,
  and fail on the code before the fix; `HttpTransportTrustedProxyTests` and
  `WebSocketTrustedProxyTests` cover the named proxy; `TrustedProxyRouteDriftTests` keeps the two
  copies of `TrustedProxyRoute` identical; `Adversarial/` holds the red team's probes),
  `Mediator/Client` (the recipient-side client against that mediator, through
  `MediatorClientHarness`: enrolment `MediatorClientEnrolmentTests`, pickup over HTTP and WebSocket
  `PickupClientHttpEndToEndTests` and `PickupClientWebSocketEndToEndTests`, re-wrapped deliveries
  `PickupClientRewrapTests`, a Coordinate Mediation 2.0 host `MediatorClientCoordinateMediation20Tests`
  on `FakeCoordinateMediation20Mediator`, the channel `MediatorClientChannelEndToEndTests`, routes
  `MediatorClientReceiveRouteInteropTests`, clock skew `MediatorClientClockSkewTests`, a host that is
  both `MediatorAndRecipientHostTests`; the QA permutations `*Qa1Tests.cs` / `*Qa2Tests.cs` with
  `Qa1Support`, `Qa2Support` and `PickupClientEndToEndSupport`; the red team's forged answers at the
  host's own endpoint `MediatorClientHostEndpointAdv1Tests`; the decrypt budget against the
  mediator's lease `PickupClientBudgetLeaseTests`),
  `DxCoverage` (public-API coverage of the nine shipped assemblies, and quickstart drift checks;
  `ExtensionMemberSurfaceTests` pins how a C# 14 extension member surfaces to the scanner;
  `ShippedAssembliesListTests` with its parser `ShippedProjects` checks that the packable
  `src/*/*.csproj` projects equal `CoverageComputation.ShippedAssemblies` and pins the
  internals-coupled package pairs, each with a negative control; since #97 the execution allowlist
  is empty and the reference allowlist holds only const holders and constructors of
  library-produced results)
  and `Samples` (one smoke test per sample; `MediatedAgentSmokeTests` also pins the wallet library's
  boundary, in its compiled references and in its restore graph; `MediatedAgentWalletProxyTests`
  pins that the wallet's invitation fetch never reaches a configured proxy (and owns the
  non-parallel `DefaultProxyCollection` the transport proxy tests share); `GuardedStreamReaderTests`
  pins, on a manual clock, that a timed-out or disposed read is cancelled and has ended before the
  timeout surfaces or the stream is disposed; `InboxRelaySinkTests` pins sample 04's relay bound and
  failure records). `AspNetCore/MediatorWebSocketCoreTests.cs` pins the
  same-socket / STOMP scoping of the mediator WebSocket endpoint against the public
  `MapDidCommWebSocket`.
- `tests/DidComm.Mediator.Tests`: the `DidComm.Mediator` suites (`Coordination`: fixtures, parsers,
  builders, store, handler, DI, adversarial, and for 2.0 (#99) `CoordinateMediation20HandlerTests`,
  `CoordinateMediationCrossVersionTests`, `CoordinateMediation20InteropVectorTests` (request
  shapes from the Identus and Blocktrust source), `CoordinateMediation20DiTests`,
  `CoordinateMediation20Qa1Tests` and `CoordinateMediation20Adv99Tests`); `Pickup`: wire format, store (its arrival sequence:
  `MessagePickupStoreSequenceTests`, `MessagePickupStoreSequenceAdv6Tests`), sizing, handler
  decision table, identities, faults, logs, live delivery, DI, random ids, the mailbox age limit,
  revoke-and-purge; `Pipeline`: `MediatorOptions`, `AddMediator()` DI and conflicts,
  `MediatorIdentity`, the forward sink, the push dispatcher driven through live sessions
  (`LivePushDispatcherTests`), live push and the cooldown (`LivePushTests`), the dispatcher's
  invariants D-1..D-5 (`PushDispatcherInvariantTests`), arrival order (`PushArrivalOrderTests`), the
  hosted service and `RoutingDidValidator`, a real generic host, the QA permutations in the
  `*QaTests.cs` files, and the adversarial `MediatorLifetimeAdvTests`, `OutboundGuardAdvTests`,
  `LivePushAdv4Tests`, `PushDispatcherAdv5Tests` and `PushDispatcherAdv6Tests`; `Client`: the
  recipient-side client against fake exchanges (`FakeMediatorExchange`, `FakePickupExchange`) and the
  real channel — the matcher (`MediatorResponseMatcherTests`), the channel, its deadlines and races,
  routes, DI, options, log levels and unobserved exceptions (`MediatorClient*Tests`), negotiation,
  recipients and timeouts (`MediationClient*Tests`), `ForMediation`
  (`MediationServiceBuilderExtensionsTests`), the package boundary of #113 — the client assembly's
  references, the cut line and its dependency graph (`ClientPackageReferencesTests`), fetch, acknowledgement and the live stream
  (`PickupClient*Tests`), `DeliveredMessageUnpackerTests`, `DeliveryReaderTests`,
  `DuplicateFilterTests`, `NegotiatedVersionCacheTests`; the red-team passes `*Adv1Tests.cs` (with
  `Adv1Support`), `DeliveredUnpackAdv2Tests`, `PickupClientHostileMediatorAdv2Tests` and
  `*Adv3Tests.cs` (the reproductions deferred to #111 skipped, naming it); fix round 3's
  regressions `PickupClientLiveFix3Tests` and `DeliveredUnpackFix3Tests`;
  `Coordination/CoordinateMediation20CodecTests` and `CoordinateMediation20FixtureTests`) with
  `fixtures/coordinate-mediation-3.0`,
  `fixtures/coordinate-mediation-2.0` and `fixtures/messagepickup-3.0` (the didcomm.org page
  examples normalized to valid JSON). The cases that need a real endpoint are in
  `tests/DidComm.InteropTests/AspNetCore`: `CoordinateMediationHttpTests.cs` (FR-MED-03),
  `MessagePickupHttpTests.cs` and `MessagePickupWebSocketTests.cs` (FR-PICK over HTTP and
  WebSocket), and `ReturnRouteSessionTests.BoundedSend.cs` (the size-bounded session send).
- `tests/DidComm.TestSupport`: shared helpers (`InMemorySecretsResolver`, `MultiRecipientPacker`,
  `EnvelopeReaderTestRunner`). Not shipped as a package.
- `tools/FixtureGen`: generates didcomm-dotnet's own packed vectors into the interop fixtures
  tree.
- `tools/InteropCli`: command-line peer used by the live interop runs. Its `mediator-pickup` verb
  runs a forward built by the counterpart library through an in-process didcomm-dotnet mediator,
  and `mediator-serve` keeps a didcomm-dotnet mediator running (Kestrel, `AddMediator()`,
  `MapDidCommMediator`) until its stdin closes, for counterparts that enrol with it.
- `tools/interop-live`: cross-implementation legs driven by `run-all.sh`: `python/` and `jvm/`
  (envelopes both ways against didcomm-python and didcomm-jvm) and the mediator leg
  (`run-mediator-leg.sh`, `mediator/`: the Hyperledger Identus mediator 1.2.1 and its MongoDB in
  Docker, pinned by digest, a squid forward proxy, `mediator/MediatorWallet`, a console wallet that
  references only `DidComm.Mediator.Client` and the transports, and `mediator/python/mediator_peer.py`,
  hand-written Coordinate Mediation 2.0 and Message Pickup 3.0 on didcomm-python). See
  `tools/interop-live/README.md`.
- `samples/01-Quickstart` through `samples/11-MediatedAgent` (sample 11 is two projects: the
  mediator host, and `Wallet/`, a class library that references only `DidComm.Mediator.Client` and
  the transports), with `samples/_shared` (`Narrator`, `PeerIdentityFactory`, `LoopbackPort`, which
  picks a free loopback port for a mediator whose DID must name its address before it listens, and
  `GuardedStreamReader`, which reads a live stream item by item behind a hang guard and never
  disposes it mid-read) as the common helper project.

## Inbound flow

1. A packed envelope arrives at `MapDidCommEndpoint` (HTTP POST) or `MapDidCommWebSocket` (one
   envelope per WebSocket message, text or binary, both accepted; optional STOMP session via
   `StompServerSession`). A mediator maps both
   with `MapDidCommMediator` (see "Mediator service" below).
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
`IInboundFrameConsumer` (see "Return-route client side" below). The recipient-side mediator client
takes its answers and live pushes from there, through its correlator (see "Recipient-side client"
below).

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
   over WebSocket on the endpoint's pooled socket or on a return-route session socket, as one text
   WebSocket message (FR-TRN-09, #98; a STOMP SEND frame under `UseStomp` is binary) — and returns
   a `TransportResult`. Both connect directly (FR-TRN-13): their `SocketsHttpHandler` has
   `UseProxy = false`, so no proxy from the environment is used, and its `ConnectCallback` runs
   `OutboundEndpointGuard.ConnectAsync` on every connection, pinning it to a vetted address. See
   "Outbound connections and the trusted proxy" below.
8. The facade surfaces it as `SendResult`; for a return-route send over HTTP it first turns the
   response body into `SendResult.Returned`, or a `TransportException` when the body was over the
   cap, late or unreadable. See "Return-route client side" below. `DidCommClient.SendPackedAsync`
   delivers bytes already packed: no steps 3–4 or 6, the guard always, no response read.

## Outbound connections and the trusted proxy (FR-TRN-13)

The outbound-endpoint guard can only judge the socket it opens. So both transports connect
directly, and a proxy carries their connections only when the host names one.

- **Direct (the default).** The HTTP transport's named-client handler (built in `UseHttpTransport`)
  and the WebSocket transport's `_pinnedInvoker` handler are `SocketsHttpHandler`s with
  `UseProxy = false`: `HttpClient.DefaultProxy` (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, the
  system proxy) is never read. Their `ConnectCallback` runs `OutboundEndpointGuard.ConnectAsync`
  on every connection (each 307 hop, each WebSocket connect, reconnect and session socket). A
  `SocketException` from that connect is re-thrown with the same error code and one sentence saying
  the transport connects directly and naming the `TrustedProxy` option. The WebSocket invoker is
  always built, so `DefaultConnect` never connects without it.
- **`TrustedProxy` (a `WebProxy`).** `TrustedProxyRoute.From` checks it (absolute address, scheme
  `http`/`https`/`socks4`/`socks4a`/`socks5`, a host and a port, no user info, no path, query or
  fragment; `InvalidOperationException` naming the option, never the address) and copies it. The
  HTTP transport reads it once per container (`TrustedProxySnapshot`, shared by every handler
  build and by the transport); the WebSocket transport once, in its constructor. The handler then
  has `UseProxy = true` and `Proxy` = the copy. In the callback, a connection is dialled without
  the private-address filter only when it uses the proxy as a proxy (`IsProxyConnection`): the
  endpoint matches the proxy's host and port (`IsProxyEndpoint`: one trailing dot ignored, IP
  addresses compared as addresses), and the request that opens it is the handler's own `CONNECT`
  tunnel or a request the copy does not bypass (`WebProxy.IsBypassed`, the question the handler
  asked when it chose the route). Any other connection gets the full guard: a bypassed
  destination, even one naming the proxy's own host and port (PR #124 review), and any spelling
  mismatch, so it fails closed.
- **The destination check through a proxy.** Past the proxy no connect-time check sees the
  destination, so each transport checks it itself with `OutboundEndpointGuard.Validate`, before
  handing the send to whatever connects it: the HTTP transport on every hop of its 307 loop
  (`_destinationCheck`, before the resilience pipeline, so a refusal is neither retried nor counted
  by the breaker), the WebSocket transport in `SendAsync` on every send (also under a custom
  `Connect`). `Validate` classifies the host as sent (`Uri.IdnHost`, after IDNA mapping) and
  matches `AllowedHosts` against either spelling. With `ResolveDnsNames` it also looks the name up
  locally, best effort: the proxy looks it up again. Through a proxy the WebSocket handshake follows
  no redirects (`AllowAutoRedirect = false`); direct, redirects are followed and each hop is pinned.
- **What the host owns.** The proxy must refuse internal destinations (egress filtering). Where only
  the proxy resolves names, `ResolveDnsNames = false` leaves names to it.
- **Custom paths.** A replaced HTTP primary handler or a WebSocket `Connect` delegate decides how
  and where it connects; without `TrustedProxy` nothing about them changed, and with it the
  destination check above still runs first.
- **Known limits.** One named proxy per transport: no PAC/WPAD, no proxy per destination, no
  failover (a `BypassList` can send some destinations direct, guarded). The destination check's
  name lookup is synchronous, like the facade's. An HTTP connect-time refusal on a direct
  connection (including a bypassed destination) still reaches the resilience pipeline as
  `HttpRequestException`, so it is retried and counted by the shared breaker, as before (a
  follow-up candidate on #88).

## Mediator routing core (Phase 7, #90)

`DidComm.Core/Protocols/Routing` holds the mediator-side core: `forward` is an ordinary registered
protocol handler, and everything that depends on who the recipient is runs detached from the
request. The roles a recipient talks to live in `DidComm.Mediator` (Coordinate Mediation, Message Pickup; sections below); the pipeline that joins them to this handler is the mediator's forward sink (see "Mediator
service" below).

Wiring: `DidCommBuilder.AddRoutingMediator(ForwardProcessorOptions?)`, not part of
`AddBuiltInProtocols`. It validates the options, `TryAdd`s `ForwardProcessorOptions`,
`IRecipientRoutingKeyProvider` (`StaticRecipientRoutingKeyProvider` over
`ExtraRecipientRoutingKeys`), `InMemoryForwardRecipientRegistry` as `IForwardRecipientRegistry`,
`NullForwardSink` as `IForwardSink` and the `ForwardProcessor` singleton, then
`AddProtocol<ForwardHandler>()`. Every registration is `TryAdd`, so a host replaces the sink, the
registry or the provider by registering its own before the call (`AddMediator()` registers the
mediator's own and calls this method itself, and refuses an earlier call). It also `TryAdd`s one
`ForwardMetrics` per container (the outcome counter, below) and registers `ForwardHandler` through
a factory, guarded by `DependencyCycleGuard`, that hands the handler that shared counter.
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
   `IForwardRecipientRegistry.IsRegisteredAsync` — when that subject is the DID that decrypted the
   forward (a forward addressed to the mediator itself, below), the layers are opened here first
   and the lookup is for the forward found inside. Every failure is a drop: a log line carrying the
   message id only (rate-limited per reason, below), one count on the outcome counter, a `null`
   reply, no problem report. `please_ack` is never read.
4. An admitted forward reserves its place on a bounded channel inline (`DefaultCapacity` 1024
   outstanding and `DefaultByteBudget` 64 MiB of the bytes they hold, the charge described below;
   the newest is dropped beyond either bound, decided on the request) and the handler returns
   `null`. The item is published to the pump only
   once `ProtocolContext.Acknowledged` completes: the registry-aware `MapDidCommEndpoint` creates
   it per request, completes it from `Response.OnCompleted` (the 202 is committed) and cancels it
   on `RequestAborted` or a post-handler 400, passing it through the
   `ProtocolDispatcher.DispatchAsync(…, Task acknowledged, …)` overload — so neither the processing
   nor the sink runs before the 202 is committed (only a forward addressed to the mediator
   itself is opened before it, inline; see below). "Committed" means the server has taken the 202
   for the connection, not that the client has read it: when a client that pipelined requests and
   stopped reading is reset, the 202s still in the server's buffers were committed and are lost,
   and their forwards are relayed (at-least-once; the sender may send them again). The WebSocket endpoint and a direct
   dispatcher caller pass `Task.CompletedTask` and publish immediately. The pump relays in
   publication order, so forwards received on one WebSocket connection are relayed in the order
   they arrived, while forwards of separate HTTP requests are published as each response completes
   and two sent one after the other may be relayed in either order (#119). A cancelled or faulted
   acknowledgement releases the reservation and drops the forward with a warning (id only).
   **The wait is bounded (#98, D3-F3).** A 202 that cannot be committed (a client that pipelines
   requests and never reads, so the kernel buffers and Kestrel's output pipe fill up) used to keep
   its reservation for as long as the connection lived, and a self-enrolled stranger could hold
   the whole budget so that every other tenant's forward was `Dropped-Saturated`. Now, when the
   handler that ran is the `ForwardHandler`, the endpoint returns the 202 through
   `HttpResponseAcknowledgement.WithDeadline`: its result writes the 202 (`Content-Length: 0`, as
   the server would), starts it (the `OnStarting` callbacks) and completes it itself
   (`Response.CompleteAsync()`), and once the server has taken the whole response it hands it over
   — the deadline ends and the acknowledgement completes there, not when completion callbacks run.
   Kestrel runs those only after the response was sent, one at a time and the last registered
   first, so a middleware that registers slow `OnCompleted` cleanup after the endpoint used to delay
   the acknowledgement's own callback past the deadline, which then cancelled it and aborted a
   connection whose client already held its 202: silent loss (PR #130 review, P1). A client closing
   its connection while such callbacks ran cancelled it too, through `RequestAborted`. Neither can
   happen now: after the hand-over the abort registration is disposed. The deadline is
   `ForwardResponseDeadline`, 10 s from the handler's return, on the container's `TimeProvider`
   (`TimeProvider.System` when none is registered). An honest client takes the empty 202 at once,
   and Kestrel's own default data rate (240 B/s after a 5 s grace) would end a lone stalled 202
   write in about 5.4 s. If the write has not finished at the deadline, `HttpResponseAcknowledgement`
   cancels the acknowledgement first — the forward is `Dropped-Unacknowledged` and its reservation
   released, whatever the server then does — and aborts the request (`HttpContext.Abort()`), so the
   sender never receives that 202. The hand-over and the deadline are decided under one lock and
   the abort is made under it; a write that finished a moment before the deadline fired wins even
   if its continuation has not handed it over yet (the deadline looks at the completion task). The
   timer is disposed by the hand-over, a client abort or its own firing. The deadline is armed for
   every forward the handler judged, registered `next` or not, so the abort tells a sender nothing
   about registration (D7). Because the 202 is complete when the endpoint returns, middleware that
   runs after the endpoint can no longer change it. Each abort is
   counted on a rate-limited line of the endpoint (first and every 1000th at Warning, no wire
   value). Within the 10 s a sender that does not read can still hold the shared bounds; it has to
   stall a connection again for every 10 s it wants to keep a reservation (Documented limits of the
   mediator service). The
   clock was read once, at step 3: the same instant fixes the `delay_milli` schedule
   (`ForwardProcessor.ResolveDelay`, under the processor's `MaxDelay` cap → `NotBefore`) carried in
   the queued item. The item is a `HeldForward` (the dispatcher's `ProtocolContext`, held as below)
   plus that schedule; the request token is not stored. **What a waiting forward holds, and its
   charge (#98, L-062):** from admission until the pump takes it, the item holds, of what the wire
   sized, the plaintext as UTF-8 bytes (from `InboundMessageSnapshot.Detach`), the envelope's
   recipient kid list (`UnpackResult.AllRecipientKids`: any number of kids, of any length, the
   sender's verbatim) in one string, and the thread id of the context's thread state (the
   dispatcher's, shared with its thread store: the message's `thid` or `id`, which no FR-API-09 bound
   caps); for a forward addressed to the mediator itself, also the plaintext and kid list of the
   forward found inside it. The charge against the byte budget is exactly that (`HeldForward.Bytes`):
   the plaintext's UTF-8 bytes, plus 2 bytes for each character of the kid lists and the thread id,
   plus 4 bytes for each kid. It is taken before the registry lookup, so the inline work is the same
   whether or not `next` is registered. Everything else the item holds is bounded and not charged:
   the forward's id for logs (at most 128 characters); the kids the unpack resolved or holds, the
   `from_prior` claims and the key bindings (FR-API-09: 4 096 characters a kid, 2 048 a DID); the
   algorithm names, the envelope stack and the parsed `return_route` header; the context's shared
   services — a few hundred bytes for a typical forward, under about 100 KB per forward with every
   capped value at its limit. It never holds the parsed `Message`: a JSON attachment of many small entries parses to a
   DOM tens of times its plaintext (about 45× for 190 000 one-character entries). Measured with
   forwards held behind their acknowledgement, the heap they hold is 1.0–1.3× what they are charged:
   32 flat, 4 many-entries and 8 forwards naming 192 kids of 4 000 characters (it was 3×, 45× and
   about 9 000×; `ForwardHandlerRetentionProbeTests`).
5. The pump first rebuilds the held results (`HeldForward.Restore`, one forward at a time). **The
   sink gets an equal result, not the original object:** its context is the one the handler was
   given except that `ProtocolContext.Received` is an equal `UnpackResult` rebuilt after the
   acknowledgement, with a new `Message` parsed from the same plaintext and a new kid list of the same
   kids; a verified unpack's snapshot is registered for that message with the same trust fields, so
   `InboundObservation.FromUnpackResult` reads the same facts. A failure there could only be the
   mediator's own (the plaintext parsed once already) and is `Faulted`. It then calls the
   internal `ForwardProcessor.ProcessAsync(UnpackResult, delay, notBefore)`
   overload with the schedule fixed at admission (the public overloads resolve it when called):
   parse, posture, attachment count, attachment decode (an attachment that decodes to no bytes is
   malformed, #95), then pass-through (verbatim, or one extra wrap per key the
   `IRecipientRoutingKeyProvider` returns for the DID subject of `next`) or rewrap
   (`RewrapMode.ReanoncryptToNext`: a fresh forward, addressed to the DID subject of `next` — path,
   query and fragment dropped (#95) — anoncrypted to every keyAgreement key of that DID; the
   provider is not consulted). The result carries `NextHop`, `OnwardPacked`,
   `ExpiresTime`, `Delay` and `NotBefore` (receipt time plus `delay_milli`, for a queue to store;
   the handler never sleeps on it).
6. Expiry is re-checked with `ForwardProcessingResult.IsExpired` for the time spent queued, then
   `IForwardSink.AcceptAsync(result, context, shutdownToken)` runs. A processor or sink fault is
   logged with the message id and the exception type name(s) only — the exception object never
   reaches the logger, because a `DidResolutionException` or a sink fault can carry the next-hop
   DID in its message — and swallowed.

**Delay cap (#95).** `ForwardProcessorOptions.MaxDelay` (`TimeSpan?`, init-only and not positional,
so binary/API compatible with 1.5.1; value equality now includes it) caps the honoured
`delay_milli`. The processor validates it (0 to `int.MaxValue` ms) and copies it at construction.
`ExtractDelay(message, maxDelay)` takes the cap in whole milliseconds (floored), holds a positive
hint for `min(n, cap)`, and for a negative hint clamps `|n|` to the cap before the single
`RandomNumberGenerator.GetInt32` draw in `[0, min(|n|, cap)]`, all in `long`; `null` keeps the 1.5.1
bound of `int.MaxValue` ms, and 0 makes every forward due at once. `ForwardProcessor.NotBeforeOf`
turns the hold into the not-before, pinned to `DateTimeOffset.MaxValue` when the sum would run past
the end of time, so it never throws. The handler reaches it through
`ForwardProcessor.ResolveDelay` and the public `ProcessAsync` overloads through `ResolveSchedule`,
so the clamp comes before the only draw on every path. `AddMediator()` sets it from
`MediatorOptions.MaxForwardDelay` (1 hour).

**Forwards addressed to the mediator itself (#95, FR-ROUTE-06 receiver side).** Rewrapping hands
the next hop a fresh forward whose `next` is that hop. When the DID subject of `next` is the DID of
the key that decrypted the forward (`UnpackResult.RecipientKid`), the handler does not look that
DID up (the mediator's own DIDs are never registered and are public). Instead
`ForwardHandler.OpenSelfAddressedAsync` opens the layers **inline, on the sender's request, before
any reservation**: `ForwardProcessor.PeelAsync` takes the single attachment as it is and unpacks it
with the mediator's own client through `DidCommClient.UnpackAnoncryptedPlaintextAsync` — the
receive rules (size, FR-API-05 expiry, consistency) with one restriction: the layer must be exactly
an anoncrypt envelope around a plaintext message without `from_prior` (what rewrapping produces, and
what a sender's forward layers are, FR-ROUTE-02). `EnvelopeReader` refuses any other shape — a
plaintext or signed layer, an authcrypt layer, a signature or another envelope inside — from the
envelope kind alone, with `MalformedMessageException`, and the facade refuses `from_prior`; no
sender or signer lookup is supplied, so no DID the sender chose is ever resolved. What is inside is
then screened as `HandleAsync` screens an arriving forward, at the same receipt reading: a forward,
not expired, `next` a DID. A forward inside that is again self-addressed is opened in turn, at most
`MaxSelfAddressedLayers` (8, `MediatorEndpointExpander.MaxForwardLayers`) decryptions per received
forward after the endpoint's own, each layer within `MaxReceiveBytes` (an opened layer can be larger
than its carrier, because a `data.json` attachment is re-serialized); one more layer is
`Dropped-Malformed`. Only the decrypting DID counts as "itself": another DID of the mediator
named as `next` is looked up and dropped as unknown. The forward found inside is then looked up in
the registry (the request's one lookup) and admitted in the received forward's place: the work item
carries it (as its plaintext and kid list, like the received forward), the byte budget is charged
with the received forward's plaintext and kid list plus its own, and the thread id once, its
not-before is the latest of the holds asked for by the received forward and by every opened layer
— each capped and drawn at the receipt reading, never added, so a chain is held at most the cap —
and its `expires_time`
on the relayed result is the earliest of the received forward's and every opened layer's — checked
inline, again on the pump, and stored by the queue. So the sender pays for opening what it built,
as for the outer unpack, and the pump never runs a stranger's decryption or DID resolution: it only
processes and sinks the forward found inside. Whatever stops a layer from opening — its shape, its
structure, a key the mediator does not hold, any other refusal of the unpack — is
`Dropped-Malformed` (`Dropped-Expired` when what is inside has expired), never `Faulted`; a
plaintext or signed-only layer inside is malformed too (the shape rule refuses it before the
posture rule would). The received forward is counted once, by how the chain ended, and its id is the one
logged. This was missing on `main` before #95, so a two-mediator route behind a rewrapping mediator
never delivered; the first #95 version opened the layers on the pump, which let a stranger's slow
DID stall every relay (adversarial finding AD-1), and the next opened any envelope shape, resolving
the sender DIDs an inner authcrypt or signed layer named (ADV-3).

**Bounded ids, no message type, and the pump's context (#95).** Every message id on the receive
path — `ForwardHandler`, `ProtocolDispatcher`, `InboundAdmission`, the return-route sessions, the
endpoints, the mediator — goes through `LogValues.Id`: up to 128 characters as it is, a longer one
as "(id of N characters)" (on `main` the handler and the dispatcher wrote ids raw). A stranger's
message type is never written to a log line: the dispatcher's no-handler line names the bounded id
only (the type stays on the `didcomm.handle` span's `didcomm.message.type` tag). Lines a stranger can repeat
are rate-limited — the first and every 1000th at their level, Debug otherwise — where listed here:
the handler's drop lines per reason, the mediator sink's drops, `InboundAdmission`'s malformed
`return_route` and replay-refusal lines, and the dispatcher's FR-THR-04 rule-2 drop. Others are one
line per message: the dispatcher's no-handler Information line, the endpoints' rejection lines for an
envelope that cannot be unpacked, the consumed pure-ACK Information line and the opt-in Trace 2.0
line.
The pump is started with the execution context suppressed, so it carries no ambient state of
whoever built the handler (an `AsyncLocal`, a request's activity).

**Outcome counter and drop lines (#95).** Each forward the handler ends is counted once on
`didcomm.forward.outcomes` (`ForwardMetrics`, tag `didcomm.forward.outcome`): `Dropped-Malformed`
(not a forward, malformed, or refused by the processor as inconsistent or malformed),
`Dropped-NotEncrypted`, `Dropped-Expired` (on arrival or while queued), `Dropped-Unknown` (`next`
not a DID or not registered, or, in rewrap mode, a next hop whose DID resolves to no usable
`keyAgreement` key or is of a refused method — `DidResolutionException`,
`UnsupportedDidMethodException`, logged "the next hop cannot be reached"), `Dropped-Saturated`, `Dropped-Unacknowledged` (the acknowledgement was cancelled or faulted — over HTTP also when the
202 was not committed within the 10 s deadline — or the request was cancelled), `Dropped-Shutdown`, or `Faulted` (a fault of the mediator's own
collaborators: the registry, clock or logger while screening, the processor or the sink on the
pump, a resolver or key service that throws anything else — never a layer a sender built that fails
to open). A
forward the sink returns from normally is the sink's to count: the mediator's sink counts it, a
host's own sink counts nothing. Any stranger can cause a drop, so the drop lines are rate-limited
per reason (`RateLimitedLog`, Core's copy of the mediator's rule): the first and every 1000th at
Warning — "expired while queued" at Information — and the rest at Debug, with the same text. The
public constructor creates and disposes its own `ForwardMetrics`; the factory `AddRoutingMediator()`
registers uses an internal constructor that takes the container's shared one, which the
mediator's sink counts on too.

Shutdown: `ForwardHandler` is `IDisposable` / `IAsyncDisposable` and the container disposes it with
the provider. Dispose stops admission, cancels the pump, discards queued forwards and unblocks the
forwards still waiting for their acknowledgement (each releases its reservation and counts as
discarded; a later acknowledgement publishes nothing) — fail closed; `DisposeAsync` additionally
waits for the pump and those pending forwards, up to 5 s. `FlushAsync` (test seam) waits for
pending acknowledgements within its timeout before fencing the pump.

`ForwardProcessor.ProcessAsync(string)` stays as a convenience: it unpacks with the mediator's
client and follows the same path, so a plaintext or signed-only forward is refused with
`ConsistencyException`. `samples/04-MediatorAgent` (since #97) runs `AddRoutingMediator()` behind the
registry-aware `MapDidCommEndpoint` with a host `IForwardSink` (`InboxRelaySink`) that relays to the
recipient's inbox with `SendPackedAsync`, and composes the same relay without a container; see
"Demonstrated by" below.

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

### Demonstrated by (#97)

- `samples/04-MediatorAgent`: `AddRoutingMediator(new ForwardProcessorOptions(...) { MaxDelay = … })`
  behind the registry-aware `MapDidCommEndpoint("/didcomm")`; `InMemoryForwardRecipientRegistry`
  (`Register`, `Unregister`, `Count`, `IsRegisteredAsync` through `IForwardRecipientRegistry`); the
  host sink `InboxRelaySink : IForwardSink`, registered before `AddRoutingMediator()`, which awaits
  `context.Acknowledged`, re-checks `IsExpired`, and relays with `SendPackedAsync` to an inbox from
  a bounded operator-filled directory (16 entries), each relay bounded by its own timeout (5 s) and
  recorded once whatever the outcome; `NullForwardSink` for a registered recipient with no inbox; a
  forward for an unregistered DID dropped with 202 and seen as `Dropped-Unknown` on
  `didcomm.forward.outcomes`; and section 8, the same relay without a container (the public
  `ForwardHandler` and five-argument `ForwardProcessor` constructors, `StaticRecipientRoutingKeyProvider`,
  both `ProtocolDispatcher.DispatchAsync` overloads with an acknowledgement held back, `Dispose` then
  `DisposeAsync`). Known limit, stated in the sample: the sink runs on the handler's single pump, so
  a slow inbox delays the forwards behind it by up to the relay timeout each (L-064); a production
  relay hands each forward to a bounded per-recipient queue.
- `samples/11-MediatedAgent` and cookbook FF: the same routing core under `AddMediator()`.

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
   dispatch; the mediator handlers (#93, #94) refuse unauthenticated requests themselves
   (FR-MED-05, FR-PICK-01).
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
   `InboundReplayGate` and dispatches with no session. `MapDidCommMediator` (#95) maps these
   endpoints themselves, so steps 1–4 run there.
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
  `ProtocolContext.Acknowledged` completes once the `200` or `202` is committed (from
  `Response.OnCompleted`, or, for a response under a deadline, when the endpoint's own result has
  handed it to the transport); a forward never has a session, so `ForwardHandler` always waits for
  a `202`. The public `MapDidCommEndpoint(pattern)` puts no deadline on a `200`;
  `MapDidCommMediator`'s HTTP endpoint completes its `200`s itself and aborts one the server has
  not taken within the pickup lease (Mediator service, below).

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
  socket; it writes each frame as one text WebSocket message (FR-TRN-09, #98: every frame is an
  envelope the session packed itself, so UTF-8 JSON), and its "may I send?" check and the start
  of `SendAsync` share a lock with `ApplyHeader`
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

- Settings: validated and copied at construction (`Window`, `ClockSkew`, `RequireCreatedTime` and
  the three bounds); a later change to the `ReplayProtectionOptions` object has no effect. The guard
  depends on nothing else — in particular not on `DidCommOptions` (#105: `ExpiresClockSkew` is
  FR-API-05's, `ClockSkew` the replay rule's own, default 60 s).
- `MaxMessageIdLength` (default 512) is checked first → `MessageIdTooLong`. Then whole-second
  arithmetic on the caller's reading: `created_time < now − Window` → `TooOld`; `created_time > now
  + ClockSkew` → `FutureDated`; expiry = `created_time + Window + ClockSkew`. No `created_time` →
  `MissingCreatedTime`, or with `RequireCreatedTime = false` expiry = `now + Window` (the caller's
  reading).
- Under one lock: purge the min-heap of `(sender, id)` by expiry (entries strictly earlier than the
  reading; the only removal path, so heap and buckets never diverge) and advance `purgedAt`, the
  latest instant purged at, if the reading is later. Then `TooOld` if the message's
  expiry is earlier than `purgedAt` (its entry may have been purged — reachable only with a reading
  behind `purgedAt`), `Duplicate` if the sender's bucket holds the id (no refresh), `Saturated` at
  `MaxEntries` (default 32 768) or at the bucket's `MaxEntriesPerPeer` (default 1024) — nothing
  evicted, `SaturationCount` incremented — else insert. A bucket keeps one copy of its sender
  subject, shared by its heap entries.
- Retention property (messages with `created_time`; the limits below cover those without): an
  entry lives until `created_time + Window + ClockSkew` inclusive while age
  rejection starts at `created_time < now − Window`, so there is no instant at which a captured
  request is neither remembered nor too old — also when readings reach the lock out of order (the
  gate reads the clock before the lock, so two requests can race) or the host clock steps back,
  because anything that may have been purged is refused. The exact rule is "admitted only if
  `created_time + Window + ClockSkew ≥ purgedAt`": a reading D seconds behind refuses messages older
  than `Window + ClockSkew − D` by that reading, and one stamped at the reading only once
  D > `Window + ClockSkew`. Cost: after a step back of more than `Window + ClockSkew`, fresh in-scope
  messages are `TooOld` until the clock is back within that span of `purgedAt`, or the process
  restarts; those refusals are logged (rate-limited Warning with the lag in seconds). An entry stamped `now + ClockSkew` lives
  `Window + 2 × ClockSkew` (420 s at the defaults).
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
  change an admission decision, a session push or the response that carries it. The endpoints'
  unpack-rejection, handler-fault and callback-fault lines, on HTTP, in both WebSocket loops and
  for a malformed STOMP frame, log exception type names (and the bounded message id where there is
  one) — never the exception object, whose text can quote the peer's values (#95; before, they
  logged the whole exception). These rejection lines are not rate-limited; the malformed
  `return_route` and replay-refusal lines are (the first and every 1000th at Warning). Every id is
  bounded through `LogValues.Id` (#95).

Known limitations (documented, not fixed; W-2..W-4 in the #91 plan; W-1, a server clock that
jumps forward past an entry's retention and back re-admitting a request, is closed by #105 for
messages that carry `created_time`, at the cost described under the retention property): freshly
minted DIDs can saturate the global replay
budget and deny in-scope traffic for up to `Window + 2 × ClockSkew` (put rate limiting in front
of a mediator: `MapDidCommMediator(...).RequireRateLimiting(...)`, #95); the first delivery of a captured
authcrypted `all` message owns the session socket (confidentiality holds); a handler that throws
after pushing gets the uniform `400` and its push is discarded. Two more:

- **A request without `created_time` and a clock jump.** The #105 rule needs a `created_time`. With
  `RequireCreatedTime = false`, a request without one is remembered for `Window` from the clock
  reading it arrived at. If the host clock jumps forward past `Window` and back, the guard forgets
  it, and the same request is admitted again, as it is once `Window` has really passed.
- **Self-made DIDs can hold every session.** A WebSocket session needs only an authcrypted message
  with `return_route: all`, so freshly minted DIDs on idle WebSocket connections can fill the total
  bound (`MaxSessions`, 4,096 by default). Until some of those sessions close, a new peer gets no
  session: its messages are still dispatched, but nothing is sent back on its socket and nothing is
  pushed to it live. HTTP requests are not affected. Limit how many WebSocket connections one
  client may hold, in front of the endpoint.

### Demonstrated by (#97)

- Cookbook EE (`Section_EE_LiveDeliveryOverWebSocket`): a sample `IProtocolHandler` on a mediator host
  that answers a `return_route: all` request through `context.Session` (every `IReturnRouteSession`
  member; `TrySendAsync` unbounded, and bounded → `TooLarge` then `Sent`), compares
  `context.ReturnRoute` with `ReturnRouteHeader.None` / `Read` / `TryRead`, and waits on the session's
  `Closed` token; the host registers its own `InMemoryReturnRouteSessionRegistry` with
  `ReturnRouteSessionOptions`.
- `samples/11-MediatedAgent`: `ObservedSessionRegistry`, a host decorator over the in-memory registry
  (every `IReturnRouteSessionRegistry` member, `SessionRegistration`, the best-effort
  `SessionOpened` / `SessionClosed` events), the operator's `Find`, and the mediator's replay guard
  built from `ReplayProtectionOptions` with its counters read. Note for hosts: a wrapped registry or
  guard is no longer the in-memory type the mediator looks for, so the mediator's
  dropped-notifications metric and purge-tick warning for it go quiet; the sample reads
  `DroppedNotifications` itself.
- Cookbook FF.3: `ReplayProtectionOptions` (every property, `Validate`) and a standalone
  `InMemoryReplayGuard` admitting an id once, then `Duplicate`, `FutureDated`, `TooOld`,
  `MissingCreatedTime`, `MessageIdTooLong` and `Saturated`; FF.2(b) runs the admission step itself
  (`TryAdmit` before `DispatchAsync`, the library's scope rule rebuilt from `MediatorServiceProtocols`
  and `ReturnRouteHeader`), because a receive path of the host's own must do what the library's
  endpoints do (see `ProtocolDispatcher`).

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
The HTTP transport's 2xx line names the scheme and the status, never the endpoint, and is
guarded, so a throwing logging provider cannot turn a delivered send into a failure (#95).

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
- Clock skew (#105): FR-RR-08 admission, on the receiver and in the client's inbound consumer,
  refuses `created_time > now + ReplayProtectionOptions.ClockSkew` in whole seconds (default 60 s),
  so a peer whose clock runs up to a minute ahead is admitted; one container has one value for both
  roles. `SendResult.Returned` is not replay-admitted, so over HTTP no replay setting decides the
  reply; over WebSocket the mediator's replies and pushes are judged by the client's own
  `ClockSkew`. `DidCommOptions.ExpiresClockSkew` (default 0) governs FR-API-05 expiry only.

### Demonstrated by (#97)

- `samples/04-MediatorAgent` section 5: Alice's Discover Features query with
  `SendOptions.ReturnRoute = All`, answered in the HTTP 200 body and read from `SendResult.Returned`
  (authenticated, threaded to the question, `FeatureDisclosure.Roles` = `mediator`).
- Cookbook EE.2: Bob's hand-built `WebSocketDidCommTransport` (four-argument constructor) with a sample
  `IInboundFrameConsumer` that reads each `InboundFrame` and keeps only frames whose authcrypt sender
  is the frame's peer (DID subjects, ordinal); EE.3 and sample 11 use the three set-up calls
  (`UseWebSocketTransport(o => o.ReceiveInbound = true)`, `UseInboundDispatch()`,
  `AddMediatorClient()`).
- Cookbook CC: `QueueTransport` (`IsQueueTransport`, both overloads) and `SendAsync` refusing the
  queue URI; cookbook Z: a custom transport reading `TransportRequest.LocalDid` / `PeerDid` /
  `ReturnRoute` / `MaxResponseBytes` and refusing a return-route send it cannot carry.

## Coordinate Mediation 3.0, mediator role (Phase 7, #93)

The enrolment protocol of the mediator service (PRD §8b, FR-MED-01..06 and -08;
https://didcomm.org/coordinate-mediation/3.0/): a recipient asks for mediation, gets the mediator's
routing DIDs, and registers the DIDs it wants forwards accepted for. It lives in the new package
`DidComm.Mediator`, namespace `DidComm.Mediator.Coordination`, and is a protocol handler like any
other (the builders and parsers, `CoordinateMediation`, its wire records and `MediationBodies` are
in `DidComm.Mediator.Client`, same namespace, since #113, because the recipient side uses them too): it is reached through `ProtocolDispatcher` and knows nothing about transports.

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
  grant that another grant holds the DID. A sender without a grant learns nothing, but under the
  default open enrolment getting one is easy: a freshly made DID is granted after one
  `mediate-request` and can ask with its second message. An allow-list `IMediationPolicy`, which
  enrols known peers only, keeps strangers from asking.
- **Open enrolment fills the store.** The default policy grants any authcrypt sender and grants do
  not expire on their own (owner decision: the protocol has no message that tells a recipient its
  grant lapsed; a host ends a grant with `RevokeAsync`). Self-made DIDs can exhaust `MaxGrants` and
  `MaxRecipients`; existing grants keep working. A public mediator needs an allow-list or rate
  limit in its policy.
- **Forward lookup.** One probe either way; a hit compares one key more than a miss (the same as
  `InMemoryForwardRecipientRegistry`). This is a limit, not a guarantee: after the lookup, a
  forward to a registered `next` does more work than one that is dropped (it reserves its place in
  the forward queue and is tracked until its `202` goes out). On a fast local network that small
  difference can still be measured with many samples: about 2 microseconds, at 5,000 samples per
  class over loopback.
- **Revocation.** `RevokeAsync` stops new forwards for the grant's DIDs. A forward already admitted
  by `ForwardHandler` still reaches the sink; the pickup queue looks the recipient up when the
  message arrives and again after storing it, and a live push checks once more before it sends
  (#95; "Message Pickup 3.0" and "Mediator service" below). `MessagePickupQueue.RevokeAndPurgeAsync`
  revokes and drops what is queued in one call.
- **A DID rotation moves nothing.** The mediator does not act on `from_prior`. A peer that rotates
  to a new DID is a new peer here: the new DID asks for its own grant and sees only its own queue.
  The old DID keeps its grant, its registered DIDs and its queued messages until the host revokes
  it (`MessagePickupQueue.RevokeAndPurgeAsync`). While the old grant holds a DID, the new DID
  cannot register it.
- **Elsewhere:** the 2.0 handler is the next section (#99). The recipient role is
  `MediationClient` (#96, "Recipient-side client" below), and the start-up check of routing DIDs
  against their DID documents (FR-MED-07) is `AddMediator()`'s (#95, below).

### Demonstrated by (#97)

- `samples/11-MediatedAgent`: `AddMediator()` with an `InMemoryMediationStore` instance, a
  `MediatorIdentity`, and `AllowListMediationPolicy` (wraps `DefaultMediationPolicy`; compares DID
  subjects ordinally in one pass with no early exit; bounded at 1024 peers; gates only the first
  grant, so removing a peer from the list must also end its grant with `RevokeAndPurgeAsync`); Carol's
  `mediate-deny` comes back as `null`; the operator console calls every `IMediationStore` member
  (`GrantAsync` pre-provisions Carol, `RevokeAsync` ends it).
- Cookbook CC: enrolment with `MediationClient`, "the first peer to register a DID gets it" shown
  with a `ClientError`, the wrong peer (`MediationNotSupportedException`), and every
  `CoordinateMediation` builder and parser round-tripped. FF.1: an allow-list host;
  FF.2: `AddCoordinateMediation()` before `AddRoutingMediator()`, and the handler built without a
  container (`CoordinateMediationHandler`, every `CoordinateMediationOptions` property).

### Coordinate Mediation 2.0, mediator role (#99)

The same role in the version the Identus and Blocktrust mediators and their wallets speak
(FR-MED-10; https://didcomm.org/coordinate-mediation/2.0/). `CoordinateMediation20Handler` is the
3.0 handler's twin: the same pipeline (peer → parse → act → reply) over its own
`MediationCoordinator` instance, built from the **same** `IMediationStore`, `IMediationPolicy` and
`IMediatorIdentity` singletons. A peer is therefore one tenant in both versions: one grant, one set
of registered DIDs, listed and removed through either version, and the store's ownership rule
(D5) holds across them. Each handler has its own coordinator, so each logs under its own category
and keeps its own rate-limited counters (at most twice the log lines, never shared state).

What differs is only the wire layer, through the internal codec `CoordinateMediation20`
(`DidComm.Mediator.Client`), which now works in both directions:

| 3.0 | 2.0 |
|---|---|
| `mediate-grant` `routing_did`: array of every routing DID | a single string: the grant's **first** routing DID |
| `recipient-update` → `recipient-update-response` | `keylist-update` → `keylist-update-response` |
| `recipient-query` → `recipient` with `dids` | `keylist-query` → `keylist` with `keys` |

- **The grant.** `MediationCoordinator.RequestAsync` returns the stored grant's routing DIDs (the
  configured list when the grant was made, or the decrypting DID when none is configured); the 2.0
  reply takes element 0 inside the reply's fault boundary, so a host store that returns a grant
  without routing DIDs is "not answered", logged by exception type. A repeated request returns the
  stored grant unchanged, so reordering the configuration later does not change what a peer was
  given. The order rule (first = primary) is documented on `CoordinateMediationOptions.RoutingDids`
  (FR-MED-07); forwards to every routing DID keep being relayed.
- **Replies** always carry the canonical type strings (a 2.0 mediator in the field matches types by
  exact string), `thid` per DD-11, and, for `keylist`, `pagination`. No problem reports: the
  protocol defines none.
- **Wiring.** `AddCoordinateMediation()` registers the 2.0 handler (a `TryAdd` factory, then
  `AddProtocol<CoordinateMediation20Handler>()`) when `CoordinateMediationOptions.EnableV2` is `true`
  in that call's settings (the default), read from the same scratch instance as
  `UseAsForwardRecipientRegistry`. Both handlers' factories then run
  `VerifyVersion2Registration`: the `EnableV2` in effect (`IOptions`) must agree with whether a 2.0
  handler was registered, or building either handler throws `InvalidOperationException` naming the
  property (a `Configure<>`, configuration binding, or two calls that disagree). Under `AddMediator()`
  this happens at start-up, when the hosted service resolves the dispatcher. The registry keys the
  two handlers by PIURI and resolves a type by major version, so 2.x and 3.x never cross; Discover
  Features discloses both with `roles: ["mediator"]` (the order of disclosures is not fixed).
- **Replay.** 2.0 is one of `MediatorServiceProtocols`, so the endpoints admit its requests under
  FR-RR-08 before dispatch, and FR-RR-09 stamps the replies. A hand-built receive path must admit
  them too (cookbook FF.2 (b)).
- **The same documented limits.** The limits of the 3.0 role (its "Documented limits" above) apply
  to 2.0 peers unchanged: the first peer to register a `recipient_did` gets it; `client_error` on a
  `keylist-update` `add` tells an enrolled peer that another grant holds the DID; and under open
  enrolment, self-made grants can fill the store.
- **Interop notes (from the real implementations' source, recorded in the #99 plan).** The Identus
  wallet SDK sends `keylist-update` without `return_route`: the update is applied and the reply has
  no channel over HTTP. Blocktrust's wallet sends no `created_time`, so replay admission refuses its
  requests unless the host sets `Replay.RequireCreatedTime = false`. Both real 2.0 mediators write a
  string `routing_did`, ignore `paginate` and send no `pagination`.
- **The client side of the same release (owner decisions 2026-11-18).** The recipient client's
  version question changed so that it works with the Identus mediator's Discover Features answer
  (two version-prefix queries, and a `roles` list naming no role of the protocol says nothing; see
  "Recipient-side client" below). The client still cannot enroll with a Blocktrust mediator: its
  first answer comes from a new DID with a `from_prior` that has no `iat` and no `from`, and its
  `keylist-update-response` has no `result`; Core and the client refuse those on purpose (the
  library stays spec-strict), and #98 targets Identus.

Demonstrated by: cookbook CC (a wallet pinned to 2.0 enrolls with the same mediator, gets the first
routing DID and lists what it registered in 3.0), FF.2 (a) (`EnableV2`), FF.2 (b) (the handler built
by hand over the 3.0 handler's store, policy and identity, answering a 2.0 `mediate-request`).

## Message Pickup 3.0, mediator role (Phase 7, #94)

Source: https://didcomm.org/messagepickup/3.0/ ; PRD §8c FR-PICK-01..10. It lives in
`DidComm.Mediator`, namespace `DidComm.Mediator.Pickup`, beside Coordinate Mediation, and is a
protocol handler like any other: no transport code, no ASP.NET Core. The builders and parsers,
`MessagePickup`, with `PickupStatus`, `DeliveredMessage` and `PickupBodies`, are in
`DidComm.Mediator.Client`, same namespace, since #113, because the recipient side uses them too.

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
  **Arrival sequence** (#95, PR #109 review; all three init-only): the store gives every record it
  stores a `QueuedMessage.Sequence` greater than that of every record it stored before (a value the
  caller set is ignored; `InMemoryMessageStore` keeps one counter for the whole store, starting at
  1), and the record keeps it when it is given back, passed over or its lease runs out; every
  `QueueStatus` reports `HighWaterSequence`, the highest one assigned when the status was read (0
  before the first; read in the same step as the counts, and never lowered by a removal);
  `MessageLeaseRequest.AfterSequence` (`null` by default: any record) limits a lease to the records
  with a greater sequence. A refused enqueue takes no number. The mediator decides "arrived after"
  by it — which records a live session is pushed — never by two clock readings. The `IMessageStore`
  remarks make it part of the contract a host store keeps; what the mediator does with a store that
  does not is under `TakeNextAsync` below.
- `PickupCoordinator` (internal): the role without the wire format — authorisation, status,
  lease → check → push → trim, acknowledgement, the live flag, `DeliverAsync`, and
  `HasLiveTarget`, `TakeNextAsync`, `PushNextLiveAsync` and the `LiveDeliveryStarted` event for
  the mediator's push workers.
  One instance, owned by `MessagePickupQueue` and shared with every handler over it, so a 4.0
  handler shares leases, live flags and log counters.
- `PickupSessionTracker` (internal): one `PickupSessionState` per live session, held in a
  `ConditionalWeakTable` keyed by the session object (no bound of its own; it cannot outlive the
  session). It carries the live mode (`LiveMode`: the live-delivery framing set by the handler
  that turned live mode on, and `AfterSequence`, its arrival mark — the `HighWaterSequence` of the
  status that answered the `live-delivery-change`; turned on again while on, it keeps its first
  mark), a random owner token for the session's leases, and a hook on
  `IReturnRouteSession.Closed`.
- `PickupSizing` (internal): the byte arithmetic of `max_delivery_bytes`.
- `MessagePickupQueue` (public): `DeliverAsync`, `RemoveAsync`, `PurgeExpiredAsync`,
  `RevokeAndPurgeAsync`, `MaxAdmissiblePayloadBytes` — what the mediator's sink and a host call.
  Its public constructor takes no id generator (#95); an internal constructor takes the id source,
  which `AddMessagePickup()` fills only from the internal `PickupIdSource` test seam.

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

A `live-delivery-change` (`PickupCoordinator.SetLiveDeliveryAsync`): `live_delivery: true` needs a
live (WebSocket) session in mode `all`. Without one (HTTP, no session, or a session the peer
downgraded with `return_route: none`, `thread` or a malformed value, FR-RR-05) the answer is the
`e.m.live-mode-not-supported` problem report and the flag is not touched (FR-PICK-07). A downgraded
session can carry neither a status nor a push, so the report is not written either. A flag that was
already on stays on, and its pushes resume when a later `all` restores the session. Otherwise the
status is read first and its `HighWaterSequence` is the mark (FR-PICK-06). A closed session is
answered `status{live_delivery: false}`, whenever the close lands. `live_delivery: false` turns the
flag off and is answered with `status` on any connection.

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

A new message (`MessagePickupQueue.DeliverAsync`; the mediator's sink calls it with
`AllowLive: false`, a host may call it with the default `true`): size bound first
(`MaxAdmissiblePayloadBytes`, before any lookup) → expiry → `FindGrantByRecipientDidAsync` → the
live target, if any (only with `AllowLive` and a message that is due) → `EnqueueAsync` (a random
id; `ExpiresAt` = the earlier of the forward's expiry and now + `MaxMessageAge`), already leased to
that session when there is one → the registration re-check → the push (a `delivery` with one
attachment and no `thid`, size-bounded), on the caller's thread. Pushed → `PushedLive`; not taken →
the lease is released at once and the outcome is `Queued`. The live target is the newest session
`IReturnRouteSessionRegistry.Find(peer)` returns that has the flag on, `Mode == All`, and whose own
`PeerDid` is the peer. Packing the `delivery` resolves the recipient's DID, so with `AllowLive` the
call takes as long as that resolution: the reason the mediator never pushes live inside it.

What the mediator's push workers call (#95, PR #109 review rounds 1 and 2) takes from the store
instead of naming a record:

- `TakeNextAsync(grantPeerDid, lease, afterSequence, now, ct)`: `LeaseAsync` for one record of the
  grant (no recipient filter, no byte cap, `AfterSequence = afterSequence`) — due, unexpired,
  unleased, not passed over, oldest first — then the contract checks of a `delivery-request`
  (step 3; a record with no arrival sequence (`Sequence` < 1), or not after `afterSequence`, is
  outside the contract too: nothing is handed over, the take is given back and the push counts
  `Push-Failed`; a pickup does not look at the sequence, so it still delivers those records) and
  its registration check (step 4: a record whose DID is no longer registered to the peer is
  removed by id → `Removed`). Nothing handed out → `Empty`. Whatever it took and does not hand
  over is released in a `finally`.
- `PushNextLiveAsync(grantPeerDid, ct)`: the same live target as `DeliverAsync`, looked up again,
  its live mode read once (none → `NoSession`) → `MarkLeasing` → `TakeNextAsync` under a lease
  owned by the session's token for `LeaseTimeout`, with `afterSequence` = `LiveMode.AfterSequence`:
  what the status that answered the `live-delivery-change` counted is the recipient's to pick up
  (FR-PICK-06) →
  a take that came back after the caller's deadline sends nothing → unless the session is closed,
  the one-message `delivery` in the session's framing through `TrySendAsync(delivery,
  MaxDeliveryBytes, ct)`. `Sent` with the session still open → `Sent`: the record stays leased to
  the session until `messages-received`, its close or `LeaseTimeout`. `TooLarge` → passed over
  (`PassOverAsync` until `now + LeaseTimeout × 2^PassOvers`), as a pickup does → `PassedOver`.
  Anything else — not taken, closing, a fault, a cancellation — gives the lease back in a
  `finally` (`ReleaseAsync` naming the lease, never cancelled) once the send has ended, however
  long that takes; `NotSent`, or the fault or cancellation propagates after the release.
- `HasLiveTarget(grantPeerDid)` is the synchronous check the sink makes before waking a grant; its
  answer may be stale by the time the push runs, which is why `PushNextLiveAsync` looks again.

The arrival mark (#95, PR #109 review, ADV-5): `SetLiveDeliveryAsync` reads the grant's status
first and turns live delivery on with that status's `HighWaterSequence` as the mark
(`PickupSessionState.SetLive(framing, queue.HighWaterSequence)`); the live step then leases only
records with a greater sequence. So a message is either counted in the status the recipient was
answered with — and is the recipient's to pick up — or entered the store after that status read and
is pushed, whatever the clocks did (two readings of `DidCommOptions.Clock` decided it before: a clock
step back, or a delivery that read the clock before live delivery went on and stored its message
after, left a message stored for a live session unpushed). Turning live delivery on raises
`PickupCoordinator.LiveDeliveryStarted` (a handler that throws is logged and ignored), which the
dispatcher turns into a wake-up of the grant, so a message stored after the status read but before
the session was live — whose own wake-up found no live session — is pushed too. A peer with no grant
reads an empty status without a store call, so its mark is 0. `ReadQueueAsync` refuses a status
with a negative mark (or a negative count) as outside the contract: that request gets no reply.

Added by #95:

- **Random ids.** `PickupIds.NewRandomId()`: 16 bytes from `RandomNumberGenerator` as 32 lowercase
  hex characters (unreserved, within `PickupSizing.MaxMintedIdLength`). The container's
  `IMessageIdGenerator` is never read for queue ids, so a host's sequential generator cannot make
  ids predictable; every id is still checked before it is stored.
- **Mailbox age limit.** `MessagePickupOptions.MaxMessageAge` (30 days; `null` turns it off), copied
  at construction, is folded into the record's `ExpiresAt` at admission, on `DidCommOptions.Clock`
  with no skew and saturating near `DateTimeOffset.MaxValue`; "expired at admission" still uses
  the forward's own expiry. Every expiry path of the store — lease selection, `status`, the
  per-grant and store-full sweeps, `PurgeExpiredAsync` — then applies it unchanged, to waiting,
  leased and passed-over records alike. No `IMessageStore` signature changed; the meaning of
  `QueuedMessage.ExpiresAt` widened. The limit in force at admission stays fixed on the record.
- **Post-enqueue re-check (L-057).** After `EnqueueAsync`, `DeliverAsync` calls
  `FindGrantByRecipientDidAsync` again. If the DID no longer belongs to the grant the record was
  stored under, it removes exactly that record (`AcknowledgeAsync(grant, [id])`) and reports
  `NotRegistered` with no live push; if that removal fails, the record stays, its lease released,
  and the outcome is `Queued`. If the re-check itself faults, the record is kept, its lease
  released, `Queued`, no live push.
- **`RevokeAndPurgeAsync(peerDid)`.** A bare-DID check (refused before any store call, never
  quoted) → `IMediationStore.RevokeAsync` → `IMessageStore.PurgeGrantAsync` →
  `GrantRevocation(Revoked, Purged)`. Revoking first means every later re-check finds the DID
  unregistered, so a delivery that stores its record after the purge removes it again. Store
  faults propagate (a host API); a retry purges what is left.
- **Live pushes off the caller (PR #109 review).** `PickupDeliveryOptions.AllowLive` documents that
  `true` pushes live inline, on the caller's thread; `HasLiveTarget`, `TakeNextAsync` and
  `PushNextLiveAsync` (above) let the mediator store with `AllowLive: false` and push from its push
  workers, so no forward pump ever waits on a recipient's DID resolution.

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
release ran is released too. This is a two-flag handshake: a taker writes the leasing flag and then
reads the closed mark, the close writes the closed mark and then reads the leasing flag. Both writes
are `Interlocked.Exchange` (a full fence; until the PR #109 review they were volatile writes, which
release/acquire ordering lets move after the read that follows them), the reads `Volatile.Read`, so
one side always sees the other. HTTP sessions are never tracked: their leases end by time only.

Faults: a message store, mediation store, registry, session or id source that throws, or
returns something outside its contract, never leaves the role — a request gets no reply, a
delivery reports `Faulted` (or `Queued` when the message was already stored). When the caller's
token is cancelled, only `OperationCanceledException` leaves — for `DeliverAsync`, only before the
message is stored: once it is, a cancellation is reported `Queued` too (#95), and a delivery
already in flight, a live push included, completes even if a concurrent acknowledgement or
`RevokeAndPurgeAsync` removes the record.

Documented limits:

- **At-least-once.** A lease that ran out, or a connection that closed mid-delivery, makes a
  message deliverable again; recipients acknowledge by id.
- **A delivery nobody reads (#98, D3-F2).** Over HTTP a delivery is the `200` of the request, and
  a client that never reads it used to hold it for as long as Kestrel's minimum data rate allowed
  (about 68 minutes for 1 MiB at 240 B/s) while the lease ran out every 60 s and served the same
  records into another held response. `MapDidCommMediator`'s HTTP endpoint now completes its `200`
  itself and aborts one the server has not taken within `LeaseTimeout`, counted from when its
  dispatch began (before the handler leased anything), on the container's `TimeProvider`: the
  abort comes no later than the lease runs out, so a client that never reads holds at most one copy
  of a leased record at a time, and the records are served again afterwards — nothing is lost. A
  delivery the server has taken is untouched, however long later completion callbacks run. The public `MapDidCommEndpoint(pattern)` keeps no such bound (it knows no lease).
- **Live deliveries must be acknowledged.** They stay stored and counted until `messages-received`;
  a recipient that never acknowledges fills its quota.
- **Many-key recipients.** An envelope larger than the allowance means trimmed batches. A message
  that does not fit alone is never delivered and never removed by the role: it is passed over,
  tried again at intervals that double, and stays stored and counted until it expires or is
  purged, or ages out — so `message_count` does not reach zero while it exists.
- **Not-yet-due messages** count against the quota but are not reported by `status`; a sender
  asking for long delays can use a recipient's quota up unseen for at most the delay cap after it
  stops sending (FR-ROUTE-10: `MaxForwardDelay`, 1 hour under `AddMediator()`; a host that calls
  `DeliverAsync` itself sets its own `NotBefore`).
- **Mailbox age limit.** A recipient offline longer than `MaxMessageAge` (30 days) loses those
  messages without notice.
- **A live session whose peer stops reading** can hold up to `MaxPendingPushes` packed deliveries.
- **Revoked grants.** `RevokeAndPurgeAsync` revokes and purges in one call. A host that revokes
  through `IMediationStore.RevokeAsync` alone leaves the grant's records stored until it calls
  `IMessageStore.PurgeGrantAsync` (the mediation store raises no change notification). They are
  not delivered while the peer has no grant. A new grant for the same peer starts with no
  registered DIDs, so a leftover comes back only if the peer registers its DID again first.
  Otherwise the peer's first `delivery-request` without a `recipient_did` removes the leftover,
  and a `status-request` without one still counts it until then. No leftover ever reaches another
  peer, even one that has since registered the same DID.
- **Lazy expiry.** Memory of expired records comes back when their grant is next touched, when the
  store runs full, or on `PurgeExpiredAsync` (which `AddMediator()`'s hosted service calls every
  `PurgeInterval`).
- **Open enrolment.** Self-made grants can use up the store-wide bounds; what is held stays
  deliverable.
- **A full store is observable by a peer that sends to itself.** A sender gets the same `202`
  whether its forward is stored or dropped. But a peer can send a forward to a DID it registered
  itself, while its own queue is below its quota. When the store is full, that forward is dropped
  (`Dropped-Quota`) and nothing arrives in the peer's queue, so the peer learns the store is full.
- **No session, no delivery.** Over the library endpoints a request without `return_route: all`
  gets nothing; over a WebSocket the endpoint needs `AllowSameSocketReplies` (the mediator's own
  WebSocket endpoint, `MapDidCommMediator`, has it on).

### Demonstrated by (#97)

- Cookbook DD: `PickupClient` status → fetch → `AckAsync` of a message that will not open;
  `MessagePickupQueue.DeliverAsync` with `PickupDeliveryOptions` (`Queued`, `NotRegistered`); every
  `MessagePickup` builder and parser; and DD.3, the `IMessageStore` contract walked call by call on a
  standalone `InMemoryMessageStore` (lease, release, pass over, release leases, acknowledge, the three
  purges, a bounded store refusing with `GrantMessageLimitReached`) for authors of persistent stores.
  `IMessageStore.PurgeRecipientAsync` is for the host only: the pickup role removes an unregistered
  DID's records by id with `AcknowledgeAsync` as it meets them.
- `samples/11-MediatedAgent` section 7: `MessagePickupQueue.RemoveAsync`, `PurgeExpiredAsync`,
  `RevokeAndPurgeAsync` (`GrantRevocation`), `MaxAdmissiblePayloadBytes`, and
  `IMessageStore.PurgeRecipientAsync` after the host removes one of Bob's DIDs.
- Cookbook FF.2: `AddMessagePickup()` à la carte with a host sink calling `DeliverAsync`
  (`AllowLive: false`, as the library's own sink, so the forward pump never waits on a session), and
  `MessagePickupQueue` / `MessagePickupHandler` built without a container.

## Mediator service: pipeline, `AddMediator()`, `MapDidCommMediator` (Phase 7, #95)

The piece that joins the roles above into one mediator (PRD FR-ROUTE-09..11, FR-MED-07,
FR-PICK-09/10): every forward the routing core relays goes into the recipient's pickup queue first,
and is then pushed by background workers over the recipient's live connection when it has one;
otherwise it waits for pickup — and one call wires the services, another maps the endpoints.
`AddMediator()` and the pipeline live in `DidComm.Mediator` (the pipeline in namespace
`DidComm.Mediator.Pipeline`, all internal), which still has no ASP.NET Core reference;
`MapDidCommMediator` lives in the new package `DidComm.Mediator.AspNetCore`. The spec's optional
third delivery step, a direct push to the recipient's own endpoint, is deferred to #110 (owner
decision on #88); until then a host that needs it pushes forwarded messages itself through its
own `IForwardSink` (the #90 seam, with `AddRoutingMediator()` — `AddMediator()` refuses a host
sink).

### Wiring: `AddMediator(configure?)`

`MediatorBuilderExtensions.AddMediator` on `DidCommBuilder`, in this order:

1. **Checks, before anything is added.** The delegate runs once on a scratch `MediatorOptions` and
   `Validate()` checks it (a replay or session setting's `InvalidOperationException` comes out as
   `ArgumentException` with it inside). Then the conflicts: an earlier `AddRoutingMediator()` (a
   `ForwardProcessorOptions` registration that is not the mediator's own, or a `ForwardHandler` /
   `ForwardProcessor` registration) and an `IForwardSink` already registered →
   `InvalidOperationException` (its message says that a host that pushes forwards to recipients' own
   endpoints itself keeps its sink and uses `AddRoutingMediator()` instead);
   `RewrapMode.ReanoncryptToNext` together with a host `IRecipientRoutingKeyProvider` (re-wrapping
   never reads it) → `ArgumentException`; no routing DID in the delegate and nothing that could
   supply one (no `IMediatorIdentity`, no `Configure` / `PostConfigure` of
   `CoordinateMediationOptions`) → `ArgumentException`. The mediator recognises its own earlier
   registrations by their factory delegates, so a repeated call passes.
2. `MediatorMarker`, which tells `MapDidCommMediator` and a repeated call that the mediator is
   registered.
3. **Options by projection.** `AddOptions<MediatorOptions>().Configure(configure)`, and for each role
   options type a `Configure` that runs the same delegate on `MediatorOptions.Projecting(...)`: a
   view whose slot for that role is the instance being configured and whose other slots are scratch.
   So the delegate assigns only what the host sets, and composes with the host's own
   `Configure<T>` in registration order (an earlier one keeps every property the delegate leaves
   alone; a later one wins for what it sets). It may run several times, on different instances, and
   must only assign values. `ReplayProtectionOptions` and `ReturnRouteSessionOptions` are configured
   here, Coordinate Mediation and Pickup through their own builders in step 5.
4. `IMediatorIdentity` → `MediatorIdentity` over `CoordinateMediationOptions.RoutingDids` (a TryAdd
   made before `AddCoordinateMediation()`, whose own identity registration it pre-empts; a host
   identity registered earlier wins).
5. `AddCoordinateMediation(projected)`, then `AddMessagePickup(projected)`. Coordinate Mediation
   must come before the routing mediator: it makes the grant store the forward recipient directory.
6. TryAdds: `ForwardProcessorOptions` as a factory over `IOptions<MediatorOptions>` (`Mode` =
   `RewrapMode`, `MaxDelay` = `MaxForwardDelay`, validated), the internal `NoRecipientRoutingKeys`
   as the default `IRecipientRoutingKeyProvider`,
   `ForwardMetrics`, `MediatorMetrics`, `PushDispatcher`, `MediatorForwardSink` with
   `IForwardSink` → it, and `MediatorInvitationHolder`.
7. `AddRoutingMediator(...)` with the delegate's values: it validates them, finds the options and the
   sink already registered, and adds the processor and the cycle-guarded `ForwardHandler` factory
   that shares `ForwardMetrics` with the sink.
8. `AddBuiltInProtocols()` (Trust Ping answers health checks; Discover Features discloses the three
   mediator roles; idempotent).
9. `IHostedService` → `MediatorHostedService`, through `TryAddEnumerable`, so repeated calls register
   one.

A host's own collaborators — `IMediationStore`, `IMessageStore`, `IMediationPolicy`,
`IMediatorIdentity`, `IRecipientRoutingKeyProvider` — are registered
before `AddMediator()`; a replay guard or session registry of its own before `AddDidComm`, and
`MediatorOptions.Replay` / `Sessions` reach it only if it reads `IOptions<T>`. A sink registered
after `AddMediator()` is refused at start-up. None of them may take the dispatcher, the handler
registry, a handler or `DidCommClient` in its constructor (L-017), and the pipeline's own parts take
none of these either.

`MediatorOptions` exposes the role options as sub-objects (`CoordinateMediation`, `Pickup`, `Replay`,
`Sessions` — the types each role reads, nothing aliased) and adds the pipeline settings:

| Setting | Default | Allowed |
| --- | --- | --- |
| `RewrapMode` | `PassThrough` | a defined value; `ReanoncryptToNext` excludes a host routing-key provider |
| `MaxForwardDelay` | 1 hour | 0 to `int.MaxValue` ms |
| `PurgeInterval` | 5 minutes | 1 second to 24 hours |
| `PushConcurrency` | 4 | 1 to 64; the push workers, for live pushes; at most 2 × this many push operations unsettled |
| `PushTimeout` | 30 seconds | above 0, at most 5 minutes; how long a worker waits for one live push |

One rule spans two roles: `Pickup.MaxMessageAge`, when set, must be longer than `MaxForwardDelay`,
or a delayed message would age out before it is due. Settings are checked at registration (the
delegate's values), by each role when it is built, and all together at start-up on the values in
effect (`MediatorOptions.Effective`). Every part copies its settings when it is built.

### A forward, from the endpoint to the queue

1. `POST {pattern}` (or a WebSocket message on `GET {pattern}`) → unpack → `InboundAdmission` (a
   forward is replay-exempt and never opens a session) → `ProtocolDispatcher` → `ForwardHandler`:
   the inline screening of "Mediator routing core" above, the 202 committed (HTTP), the schedule
   fixed at receipt under the delay cap.
2. On the handler's pump, after the processor: `MediatorForwardSink.AcceptAsync(result, context,
   shutdownToken)` → `MessagePickupQueue.DeliverAsync(DidSubjectOf(result.NextHop),
   result.OnwardPacked, new PickupDeliveryOptions(result.NotBefore, result.ExpiresTime, AllowLive:
   false))`: the size bound, the forward's expiry on `DidCommOptions.Clock` with
   `ExpiresClockSkew`, the grant lookup, `EnqueueAsync` (a random id; `ExpiresAt` = the earlier of
   the forward's expiry and now + `MaxMessageAge`), the post-enqueue re-check (see "Message Pickup
   3.0"). **The pump only stores**: it never awaits a session, because a live push packs a
   `delivery` for the recipient, which resolves the recipient's DID, and the recipient controls how
   long that takes (L-061). Live pushes are the push workers' job (step 4).
3. The outcome is counted once on `didcomm.forward.outcomes`; every stored forward is `Queued`,
   whatever happens to its push afterwards (that has its own counter):

| `DeliverAsync` outcome | Forward outcome | Log | Then |
| --- | --- | --- | --- |
| `Queued` | `Queued` | Debug | the wake-up of the push workers (step 4) |
| `NotRegistered` | `Dropped-Unknown` | rate-limited Warning | — |
| `Expired` | `Dropped-Expired` | rate-limited Warning | — |
| `PayloadTooLarge` | `Dropped-TooLarge` | rate-limited Warning | — |
| `QueueFull` | `Dropped-Quota` | rate-limited Warning | — |
| `Faulted`, or an exception | `Faulted` | rate-limited Error, type name only | — |

   A `next` with no DID subject (the handler has already screened it out) is `Dropped-Unknown`.
   Cancellation of the handler's token leaves as cancellation, and the handler counts
   `Dropped-Shutdown`. Nothing travels back toward the sender: the transport answer was 202 before
   the sink ran. "Rate-limited" is the first and every 1000th occurrence of a reason at its level,
   the rest at Debug, with the same text.
4. **The wake-up**, best effort and synchronous, only for a `Queued` message whose `NotBefore` is
   not after `context.Options.Now()`, and only when `PickupCoordinator.HasLiveTarget(grantPeerDid)`
   says the peer has, right now, a live session with live delivery on. The sink then calls
   `PushDispatcher.Wake(grantPeerDid)`, which is O(1), never blocks and carries nothing of the
   message: the push takes what the store holds when it runs. Otherwise nothing is woken and the
   message waits for pickup or for the grant's next push. A message that is not yet due wakes
   nothing and is never pushed before it is due; once due it goes with the grant's next push, or
   is picked up. A failure here (a `Wake` that throws) is a Warning with the exception
   type; the forward is already counted `Queued` and its message waits for pickup.

### `PushDispatcher`

Pushes stored messages over their recipients' live sessions on a few background workers, after the
message has been stored and the forward acknowledged — never on the request or on the forward pump.
It is built whenever `AddMediator()` is used and its workers start with it; it makes live pushes
only (see the section's start). The PR #109 review's second round found two defects in the first
design, both from one root — it kept a job per stored message beside what the store already knew,
and it released its accounting at the deadline instead of when the work ended: a job whose record
had expired or been removed still took the place of a newer message's live push, and sends that
ignored their cancellation piled up across deadlines and session replacement. It was redesigned
around five invariants (D-1..D-5 in the #95 plan). A break-it pass on the redesign (ADV-5) then
found that "arrived after live-on" and the cooldown leaned on clock readings, and that pruning a
grant's entry by key could start a second operation; the store's arrival sequence, pruning by
identity and the monotonic cooldown below are its fixes.

**D-1 The store decides what is pushed.** The dispatcher keeps no per-message state. An operation
takes, one at a time, the next record the store hands out at that moment
(`PickupCoordinator.TakeNextAsync`: due, unexpired, unleased, not passed over, oldest first, its
recipient DID still registered under the grant) and pushes it. An expired, acknowledged, purged or
picked-up record is therefore never pushed and takes no one's place. An operation is step 1 of the
D6 order, the **live push** (step 2 is the pickup queue, where everything not pushed waits):
`PickupCoordinator.PushNextLiveAsync` (see "Message Pickup 3.0") — the newest live session of the
grant's peer with live delivery on; only records that entered the store after the status that
answered its `live-delivery-change` (a greater arrival sequence than that status's
`HighWaterSequence`); a lease owned by the session. `Sent` → `Pushed-Live`, next record.
`PassedOver` (too large for the session's envelope even alone) → `Push-Failed`, next record.
`Removed` → `Push-Skipped-Unregistered`, next record. `NotSent` (no free push slot, a downgraded or
closing session) or a fault → `Push-Failed`, the record given back, and the operation stops.
`NoSession` → the operation ends (`Push-Skipped-NoLiveSession` when it is the operation's first
step). `Empty` → the operation ends; a wake-up that finds nothing left (its message already pushed
by an operation that was running, or taken by a pickup) is no outcome.

A wake-up is a stored message (the sink's) or a session turning live delivery on
(`LiveDeliveryStarted`).

**Retries.** A live push the session did not take (`NotSent`: no free push slot, downgraded or
closing), or that faulted, or that reached its deadline and was then given back, is tried again at
the grant's next wake-up. That cannot multiply the traffic of what was forwarded: an operation
makes at most one failed attempt (it ends there, and a deadline also pauses the grant), a wake-up
is a stored message or a session turning live delivery on, and nothing is written to the
connection unless the session takes it. One too large for the session is passed over instead, for
a period that doubles each time. A push that was skipped (saturation, a cooldown) made no attempt,
and its message goes with the grant's next push. A delayed message is never pushed before it is
due (the store does not hand it out, and the sink wakes nothing for it); once due it goes with the
grant's next push. Pickup always works. Delivery is at-least-once.

**D-2 Wake-ups, not jobs.** `Wake(grantPeerDid)` is O(1), never blocks, and throws only for a null
or over-long (above 2 048 characters) DID; wake-ups coalesce into one flag per grant. The flag is
consumed when a worker takes the grant, before the operation reads the store, so a wake-up that
arrives during an operation is served by another one right after it.

**D-3 One operation per grant, until it settles.** A grant has at most one operation from its start
until the work it started has actually ended — not until its deadline. Keyed by the grant's DID, so
a new session does not reset it. A recipient that is slow to take a push (a DID that is slow to
resolve, a session that is slow to take it) therefore holds one worker at a time, while the other
grants' pushes go on with the remaining workers.

**D-4 Bounded.** `PushConcurrency` workers, started with the execution context suppressed. At most
`UnsettledPerWorker` (2) × `PushConcurrency` operations between their start and the end of the work
they started, abandoned ones included (`Unsettled`, `MaxUnsettled`). A worker that takes a woken
grant while that many exist starts nothing: `Push-Skipped-Saturated` (a rate-limited Warning), the
wake-up is consumed, and the grant's messages wait for pickup or its next wake-up. Grants with a
wake-up are served in turn from one ready queue (a semaphore counts its entries); an operation takes
at most `MaxDeliveryBatch` (10) records, then yields its worker and its grant takes its turn again
behind the others, so a busy grant cannot monopolise a worker. Each operation holds at most one
record at a time. A host collaborator — a return-route session, the DID resolver or the store — that
never completes a call after it was cancelled keeps that call's operation, and its grant, unsettled
until it does complete; 2 × `PushConcurrency` such calls hold every operation there is, and then no
push starts, for any recipient, until one of them completes. Pickup keeps working throughout. Per
grant that is woken, in line, busy or cooling down the dispatcher keeps one small entry — the
grant's DID (at most 2 048 characters), a few flags and the monotonic timestamp its last cooldown
started at — dropped once it is none of these (checked when the grant is served or settles, and for
every entry when a cooldown starts). Only that very entry is dropped (`ReferenceEquals` with the
map's): a worker that finishes a saturated skip may still hold an entry that was dropped and
replaced meanwhile, and removing by key would drop the replacement, which may be busy, and let a
second operation of the grant start (D-3; ADV-5 found it). It keeps nothing of any message
(`RetainedCharacters` and `GrantEntries` are the test seams).

**D-5 A push names what it took.** Every send is preceded by a lease on exactly that record and
followed, once, by its release, its pass-over, or the session's lease running on until
`messages-received`, its close or `LeaseTimeout`. The release is never cancelled: it runs when the
send has ended, however long that takes.

**What a live recipient can rely on.** While it has a live session with live delivery on, every due
message that entered the store after the status that answered its `live-delivery-change` is pushed
over that connection (to the newest such session), oldest first, one at a time, as long as its
pushes succeed — whatever the clocks did. A push that fails, a cooldown, saturation or shutdown
leaves the message queued, for pickup and for the grant's next wake-up. Messages that status
counted are pickup-only (FR-PICK-06). Delivery is at-least-once: a pushed message stays leased to the
session until `messages-received`, and goes back to the queue if the session closes or the lease
times out first.

**The deadline.** Each step — one take and its push — has its own deadline: a token source linked
to the dispatcher's shutdown and cancelled by a `TimeProvider` timer at `PushTimeout`, whose
callback cancels inside a `try` (never `CancelAfter`, so a callback host code registered on the
token cannot crash a timer thread). It covers the take, the registration check, the send and the
release or pass-over. At the deadline the worker stops waiting and serves other grants;
the step counts `Push-Failed` — the worker's last word, though the abandoned call may still deliver
— and the grant cools down (below). The abandoned step runs on until its call ends; its record stays
under the session's lease meanwhile, and when the call ends the record is given back, passed over,
or left leased to the session (a session that ignored the cancellation and reports
`Sent`). Only then does the operation settle and the grant become servable again; a wake-up
meanwhile is held (or skipped during the cooldown). `WebSocketReturnRouteSession.TrySendAsync`
honours the cancellation: the pack observes the token, and the send looks at it once more right
before handing the frame to the socket writer, so nothing is queued once it is cancelled (a DID
resolution that ignores the token still runs to its end first).

**Cooldown.** The workers are shared, and a peer controls how long its own pushes take (its DID's
resolution, how fast its session takes a push). So a grant whose push — in any step, the
registration check included — reaches the deadline is paused for `CooldownPushTimeouts` (2) ×
`PushTimeout`, one minute at the defaults, measured on the monotonic clock
(`TimeProvider.GetTimestamp` / `GetElapsedTime`), so a step of the wall clock neither lengthens nor
shortens it (until ADV-5 it was judged on `GetUtcNow()`, and a backward step lengthened it by the
step): its wake-ups meanwhile are skipped with `Push-Skipped-Cooldown` and take no worker, and their
messages wait for pickup or the grant's first wake-up after the pause. A peer that stalls on purpose
therefore holds a worker at most once every three push timeouts, not continuously. Two timeouts, not
more: an honest recipient whose resolution was slow once (a cold cache) gets its live pushes back
within a minute, the same order as the lease timeout after which an unacknowledged push is delivered
again anyway. The pause lives in the grant's entry, keyed by grant like D-3.

Outcomes on `didcomm.mediator.push.outcomes` (`PushOutcomes`): one per push attempt — a take and its
push over the live session (a fault or the deadline while taking counts as a failed attempt) — and
one per wake-up that was skipped or could push nothing it had.

| Outcome | When | The record |
| --- | --- | --- |
| `Pushed-Live` | the live session took the `delivery` | leased to the session until `messages-received`, its close or `LeaseTimeout` |
| `Push-Failed` | the session did not take it, or it was too large for the session even alone; a store or session fault; the deadline, in any step | given back (passed over when too large); after the deadline, when the abandoned call ends: given back, passed over or left leased to the session |
| `Push-Skipped-Unregistered` | the record taken has a recipient DID no longer registered under the grant | removed by id |
| `Push-Skipped-NoLiveSession` | at the operation's first step, no live session with live delivery on | none taken |
| `Push-Skipped-Saturated` | a worker took a woken grant while 2 × `PushConcurrency` operations were unsettled | none taken; the wake-up is consumed |
| `Push-Skipped-Cooldown` | a wake-up while the grant is paused, or one held during its operation that settles in the pause | none taken |
| `Push-Skipped-Shutdown` | a wake-up after `Dispose`, a waiting wake-up it dropped, or a running push it cancelled | given back when the push's call ends |
| (none) | a wake-up that finds nothing left to push | — |

Shutdown: `Dispose` stops admission, cancels the running pushes (their leases are given back) and
drops the waiting wake-ups; every message stays queued. `DisposeAsync` also waits up to 5 seconds
for the workers. Log lines carry outcome names, counts and exception type names — never a DID, the
queue's record id, the forward's id or an exception message; failures are rate-limited per reason.
The dispatcher is built from the pickup queue (which holds the mediation store and the message
store); a store that takes the sink or the dispatcher in its constructor fails the first resolution
with `InvalidOperationException` instead of re-entering the factory.

### `MediatorHostedService`

An `IHostedLifecycleService`: `StartingAsync` runs before any `StartAsync`, so under `WebApplication`
the checks finish before Kestrel listens, and a failure fails the host's start. Nothing is resolved
in its constructor. `StartingAsync`, in order:

1. The routing DIDs from `IMediatorIdentity`, read once: at least one, each a bare DID (a throwing
   identity fails by exception type; messages name the index, never the DID).
2. The `IForwardSink` in effect must be `MediatorForwardSink`.
3. Resolve `ProtocolDispatcher`, which builds every handler: the pickup queue checks its delivery
   budget against `MaxReceiveBytes` and logs the largest message it admits; the forward processor
   checks `MaxDelay`.
4. Resolve the replay guard and the session registry, which check their settings when built.
5. `MediatorOptions.Effective(...).Validate()`: every setting together on the values in effect,
   including the host's own `Configure<T>` calls; the purge interval is fixed here. Then the forward
   settings in effect, read from what the container hands out (so a host `Configure<MediatorOptions>`
   or a registration made after `AddMediator()` is caught): the `ForwardProcessorOptions` must carry
   a `MaxDelay` no longer than `MaxForwardDelay` and `MediatorOptions.RewrapMode`; re-wrapping is
   refused with any routing-key provider but `NoRecipientRoutingKeys`; and the forward recipient
   directory must not be the routing mediator's empty `InMemoryForwardRecipientRegistry`. Each is an
   `InvalidOperationException` that says what to remove.
6. FR-MED-07, per routing DID, through `RoutingDidValidator` (it needs an `IServiceEndpointResolver`;
   without one the message names `UseNetDidResolver()`), each check bounded by 30 seconds on the
   `TimeProvider`: the DID resolves; its first `DIDCommMessaging` entry has an absolute `http`,
   `https`, `ws` or `wss` `uri` that is not a DID and not the queue URI; at least one `keyAgreement`
   kid is found by `ISecretsResolver.FindPresentAsync`. Resolution failure, an unsupported method, a
   resolver or secrets resolver that throws, or the time bound → `InvalidOperationException` naming
   the index (and the exception type), never the DID, an endpoint or exception text. Routing keys on
   that first entry, and a later entry whose `uri` is a DID, are Warnings.
7. The out-of-band invitation: `OutOfBand.CreateInvitation(from: routingDids[0], goal:
   "RequestMediate", goalCode: "request-mediate", accept: ["didcomm/v2"], id: <new GUID>)`, packed as
   plaintext into `MediatorInvitationHolder`, now that the DID is known good.
8. The observable instruments (below).
9. One Information line with counts and settings, no DID: "Mediator ready: {N} routing DID(s)
   checked (FR-MED-07); forward delay honoured up to …; mailbox age limit …; purge every …".

`StartedAsync` starts the purge loop: a `PeriodicTimer(PurgeInterval, TimeProvider)` on a task started
with the flow suppressed. Each tick, `PurgeOnceAsync` (the test seam) calls
`MessagePickupQueue.PurgeExpiredAsync`, which judges by `DidCommOptions.Clock`, adds what it removed to
`didcomm.mediator.queue.purged` and logs it at Information when it is above zero; then it reports as a
Warning, with the remedy, any growth since the last tick of `InMemoryReplayGuard.SaturationCount`,
`InMemoryReplayGuard.ClockBehindRefusalCount` and
`InMemoryReturnRouteSessionRegistry.DroppedNotifications`. A store fault is logged by type
(rate-limited) and the schedule goes on. `StoppingAsync` cancels the loop; `StopAsync` waits for it
within the host's token. The `TimeProvider` is the container's, or `TimeProvider.System`.

Outside a generic host (a plain `ServiceCollection`) the hosted service never runs: each role still
checks its own settings when it is first used, but the routing DIDs are not checked against their
DID documents, no invitation is built (the invitation endpoint answers `404`) and nothing is purged
unless the host calls `PurgeExpiredAsync` itself.

### `MapDidCommMediator(pattern)` (`DidComm.Mediator.AspNetCore`)

| Route | Endpoint |
| --- | --- |
| `POST {pattern}` | `MapDidCommEndpointCore(endpoints, pattern, returnedResponseDeadline: LeaseTimeout)`: the registry-aware `MapDidCommEndpoint(pattern)` — content type and size checks, unpack, `InboundAdmission`, and the 6-argument `DispatchAsync` with a real acknowledgement (the 202 is committed before the processor and the sink run, within the 10 s forward deadline) and the request's session — plus one bound: a returned `200`, completed by the endpoint's result, that the server has not taken within `MessagePickupOptions.LeaseTimeout`, counted from when its dispatch began, aborts its request |
| `GET {pattern}` | `MapDidCommWebSocketCore(endpoints, pattern, mediatorEndpoint: true)`: the receive loop of the registry-aware `MapDidCommWebSocket(pattern)` (admission on; acknowledgement `Task.CompletedTask`) with `DidCommReceiveOptions.ForMediatorEndpoint()` |
| `GET {base}/oob` | `MapDidCommOobEndpoint(base + "/oob", MediatorInvitationStore)`, where `base` is `pattern.TrimEnd('/')` (so `"/"` gives `/oob`) |

- `pattern` must start with `/`; both patterns are parsed before anything is mapped, so a refused
  pattern leaves nothing mapped, and the refusal does not quote it. Without `AddMediator()` (no
  `MediatorMarker` in the container) the call throws `InvalidOperationException` at map time.
- **Same-socket replies and STOMP are scoped to this endpoint.** `ForMediatorEndpoint()` copies the
  host's `DidCommReceiveOptions` per connection with `AllowSameSocketReplies = true` and
  `UseStomp = false` (a STOMP session can never carry a reply, so pickup over it would be silent).
  The global options are untouched, so the host's other `MapDidCommWebSocket` endpoints stay one-way
  (FR-TRN-10). Sessions open under the #91 rules: authcrypt, `return_route: all`, admitted.
- **The invitation.** `MediatorInvitationStore` is a read-only `IOobInvitationStore`: it serves the
  holder's invitation for `_oobid=mediator`
  (`MediatorEndpointRouteBuilderExtensions.InvitationOobId`) only, and nothing before the mediator
  has started; any other id is `404`, a request without `_oobid` is `400` (the OOB endpoint's
  rule); `Store` and `Remove` throw `NotSupportedException`. The operator publishes
  `OutOfBand.ToShortUrl($"{publicBase}{base}/oob", InvitationOobId)`. The invitation reveals only the
  primary routing DID, which recipients' DID documents name anyway.
- The returned builder is an internal `CompositeEndpointConventionBuilder` that adds each convention
  (`RequireRateLimiting`, host filters, authorization) to all three endpoints. On the WebSocket
  endpoint a rate limiter counts connections, not messages.
- One Information line at map time, with the paths only.
- `DidComm.Mediator.AspNetCore` reaches `MapDidCommWebSocketCore`, `MapDidCommEndpointCore`,
  `ForMediatorEndpoint` and `SafeLog` in `DidComm.AspNetCore`, and `MediatorMarker` / `MediatorInvitationHolder` in
  `DidComm.Mediator`, through `InternalsVisibleTo`: no public surface was added to the released
  `DidComm.AspNetCore` for one caller, and the packages ship with one version.

### Observability

Everything is on the meter `didcomm-dotnet` (`DidCommDiagnostics.MeterName`), created through the
container's `IMeterFactory` when one is registered (ASP.NET Core registers one); otherwise the
metrics object owns its meter and disposes it. Tag values are closed vocabularies; no tag carries a
DID.

| Instrument | Kind, unit | Tag | Values, source |
| --- | --- | --- | --- |
| `didcomm.forward.outcomes` | Counter, `{forward}` | `didcomm.forward.outcome` | `Queued`, `Dropped-Malformed`, `Dropped-NotEncrypted`, `Dropped-Expired`, `Dropped-Unknown`, `Dropped-Saturated`, `Dropped-Unacknowledged`, `Dropped-Shutdown`, `Dropped-TooLarge`, `Dropped-Quota`, `Faulted`; one per forward that reaches the handler (one refused at unpack is not counted), from the handler or the sink (`ForwardMetrics`, in Core) |
| `didcomm.mediator.push.outcomes` | Counter, `{message}` | `didcomm.push.outcome` | `Pushed-Live`, `Push-Failed`, `Push-Skipped-Saturated`, `Push-Skipped-NoLiveSession`, `Push-Skipped-Unregistered`, `Push-Skipped-Cooldown`, `Push-Skipped-Shutdown`; one per push attempt (a take and its push over the live session) and one per wake-up that was skipped or could push nothing it had; a wake-up that finds nothing left is not counted (`PushOutcomes`; the table under `PushDispatcher`) |
| `didcomm.mediator.queue.purged` | Counter, `{message}` | — | messages the scheduled purge removed |
| `didcomm.replay.saturated`, `didcomm.replay.clock_behind_refusals` | ObservableCounter, `{message}` | — | `InMemoryReplayGuard.SaturationCount`, `ClockBehindRefusalCount` |
| `didcomm.return_route.dropped_notifications` | ObservableCounter, `{notification}` | — | `InMemoryReturnRouteSessionRegistry.DroppedNotifications` |
| `didcomm.mediator.queue.messages`, `didcomm.mediator.queue.bytes` | ObservableGauge, `{message}` / `By` | — | `InMemoryMessageStore.MessageCount`, `TotalBytes` |

The `didcomm.mediator.*`, replay and session instruments come from `MediatorMetrics`. The observable
ones exist only for the library's in-memory types, and a callback whose read throws reports nothing.
Log categories: `DidComm.Mediator.Pipeline.MediatorForwardSink`, `…PushDispatcher`,
`…MediatorHostedService`, and `DidComm.Mediator.AspNetCore` for the map-time line.

### Operating a mediator

The `AddMediator()` and `MediatorOptions` remarks carry this guidance:

- Put rate limiting or admission control in front of the endpoints, for example
  `app.MapDidCommMediator("/didcomm").RequireRateLimiting("mediator")`: anyone can post forwards, and
  a flood from fresh DIDs fills the replay budget, which then refuses in-scope messages for everyone
  for up to `Window + 2 × ClockSkew`.
- Enrolment is open by default and grants never expire: register an allow-list or rate-limiting
  `IMediationPolicy`, and end a peer's mediation with `MessagePickupQueue.RevokeAndPurgeAsync`.
- Size `Replay.MaxEntries` to the accepted in-scope rate × (`Window + ClockSkew`); the default
  32 768 carries about 91 messages a second.
- A host clock that steps back by more than `Window + ClockSkew` (6 minutes at the defaults) makes
  the mediator refuse in-scope traffic until the clock catches up or the process restarts; the purge
  schedule reports it.
- A live session whose peer stops reading holds up to `Sessions.MaxPendingPushes` packed deliveries
  of at most `Pickup.MaxDeliveryBytes` each.
- A forwarded message is always stored first; a recipient with live delivery on gets it pushed
  afterwards by the push workers (`PushConcurrency`, `PushTimeout`), so a recipient that is slow to
  take a push holds up no one else's messages. A push takes what is queued when it runs, oldest
  first, one at a time per recipient; a live session is pushed what arrived after the status that
  answered its turning live delivery on (by the store's arrival order, whatever the clock does). A
  message whose live push fails or reaches `PushTimeout`, or that arrives while the recipient's
  pushes are paused (2 × `PushTimeout` after a deadline), stays queued and is tried again when the
  recipient next receives a message, so a recipient in live mode should still ask for its queued
  messages — a `status-request` shows them — after a reconnect or a slow period.
- Expiry of relayed and queued messages is judged with `DidCommOptions.ExpiresClockSkew`, never the
  replay `ClockSkew`.
- The mediator pushes only over live sessions; every other message waits for pickup.
- Call `app.UseWebSockets()` before mapping, or the WebSocket endpoint answers `400`.
- **Responses a client does not read** are bounded on the HTTP endpoint (#98): a forward's `202`
  has 10 s from the handler's return to be taken by the server — then the forward is
  `Dropped-Unacknowledged`, its place in the forward queue is released and the request is aborted
  — and a `200` has `Pickup.LeaseTimeout` from its dispatch — then the request is aborted and the
  leased records come back when the lease runs out. Both are timed on the container's
  `TimeProvider` and hold only while the response is being written: the endpoint completes these
  responses itself, so a slow `Response.OnCompleted` cleanup of other middleware, or a client that
  closes its connection after reading, changes nothing once the server has the response — and
  middleware after the endpoint cannot modify them any more. An abort ends the HTTP/1.1 connection (and every request pipelined on it) or
  resets the HTTP/2 stream; each is counted on a rate-limited line (first and every 1000th at
  Warning, no wire value). Everything else about slow or idle clients is Kestrel's, and its
  defaults are generous for a public endpoint: `KestrelServerLimits.MinResponseDataRate` (a slow
  reader of any other response), `MinRequestBodyDataRate` (a body sent fast and then stalled is
  held about bytes / 240 s), `KeepAliveTimeout`, `MaxConcurrentConnections` and
  `MaxConcurrentUpgradedConnections` still decide how long and how many. The lease bound has a
  cost for slow honest recipients: one whose link is slower than `Pickup.MaxDeliveryBytes` /
  `Pickup.LeaseTimeout` (1 MiB in 60 s, about 17 KB/s, at the defaults) cannot finish reading a full
  delivery over HTTP; it asks for smaller batches (`limit`), or the host raises the lease or lowers
  `MaxDeliveryBytes`.
- **ASP.NET Core's own log lines** carry some request metadata: Hosting's "Request starting" line
  includes the Content-Type with its parameters, and when a host turns on
  `ActivityTrackingOptions.Baggage`, its logging scopes carry the inbound `baggage` header. Those
  are framework logs the host configures. If an internet-facing endpoint's logs must not carry
  request metadata, keep the `Microsoft.AspNetCore.Hosting` and `Microsoft.AspNetCore.Server.Kestrel`
  categories at Warning or above, and leave `Baggage` off.

### Documented limits

- **Live pushes are tried again only at the next wake-up.** A message whose live push failed, or
  whose push was skipped (saturation, a cooldown), is tried again only when its recipient next
  receives a message (or one of its sessions turns live delivery on), and a delayed message, never
  pushed before it is due, goes once due with the grant's next push. Otherwise — and for good if
  nothing more arrives for that recipient — it waits for pickup. A recipient that relies on live
  pushes and never picks up accumulates what could not be pushed until it expires or ages out;
  once its quota is full (`MaxQueueMessages`, 100), further forwards for it are dropped as
  `Dropped-Quota` and wake nothing, so nothing is pushed to it until a record expires or ages out
  and frees a place. A timed retry is a possible follow-up, not built.
- **At-least-once.** While a push's lease runs, a push and a pickup never hold the record at the
  same time, but a message a recipient fetched and did not acknowledge before its lease ran out can
  be pushed to it again, and a push abandoned at its deadline may still reach the recipient later.
  The recipient may then see the message twice.
- **Live pushes are best effort, after storing.** A message whose live push fails or reaches the
  deadline, one stored while the grant's pushes are paused or the push operations are saturated,
  and one stored while the recipient had no live session stay queued — for pickup, and for the
  grant's next wake-up; one counted in the status that answered the session's
  `live-delivery-change` is never pushed live (FR-PICK-06). A recipient in live mode still asks for
  queued messages after a reconnect or a slow period.
- **Stalled grants and the shared workers.** A grant whose push reaches the deadline is paused for
  2 × `PushTimeout`, but with K grants whose every push stalls until it ends at its deadline (a DID
  that resolves slowly on purpose) and W = `PushConcurrency`
  workers, a healthy recipient's push can still wait about ceil(K / W) × `PushTimeout` for a worker,
  and once K ≥ 3 × W the stalled grants, each back after its cooldown, can keep every worker busy,
  so that wait recurs. Nothing is lost and pickup always works; a mediation policy that enrols only
  known peers (an allow-list `IMediationPolicy`) closes it — grants are free under the default
  policy. A push that does not end at its deadline is the next item's case.
- **A push whose call never ends.** A host collaborator — a return-route session, the DID resolver
  or the store — that never completes a call after it was cancelled keeps that call's operation, and
  its grant, unsettled until it does complete: the grant gets no further push, and its operation
  holds one of the 2 × `PushConcurrency` unsettled slots. 2 × `PushConcurrency` such calls stop
  every push, for any recipient — each wake-up counts `Push-Skipped-Saturated` — until one of them
  completes; pickup keeps working. The library's WebSocket session and in-memory store honour the
  cancellation; the session's pack waits for the recipient's DID resolution.
- **Silent loss after the age limit.** A recipient offline longer than `MaxMessageAge` (30 days)
  loses those messages; neither it nor the sender is told.
- **Unseen quota occupation** for at most `MaxForwardDelay` (1 hour) after a sender stops sending.
- **Replayed forwards.** A forward is outside replay admission (FR-RR-08), so a captured forward
  posted again is relayed again: each copy is stored, and a live recipient is pushed each one. The
  recipient's quota bounds how many copies are held at once, but an acknowledged push frees its
  place: recipients de-duplicate by message id.
- **A captured `live-delivery-change`.** A captured, not-yet-sent authcrypted
  `live-delivery-change` sent first on a fresh socket holds the victim's live session: the message
  is authenticated, the channel is not bound to it (the same on `main`; on the #98 checklist). The
  attacker's socket also takes the leases: every message pushed to it stays leased to it for
  `LeaseTimeout`, and once a lease runs out, the next arrival leases that message to the same
  socket again. This goes on until the socket closes, which releases the leases. Meanwhile the
  victim's own fetches get those messages only in the gap between a lease running out and the next
  arrival. The attacker still cannot open them: they are encrypted to the victim.
- **A host-registered `InMemoryMessageStore`** keeps the quotas it was built with;
  `MediatorOptions.Pickup` reaches only the store `AddMessagePickup()` builds.
- **Revocation.** A push never sends a record the purge removed before the push took it, but a live
  delivery or a push that has taken its record completes; a peer granted mediation again while
  `RevokeAndPurgeAsync` runs loses what was stored for the new grant before the purge.
- **Clock readings.** Aging is judged by each caller's clock reading inside the store; a request that
  read the clock just before a sweep can still deliver a record that has just aged out (reading
  skew only; removal is by the lock holder, L-060). Which messages a live session is pushed is
  decided by the store's arrival sequence, and the cooldown is measured on the monotonic clock, so a
  step of the wall clock changes neither.
- **Outside a generic host** there are no start-up checks, no invitation and no purge (above).
- **Ten seconds of a stranger's held 202s.** The forward deadline bounds how long one stalled 202
  keeps its reservation, not how many a sender can hold within it: each stalled HTTP/1.1
  connection holds one reservation (an HTTP/2 connection, whose streams share one output, can
  hold one per open stream), and the shared
  bounds (1 024 forwards, 64 MiB) are one sender's for as long as it re-stalls connections every
  10 s, which costs it the requests that fill the buffers each time. Rate limiting per client in
  front of the endpoint, and an enrolment policy that is not open, close it.
- **Held deliveries, one per record.** A client that never reads still gets a delivery's `200`
  (up to `MaxDeliveryBytes` for a 2 KB request) and can ask again each lease; what changed is that
  a leased record is in at most one held response at a time. All held deliveries together therefore
  carry at most what the store holds (`MaxStoreBytes`, 128 MiB, times the delivery's encoding
  overhead), and self-made grants under open enrolment can use that up.

### Demonstrated by (#97)

- `samples/11-MediatedAgent/MediatorHost.cs`: `AddMediator(ConfigureMediator)` setting every
  `MediatorOptions` property, `MediatorOptions.Validate()` and `ReturnRouteSessionOptions.Validate()`
  each shown refusing an invalid copy, a
  per-client rate limiter (`AddRateLimiter` + a policy partitioned by remote address;
  `RequireRateLimiting` covers the HTTP, WebSocket and invitation routes, and the WebSocket endpoint
  counts connections, not messages), `UseWebSockets()`, `MapDidCommMediator("/didcomm")`, and the
  invitation URL (`OutOfBand.ToShortUrl(..., InvitationOobId)`). The mediator's DID is minted for a
  port picked before start (`LoopbackPort`), because `MediatorHostedService` checks the routing
  DIDs in `StartingAsync`, before the server binds. `StoredForwardsWatcher` waits for a forward to
  be stored by listening to `didcomm.forward.outcomes` (outcome `Queued`), because the 202 goes out
  before the sink stores it.
- Cookbook FF.1: the same host in about twenty lines with an allow-list policy, and `AddMediator()`
  refusing a host `IForwardSink` ("use `AddRoutingMediator()`"). `CookbookMediatorHost` is the
  ready-made host sections CC and DD start and dispose.

## Recipient-side client: `MediationClient`, `PickupClient` (Phase 7, #96)

The other end of the two mediator roles (PRD FR-MED-09, FR-MED-10 on the client side, FR-PICK-11,
FR-ROUTE-06 receiver side): a recipient asks a mediator for mediation, builds the service entry of
the DID it publishes from the grant, registers that DID, and then fetches what the mediator holds
for it or receives it live. It lives in its own package, `DidComm.Mediator.Client`, namespace
`DidComm.Mediator.Client` (internals in `DidComm.Mediator.Client.Internal`), together with the
message builders, parsers and records both roles share (#113 split it out of `DidComm.Mediator`,
where #96 built it, before either package was published; namespaces unchanged). The package
references `DidComm.Core`, `DidComm.Extensions.DependencyInjection` and the DI and logging
abstractions only: no mediator roles, no hosting, no ASP.NET Core. A wallet references it alone;
`DidComm.Mediator` references it, so a mediator gets it too.
Two thin public facades, `MediationClient` and `PickupClient`, share one internal
`MediatorClientChannel` per container. `DidComm.Core` gains internals only; no released member
changes.

### Wiring

`MediatorClientBuilderExtensions.AddMediatorClient(configure?)` on `DidCommBuilder`, in this order:

1. The delegate runs once on a scratch `MediatorClientOptions` and `Validate()` checks it: a setting
   out of range throws `ArgumentOutOfRangeException` at registration.
2. `AddOptions<MediatorClientOptions>().Configure(configure)`. Repeated calls compose in order;
   the delegate may run more than once, on different instances, and must only assign values.
3. `TryAddSingleton` of the channel, built from `DidCommClient`, a validated copy of
   `IOptions<MediatorClientOptions>.Value` (L-060), `receivesInbound` =
   `IServiceProviderIsService.IsService(typeof(IInboundFrameConsumer))` (registrations are read,
   nothing is resolved), the container's `TimeProvider` (else `TimeProvider.System`) and
   `ILoggerFactory`.
4. `TryAddEnumerable` of `IInboundCorrelator` → that same instance (the Discover Features client's
   pattern), so a repeated call registers one correlator.
5. `TryAddSingleton` of `MediationClient` and `PickupClient`, through internal constructors over
   the channel.

The clients build no connection of their own: they send through `DidCommClient` and the host's
`UseHttpTransport` / `UseWebSocketTransport` registrations. A wallet that reaches its mediator only
through a proxy names it there (`TrustedProxy`, FR-TRN-13).

| Setting (`MediatorClientOptions`) | Default | Allowed |
| --- | --- | --- |
| `RequestTimeout` | 30 seconds | above 0, at most 10 minutes; one for each step of a call (below) |
| `MaxPendingRequests` | 64 | 1 to 4096; both clients of the container together |
| `CoordinateMediationVersion` | `Negotiate` | `Negotiate`, `V3`, `V2` |
| `DedupeCapacity` | 4096 | 0 (off) to 1 000 000 |
| `LivePollInterval` | 1 minute | 1 second to 24 hours |
| `UnpackTimeout` | 20 seconds | above 0, at most 10 minutes; one budget per delivery, every layer of every message (below); keep it well below the mediator's lease (60 s at ours) |

- **Transports.** Nothing is added for the host. Over HTTP, `UseHttpTransport()` is enough: the
  answer is the response body. Over a WebSocket the answers and live pushes arrive on the session
  socket S(E, L, M), so the host registers `UseWebSocketTransport(o => o.ReceiveInbound = true)` and
  `UseInboundDispatch()`; the dispatcher hands every verified inbound to the channel's correlator
  before any handler. Without that pair, a WebSocket route is refused before anything is sent
  (Routes, below).
- **Dependency walk (L-017).** Dispatcher factory → correlators → channel → `DidCommClient` →
  router → transports → `WebSocketDidCommTransport` → `IInboundFrameConsumer`
  (`InboundDispatchConsumer` takes the provider and a logger, and resolves the dispatcher per
  message). Nothing leads back to the dispatcher, the registry or a handler, so an application
  handler may take `MediationClient` or `PickupClient` in its constructor.
- **Disposal.** The container disposes the channel (idempotent; once per registration it was
  resolved through): waiting requests fail with `ObjectDisposedException`, live streams end with
  it, the shutdown token cancels sends and resolutions in flight, and new work is refused with it.
  The public clients are not disposable.
- **Without DI.** `new MediationClient(didComm, options?, logger?)` and
  `new PickupClient(didComm, options?, logger?)` each build a private channel that is not a
  correlator (`receivesInbound: false`): HTTP only, every other route refused before sending. It
  runs on `TimeProvider.System`, and its lines go to the caller's logger (`SingleLoggerFactory`
  hands that logger to every category). Each private channel has its own pending bound, duplicate
  filter, version cache and unpack limiter.
- **A mediator in the same host.** The mediator's handlers own the Coordinate Mediation and pickup
  PIURIs and ignore the answer types, and correlators run before handlers, so `AddMediator()` and
  `AddMediatorClient()` compose (`MediatorAndRecipientHostTests`). Both roles share one replay
  cache, keyed (sender subject, id), so they do not collide.
- **The receive-loop rule.** A session socket's loop reads its next frame only after the consumer
  returns (`IInboundFrameConsumer`). A handler or observer running for a message on S(E, L, M) must
  therefore never call these clients for the same session: the answer would wait behind it until
  the call times out. An `AsyncLocal` guard was rejected, because it flows into child tasks.
- **`DidCommServiceBuilder.ForMediation(grant)`** is a C# 14 `extension(DidCommServiceBuilder)`
  static member in `MediationServiceBuilderExtensions`. It compiles to the plain static method
  `MediationServiceBuilderExtensions.ForMediation(MediationGrant)`, which F#, Visual Basic and
  C# below 14 call directly. A `null` grant throws `ArgumentNullException`, a grant with no routing
  DID `ArgumentException`; otherwise it returns `DidCommServiceBuilder.ForMediator(grant.RoutingDids[0])`
  with that method's own checks (a bare DID of at most 2048 characters; `WithRoutingKeys` counts the
  mediator's layer toward the 8-layer bound). The DX scanner sees the static class and that method
  only; it skips the compiler's `<G>$…` grouping type (`ExtensionMemberSurfaceTests`).

### One request, one answer (`MediatorClientChannel.ExchangeAsync`)

1. **Refused before anything is registered or sent:** a disposed channel (`ObjectDisposedException`);
   a spec whose expected types are outside the answer-type table (`MediatorAnswerTypes.Validate`,
   `ArgumentException`: a facade's programming error); a request id that is empty or longer than
   64 characters (ours are 36-character UUID v4 ids from `UuidV4MessageIdGenerator.Instance`, never
   the host's generator); `MaxPendingRequests` already waiting (the internal `MediatorBusyException`,
   an `InvalidOperationException` to callers).
2. **Registered before the send** (`TryAdd`), because over a WebSocket the answer can arrive before
   `SendAsync` returns; an id already waiting → `InvalidOperationException`. A `PendingRequest`
   holds our id, the static spec, the two DID subjects of the (mediator, local DID) pair and a start
   timestamp, never a wire value. Its outcome is one terminal compare-and-swap (Waiting → Completed,
   Abandoned or Failed), the Discover Features client's `PendingQuery` pattern, over a
   `TaskCompletionSource` with `RunContinuationsAsynchronously`.
3. **Deadline** (`GuardedDeadline`): a timer on the injected `TimeProvider` at `RequestTimeout`, plus
   registrations of the caller's token and the channel's shutdown token. Each cancels inside a
   `try`, so a callback host code registered on the token cannot escape onto a timer thread or into
   the caller's `Cancel()`. It is never `CancelAfter` or a linked source handed to host code (#92
   follow-up 1). Its token is the send's; ending it abandons the pending request through the same
   compare-and-swap.
4. **Send.** `SendAsync(request, new SendOptions([mediatorDid], From: localDid,
   ServiceEndpointOverride: route.Endpoint) { ReturnRoute = All })`, awaited with `WaitAsync` on the
   deadline. `SendAsync` stamps `created_time` on its own copy (FR-RR-09); the request the channel was
   handed is never stamped by hand or modified. A send abandoned at the deadline is observed
   (`GuardedDeadline.ObserveAbandoned`), so a later fault never becomes an unobserved task
   exception.
5. **HTTP.** The verified snapshot of `SendResult.Returned` is offered to the matcher, bound to this
   request. If the request is still waiting after that, it fails at once (`PendingRequest.TryFail`,
   which marks its fault observed) with `TimeoutException`: "got no answer in the HTTP response;
   the mediator may still have acted on it, so retry it". HTTP returns at most one message
   (FR-RR-04), so a 202, an identity-refused body or a body that answers nothing leaves nothing to
   wait for. A `TransportException` (status 200: over the cap, stalled or unreadable, FR-RR-07)
   propagates as it is. The one exception is `ExchangeAllowingNoAnswerAsync`, used only by
   `PickupClient.AckAsync` and `FetchAsync`'s own acknowledgement for `messages-received` (whose
   `status` is a SHOULD): there the waiting request is abandoned through the same compare-and-swap
   (`TryAbandon`) and the call returns `null`. An answer that won the race still holds.
6. **Awaiting the answer.** Over a WebSocket the answer arrives through the correlator (over HTTP it
   has already completed the request in step 5). It is awaited; then the continuation yields
   (`ForceYielding`), so parsing never runs on the dispatcher's receive path.
7. **A problem report** answering the request (by `pthid`, or `thid`) → `MediationProtocolException`
   with `ProblemCode` sanitized (`MediatorProblemCode`: at most 128 characters of `a`–`z`, `0`–`9`,
   `.`, `_`, `-` that parse as a problem code, else `null`). The body is read on the caller's
   continuation.
8. **No answer:** a disposed channel → `ObjectDisposedException`; the caller's cancellation →
   `OperationCanceledException` (the caller's cancellation wins when it was requested before the
   call ended without an answer, even if the deadline fired first; one requested after that changes
   nothing); the deadline → `TimeoutException` naming our request id and the message name, never a
   DID.
9. **Finally:** abandon, remove exactly this entry (a `KeyValuePair` removal), release the counts.
   A late answer then finds no waiting request and is dropped. A late `delivery` is never
   acknowledged, so it comes back after its lease; a late grant or update was applied, and a retry
   is idempotent.

Each step of a public call has its own `RequestTimeout`: finding the mediator's endpoint (its own
deadline in `ResolveRouteAsync`; running out names no request, because nothing was sent), the
Discover Features query when the version is neither pinned nor remembered, and each request (one
per page in the walker). A call can therefore take longer than `RequestTimeout` in total.

### Matching answers (`Offer`)

The correlator calls `Offer(snapshot, boundTo: null)` for every verified inbound the dispatcher
sees (`IInboundCorrelator.OnInbound`, which returns whether the message was claimed); the HTTP
exchange calls `Offer(returned, pending)`. The matcher never awaits, never parses a body, completes
at most one request and never removes or cancels one, so a forgery can neither answer nor cancel a
request.

1. **Idle.** An unbound offer with nothing pending and no live stream open costs one volatile read
   (a host that is also a mediator pays that per inbound).
2. **Type.** The type is parsed once and matched against the twelve answer types of
   `MediatorAnswerTypes`: Discover Features 2.0 `disclose`; Coordinate Mediation 3.0 `mediate-grant`,
   `mediate-deny`, `recipient-update-response`, `recipient`; Coordinate Mediation 2.0 `mediate-grant`,
   `mediate-deny`, `keylist-update-response`, `keylist`; Message Pickup 3.0 `status` and `delivery`;
   Report Problem 2.0 `problem-report`. Matching is the registry's (case and punctuation ignored, any
   minor of the major). Anything else is not looked at. Log lines name the type by its constant,
   never by the wire value.
3. **Identity before any lookup** (D-7). Every comparison runs, combined with a non-short-circuit
   `&`: the snapshot is `Verified` (registered by unpack, never a `CreateFallback` snapshot, whose
   fields are the caller's claims); `ReturnRouteEligibility.TryGetPeer(snapshot, …)` passes
   (authcrypt; the sender key's DID subject is the peer; a signer, if any, agrees; the decrypting
   key's subject is the local DID; not a forward), the same rule as the receive side and
   `Returned`, now with a snapshot overload over one shared core; the `from`'s DID subject is the
   peer; and `to` is absent or empty (it is optional in DIDComm v2.1, and the decrypting key already
   names the local DID) or one of its first 64 entries names the local DID. A failure is other
   traffic: it returns silently and is not logged.
4. **Thread.** `pthid`, else `thid`, for a problem report; `thid` otherwise. A thread longer than
   64 characters is never looked up.

| The snapshot (identity passed) | Unbound: through the dispatcher (WebSocket) | Bound: in the HTTP response to request R |
| --- | --- | --- |
| thread = a waiting request P of the same pair; a type P expects, or a problem report | completes P | completes P (P may be another request than R; R then fails as no answer) |
| thread = a waiting request P of the same pair; a type P does not expect | dropped (Warning); P keeps waiting | dropped (Warning); R fails as no answer |
| thread names no waiting request of the pair | dropped (quiet) | dropped; R fails as no answer |
| no thread, `delivery` | the live stream of (peer, local), never a request; no stream, or its buffer full → dropped, not acknowledged | completes R when R expects a `delivery` (else dropped, Warning); never a stream |
| no thread, problem report | dropped (`pthid` is required) | completes R (it fails with the code) |
| no thread, another answer type | the only waiting request of the pair expecting the type takes it; none → dropped; two or more → dropped, they keep waiting (Warning); the only one requires a thread (`MediatorExchangeSpec.RequiresThread`) → dropped, it keeps waiting (Warning) | completes R when R expects the type, else dropped (Warning) |

"Warning" and "quiet" are the rate-limited levels under Logging, below. So a thread-less live push
never answers a waiting `delivery-request`, and a `delivery` with a thread is never taken for a
push. Over HTTP the exchange binds the body to its request, so a
third-party mediator that answers without `thid` works there; over a WebSocket a thread-less
`delivery` reaches only a live stream (R-10, an interop point for #98).

`RequiresThread` (#98 F-1, PR #130 review P2) is set only by `PickupClient.AckAsync`, on the
requests it sends after it gave up waiting for an earlier one on a WebSocket
(`DeliveryReader.MessagesReceivedThreadedSpec`, `StatusRequestThreadedSpec`). The given-up request
is gone from the pending map, so its late thread-less `status` would otherwise be taken as the next
request's answer. A request with the flag still counts as a candidate, so the candidates (and so
what any other request, a live stream's included, is offered) are the same as without it; it is only
never completed by a thread-less answer. A bound answer (the HTTP response) is not affected.

### Routes (`ResolveRouteAsync`)

One route per operation: the Discover Features query and the request, every page of a walk, a fetch
and its acknowledgement, every request of a live stream.

- **The endpoint** is the caller's `serviceEndpointOverride` (absolute, without user information;
  trusted, not checked against `OutboundEndpointPolicy`, as `SendOptions.ServiceEndpointOverride`
  is), or `DidCommClient.ResolveDirectEndpointAsync(mediatorDid)` under its own deadline. That
  Core internal selects the endpoint exactly as `SendAsync` selects a direct return-route endpoint,
  with `SendAsync`'s refusals in its order and with its exception types: the recipient entry's shape
  (`ArgumentException`), no service resolver (`InvalidOperationException`), `did:web`
  (`UnsupportedDidMethodException`), one read of the service entries and none
  (`DidResolutionException`), a first entry that needs forward layers (`InvalidOperationException`,
  `RoutedReturnRouteRefusal`), the queue URI or a URI that is not absolute (`TransportException`),
  then `OutboundEndpointGuard` (`TransportException`), applied here because the override the send
  will carry skips it. The send uses that URI as its override, so the HTTP-or-WebSocket decision and
  the send come from one read of one entry (L-041). There is no automatic "prefer `wss`": a mediator
  whose first entry is `https` is reached over HTTP unless the caller passes its `wss` address.
- **The scheme:** `http`/`https` — the answer is the response; `ws`/`wss` — the answer arrives
  through the dispatcher; any other scheme is treated like a WebSocket.
- **A route that is not HTTP is refused** with `InvalidOperationException`, before anything is
  sent, when no answer could reach the channel. The message names the fix:
  `UseWebSocketTransport(o => o.ReceiveInbound = true)` and `UseInboundDispatch()`. The route is
  refused when `receivesInbound` is false (without DI, or no `IInboundFrameConsumer` registered),
  or when `DidCommClient.ReceivesPushesOn(endpoint)` is `false`. That Core internal asks the
  transport `TransportRouter.Select` picks for the endpoint (the one `SendAsync` would use, L-041)
  through the internal `IInboundReceivingTransport`. `WebSocketDidCommTransport` implements it
  through `InternalsVisibleTo` and answers `ReceiveInbound && !UseStomp` (STOMP opens no session
  socket and reads nothing back). A router or transport of the host's own, no router, the queue URI
  or a scheme no transport handles is `null`, "cannot tell", and the route is allowed (the send then
  refuses what it must). Before this check a WebSocket route with `ReceiveInbound` off sent the
  request and timed out (QA-1, Q1-1).
- **Live delivery** (`SetLiveDeliveryAsync`, `ReceiveLiveAsync`) also refuses a route that is not
  `ws`/`wss`, with `InvalidOperationException` and nothing sent.

### Coordinate Mediation (`MediationClient`)

- **Arguments** are checked when the method is called, before anything is resolved or sent (L-008),
  and never quoted: `mediatorDid` and `from` are bare DIDs of at most 2048 characters
  (`ArgumentNullException` for `null`); the override is absolute and has no user information;
  `updates` holds 1 to 1024 entries (the count first, then a private copy, so what is checked is what
  is sent; each entry non-null, a bare DID, a defined action; the message names the index);
  `paginate.Limit` is 0 to 1000 and `Offset` 0 or more. There is no client-side splitting of
  updates.
- **Version** (`StartAsync`, after the route): a pinned `V3` or `V2` sends no query. Otherwise the
  `NegotiatedVersionCache` answers: keyed by the mediator's DID subject, at most 64 entries, each
  used for 1 hour on the monotonic clock; when full, expired entries are dropped first and a full
  cache of live entries takes no new one; an entry stamped ahead of the clock counts as expired; the
  clock is read inside the lock. Otherwise a Discover Features `queries` message with two queries
  (`feature-type: protocol`, `match: https://didcomm.org/coordinate-mediation/3.*`, then
  `…/2.*`; since #99, owner decision 2026-11-18) goes through the exchange on the same route. Each
  match reads the same way as the spec's trailing wildcard, a whole-id regular expression (the
  Identus mediator) and a prefix with `.*` removed (the Blocktrust mediator); the single
  `coordinate-mediation/*` the client asked before matched nothing under the last two.
  `FeatureNegotiation` reads the `disclose`: at most the first 256 disclosures; `feature-type`
  `protocol`; an `id` of at most 256 printable ASCII characters that parses as a PIURI; `roles`
  absent or `null` counts; a `roles` array (its first 32 entries, ignoring case) counts when it
  names `mediator`, does not when it names `recipient` without `mediator`, and counts when it names
  neither (empty, or only names such as Identus's `responder`: it says nothing about this
  protocol's roles); a `roles` value that is not an array does not count. Coordinate Mediation 3.x
  wins over 2.x, matched by major. Neither → `MediationNotSupportedException`, whose text carries
  only a count and whose `DisclosedProtocols` lists up to 32 distinct clean protocol ids among those
  inspected, in any role (D-11). A query that is not answered → `TimeoutException`: the client does
  not guess. Pin the version for a mediator without Discover Features.
- **Coordinate Mediation 2.0** (`Coordination/CoordinateMediation20`, internal): the 2.0 type URIs,
  builders for `mediate-request`, `keylist-update` and `keylist-query`, and strict parsers for
  `mediate-grant` (a string `routing_did`; an array is accepted too), `mediate-deny`,
  `keylist-update-response` and `keylist` (the list is `keys`). It writes and reads through
  `MediationBodies`, since the entries are the same as in 3.0. Since #99 it also holds the
  mediator direction (the request parsers and the four answer builders) for
  `CoordinateMediation20Handler` ("Coordinate Mediation 2.0, mediator role" above); it stays
  internal. The seven didcomm.org 2.0 examples, normalized, are `fixtures/coordinate-mediation-2.0`
  in `DidComm.Mediator.Tests`.
- **Answer checks** (`CoordinateMediationWire`). Each refusal is a `MediationProtocolException`
  naming our request id and the rule; a shape error carries the parser's `MalformedMessageException`
  inside.
  - Grant: `routing_did` is a string, or an array of at most 16, counted before any entry is read
    (L-055); each entry is a bare DID of at most 2048 characters. It returns
    `MediationGrant(PeerDid: from, RoutingDids)`. A deny returns `null`.
  - `recipient-update-response` / `keylist-update-response`: a list longer than the request is
    refused from its length; the count must be the request's, and the entry at each index must name
    the same `recipient_did` (ordinal) and `action` as the update there (every position compared).
  - `recipient` / `keylist`: at most 1000 DIDs whatever limit was asked for (counted first; a
    mediator that ignores `paginate` still works). With `pagination`, `count` must be the page's
    size, `offset` the one asked for (0 without `paginate`) and `remaining` not negative. A page
    without `pagination` is returned as it came.
  - The walker (`QueryAllRecipientsAsync`) uses one route and one version for the whole walk. It
    asks for pages of 100 from offset 0 and advances by what each page held. It ends normally when
    the mediator reports no position or nothing remaining. An empty page while DIDs remain, or DIDs
    still remaining after 1000 pages, throws `MediationProtocolException` after the DIDs already
    yielded: the walk never stops early in silence. Nothing is sent before the first
    `MoveNextAsync`.
- **Retrying is safe:** a repeated `mediate-request` gets the same grant, a repeated `add`
  `no_change`, a repeated `remove` `client_error`.

### Status, fetch and acknowledgement (`PickupClient`)

`GetStatusAsync` sends `status-request{recipient_did?}`. Whenever the request carried a filter or
could have, a `recipient_did` in the answer (`status` or `delivery`) must be absent or name the DID
asked about (as a DID subject). One present without a filter is refused too, as a
`MediationProtocolException`.

`FetchAsync(mediatorDid, from, limit, recipientDid?)`:

1. Arguments (`limit` 1 to 100; `recipientDid` a bare DID or `null`), then the route.
2. `delivery-request{limit, recipient_did?}`; the spec expects `delivery` or `status`.
3. `status` → `PickupBatch(Items: [], Drained: true, Status)`.
4. `delivery`: its `recipient_did` is checked before anything is unpacked.
   `PickupBodies.ReadAttachmentsEach` reads at most the first `limit` attachments, each on its own.
   The rest are not looked at, not even their ids: never decoded, unpacked or acknowledged, so they
   stay queued (a log line counts them). An attachment without a usable id (empty, over 2048
   characters, not unreserved URI characters) is ignored, because it could not be acknowledged. A
   repeated id: the first wins. A payload that does not decode is a failure item. No attachments →
   drained, no status. Attachments of which none has a usable id → `MediationProtocolException` (a
   hostile mediator cannot spin a fetch-until-drained loop).
5. **Open the delivery** through the unpacker (`UnpackDeliveredBatchAsync`, naming our request id):
   every decodable message under one budget, `UnpackTimeout`, four at a time, each with an equal
   share of it (below). A message not open when its share is up is a `TimeoutException` failure
   naming the request; the outcomes keep delivery order.
6. **The filter** (D-20, owner decision 1-A on the L-063 stop). With `recipientDid`, a message is
   that DID's only when its `RecipientKid`, the key that decrypted the innermost envelope, has the
   filter DID's subject; the kid is held to the FR-API-09 bound before it is parsed, and it is one
   comparison. The envelope's other recipient labels are never read: whoever encrypts a layer writes
   them (L-040). A message for another DID is a failure item (`MediationProtocolException`, "for
   another of your DIDs") and is not acknowledged. So a message encrypted to two of our DIDs, which
   opens with the first held key its envelope lists, passes an unfiltered fetch or that first DID's
   filter; under the second DID's filter its copy is a failure that stays queued until the host
   fetches it without a filter, discards it with `AckAsync`, or it ages out. Decrypting with the
   filter DID's key first (option B) would change Core's key choice; it is a candidate follow-up,
   not built.
7. **Classify.** A key the duplicate filter remembers for this mailbox (`from`), or one already
   seen in this batch, is a repeat: acknowledged and left out. Otherwise it is new.
8. `ct.ThrowIfCancellationRequested()`: the last cancellation point. Nothing is acknowledged or
   remembered yet, so everything comes back.
9. **Commit:** the new keys go into the duplicate filter, scoped to `from`.
10. **Acknowledge** the new messages and the repeats in one `messages-received` (at most 100 ids) on
    the client's own deadline (`OwnDeadline`, `RequestTimeout`), not the caller's token, through
    `ExchangeAllowingNoAnswerAsync`. The status that may answer it is not read, so over HTTP a
    response with no answer (the Identus mediator's empty 202; Message Pickup 3.0 makes that status a
    SHOULD) is a sent acknowledgement, not a failure, and nothing is asked afterwards. A failure (a
    problem report, no response within `RequestTimeout`, on a WebSocket no status within it, a
    transport error) is logged by exception type name (rate-limited Warning), and the batch is still
    returned; those messages come back and are repeats then.
11. Return `PickupBatch(Items, Drained: false, Status: null)`: the messages that unpacked
    (acknowledged) and the failures (left queued), in delivery order.

Once `FetchAsync` returns, the caller holds the only copy: the acknowledgement is inside the call
(FR-PICK-11; no opt-out, R-6). A failed item stays leased for the mediator's lease (60 s at ours) and
is not delivered again within it, so a loop that fetches until `Drained` gets past the failures to
what is behind them (failures that are slow to open only as many as its fetches pass over within
one lease: ADV3-6, a known limit deferred to #111, Documented limits); a caller that polls more
slowly than the lease, with more failures than `limit`, sees only the failures until it discards
them with `AckAsync` (L-058). That is why the
budget must end well inside the lease (owner decision 2-A; ADV2-1): the default, 20 s, is a third of
our 60 s `LeaseTimeout`, leaving room for the request and the acknowledgement
(`MediatorClientOptionsTests` pins the relation). When the lease runs out first, our mediator hands
the messages still being opened to a concurrent or the next fetch as well (at-least-once), and it
still accepts the late acknowledgement, because the store deletes by id
(`PickupClientBudgetLeaseTests`). The client never discards a failure on its own. A mediator that
does not set fetched messages aside delivers a failing message first on every fetch, and only the
host's `AckAsync` gets past it; the budget does not.

`AckAsync(ids)` takes 1 to 10 000 ids (the count first, then a copy; each non-empty, at most 2048
characters and unreserved URI characters). It sends 100 per `messages-received` and returns the
`status` after the last one; a failing chunk ends the call after the earlier chunks were sent. A
mediator configured to accept fewer ids per message refuses a longer one as malformed, so call it
with at most as many.

Only that status confirms the acknowledgement (#98 F-1). Message Pickup 3.0 says the mediator
SHOULD answer `messages-received` with an updated `status`, not that it must, and the Identus
mediator v1.2.1 sends none (its `PickupExecuter` returns `NoReply`; over HTTP a 202 with an empty
body). So a chunk left without a status is not an error: the next chunk goes out, and when the last
one is left without a status the client sends one `status-request` (no `recipient_did`; a status
that names one DID does not answer it) and returns its status, whose count is the confirmation. A
mediator that does answer gets exactly one request per chunk. A problem report or a malformed status
is still a `MediationProtocolException`. How "no status" is known:

- **Over HTTP, at once.** The chunk goes through `IMediatorExchange.ExchangeAllowingNoAnswerAsync`:
  the channel's HTTP no-answer branch, which for every other request throws `TimeoutException`
  ("got no answer in the HTTP response"), settles the pending request with the same terminal CAS
  (`TryAbandon`) and returns `null`. A real timeout (no response within `RequestTimeout`) is still a
  `TimeoutException` and ends the call.
- **On a WebSocket (any route that is not HTTP), after half of `RequestTimeout`.** Silence cannot be
  told from a slow answer there, so the chunk's `ExchangeAsync` runs under a `GuardedDeadline` of
  `RequestTimeout / 2` linked to the caller's token; when that deadline (not the caller) ends the
  wait, the chunk counts as left without a status. Its late status is not simply dropped: one with
  its `thid` finds no waiting request (dropped, quiet), but one without `thid` would be the only
  candidate for the next request of the pair, and the matcher would take it (the thread-less row of
  the matcher table). So once a wait was given up, every later request of the call (the next chunks
  and the `status-request`) uses a `RequiresThread` spec and takes only a status whose `thid` names
  it. DIDComm threading requires a reply to carry `thid` (v2.1 "Threading", and "Threads" under
  "When Problems Happen"); the Message Pickup 3.0 `status` example leaves it out, and that shape still
  works whenever nothing was given up. The cost: a WebSocket mediator that both leaves out `thid`
  and answers later than `RequestTimeout / 2` makes the call fail with `TimeoutException` (it would
  have succeeded before #98 F-1 when it answered within `RequestTimeout`). Over HTTP nothing is
  ever given up and nothing changes.

The budget rule: a `messages-received` and the `status-request` that may follow it share one
`RequestTimeout`, counted from when the `messages-received` is sent. The `status-request` runs under
a `GuardedDeadline` of what is left (over HTTP, the rest; on a WebSocket, the other half); with
nothing left it is not sent. When it gets no answer (that deadline, or over HTTP an empty response)
the call fails with a `TimeoutException` naming the request ids, never a DID; it never waits a second
full `RequestTimeout`. `FetchAsync`'s acknowledgement (step 10 above) shares only the HTTP half of
this: it asks for no status, and on a WebSocket it still waits its `RequestTimeout` and logs a
missing status as a failed acknowledgement. The live stream does not take this path: it sends
through `ExchangeAsync`, treats a missing status as a failed acknowledgement, keeps the ids and sends
them again after its back-off.

`SetLiveDeliveryAsync` needs a WebSocket route, sends `live-delivery-change` and returns the
`status`; a problem report is a `MediationProtocolException` with `ProblemCode`, for example
`e.m.live-mode-not-supported`.

### The live stream (`ReceiveLiveAsync`, `LiveDeliveryStream`)

The arguments are checked when the method is called. At the first `MoveNextAsync`, the route is
resolved with `requireWebSocket` and the stream's mailbox is registered
(`MediatorClientChannel.RegisterLiveStream`): one per (mediator, local DID) pair and at most 16 per
channel, else `InvalidOperationException`; nothing was sent. The mailbox (`LiveRegistration`) is a
bounded `Channel` of 8 snapshots. The matcher writes to it with `TryWrite`, so the receive path never
waits; a push that finds it full is dropped and was never acknowledged. Each stream also keeps a
ready queue of opened messages (one delivery, at most 10), the ids it holds (opened and not yet
yielded, or taken), and the taken ids waiting for an acknowledgement (at most 100).

Each `MoveNextAsync` loops, in this order: flush the acknowledgements when due; run the cycle when
due; yield the next opened message; then, while a cycle drains and no back-off is running, its
drain rounds and the buffered pushes take turns, a drain round first (fix round 3, ADV3-1): a drain
round runs unless the last step was a drain round and a push is waiting, in which case that push is
read and opened whole; with no push waiting the drain goes on; otherwise wait. A delivery is always
opened completely, under its budget, before its first item is yielded. Before fix round 3 a
waiting push always went first, so a steady flow of pushes that each took their whole budget to
open (a sender whose DID never resolves, one message every 20 s) kept the drain from ever running,
and what only a drain reaches (messages queued before the stream, delayed ones, pushes the full
mailbox dropped) never arrived. Taking turns, neither side can keep the other from its turn: a
push that waits stays leased to the session (nothing is lost; one that waits past its lease is
delivered again and the stream skips the copy it holds). The wait is one awaited
`WaitToReadAsync` on a token that an `OwnDeadline` timer cancels when the next cycle is due, linked
to the caller's token. One awaited operation leaves no losing task of a `WhenAny` race to fault
unobserved.

The stream's stored points in time — the next cycle, the end of a back-off, and each taken id's
moment (for the 10-second flush) — are judged by the time elapsed since the reading they were set
at, on the injected `TimeProvider`. A reading lower than that one (a host clock that stepped back:
an NTP correction, a restored snapshot) counts as due: one early cycle, retry or flush, never one
postponed by the step (fix round 3, ADV3-5; L-060, as `NegotiatedVersionCache` treats such an age).

- **The cycle** runs at the start, one `LivePollInterval` after the previous cycle's drain ended
  (on the monotonic clock), and after a failed step once the back-off has passed. A cycle that has
  started is never pre-empted: the loop arms the next cycle only once this one's drain has ended
  (`DrainDone`), so a mediator answering more slowly than the interval still gets the drain's
  delivery requests (PR #112 review R1-1: the next cycle used to be scheduled when a cycle started,
  before its answer, and the loop checks a due cycle first, so such a mediator got
  `live-delivery-change` for ever and nothing was fetched). It sends `live-delivery-change{true}` → `status`; a
  status with live delivery off is logged once as a Warning, and the stream keeps polling. Then it
  drains: `delivery-request{10}` until a `status` (or a delivery without attachments), at most 50
  rounds. Live mode goes on before the drain, so a message that arrives in between is after the
  mediator's arrival mark and is pushed, or is drained, never neither (#95, FR-PICK-06). Turning it
  on again keeps the first mark and wakes the grant (#95). A dropped socket is reopened by the next
  send (`ReturnRoute: All`); the new session starts with live mode off, and the next cycle turns it
  back on.
- **Polling is the permanent design** (owner decision D2 on #88, 2026-11-14). A delayed message is
  never pushed when it falls due, and no scheduler will push it (FR-PICK-08). The start-up drain and
  the periodic cycle are how it arrives, and how the stream recovers a dropped connection. Keep
  `LivePollInterval` at or below the longest delay senders ask for (the mediator caps it at 1 hour
  by default, `MediatorOptions.MaxForwardDelay`).
- **Items.** A push that is not a readable `delivery` is ignored. At most 10 attachments are read
  from one delivery (push or drain). An id the stream already holds is skipped (not opened again,
  not acknowledged). The rest of the delivery is opened at once, on the consumer's own
  `MoveNextAsync`, through the same unpacker as a fetch: one `UnpackTimeout` budget for the whole
  delivery, four at a time with an equal share each, under the abandoned-unpack bound (owner
  decision 2-A; before it, messages were opened one at a time, each with its own `UnpackTimeout`,
  ADV2-1). A message the budget runs out on is a `TimeoutException` failure naming the drain's
  request id (a push names no request). The opened results are then yielded in delivery order, so
  the consumer's time with an item never counts against the budget. A failure is yielded with
  `Error` set, is never taken or acknowledged, leaves the held set, and is reported again when it
  comes back; the stream never discards it (the host does, with `AckAsync`). A repeat the duplicate
  filter remembers for this stream's mailbox (`from`) is taken (acknowledged) and not yielded. The
  filter is asked when the item's turn comes, not when the delivery was opened, so a copy that
  follows a message taken from the same delivery is recognised. A drain answered with a
  delivery in which no message has a usable id ends the stream (`MediationProtocolException`); such
  a push is ignored. The `recipient_did` of a delivery or status is not checked here (the stream
  sends no filter).
- **Acknowledgement (R-2).** A yielded message is taken when the consumer asks for the next item
  (the next `MoveNextAsync`) with its token not cancelled: its key goes into the duplicate filter,
  scoped to the stream's `from`, and its id joins the ids waiting for an acknowledgement. They are
  flushed when 10 are waiting, when nothing is ready (no opened message and no buffered push), or
  when the oldest has waited 10 s. That is checked at the next `MoveNextAsync`, never during a
  back-off, and sent at most 10 ids per `messages-received`: a mediator may lower its `MaxAckIds`
  to its batch size (10 by default at ours), and a longer message would be refused as malformed. A
  failed flush keeps the ids and backs off. While acknowledgements keep failing, at most 100 taken
  ids are kept, and the oldest is forgotten unacknowledged (logged); it comes back and the
  duplicate filter recognises it.
- **Failures.** A step that fails with `TimeoutException`, `TransportException`,
  `MediatorBusyException` (the pending bound), or an `OperationCanceledException` the caller did not
  cause is retried after a back-off of 1, 2, 4, 8, 16, then 30 s; while a back-off lasts nothing is
  sent, acknowledgements included. Once the run of failed steps (cycle, drain round or flush) is
  older than `RequestTimeout` + `GiveUpMargin` (30 s; one minute by default) the next failure ends
  the stream with its exception (PR #112 ADV5-2: it used to end at the fifth failure, after
  1 + 2 + 4 + 8 = 15 s, before a busy refusal — whose cause ends within one `RequestTimeout` — or a
  16 s mediator restart could clear; the 16 s and 30 s pauses were dead code). The run starts again
  only when a drain completes (ADV5-3, a known limit deferred to #111). A clock stepped back below
  the run's first failure starts the run again. A successful live-on clears a back-off left over
  from the failure before it, so the cycle's drain rounds are not gated by it (ADV5-1: a clock
  stepped back into that back-off ran a cycle early and left its drain gated, and the loop span
  without awaiting; `WaitAsync` now also waits on the point the loop acts on — the back-off while
  draining, the next cycle otherwise). Anything else ends it at once, as it ends a fetch: a `MediationProtocolException` (a
  problem report such as `e.m.live-mode-not-supported`, a malformed answer), every other
  `InvalidOperationException` (the send path's configuration refusals), a pack-stage error such as
  `SecretNotFoundException`, `ObjectDisposedException`, the caller's cancellation. (Before the fix
  round, permanent pack-stage failures were retried with back-off, QA-2 Q2-2, and the busy refusal
  was told apart from configuration refusals by whether the mediator had answered yet; it now has
  its own type.)
- **The end**, whatever ended the enumeration (`break`, `return`, an exception out of the loop body,
  cancellation, a failure of the stream, disposal of the enumerator). The last yielded message is
  not taken: the stream cannot tell whether the consumer handled it, and an acknowledgement cannot
  be undone (L-056), so it comes back later. Then what was taken is acknowledged (10 ids per
  message), and live delivery is turned off when it was turned on, all within one `RequestTimeout`
  on an `OwnDeadline` independent of the caller's token; failures are logged. Last, the mailbox is
  disposed (exactly this entry is removed). Buffered or drained messages that were never yielded are
  never acknowledged; they come back after the session closes or their lease ends. A consumer that
  wants the last item gone calls `AckAsync(item.Id)` before it stops.
- **Bounds.** What a stream holds unacknowledged: at most 8 buffered pushes, 10 opened messages
  ready and the taken ones (10; up to 100 while acknowledgements fail). Against this library's
  mediator, which pushes one message per `delivery`, that is 28 messages in normal operation, well
  below its quota of 100 per recipient. Ordering is first in, first out within each source; there
  is no global order. One consumer per enumeration, as `IAsyncEnumerator` requires.

### Opening re-wrapped deliveries (`DidCommClient.UnpackDeliveredAsync`)

A Core internal (`Facade/DidCommClient.Mediated.cs`), used for every delivered message, fetched or
live (FR-ROUTE-06, receiver side). It unpacks the delivered text with `UnpackAsync`. While the result
is a `forward` (`ForwardMessage.TryParse`; a structurally broken forward is malformed), the layer
must be one a re-wrapping mediator added for this agent (`IsRewrapLayerForThisAgent`, every condition
evaluated): an envelope stack of exactly anoncrypt around plaintext; anonymous (no sender or signer
key, neither authenticated nor non-repudiable); no `from_prior` (header or parsed); exactly one
attachment; and a `next` whose DID subject is the subject of the key that decrypted the layer. That
is what `RewrapMode.ReanoncryptToNext` produces and what the mediator itself opens (`ForwardHandler`).
Otherwise the message fails with `ConsistencyException`. After 8 layers opened, one more
self-addressed forward fails with `MalformedMessageException`, so at most nine unpacks per message.
The attachment is decoded by `ForwardProcessor.ExtractAttachmentBytes` (now internal: the one
attachment-decoding rule, relaxed base64 or `data.json`; empty or invalid base64 is malformed, no
data a consistency failure). Each layer is unpacked with the full `UnpackAsync` rules (size cap,
FR-API-05 expiry, consistency), not the mediator's restricted unpack, because the innermost message
may legitimately be authcrypt. Only the DID that decrypted a layer counts as this agent: a forward
naming another DID, another DID this agent holds included, is not opened. The result is
`DeliveredUnpack(Result, InnermostPacked, LayersOpened)`; a pass-through delivery opens no layer.
The released `UnpackAsync` is unchanged.

### The duplicate filter and the unpack limiter

- **`DuplicateFilter`.** Delivery is at-least-once, so a message can come again after a lost
  acknowledgement, a dropped connection or a lease that ran out. A message's key (`KeyOf`) is
  SHA-256, truncated to 128 bits, over length-prefixed fields. An authenticated or signed message is
  keyed by the sender's DID subject (the authcrypt sender key's, else the verified signer's), the
  decrypting DID subject and the inner message id: message ids are unique per sender, so a stranger
  who reuses an id produces another key and cannot suppress the message. An anonymous message is
  keyed by its innermost packed text, so only a byte-identical copy matches. What the filter
  remembers is that key scoped to the mailbox it came from (`InMailbox`: SHA-256/128 over the DID
  subject of the fetch's or stream's `from` and the message key; ADV1-1). So the scope is per
  mailbox, not per local DID: a copy of T's message that a third party relays into F's mailbox,
  returned from there, never makes T's own copy a repeat, while the copies of one message in one
  mailbox (a wallet whose DIDs share one grant) are still returned once. One lock, a
  `HashSet<UInt128>` and a `Queue<UInt128>`: first in, first out at `DedupeCapacity` (4096; 0 turns
  the memory between calls off, while a fetch still returns a message that comes twice in one
  delivery once), O(capacity × 16 B) whatever the wire values (L-062). A key is added only at a
  commit point: in a fetch right before the acknowledgement, in a live stream when the item is
  taken. Fetches and live streams of one channel share it.
- **`DeliveredMessageUnpacker`** opens the messages of one delivery (a fetch's answer, a live drain
  round's answer or a push) under one budget (owner decision 2-A on the L-063 stop). Each payload
  is decoded as strict UTF-8 (invalid or empty is a malformed failure). The budget is one
  `GuardedDeadline` at `UnpackTimeout` for all of the delivery's messages, every layer included,
  linked to the caller's token. At most 4 messages are opened at a time, in delivery order, and each
  has its own `GuardedDeadline`, linked to the budget, at an equal share (`ShareOf`: the budget
  divided by `ceil(count / 4)`, the rounds the workers need; the whole budget for up to 4 messages).
  A message whose share runs out gives up its worker to the next message, so slow messages cannot
  keep the ones behind them from their turn (same-delivery starvation, found in fix round 2). The
  budget is the backstop: when it ends, every message still being opened or still waiting for its
  turn is a failure. Both failures are a `TimeoutException` naming our request id when there is
  one, never a DID or a message. The delivery's outcome is complete when its last message ends or,
  at the latest, when the budget ends: the budget's callback fills in the missing outcomes and lets
  go of the unpacks still running, without waiting for host code. Before a message starts, the
  count of abandoned unpacks (one counter per channel, so per container: unpacks whose time ran out
  but which ignore cancellation, still running) is checked against 16; at or beyond it the message
  fails at once, without starting, as a `TimeoutException` failure ("unpack capacity exhausted",
  L-059). It is a check, not a reservation (ADV3-2, a known limit: Documented limits). An abandoned
  unpack's outcome is observed when it ends, and it keeps its slot until then (ADV2-2, a known
  limit). Every unpack runs on a thread-pool work item of its own (`Task.Run`, fix round 3,
  ADV3-4): host code (the DID and secrets resolvers behind `UnpackDeliveredAsync`) never runs on
  the caller's stack or a share timer's, so the caller gets its task back and the budget decides
  the outcome even when a resolver blocks its thread. The unpack's task exists from the start, so
  one that blocks past its share or the budget keeps an abandoned slot like any other; the blocked
  thread itself is the host's until it returns (blocking host code cannot be bounded). The budget
  is linked to the caller's token and to the channel's shutdown token (fix round 3, ADV3-7):
  disposing the container ends a delivery being opened at once, every host lookup's token is
  cancelled, and the channel reports the cancellation as `ObjectDisposedException`, so a fetch or a
  live stream opening a delivery ends with it as their XML promises. Before the fix the disposal
  reached nothing, the lookups ran on against a disposed container and the call waited out the
  whole budget. A bad message is always a failure outcome; only the caller's cancellation and the
  shutdown throw.

### Logging

Categories `DidComm.Mediator.Client.MediationClient`, `DidComm.Mediator.Client.PickupClient` and
`DidComm.Mediator.Client.Internal.MediatorClientChannel`. A line carries our request ids (bounded),
constant message names, counts, enum names and exception type names. It never carries a DID, a key
id, an attachment or queue id, a message id the mediator or a sender chose, or an exception object
or text. Every call goes through `MediatorLog.Write`, so a throwing provider changes nothing.

- **Rate-limited, Warning the first time and every 1000th, Debug otherwise:** drops that point at the
  mediator (an answer from a request's pair of a type it does not expect, an HTTP response whose
  thread-less answer does not fit its request, an ambiguous thread-less answer, a thread-less
  answer whose only taker requires a thread, a push its stream's full buffer refuses), problem reports received, and the pickup lines (messages beyond the limit,
  unusable or repeated ids, unpack failures, filter mismatches, acknowledgement failures, cycle
  failures, ignored pushes, taken ids forgotten, failures at a stream's end). The pickup counters
  are per client, shared by its fetches and streams.
- **Quiet, Debug the first time and every 1000th, Trace otherwise:** a message of an answer type that
  no waiting request takes (late, or meant for another part of this agent, such as a `disclose` for
  the Discover Features client), and a push with no stream or after its stream ended.
- **Debug:** a request sent, answered or ended (id, name, elapsed milliseconds, exception type); the
  negotiated version; a fetch's counts; a stream's start and end. **Warning, once per stream:** the
  mediator answered live-on with live delivery off.
- **Not logged:** a message that fails the identity rule (other traffic).
- **The dispatcher (R-3).** When a correlator claimed a message (it completed a waiting request or
  reached a live stream; the Discover Features client claims a `disclose` that completed a query),
  the dispatcher's "No protocol handler registered" line is written at Debug instead of
  Information. Otherwise a host that only runs the client would log one Information line per
  mediator answer. Unclaimed messages keep the Information line.

### Threat model

- **Registration surface.** Host code at start-up only (`AddMediatorClient`): no runtime seam, no
  callbacks, no public correlator. `IInboundCorrelator` and `InboundMessageSnapshot` stay internal
  (R-1): a public inline hook would see every decrypted inbound and could stall the receive path.
  The correlator sees every verified inbound, does no I/O and no body parsing, and performs one
  `TrySetResult` or one `TryWrite`.
- **A hostile or compromised mediator** is authoritative for its own answers, within these bounds:
  each answer at most `MaxReceiveBytes`; at most `MaxPendingRequests` waiting; at most `limit`
  attachments read per fetch and 10 per live delivery, 8 buffered pushes per stream; at most 16
  routing DIDs, each a bare DID of at most 2048 characters; at most 1000 DIDs per page, pagination
  that must agree with the page and the request, at most 1000 pages per walk; update answers equal
  to the request in count, order, DID and action; at most 256 disclosures inspected and 32 reported,
  cleaned; problem codes sanitized; attachment ids validated before they are sent back, and never
  logged. The acknowledgement is exactly what unpacked plus the repeats, so a mediator cannot make
  the client acknowledge a failure or an id it never delivered. A delivery whose messages all lack a
  usable id is refused rather than spinning a loop. Unsolicited answers complete nothing unless a
  waiting request matches (its thread, or the one candidate of its pair). The routing DID of a grant
  is the mediator's word (publish it only for a mediator you trust).
- **Misaddressed payloads.** A message encrypted to someone else fails to unpack and is reported.
  Under a `recipientDid` filter, a message that a key of another of our DIDs opened is a failure;
  without a filter it is returned, so a multi-tenant host checks `Unpacked.RecipientKid` or uses the
  filter. Checking `RecipientKid` after the fetch costs the other DID nothing, because the duplicate
  filter is per mailbox (ADV1-1). The filter never reads an envelope's recipient labels: a
  co-recipient that re-encrypts a signed message to T and F and relays it into F's mailbox
  (ADV1-2), or a relay that appends F's label to an envelope without `apv` (ADV1-3), could write any
  of them. The first fix of QA-2 Q2-1 accepted such a label and was broken by both; the owner chose
  the strict rule and the documented two-DID behaviour (fetch step 6).
- **Third parties** cannot write on S(E, L, M); only E writes there. Through the host's own receive
  endpoints: a post-sign tamper fails to unpack and reaches nothing (over HTTP a tampered body is
  `TransportException` status 200; over a WebSocket the frame is dropped and the call times out); a
  self-consistent forgery (Mallory's authcrypt with `from` = Mallory and our waiting `thid`) passes
  the identity rule, which binds a sender to its own `from`, reaches the pending-map lookup and is
  refused by the request's (mediator, local DID) pair check, indistinguishable from a miss; the
  real answer still completes (ADV1-4 corrected the plan's "fails before any lookup";
  `MediatorClientChannelAdv1Tests`, `MediatorClientHostEndpointAdv1Tests`); `from` = the mediator
  with Mallory's sender key is refused at unpack (FR-CONSIST); anoncrypt, signed-only and
  `anoncrypt(sign)` fail the identity rule; authcrypt from the mediator to another of our DIDs fails
  the pair; a hand-built `UnpackResult` dispatched by host code has `Verified = false`
  (`MediatorResponseMatcherTests`).
- **Replays.** A captured genuine answer with a thread can only complete a request id that is
  waiting now (a fresh UUID v4 per request); a thread-less `disclose` is the exception, below
  (Documented limits). A captured thread-less push replayed to a host endpoint meets the shared
  FR-RR-08 cache (duplicate, or too old); if admitted, the duplicate filter recognises it, and it is
  acknowledged again harmlessly.
- **Resources.** Pending requests ≤ 64; live streams ≤ 16, each ≤ 8 buffered snapshots of at most
  `MaxReceiveBytes`; the ids a stream holds are bounded by its ready, buffered and taken bounds
  (10 + 8 + 100), each id at most 2048 characters; duplicate filter ≤ 4096 × 16 B; version cache
  ≤ 64; abandoned unpacks checked against 16 when an unpack starts (not reserved: deliveries opened
  together can exceed it, ADV3-2, Documented limits); an idle correlator costs one volatile read.
- **Timing.** Identity is decided before any dictionary is probed, with `&`-combined compares. The
  thread lookup runs for any authcrypt addressed to one of our DIDs whose `from` is its sender, not
  only a mediator's (ADV1-4): a hit on another pair's request is refused by the pair check and looks
  exactly like a miss (same result, same log line, the request keeps waiting), and our ids are
  122-bit random.
- **Peer-controlled latency (L-061).** The correlator never awaits, and the receive loop is never
  held by the client. Resolving an inner sender's DID runs on the caller's own await (a fetch) or
  the stream consumer's, bounded by one `UnpackTimeout` per delivery, four at a time with an equal
  share each, in a fetch and in a live stream alike, and by the abandoned-unpack check. So a
  delivery ends within its budget, well inside the mediator's lease, whatever its senders' resolvers
  do (ADV2-1), resolvers that block their threads included, since every unpack runs on the pool
  (ADV3-4). The live stream is one serial worker: once a cycle drains, its drain rounds and the
  pushes take turns, so peer-controlled work on one side cannot decide that the other never runs
  (ADV3-1). What one delivery's budget does not bound is progress across deliveries: see the known
  limits deferred to #111 (Documented limits). A mediator's latency is bounded by `RequestTimeout`,
  the mediator's DID resolution by the endpoint step's own deadline.
- **Clocks.** Over a WebSocket, answers and pushes pass the client's own replay admission: a
  mediator more than `ReplayProtectionOptions.ClockSkew` (60 s by default) ahead has them dropped
  (`FutureDated`) although it acted, so the call times out; retries are idempotent, and live pushes
  come back after their lease. Requests pass the mediator's admission: a client more than the
  mediator's `ClockSkew` ahead (`FutureDated`), or more than its `Window` (5 minutes by default)
  behind (`TooOld`), is refused unexecuted and times out. Over HTTP, `Returned` is not admitted, so skew decides nothing for answers there. Keep the
  clocks within a minute of each other by default (`MediatorClientClockSkewTests`,
  `PickupClientClockSkewQa2Tests`).
- **Unobserved task exceptions** (QA-1 Q1-3, QA-2 Q2-3, fixed): a failed pending request that nobody
  awaits (HTTP no-answer, disposal during a send), a send or resolution abandoned at its deadline, and
  an unpack that ended as its deadline fired are all observed (`MediatorClientUnobservedTests`,
  `PickupClientUnobservedQa2Tests`).

### Documented limits

- **At-least-once.** Repeats are recognised while the duplicate filter remembers them; an
  application that must never act twice tracks the message ids it handled.
- **A fetch is at-most-once to the application** once it returns (above); `ReceiveLiveAsync`, or the
  caller's own `AckAsync`, gives at-least-once to the application.
- **The last item of a live stream is not acknowledged** when the enumeration ends; it comes back.
- **The 10-second flush is checked at the next `MoveNextAsync`.** A consumer that holds an item longer
  than the mediator's lease (60 s at ours) leaves the taken ids before it unacknowledged that long,
  and they can be delivered again; the duplicate filter recognises them.
- **A burst re-pushed after the push buffer overflowed** is cleared over several cycles: latency
  only, nothing is lost.
- **A slow sender DID** costs each delivery that carries one of its messages up to that message's
  share of the budget, and its failures come back once per lease period until the application
  discards them with `AckAsync` (the client never does). A mediator that does not set fetched
  messages aside delivers such a message first on every fetch, and the budget does not get past it.
- **Known limits with slow senders, deferred to #111** (slow-sender hardening,
  https://github.com/moisesja/didcomm-dotnet/issues/111; owner decision on #88, 2026-11-14). Their
  reproductions are checked in as skipped tests whose skip reason names #111. One budget bounds one
  delivery, not the progress of a loop across deliveries:
  - **Nothing remembers a slow message from one fetch to the next** (ADV3-6, Medium; skipped
    `PickupClientLiveAdv3Tests.ADV3_6_…`, interop). A fetch of slow messages takes its whole
    budget, so within one 60 s lease a loop makes about three fetches and passes over at most about
    3 × `limit` slow messages; with more of them in front of an honest message the oldest come back
    first as their lease ends, and a fetch-until-drained loop never reaches the honest one (16 in
    front of it for `limit: 4`, 31 for `limit: 10`). A live stream's drain is in the same position,
    and while such a drain cannot end, a push gets one turn per drain round. Remedy for now: discard
    what keeps failing with `AckAsync`. Before fix round 3 this section said a fetch-until-drained
    loop "gets past slow messages" without that bound.
  - **A message's share is fixed up front** (ADV3-3, Low; skipped
    `DeliveredUnpackAdv3Tests.ADV3_3_…`). `ShareOf` divides the budget by the delivery's rounds of
    four before any message runs, so 99 messages that fail at once leave an honest message whose DID
    takes 2 s to resolve a share of 0.8 s: it fails as out of time though most of the budget is
    unused.
  - **The abandoned-unpack bound is a check, not a reservation** (ADV3-2, Low; skipped
    `DeliveredUnpackAdv3Tests.ADV3_2_…`). Deliveries opened at the same moment all pass the check
    before any of their unpacks is abandoned: 8 together left 32 abandoned unpacks running, and 16
    live streams could leave 64.
  - **The abandoned-unpack slots are shared by the container** (ADV2-2, Low; pinned as a passing
    residual by `DeliveredUnpackAdv2Tests`). Only a host DID resolver that ignores cancellation
    keeps a slot past its message's time, and one such sender can then hold all of them: until those
    lookups end, every delivered message fails at once ("unpack capacity exhausted"), for every DID
    and tenant of the container. The fix for now is a resolver that honours its token;
    `UnpackTimeout` and `FetchAsync` document it.
  - **Deferred from the ADV-5 pass (owner decision on #88, 2026-11-14), reproductions skipped
    naming #111 in `PickupClientLiveAdv5Tests`:**
    - **The run of failures resets only when a drain completes** (ADV5-3, Low). Cycles that deliver
      and acknowledge but whose drain never completes (a lossy link and a long backlog) keep one run
      of failures going, so their scattered failures end the stream once the run is older than
      `RequestTimeout` + 30 s.
    - **A mediator that does not lease** (ADV5-4, Low). Pickup 3.0 does not require a mediator to
      set delivered messages aside; at one that does not, a message that never opens is at the head
      of every answer, so every drain sends 50 `delivery-request`s and yields it 50 times, and the
      messages behind it wait until the host discards it with `AckAsync`. This library's mediator
      leases, so it is not affected; #98 checks which external mediators lease.
  - **Found by the final break-it pass on fix round 3 (ADV-4), deferred to #111 by owner decision
    (that pass's findings are documented, not patched); none loses a message; reproductions skipped
    naming #111 in `PickupClientLiveAdv4Tests` and `DeliveredUnpackAdv4Tests`:**
    - **A new cycle took a waiting push's turn back** (ADV4-1, Low) — **resolved in #96** by the
      fix for the review's R1-1: no cycle can come due while a drain is in progress, so nothing
      clears the owed push turn; `PickupClientLiveAdv4Tests.ADV4_1_…` runs again and passes.
    - **A blocked unpack keeps its whole delivery reachable** (ADV4-2, Low). The `Task.Run` work
      item captures the `Delivery`, so while host code blocks its thread the delivery's bytes and the
      messages opened so far stay in memory until that code returns (bounded by the slots; L-062).
    - **Host code can start after its delivery ended** (ADV4-3, Low). A message a worker was starting
      when the budget, the caller or the container's disposal ended the delivery can still have its
      DID and secrets lookups started afterwards, with an already-cancelled token, possibly after the
      container was disposed.
    - **A clock whose readings keep going lower** (ADV4-4, Low; needs a host `TimeProvider` that
      breaks its monotonic contract). Every pass then finds the next cycle due: the live stream
      re-sends `live-delivery-change` in a tight loop and skips its back-offs. One step back costs one
      early cycle, as designed.
    - **Pool threads per blocking sender** (residual). A resolver that blocks its thread can now hold
      up to four pool threads per delivery being opened besides the 16 slots (and the ADV3-2
      overshoot); on a small machine that can starve the thread pool for the whole process, its
      timers included.
- **Blocking host code cannot be bounded.** Every unpack runs on the pool, so a resolver that blocks
  its thread no longer holds the caller, and the budget still decides the outcome; but the blocked
  thread and its work stay the host's until it returns (holding an abandoned-unpack slot meanwhile).
  `FetchAsync`, `UnpackTimeout` and the unpacker's XML say so.
- **Junk that never opens stays queued** until the host discards it with `AckAsync` or it ages out
  at the mediator (`MessagePickupOptions.MaxMessageAge`, 30 days by default), and it counts against
  the recipient's mailbox quota there (this library's mediator: `MaxQueueMessages`, 100 by
  default, beyond which a message is not accepted). The client never discards a failure on its own
  (L-056), so anyone who can address the recipient can fill its mailbox for honest senders until
  the host discards the junk. `FetchAsync` documents it.
- **With `DedupeCapacity = 0` a live stream yields a message that comes twice in one delivery
  twice.** The once-per-delivery rule is a fetch's (its own per-delivery set); the stream keeps
  none (pinned by `PickupClientLiveAdv3Tests.Pinned_with_dedupe_off_…`). `DedupeCapacity` and
  `ReceiveLiveAsync` document it.
- **A message encrypted to two of our DIDs** fails under the second DID's filter (fetch step 6):
  discarded with `AckAsync`, fetched without a filter, or aged out. Decrypting with the filter DID's
  key first (option B) is a candidate follow-up, not built.
- **`PickupItem.Error` is the unpack's own exception**, and Core's unpack texts can quote DIDs and
  key ids (pre-existing in Core): a host logs its type, not its text. The client's own failures (a
  timeout, a filter mismatch) name at most our request id.
- **Picked-up messages are not replay-checked.** FR-RR-08 runs on the transport path only; a message
  a host takes from a `PickupItem` and hands to its own handlers has not been through it. The XML
  docs tell the host to track the ids it handled.
- **A mediator that withholds its answers** holds a live stream's `DisposeAsync` (the end of an
  `await foreach`) for up to one `RequestTimeout`, while the stream acknowledges what was taken and
  turns live delivery off.
- **A thread-less `disclose`** (a non-conformant mediator that leaves out the `thid` DIDComm
  requires) is outside replay admission, since `disclose` is not a mediator-service type, so a
  captured copy replayed to the host answers every later Discover Features query of its pair
  through the thread-less fallback (`MediatorClientChannelAdv1Tests`).
- **One replay budget per sender** (ADV-2). Over a WebSocket, every Coordinate Mediation and Pickup
  answer and push of a mediator counts against its sender bucket in the client's replay admission:
  `ReplayProtectionOptions.MaxEntriesPerPeer` (1024 by default), each entry kept about 6 minutes
  (`Window` + `ClockSkew`), for all of the host's DIDs together. Beyond it the rest are refused
  (`Saturated`): pushes come back by polling, and calls time out. A busy recipient host raises the
  setting.
- **A host `IInboundFrameConsumer` of its own** makes the channel believe it can receive (it looks
  only at whether a consumer is registered). If that consumer does not hand what it receives to the
  `ProtocolDispatcher`, the route is admitted and WebSocket requests time out instead of being
  refused, and a live stream receives nothing. `AddMediatorClient` and both clients' WebSocket
  remarks say so.
- **Only the first service entry** of the mediator's DID decides the route; pass an override for
  another.
- **Pre-existing, outside #96:** `DiscoverFeaturesClient.LogRejected` (Core, released) writes the
  rejected `disclose`'s message id, `from`, `to`, recipient key id and the query's requester and
  responder DIDs into its Warning line; a follow-up for Core, not for this client.
- **Interop points for #98.** A mediator that does not stamp `created_time` on its WebSocket answers
  has them dropped by the client's admission. A mediator that answers `delivery-request` without
  `thid` works over HTTP only (R-10). A mediator that puts a `thid` on its live pushes, or pushes raw
  messages instead of `delivery` (DD-13), is not taken as a push; the stream still gets those
  messages by polling. A mediator without Discover Features needs a pinned version.

### Demonstrated by (#97)

- `samples/11-MediatedAgent/Wallet` (`RecipientWallet`, `LiveConnection`): a class library that
  references only `DidComm.Mediator.Client`, the transports and the DI package. It fetches the
  invitation through the outbound guard (a `SocketsHttpHandler` whose `ConnectCallback` is
  `OutboundEndpointGuard.ConnectAsync`, no redirects, a 64 KiB cap), enrolls with a hand-built
  `MediationClient` (every method, `MediatorClientOptions` with `Validate()` and all six options),
  publishes a DID built with `ForMediation(grant)`, fetches over HTTP with `PickupClient`, then goes
  live over WebSocket with the three set-up calls: `SetLiveDeliveryAsync`, `ReceiveLiveAsync`, and
  `AckAsync(item.Id)` for the last item.
- Cookbook CC (enrolment, the wrong peer, the wire table), DD (fetch and the wire table) and EE.3
  (live pickup) carry the plain-language rules: at-least-once delivery with repeats filtered per
  mailbox, acknowledgement on the next item, `AckAsync` for a message that keeps failing, polling in
  live mode as the permanent design (a delayed message that becomes due is not pushed), clocks within
  a minute by default.

## Phase 7: still to build

Open under epic #88: the live interop and adversarial gate (#98); Message Pickup 4.0 (#100), a second thin handler over the same `MessagePickupQueue`; the push to a
host-registered recipient endpoint, deferred from #95 (#110); and the recipient client's slow-sender
hardening (#111). Sample 11, cookbook CC–FF and the sample 04 refresh landed with #97, which
retired every Phase 7 entry of the DX allowlists. Coordinate Mediation 2.0 for the mediator role
landed with #99.
