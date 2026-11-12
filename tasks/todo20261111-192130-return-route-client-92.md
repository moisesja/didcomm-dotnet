# todo20261111-192130 — Return-route client side: `SendOptions.ReturnRoute`, FR-RR-09 stamping, HTTP response read + `SendResult.Returned`, WebSocket session sockets + inbound dispatch, `SendPackedAsync` (#92)

Issue: #92 (epic #88; builds on #89 PR #101, #90 PR #102, #91 PR #103 merge `097fad9`). Branch: `feat/return-route-client-92` (L-019: re-verify before every commit).
Binding refinements: the "#92" paragraphs of the three #88 comments override the issue body where they differ; "Planning update after #91" is the most current. In short: `ReturnRoute` is `ReturnRouteMode?`. The refusal is an `InvalidOperationException` thrown after the service entry is read, only for ≥ 1 forward layer, and before any mediator DID is resolved and before packing. `Returned` is surfaced only for authcrypt by the addressed DID. Socket affinity is per S(E, L, P). FR-RR-09 stamping is load-bearing. WebSocket inbound admission uses the server's rule, then `DispatchAsync(…, session: null)`. Handlers never re-check replay.
PRD rows (the target): §8a FR-RR-07 and FR-RR-09 (#92's rows); FR-RR-01..06 and -08 (receive side, consumed); FR-TRN-03/09/10/11; FR-API-05/07/09; §4.3 `DidSubjectOf`; DD-11..14; §15.2; §12 Phase 7 exit criteria.
Orchestrator calls JC-1..JC-13 are validated in K (kept, refined or overturned, each with a reason); JC-14..JC-22 are new.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build` (SDK Package Validation against baseline 1.5.0, `Directory.Build.props`).

Files this change may touch (and nothing else):
- Core, EDIT: `src/DidComm.Core/Transports/{SendOptions,SendResult,TransportRequest,TransportResult}.cs` (init-only members); `IDidCommTransport.cs` (XML doc only); `src/DidComm.Core/Facade/DidCommClient.cs`; `src/DidComm.Core/Messages/MessageBuilder.cs` (`WithReturnRoute` delegates to the shared writer, behaviour identical); `src/DidComm.Core/Resolution/MediatorEndpointExpander.cs` (+`ForwardLayerCount`).
- Core, NEW in `src/DidComm.Core/Transports/ReturnRoute/`: `IInboundFrameConsumer.cs` and `InboundFrame.cs` (public); `ReturnRouteHeaderWriter.cs`, `OutgoingCreatedTime.cs`, `ReturnRouteIdentity.cs`, `InboundReplayGate.cs` (internal).
- AspNetCore (minimal): EDIT `src/DidComm.AspNetCore/InboundAdmission.cs`. The FR-RR-08 scope and replay block becomes a call to `InboundReplayGate`, and `SessionOutbound.TryPackAsync` stamps `created_time` (JC-17).
- HTTP: EDIT `src/DidComm.Transports.Http/HttpDidCommTransport.cs`.
- WebSocket: EDIT `src/DidComm.Transports.WebSocket/{WebSocketDidCommTransport,WebSocketTransportOptions,WebSocketDidCommBuilderExtensions}.cs`.
- DI: EDIT `src/DidComm.Extensions.DependencyInjection/DidCommBuilder.cs` (+`UseInboundDispatch`); NEW `src/DidComm.Extensions.DependencyInjection/InboundDispatchConsumer.cs` (internal).
- Sample: EDIT `samples/02-Cookbook/Sections/Section_R_ReceiveWebSocket.cs` (remove `.WithCreatedTime(...)` and its comment sentence).
- Tests, NEW: `tests/DidComm.Core.Tests/Transports/ReturnRoute/ReturnRouteClientRulesTests.cs`; `tests/DidComm.Core.Tests/Transports/DidCommClientReturnRouteSendTests.cs`; `tests/DidComm.Core.Tests/Transports/DidCommClientSendPackedTests.cs`; `tests/DidComm.InteropTests/Transports/{HttpTransportResponseTests,WebSocketSessionSocketTests,ScriptedSessionWebSocket}.cs`; `tests/DidComm.InteropTests/AspNetCore/{ReturnRouteClientHarness,ReturnRouteClientHttpTests,ReturnRouteClientWebSocketTests,ReturnRouteClientAdversarialTests}.cs`; `tests/DidComm.InteropTests/Protocols/InboundDispatchDiTests.cs`.
- Tests, EDIT: `tests/DidComm.InteropTests/AspNetCore/ReturnRouteTestHost.cs` (add a `Pickup40RecordingHandler` for `https://didcomm.org/message-pickup/4.0`; the `Stamped` comment now says "PackEncryptedAsync does not stamp; SendAsync does (FR-RR-09)"); `tests/DidComm.InteropTests/DxCoverage/{coverage,execution}-allowlist.json` (H4).
- Docs: `CHANGELOG.md`, `docs/codebase-architecture.md`, `docs/didcomm-dotnet_PRD.md` (the flagged FR-RR-07 / FR-RR-09 amendments in H5 only), this file.
- NOT touched: `samples/04` (its rewrite is #97's), `samples/05`, `ForwardHandler`, `ProtocolDispatcher`, `ProtocolContext`, `StompServerSession`, the #91 session types (except the one stamping line in `SessionOutbound`), `tools/*`, `Directory.Build.props`. No member is added to `IDidCommTransport` or `ITransportRouter`, because adding an interface member breaks every implementer.

Transitive public surface (L-006). Every referenced type is already public: `ReturnRouteMode`, `UnpackResult`, `ReadOnlyMemory<byte>`, `Uri`, `IOptions<>`, `ILogger<>`, `DidCommOptions`.
- `SendOptions.ReturnRoute` (`ReturnRouteMode?`), `SendOptions.ReturnRouteThread` (`string?`)
- `SendResult.Returned` (`UnpackResult?`)
- `TransportRequest.ReturnRoute` (`ReturnRouteMode?`), `.LocalDid` (`string?`), `.PeerDid` (`string?`), `.MaxResponseBytes` (`int?`)
- `TransportResult.ResponsePayload` (`ReadOnlyMemory<byte>?`), `.ResponseMediaType` (`string?`), `.ResponseDiscarded` (`bool`)
- `DidCommClient.SendPackedAsync(ReadOnlyMemory<byte> packed, Uri endpoint, string mediaType, CancellationToken ct = default)` → `Task<TransportResult>`
- `DidComm.Transports.ReturnRoute.IInboundFrameConsumer { ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct); }`
- `DidComm.Transports.ReturnRoute.InboundFrame(Uri Endpoint, string LocalDid, string PeerDid, ReadOnlyMemory<byte> Payload)` (sealed record)
- `WebSocketTransportOptions.ReceiveInbound` (`bool`, default `false`)
- `WebSocketDidCommTransport(IOptions<WebSocketTransportOptions> options, ILogger<WebSocketDidCommTransport>? logger, IOptions<DidCommOptions>? coreOptions, IInboundFrameConsumer? inboundConsumer)`. This is a NEW overload with all four parameters required (L-035). The existing 3-parameter constructor is unchanged and delegates with `inboundConsumer: null`. Existing calls, including `new(options, logger: null, coreOptions: core)`, still bind to the old constructor, because the new one needs a fourth argument.
- `DidCommBuilder.UseInboundDispatch()`

---

## Threat model (L-031) — read before E and F

- **Who registers a consumer.** The host, at startup only: `UseInboundDispatch()` (a `TryAdd` of the built-in consumer), its own `IInboundFrameConsumer` registration, or the new transport constructor. There is no runtime add or remove.
- **What a consumer sees.** The raw packed bytes the peer pushed on a session socket the client itself opened, together with the transport's (E, L, P) attribution. Those three values are the client's own routing facts, copied from its own request, never wire claims.
- **What a consumer can do.** It holds no socket handle and cannot write to the transport. The built-in consumer unpacks, applies the identity rule (sender subject == P and decrypting subject == L, JC-6), runs FR-RR-08 admission with the server's rule, then dispatches with `Session == null`. A handler's reply is logged, never sent (JC-9, L-036/L-037: no reflector).
- **Trust boundary.** Trust comes from authcrypt by P to L, not from the socket. Anoncrypt, signed-only, `anoncrypt(sign)`, plaintext, `forward`, another sender, or an envelope encrypted to another local DID: all are dropped before admission, so they consume no replay budget.
- **HTTP `Returned`.** The response body is read only when the caller asked for a return route (JC-3), and capped before any allocation (JC-2). Garbage becomes a `TransportException` (FR-API-07). An identity mismatch is discarded and logged. Residual risk, of a pre-existing class: unpacking a hostile response resolves the sender key's DID, exactly as every receive endpoint already does. It is bounded by FR-API-09 caps and `MaxReceiveBytes`, and reachable only for return-route sends.
- **Flood and isolation.** Each session socket hands its frames to the consumer itself, one at a time (JC-8). A peer that floods or slows its own session backpressures only that socket; no other peer's frames are dropped or delayed.
- **SSRF.** `SendPackedAsync` always applies `OutboundEndpointGuard`. The refusal (FR-RR-02) happens before any mediator or routing-key DID is resolved.
- **Audit.** Log lines carry message ids (only after the identity rule passed) and enum or exception *type* names. Never a DID, kid, thread id, endpoint, body or exception object. Lines a flood could trigger are rate-limited (first and every 1000th at Warning, Debug otherwise).
- **Adversarial items (G7):** wrong-peer, wrong-local and unauthenticated pushes; a replayed push; a 100 MB response (Content-Length and chunked); a stalled body; garbage under a DIDComm content type; a flood on S1 alongside traffic on S2; races between retire, close and send; dispose with a consumer that ignores cancellation; a log scan.

---

## A. Core model and shared rules (`DidComm.Core`)

- [x] A1. `SendOptions.cs`: add `public ReturnRouteMode? ReturnRoute { get; init; }` and `public string? ReturnRouteThread { get; init; }`, outside the positional list so the constructor and `Deconstruct` are unchanged (JC-1).
  - Docs for `ReturnRoute`: `null` emits nothing and leaves a builder-set header as is; `None` emits `"none"`; `All`/`Thread` emit those values and override a builder-set header. With `All`/`Thread` the send is refused for routed recipients and for recipients naming more than one DID, and HTTP replies come back in `SendResult.Returned`.
  - Docs for `ReturnRouteThread`: required with `Thread` and invalid otherwise; the `thid` alphabet; at most 2048 characters.
- [x] A2. `SendResult.cs`: add `public UnpackResult? Returned { get; init; }`. Docs: HTTP only. Non-null only when the send asked for `all`/`thread`, the response was `200` with a DIDComm type, and it unpacked as authcrypt by the addressed DID to the sending DID. Over WebSocket, replies arrive through the inbound consumer, so this is always `null`. Also: `Transport.ResponsePayload` holds the raw bytes, and only `Returned` has passed the identity rule.
- [x] A3. `TransportRequest.cs`: add `ReturnRouteMode? ReturnRoute`, `string? LocalDid`, `string? PeerDid`, `int? MaxResponseBytes` as init-only properties. Docs:
  - `ReturnRoute`: the effective header as sent; `null` means absent.
  - `LocalDid` / `PeerDid`: bare DID subjects set by the facade for direct authcrypt sends; transports use them for connection affinity (FR-RR-07); `null` means none.
  - `MaxResponseBytes`: `null` means "do not read an in-band response". A transport that can return one reads at most this many bytes.
- [x] A4. `TransportResult.cs`: add `ReadOnlyMemory<byte>? ResponsePayload`, `string? ResponseMediaType` (the canonical `MediaTypes` constant that matched) and `bool ResponseDiscarded` as init-only properties. Docs: `ResponseDiscarded` means "the peer returned a DIDComm body that was not read: over `MaxResponseBytes`, or not received within the timeout"; `Accepted` is unchanged. Remove "Phase 5 transports are delivery-only" from the summary (L-045).
- [x] A5. NEW `IInboundFrameConsumer.cs` and `InboundFrame.cs` (public, namespace `DidComm.Transports.ReturnRoute`).
  - The contract text: called by a transport's session receive loop, one frame at a time per socket. The next frame on that socket is read only after `ConsumeAsync` returns. A consumer or handler must not wait for another inbound on the same session, or that session deadlocks until its own timeout. `ct` is cancelled when the socket retires or the transport is disposed. Exceptions are logged by type name and the loop continues. The frame is untrusted bytes; L and P are the transport's attribution, not a claim of authenticity.
  - L-017 rule: an implementation must not take `DidCommClient`, `ProtocolDispatcher`, `ProtocolHandlerRegistry` or a handler in its constructor (the transport is a dependency of the facade). Resolve them through `IServiceProvider` at call time.
  - `InboundFrame` doc: positional record equality compares `Payload` by memory identity.
- [x] A6. NEW internal `ReturnRouteHeaderWriter`: `Validate(ReturnRouteMode mode, string? thread)` and `Write(Message m, ReturnRouteMode mode, string? thread)`, containing exactly the validation and writes now in `MessageBuilder.WithReturnRoute` (lines 196–235). `WithReturnRoute` calls them. Existing `MessageBuilderReturnRouteTests` must stay green unchanged (byte-exact `"return_route":"all"`; a stale `return_route_thread` is removed).
- [x] A7. NEW internal `OutgoingCreatedTime`:
  - `IsInScope(Message m)` = `ReturnRouteHeader.Read(m).Mode is All or Thread || MediatorServiceProtocols.IsMediatorServiceType(m.Type)`. A malformed header counts as `none`.
  - `StampIfInScope(Message m, DateTimeOffset now)` sets `m.CreatedTime = now.ToUnixTimeSeconds()` only when the value is `null` and the message is in scope. A caller's value is never refreshed (FR-RR-09).
- [x] A8. NEW internal `ReturnRouteIdentity` (JC-6):
  - Signature: `static bool IsFromPeerToLocal(UnpackResult inbound, string expectedPeerDid, string expectedLocalDid, out ReturnedMessageRefusal refusal)`.
  - Refusal enum: `{ None, NotAuthcrypt, PeerMismatch, LocalMismatch, PeerAndLocalMismatch }`.
  - Logic: `ReturnRouteEligibility.TryGetPeer` (authcrypt sender key id, signer bound to the same subject, decrypting kid is a DID, not Routing 2.0); then `var peerOk = Ordinal(peer, expectedPeer); var localOk = Ordinal(local, expectedLocal); ok = peerOk & localOk`. Both compares always run, no early exit. Both sides are DID subjects (L-034).
- [x] A9. NEW internal `InboundReplayGate` — the FR-RR-08 rule extracted verbatim from `InboundAdmission.Evaluate` lines 90–130:
  - `static bool IsInScope(UnpackResult u, ReturnRouteHeader h)` = authcrypted (`Authenticated && SenderKid` non-empty) and not `IsReplayExempt(type)` and (`h.Mode != None` or `IsMediatorServiceType(type)`).
  - `static ReplayGateResult Run(UnpackResult u, ReturnRouteHeader h, IInboundReplayGuard guard, DidCommOptions options)` with `ReplayGateOutcome { OutOfScope, Admitted, NoSenderSubject, GuardFault, Refused }` and `ReplayGateResult(ReplayGateOutcome Outcome, ReplayAdmission? Replay, string? GuardFaultType)`.
  - Behaviour: `DidSubjectOf(SenderKid)` null or empty id → `NoSenderSubject`; otherwise `guard.TryAdmit(subject, id, created_time, options.Now())` (the clock is read only when in scope, as today); a throwing guard → `GuardFault`; anything not `Admitted` → `Refused`.
- [x] A10. `MediatorEndpointExpander`: add `internal static int ForwardLayerCount(DidCommServiceInfo primary) => primary.RoutingKeys.Count + (LooksLikeDid(primary.Uri) ? 1 : 0);`. `ExpandAsync` uses it for `layerCount`. That makes it the one selection rule the refusal shares (L-041). Behaviour is identical; `MediatorEndpointExpanderTests` stay green.
- [x] A11. `IDidCommTransport.cs` (doc only): "delivery-only (FR-TRN-03)" becomes "delivery-only unless the return-route extension is in effect (§8a): HTTP may surface the response body in `TransportResult.ResponsePayload` when the request sets `MaxResponseBytes`; a WebSocket transport hands pushed messages to its `IInboundFrameConsumer`".

## B. Facade (`DidCommClient`)

- [x] B1. Restructure the pack path so the service entry is read once, before packing (L-039, JC-14).
  - The public `PackEncryptedAsync` keeps its signature. Its body moves into `private async Task<(PackEncryptedResult Result, int ForwardLayers)> PackEncryptedInstrumentedAsync(Message, PackEncryptedOptions, bool refuseForwardLayers, CancellationToken)`, which keeps the span, the failure log and the null checks. The public method returns `.Result`.
  - `PackEncryptedCoreAsync` gains `bool refuseForwardLayers` and returns the tuple. Order:
    1. Unchanged: entries, Forward shape checks, `RejectUnsupportedMethod`, `encJoseAlg`, the recipient-key loop.
    2. **New position, previously after the inner pack:** `if (options.Forward) { serviceEntries = await _serviceResolver!.ResolveAsync(entries[0].Did, ct); if (refuseForwardLayers && serviceEntries.Count > 0 && MediatorEndpointExpander.ForwardLayerCount(serviceEntries[0]) > 0) throw new InvalidOperationException(RoutedReturnRouteRefusal); }`. The message text names no DID: "A return_route of 'all' or 'thread' is not valid for a recipient reached through routing keys or a mediator: the return_route extension applies only between directly connected agents (FR-RR-02, FR-RR-07). Send without it, or to the recipient's direct endpoint (ServiceEndpointOverride)."
    3. Unchanged: sender keys, curve, recipient selection, sender key, signer, inner pack.
    4. `if (!options.Forward) return (…, 0)`. Otherwise `MediatorEndpointExpander.ExpandAsync(serviceEntries!, …)`, the SAME list with no second read, then wrap and `return (result, route.Layers.Count)`.
  - **Why always first, not only when the refusal applies.** It is one code path, so one resolution and one order to reason about. The existing pins hold: `Nine_forward_layers_are_refused_without_resolving_any_routing_key_or_mediator` (`services.Resolved == [Bob]`, only Bob's keys resolved), `SendAsync_refuses_a_resolved_queue_endpoint_after_packing_and_before_any_router_call` (`Resolved == 1`), and `Forward_accepts_entries_that_all_name_one_DID` (`Resolved == [Bob]`). The only observable change: when the service resolver and the sender-key/secret lookup both fail, the resolver's exception now surfaces first. I grepped and no test pins the opposite. An empty service list still fails inside `ExpandAsync`, after the pack, as today. "Only when the refusal applies" would put the same read in two places: two orders to test and document (rejected).
- [x] B2. `SendCoreAsync` pipeline:
  1. Unchanged: recipients non-empty; router present.
  2. **Shape checks before any I/O (L-008):** an undefined `ReturnRoute` value → `ArgumentOutOfRangeException`; `ReturnRouteHeaderWriter.Validate(ReturnRoute, ReturnRouteThread)` → `ArgumentException` without echoing the value (also when `ReturnRouteThread` is set without `Thread`).
  3. Unchanged: queue-URI override check (FR-RR-06).
  4. **Prepare the outgoing message (JC-15).** If `options.ReturnRoute is not null || (message.CreatedTime is null && OutgoingCreatedTime.IsInScope(message))`:
     - snapshot it: `JsonSerializer.Serialize`/`Deserialize<Message>` with `DidCommJson.Default`, the #91 `SessionOutbound` pattern; a failure → `ArgumentException` with the inner exception;
     - apply `ReturnRouteHeaderWriter.Write(snapshot, mode, thread)` when the mode is non-null;
     - call `OutgoingCreatedTime.StampIfInScope(snapshot, _options.Now())`, re-evaluated on the overridden header.
     Otherwise send the caller's instance unchanged, so ordinary sends pay nothing. The caller's `Message` is never mutated.
  5. **Effective header:** `present = outgoing.AdditionalHeaders?.ContainsKey("return_route") == true`; `effective = present ? ReturnRouteHeader.Read(outgoing).Mode : null`. A malformed header counts as `None`, matching the receiver's FR-RR-05 downgrade. `asksReturn = effective is All or Thread`.
  6. **JC-4:** parse the recipient entries (`ParseRecipientEntry`, a linear scan, no I/O). If `asksReturn && !AllNameOneDid(entries)` → `ArgumentException` ("ReturnRoute 'all' or 'thread' requires Recipients naming exactly one DID"), before any resolution.
  7. `(packed, layers) = await PackEncryptedInstrumentedAsync(outgoing, packOptions, refuseForwardLayers: asksReturn, ct)`. `packOptions` is unchanged (Forward = override is null). With an override there is no route, so no refusal.
  8. Unchanged: resolved queue-URI check, endpoint resolution, `OutboundEndpointGuard`.
  9. Build the request: `new TransportRequest(endpoint, payload, ForwardConstants.PayloadMediaType) { ReturnRoute = effective, LocalDid = direct && options.From is not null ? DidSubjectOf(options.From) : null, PeerDid = direct && entries name one DID ? DidSubjectOf(entries[0].Did) : null, MaxResponseBytes = asksReturn ? _options.MaxReceiveBytes : null }`, where `direct = layers == 0` (JC-18).
  10. `transportResult = await _transportRouter.SendAsync(request, ct)`.
  11. `returned = asksReturn ? await EvaluateReturnedAsync(...) : null` (JC-3: a payload on any other send is ignored and never unpacked).
  12. `return new SendResult(packed, transportResult, endpoint) { Returned = returned }`.
- [x] B3. `EvaluateReturnedAsync`:
  1. `transportResult.ResponseDiscarded` → throw `new TransportException("The request was accepted (HTTP {status}), but the returned message exceeded DidCommOptions.MaxReceiveBytes or could not be read in time and was discarded (FR-RR-07).", httpStatusCode: transportResult.HttpStatusCode, scheme: endpoint.Scheme)` (JC-2).
  2. No payload or an empty one → `null`.
  3. `UnpackAsync(UTF8.GetString(bytes))`. Catch `when (!(ex is OperationCanceledException && ct.IsCancellationRequested))` and throw `new TransportException("The request was accepted, but the returned message could not be unpacked (FR-RR-07, FR-API-07).", ex, status, scheme)`. The outer text names no DID; the typed inner exception is kept for parity.
  4. `From` is null (anoncrypt, JC-5), or the identity rule fails → `LogReturnedDiscarded(request id, refusal name)` at Warning, EventId 6, then `null`.
  5. Otherwise return the unpack result. No dispatch and no replay admission (the caller asked for it, FR-RR-07).
- [x] B4. `SendPackedAsync(ReadOnlyMemory<byte> packed, Uri endpoint, string mediaType, CancellationToken ct = default)` (JC-20):
  - `ArgumentNullException` for `endpoint` and `mediaType`.
  - `ArgumentException` for an empty payload, a relative URI, or a media type other than one of the three `MediaTypes` constants (`MediaTypes.Matches`). This stops a garbage `Content-Type` from escaping the HTTP transport as a raw `FormatException`.
  - No router → `InvalidOperationException`; queue URI → `TransportException(QueueTransport.RefusalMessage, …)`.
  - Always `new OutboundEndpointGuard(_options.OutboundEndpointPolicy).Validate(endpoint)`.
  - Send a plain `TransportRequest` with no return-route fields, so no affinity and no body read. Return the `TransportResult`.
  - Same span (`SendActivity`, scheme tag) and the same `LogSendFailed` as `SendAsync`.
- [x] B5. XML docs (L-003, L-052: state guarantees from the caller's viewpoint):
  - `SendAsync` `<exception>` entries: `ArgumentException` (thread shape, more than one DID with all/thread), `InvalidOperationException` (routed recipient with all/thread), `TransportException` (an accepted request whose returned message was over the cap or unreadable; status 200).
  - Remarks covering FR-RR-07 ordering, FR-RR-09 stamping and that the caller's `Message` is never modified.
  - `PackEncryptedAsync` remarks: with `Forward`, the service entry is read once, after the recipient's keys and before packing.
- [x] B6. Add `internal DidCommOptions Options => _options;` to `DidCommClient`. `SessionOutbound` uses it for the stamping clock (D2).

## C. HTTP transport (`HttpDidCommTransport`)

- [x] C1. Inside the hop loop, on 2xx (line 98):
  - If `status == 200 && request.MaxResponseBytes is int cap && TryDidCommMediaType(response.Content.Headers.ContentType?.MediaType, out var canonical)` → `ReadCappedAsync(response.Content, cap, ct)`.
  - Otherwise return `new TransportResult(true, status)`, exactly as today.
  - 202, a non-DIDComm type, or no cap → the body is never read (JC-3).
- [x] C2. `ReadCappedAsync`:
  - Uses a linked token source with `ct` and `RequestTimeout`, clamped to a legal wait like the WebSocket transport's `AsWaitBudget`. The Polly timeout covers only the headers (`ResponseHeadersRead`), so without this bound a slow body would hang the send.
  - `ContentLength > cap` → discarded (`OverCap`) without reading a byte.
  - Known length ≤ cap → allocate exactly that length and read it.
  - No length (chunked) → read 16 KiB pooled chunks into a `MemoryStream` that grows only as data arrives, and stop as soon as the total exceeds `cap` (`OverCap`).
  - Zero bytes → no payload.
  - Timeout (our token, not the caller's) or `IOException`/`HttpRequestException` → discarded (`ReadTimeout` / `ReadFailed`).
  - Caller cancellation → `OperationCanceledException` propagates.
  - Success → `new TransportResult(true, 200) { ResponsePayload = exact-size byte[], ResponseMediaType = canonical }`. Discarded → `new TransportResult(true, 200) { ResponseDiscarded = true }`, plus one Warning: "HTTP 200 DIDComm response discarded: {Reason} (limit {MaxBytes} bytes, FR-RR-07)". No URL and no body in the line.
  - Peak memory is never more than about 2 × cap plus one chunk; no allocation scales with the body beyond the cap.
- [x] C3. A 307 followed by a 200 reads the final hop's body. No constructor change and no new option.

## D. AspNetCore: minimal edits (`InboundAdmission.cs`)

- [x] D1. `InboundAdmission.Evaluate`: replace lines 90–130 with `var gate = InboundReplayGate.Run(unpacked, header, replayGuard, options);` and map each outcome onto the existing results and log slots:
  - `OutOfScope` → admitted, `Replay = null`;
  - `Admitted` → admitted, `Replay = Admitted`;
  - `NoSenderSubject` → `LogRefusal(NoSenderSubjectSlot, "NoSenderSubject")`, not admitted;
  - `GuardFault` → `LogRefusal(GuardFaultSlot, "GuardFault:" + type)`;
  - `Refused` → `LogRefusal((int)replay, replay.ToString())`.
  Behaviour is identical: every #91 suite passes unchanged (ReplayProtectionEndpointTests, ReturnRoute*Tests).
- [x] D2. `SessionOutbound.TryPackAsync`: first line `OutgoingCreatedTime.StampIfInScope(snapshot, client.Options.Now());`, on the library's own snapshot (JC-17). Server replies and pushes of mediator-service types (and any `all`/`thread`-headed ones) then carry `created_time`, so the client's FR-RR-08 admission (F2) admits them. Ordinary replies such as `ping-response` are unchanged. Re-run the #91 suites under L-020.

## E. WebSocket transport — session sockets S(E, L, P) and receive loops

- [x] E1. `WebSocketTransportOptions.ReceiveInbound` (`bool`, default `false`). Docs: hands messages pushed on return-route session sockets to the registered `IInboundFrameConsumer` (`DidCommBuilder.UseInboundDispatch()`). Off: session sockets are still read so closes and evictions are noticed, but frames are discarded. Under `UseStomp` no session sockets exist.
- [x] E2. Construction. The new 4-parameter constructor holds the body; the old one delegates with `null`.
  - `ReceiveInbound && inboundConsumer is null` → `InvalidOperationException` ("ReceiveInbound requires an IInboundFrameConsumer: call DidCommBuilder.UseInboundDispatch() or pass one").
  - A consumer present while `ReceiveInbound` is off → one Information line.
  - `_inboundCap = coreOptions?.Value.MaxReceiveBytes ?? new DidCommOptions().MaxReceiveBytes` (JC-16).
  - New fields: `ConcurrentDictionary<string, SessionSocket> _sessions` (active session sockets by session key), `ConcurrentDictionary<SessionSocket, byte> _liveSessionSockets` (every session socket not yet disposed, active or retiring), the one-time notice flags, `long _consumerFaults`.
- [x] E3. Session key (JC-21): `PoolKey(endpoint) + "\n" + L + "\n" + P`. A line feed can occur in neither a `PoolKey` nor a validated DID.
  - Validation in `SendAsync`, before the pipeline: when `LocalDid` or `PeerDid` is set, each must start with `did:`, be at most 2048 characters (FR-API-09) and contain no character ≤ U+0020. Otherwise `ArgumentException` without echoing the value.
  - The key reuses `_sendLocks` and `_connectLocks`, so dispose's drain, which iterates `_sendLocks.Keys ∪ _pool.Keys ∪ _sessions.Keys`, covers session sockets with its existing logic.
  - Gates are minted once per key and never removed before dispose (L-028). Document the bound: one `SemaphoreSlim` per (E, L, P) the application ever used.
- [x] E4. Routing, decided inside every pipeline attempt (JC-10):
  - `sessionKey` is non-null only when `!UseStomp` and both `LocalDid` and `PeerDid` are set.
  - `opens = ReturnRoute is All or Thread`; `downgrade = ReturnRoute == None`.
  - Target is the session when `sessionKey != null && (opens || _sessions.ContainsKey(sessionKey))`, else the pool.
  - Take the target key's send gate and re-check `_disposed`. When the session target finds S gone and `!opens`, release the gate and re-route to the pool in the same attempt (at most once). A send queued behind a retiring S therefore lands on the pool socket.
  - Guarantee, stated for the caller (L-052): a send that **starts after** an `All`/`Thread` send to (E, L, P) returned uses S while it exists. A send that starts after the `None` send returned uses the pool. Concurrent sends are unordered.
  - Under STOMP, an `All`/`Thread` send uses the pool and logs one Information line ("return-route session sockets are not available under STOMP (FR-RR-05)").
- [x] E5. `GetOrOpenSessionAsync(sessionKey, request, opens, token)`, called under the key's send gate:
  - `_sessions` holds S with `State == Open` → return it.
  - S present but not `Open` (including `CloseReceived`) → conditional `TryRemove(KeyValuePair)`, `s.Abort()`, and if `s.TryEnd()`, record `Disconnected` (raised after the gate is released, as today).
  - `!opens` → `null`, so the caller re-routes.
  - Otherwise connect a fresh socket: factory plus connect, `ConnectTimeout`, no STOMP. Create `SessionSocket(key, request.Endpoint, L, P, socket)`, publish it into `_sessions` and `_liveSessionSockets`, and record `Connected`.
  - Seal check, mirroring `GetOrConnectAsync`: if `_poolSealed` after publishing → remove, dispose, throw `ObjectDisposedException`.
  - Start the loop **after** publishing, under `ExecutionContext.SuppressFlow()`, so the caller's ambient state is not held for the socket's lifetime: `s.Loop = Task.Run(() => ReceiveLoopAsync(s))`.
  - The first session socket opened with `ReceiveInbound` off logs one Information line: "pushed messages on return-route session sockets are discarded".
- [x] E6. Retire on an explicit `None`, under S's send gate, after the `None` frame was sent:
  1. `_sessions.TryRemove(KeyValuePair(sk, s))`, so later sends go to the pool or open a fresh S;
  2. `CloseOutputAsync(NormalClosure, "return-route session ended")`, bounded by `CloseBudget` (a failure is logged at Debug with the type name only);
  3. arm the backstop `s.LoopCts.CancelAfter(CloseBudget)`.
  The loop then keeps surfacing the frames already received until the peer's close frame, then ends. The receiver pushes nothing after it processed the `None` (FR-RR-05). The `None` send returns `Accepted` whatever the close outcome. An `All` queued behind a retiring S opens a new socket while the old one drains.
- [x] E7. `ReceiveLoopAsync(s)`. It never sends, except the close frames below, and it catches everything (L-043: no fault ends the transport, no unobserved exception).
  - Read one whole message, reassembling fragments (FR-TRN-09) into a `MemoryStream`, with an 8 KiB pooled read buffer and total bytes counted.
  - **Close frame** → end.
  - **Total > `_inboundCap`** (JC-11):
    1. stop buffering;
    2. take S's gate (bounded by `DrainBudget`, cancelled by the loop token, skipped when `_disposed`);
    3. `_sessions.TryRemove(kv)`;
    4. `CloseOutputAsync(MessageTooBig, "payload too large")`, bounded by `CloseBudget`;
    5. release the gate, set `s.DiscardInbound = true`, arm the backstop;
    6. one Warning ("a pushed message exceeded {MaxBytes} bytes; the session socket was closed with 1009"), with no endpoint.
    Keep reading and discarding until the close or the backstop. Nothing beyond the cap plus one read is ever buffered.
  - **`DiscardInbound` or `!ReceiveInbound`** → drop the frame.
  - **Otherwise** `await _consumer.ConsumeAsync(new InboundFrame(s.Endpoint, s.LocalDid, s.PeerDid, ms.ToArray()), s.LoopCts.Token)`, inline (JC-8).
    - Operation cancelled by the loop token → end.
    - Any other exception, including a cancellation that is not ours → rate-limited Warning "inbound consumer threw {ExceptionType}" (type name only), then continue.
  - Socket faults (`WebSocketException`, `ObjectDisposedException`, `IOException`, cancellation by the loop token) → end, Debug with the type name.
- [x] E8. Loop end, in `finally`:
  1. `_sessions.TryRemove(KeyValuePair(sk, s))` first, so routing stops using S.
  2. If `!_disposed && s.Socket.State == CloseReceived` (the peer started the close, e.g. receiver eviction): take the gate (bounded by `DrainBudget`, cancellable) → `CloseOutputAsync(NormalClosure)` bounded by `CloseBudget` → release. A close never overlaps a send, and no gate holder ever waits for a loop, so there is no deadlock.
  3. `s.Socket.Dispose()` (idempotent); `_liveSessionSockets.TryRemove(s)`; `if (s.TryEnd()) RaiseLifecycle(Disconnected, s.Endpoint)` — once per socket, naming the endpoint; then dispose `LoopCts` (`StopLoop()` tolerates `ObjectDisposedException`).
- [x] E9. A send failure on S, in the attempt's `catch`, still under the gate: conditional `_sessions.TryRemove(kv)`, `s.StopLoop()`, `s.Socket.Dispose()`, `if (s.TryEnd())` record `Disconnected`, record `SendFailed`, rethrow. The retry re-routes: `All`/`Thread` → a fresh S; header-less or `None` → the pool.
- [x] E10. `DisposeAsync`. The invariant is extended, not replaced; `Lifecycle` semantics are kept.
  1. `_disposed = true`, then the latch (unchanged).
  2. `CloseEndpointAsync(key)` for every key concurrently (unchanged). For a session key, inside the gate it removes from `_sessions` (a key lives in exactly one of `_pool` / `_sessions`), runs the same bounded `CloseOutputAsync(NormalClosure, "transport disposed")` while the socket is `Open`, then `Dispose()` + `s.StopLoop()`. It returns `s.Endpoint` when `s.TryEnd()`, so `Disconnected` names the endpoint, raised in the existing post-drain loop.
  3. `_poolSealed = true`; sweep `_pool` (unchanged) **and** `_sessions` + `_liveSessionSockets` (retiring sockets included): `StopLoop()`, `Dispose()`, and `Disconnected` when `TryEnd()`.
  4. Await every loop task: `Task.WhenAll(...).WaitAsync(CloseBudget)`, timeout swallowed and logged at Debug. Loops never throw. A consumer that ignores cancellation is abandoned; its socket is already disposed.
  5. `DisposeIdleGates` and the invoker, unchanged.
  The doc bound becomes "at most `DrainBudget + 2 × CloseBudget`". No send starts after the flag (unchanged). No open socket survives: pooled, session, or retiring. A loop never waits on a gate once `_disposed` is set, so `DisposeIdleGates` never races a waiter.
- [x] E11. `SessionSocket` (private sealed, nested): `Key`, `Endpoint`, `LocalDid`, `PeerDid`, `Socket`, `LoopCts`, `Task Loop`, `volatile bool DiscardInbound`, `int _ended` with `bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0`, `StopLoop()`, `Abort()`.
- [x] E12. Logging (JC-12). Every new line goes through a guarded call: try/catch, so a throwing logging provider changes nothing. No endpoint, DID or exception object is logged. Pooled sockets are still never read (pre-existing behaviour, documented).

## F. DI — `UseInboundDispatch()`, the built-in consumer, factory registration

- [x] F1. `DidCommBuilder.UseInboundDispatch()`: `Services.TryAddSingleton<IInboundFrameConsumer>(sp => new InboundDispatchConsumer(sp, sp.GetService<ILogger<InboundDispatchConsumer>>()))`. A host consumer registered first wins. Docs: pair it with `UseWebSocketTransport(o => o.ReceiveInbound = true)`. No hosting dependency, no `IHostedService`. Activation is deterministic: a loop runs exactly on each session socket.
- [x] F2. `InboundDispatchConsumer` (internal sealed; constructor `(IServiceProvider, ILogger?)` only — L-017, no DI cycle). `ConsumeAsync(frame, ct)`:
  1. Resolve at call time: `DidCommClient`, `ProtocolDispatcher`, `IInboundReplayGuard`, `IOptions<DidCommOptions>`. A failure, e.g. `ObjectDisposedException` at shutdown → rate-limited log with the type name, drop.
  2. `UnpackAsync(UTF8(frame.Payload))`. Failure → rate-limited "push dropped: unpack failed ({ExceptionType})" with no id (JC-12: no wire value before identity is established).
  3. `ReturnRouteIdentity.IsFromPeerToLocal(u, frame.PeerDid, frame.LocalDid, out r)`. Failure → rate-limited "push dropped: {Refusal}" with no id. This drops anoncrypt, signed-only, `anoncrypt(sign)`, plaintext, another sender, another local DID, and Routing 2.0.
  4. `ReturnRouteHeader.TryRead`; a malformed header → rate-limited log with the id.
  5. `InboundReplayGate.Run(u, header, guard, options)`. Anything except `OutOfScope`/`Admitted` → rate-limited "push {MessageId} dropped: replay admission {Outcome}", drop.
  6. `dispatcher.DispatchAsync(u, client, options, Task.CompletedTask, session: null, ct)`. A handler exception → Warning with the type name, then return; cancellation by `ct` propagates.
  7. `ReplyProduced` → Debug "push {MessageId}: handler reply not sent (replies to pushes are sent explicitly)" (JC-9). Handlers never re-check replay (a second admission would refuse the message itself).
- [x] F3. `UseWebSocketTransport` (JC-19): replace `AddSingleton<WebSocketDidCommTransport>()` with `AddSingleton(sp => new WebSocketDidCommTransport(sp.GetRequiredService<IOptions<WebSocketTransportOptions>>(), sp.GetService<ILogger<WebSocketDidCommTransport>>(), sp.GetService<IOptions<DidCommOptions>>(), sp.GetService<IInboundFrameConsumer>()))`. The values are identical to today's type activation, and constructor selection no longer depends on what happens to be registered.
- [x] F4. Dependency walk, documented in the XML remarks: `DidCommClient` → `ITransportRouter` → transports → WebSocket transport → `IInboundFrameConsumer` → (`IServiceProvider`, logger). Nothing resolves the dispatcher, registry or facade at construction.

## G. Test matrix

Determinism rules: no fixed sleeps (L-044). Use `TaskCompletionSource` rendezvous, Lifecycle-event TCSs, `ReturnRouteTestHost.WaitUntilAsync` polling with a deadline, and the host's `Rendezvous` (which raises the thread-pool floor) wherever a synchronous party blocks. Client containers set `DidCommOptions.Clock = () => host.Clock.Now`. Logs are captured with `CapturingLoggerProvider` and scanned with `host.LeakingLogLines(...)`.

- [x] G1. `Core.Tests/Transports/ReturnRoute/ReturnRouteClientRulesTests.cs` (pure, synthetic `UnpackResult`):
  - `Identity_rule_accepts_authcrypt_from_the_addressed_peer_to_the_sending_local_DID`
  - `Identity_rule_refuses_non_authcrypt_shapes` (Theory: plaintext, signed-only, anoncrypt, anoncrypt(sign), empty `SenderKid`)
  - `Identity_rule_refuses_another_peer` / `_another_local_DID` / `_both`
  - `Identity_rule_refuses_a_forward_type`
  - `Identity_rule_compares_DID_subjects_so_a_DID_URL_of_the_same_subject_matches` (L-034)
  - `Replay_scope_table_matches_the_receive_side` (all, thread, none+app → out, 4 mediator PIURIs any minor, forward exempt, signed-only and anoncrypt out)
  - `Replay_gate_outcomes` (NoSenderSubject on empty id or non-DID kid, GuardFault, Refused per `ReplayAdmission`, Admitted, OutOfScope; the clock is not read out of scope)
  - `Created_time_scope_table` (header all/thread; malformed → none; CM 2.0/3.0, pickup 3.0/4.0 incl. header-less 4.0 `messages-received`; app type → no)
  - `StampIfInScope_never_refreshes_a_caller_value`
  - `Header_writer_matches_WithReturnRoute` (byte-exact)
- [x] G2. `Core.Tests/Transports/DidCommClientReturnRouteSendTests.cs`. Packs to Bob via `OneKeyService` / `TestKeyMaterial` with a capturing router; Bob's client unpacks; refusal cases use the recording `SpyDidResolver` / `SpySecretsResolver` (`Facade/IdentifierLengthCapFixtures.cs`) and `AllRecipientKeysTests.StubServiceResolver`:
  - `All_stamps_return_route_all_byte_exact_on_a_snapshot` (the decrypted plaintext contains `"return_route":"all"`); `Callers_message_is_not_mutated` (header and `created_time` unchanged)
  - `None_emits_explicit_none`; `Null_preserves_a_builder_header`; `NonNull_overrides_a_builder_header_and_drops_a_stale_thread`; `Thread_emits_thread_and_return_route_thread`
  - `Thread_shape_is_refused_before_any_resolution` (Theory: null, whitespace, reserved character, 2049 characters, thread with `All` → `ArgumentException`; `SpyDidResolver.Requested` empty)
  - `All_with_two_recipient_DIDs_is_refused_before_any_resolution` (JC-4)
  - `Created_time_is_stamped_from_the_injected_clock` (Theory: SendOptions All, builder All, Thread, CM 2.0, CM 3.0, pickup 3.0, header-less pickup 4.0 `messages-received`)
  - `Ordinary_application_message_gets_no_created_time`; `A_10_minute_old_caller_created_time_is_sent_unchanged`
  - `Routed_recipient_with_All_is_refused_after_one_service_read_before_any_mediator_sender_or_secret_lookup_and_before_packing` (Theory: DID-URL routing key, bare-DID routing key, DID as `uri`, builder-set `all`). Asserts: `InvalidOperationException`; `services.Resolved == [Bob]`; `SpyDidResolver.Requested` contains only Bob; `SpySecretsResolver.FindPresentCalls == 0`; router calls 0; the message contains no `did:`.
  - `Direct_recipient_with_All_is_allowed_with_the_Forward_default` (request: `ReturnRoute == All`, `MaxResponseBytes == MaxReceiveBytes`, `LocalDid` / `PeerDid` are subjects)
  - `Override_with_All_reads_no_service_entry`; `Queue_endpoint_with_All_still_throws_TransportException_before_any_router_call` (resolved and override variants)
  - `A_send_without_ReturnRoute_reads_the_service_entry_exactly_once` (L-039 regression)
  - `Header_less_direct_authcrypt_send_carries_identities_but_no_response_cap`; `Anoncrypt_All_carries_no_LocalDid` (JC-5); `Routed_header_less_send_carries_no_identities` (JC-18)
  - `ResponseDiscarded_on_an_All_send_throws_TransportException_status_200_naming_no_DID` (stub router)
  - `Payload_on_a_send_without_ReturnRoute_is_ignored_and_never_unpacked` (`Returned` null; no extra key-service or secrets calls)
- [x] G3. `Core.Tests/Transports/DidCommClientSendPackedTests.cs`:
  - `Guard_refuses_a_private_ip_literal_before_the_router`
  - `Queue_uri_is_refused`
  - `Router_receives_exact_bytes_media_type_and_no_return_route_fields`
  - `Unsupported_media_type_and_relative_uri_are_ArgumentException`
  - `Without_router_throws_InvalidOperationException`
- [x] G4. `InteropTests/Transports/HttpTransportResponseTests.cs` (stub `HttpMessageHandler` plus a lazily generating, read-counting `HttpContent`):
  - `Status_200_with_each_DIDComm_type_is_read_when_the_request_asks` (Theory, including a `; charset=utf-8` parameter; `ResponseMediaType` canonical)
  - `Status_202_reads_nothing`; `Non_DIDComm_type_reads_nothing`; `Without_MaxResponseBytes_the_body_is_never_read` (read count 0)
  - `Over_cap_Content_Length_is_discarded_without_reading_a_byte` (declared 100 MB; read count 0; `Accepted`; `ResponseDiscarded`; one Warning with no URL)
  - `Over_cap_chunked_100_MB_body_is_cut_at_cap_plus_one_chunk` (bytes pulled ≤ cap + 16 KiB)
  - `Exactly_cap_is_accepted_and_cap_plus_one_is_discarded`
  - `A_stalled_body_is_discarded_after_RequestTimeout_and_still_accepted` (RequestTimeout 200 ms; the test asserts completion within 10 s, not a sleep)
  - `Caller_cancellation_during_the_body_propagates`
  - `A_307_then_200_reads_the_final_body`; `Zero_length_200_yields_no_payload`
- [x] G5. `InteropTests/AspNetCore/ReturnRouteClientHttpTests.cs` (the #91 `ReturnRouteTestHost`; client = a `ReturnRouteClientHarness` container with `UseHttpTransport`, primary handler = `host.Server.CreateHandler()`, `AllowedSchemes` http, `ServiceEndpointOverride` = host `/didcomm`, no caller timestamps):
  - `Default_client_ping_with_All_gets_the_authenticated_ping_response_in_Returned` (type `ping-response`, `thid` = ping id, sender subject = server DID, `to` = Alice)
  - `Builder_set_all_with_SendOptions_unset_is_stamped_and_returns_the_reply`
  - `Caller_created_time_10_minutes_old_is_sent_unchanged_and_gets_202_no_reply` (recorder 0, `DropLines` shows `TooOld`)
  - `Pickup_3_0_and_header_less_pickup_4_0_messages_received_are_stamped_and_admitted` (server `PickupRecordingHandler` + `Pickup40RecordingHandler`; recorded `created_time` == host clock)
  - `Application_message_without_return_route_arrives_without_created_time`
  - `Anoncrypt_All_gets_202_and_no_Returned`; `Thread_gets_202_and_no_Returned`
  - `A_mediator_type_reply_pushed_by_the_server_carries_created_time` (JC-17, the receiver side)
- [x] G6. `InteropTests/AspNetCore/ReturnRouteClientWebSocketTests.cs` (`AllowSameSocketReplies = true`; client container: `UseWebSocketTransport(ReceiveInbound, ws schemes, factory = host.Server.CreateWebSocketClient().ConnectAsync(...)` with a call counter`)` + `UseInboundDispatch()` + `AddBuiltInProtocols()` + a client recording handler or observer). The client holds Alice's and Carol's secrets.
  - `Default_client_ping_with_All_over_WebSocket_dispatches_the_ping_response_to_a_registered_handler`
  - `A_server_push_reaches_a_registered_client_handler_with_Session_null`
  - `Two_local_DIDs_through_one_transport_use_two_sockets_and_each_gets_its_own_reply` (factory count 2; each reply's `to` = its own DID, `thid` = its request)
  - `Without_ReturnRoute_two_local_DIDs_share_one_pooled_socket` (factory count 1)
  - `All_then_header_less_then_None_keeps_the_session_socket_then_retires_it` (the header-less reply arrives over S; the server's push result after `None` is false; server `EndedWebSocketConnections` +1; a later `All` → factory +1 and its reply arrives)
  - `Header_less_pickup_4_0_messages_received_after_live_pushes_travels_over_S_and_pushes_continue` (server `Pickup40RecordingHandler`: the `All` request pushes 2; `messages-received` records `context.Session != null` and pushes 1 more; the client dispatches 3)
  - `Builder_set_all_uses_the_session_socket` (FR-RR-09 acceptance)
  - `Receiver_eviction_ends_the_loop_without_unobserved_exceptions_and_the_next_header_less_send_uses_the_pool`. `MaxSessionsPerPeer = 1`; a raw socket authcrypting Alice `all` evicts the client's S; wait on the Lifecycle `Disconnected` TCS; next send → factory +1 and the server records `Session == null`; `UnobservedTaskException` hook with three `GC.Collect` passes (the #91 pattern).
  - `Under_STOMP_no_session_socket_is_opened` (one Information line)
  - `ReceiveInbound_off_drains_session_sockets_and_logs_once` (no dispatch; retiring completes without waiting for the backstop)
- [x] G7. `InteropTests/AspNetCore/ReturnRouteClientAdversarialTests.cs`:
  - HTTP stub-handler bodies: `Returned_is_null_and_logged_for_bodies_failing_the_identity_rule` (Theory: anoncrypt from the server, signed-only, plaintext, authcrypt by Mallory, server → Carol (a local DID the client holds), forward type; one Warning with the request id and the refusal name)
  - `Garbage_under_a_DIDComm_type_is_TransportException_status_200` (Theory: `{`, invalid UTF-8, JSON without `ciphertext`, a JWE with a flipped tag; typed inner exception; no `did:` in the message)
  - `A_hostile_100_MB_response_is_cut_at_the_cap_and_SendAsync_throws_TransportException` (Content-Length and chunked; the transport alone reports `Accepted`)
  - Consumer matrix: resolve `IInboundFrameConsumer` from a client container and call `ConsumeAsync` directly with frames packed by real peers.
    - `Pushes_failing_the_identity_rule_are_never_dispatched` (Theory: Mallory → Alice; server → Carol under `L = Alice`; anoncrypt; signed-only; plaintext; forward)
    - `A_replayed_push_is_dispatched_once`
    - `An_in_scope_push_without_created_time_is_dropped`; `An_out_of_scope_app_push_without_created_time_is_dispatched`
    - `A_handler_reply_to_a_push_is_never_sent` (zero router or socket sends; Debug line)
    - `A_throwing_handler_does_not_break_the_next_frame`
  - Flood: `A_flood_on_one_session_does_not_drop_or_delay_another` (ScriptedSessionWebSocket; consumer parked on S1)
  - `Log_scan_finds_no_DID_kid_thread_or_body` (over every case above)
- [x] G8. `InteropTests/Transports/WebSocketSessionSocketTests.cs` with a new `ScriptedSessionWebSocket` fake: scripted inbound channel, `PushFrame`, `PushClose`, a gated send, an in-flight send counter, recorded sends and close-output status.
  - `Parallel_sends_on_one_session_socket_never_overlap` (8 header-less sends behind a gated first send; max in-flight 1; one session socket)
  - `A_send_queued_behind_a_retiring_session_re_routes_to_the_pool`
  - `An_All_queued_behind_a_retiring_session_opens_a_fresh_socket`
  - `Two_local_DIDs_racing_All_open_two_sockets` (`Rendezvous(2)` in the factory; `MaxConcurrent == 2`)
  - `A_session_send_failure_removes_it_and_the_retry_re_routes`
  - `Session_sockets_are_keyed_by_endpoint_local_and_peer`
  - `An_oversized_push_closes_with_1009_and_buffers_at_most_cap_plus_one_read`
  - `A_consumer_that_throws_does_not_end_the_loop` (L-043; a foreign-token cancellation included; one rate-limited Warning with the type name only)
  - `DisposeAsync_with_live_and_retiring_session_sockets_is_bounded_and_leaves_no_open_socket` (peer never answers the close; consumer ignores cancellation; elapsed ≤ `DrainBudget + 2 × CloseBudget` + slack; every socket disposed; `Disconnected` once per socket, naming its endpoint)
  - `ReceiveInbound_without_a_consumer_fails_fast`; `Invalid_LocalDid_or_PeerDid_is_refused_without_echo`
  - Every existing `WebSocketTransportBehaviorTests` / `StompWebSocketTransportTests` test unchanged and green.
- [x] G9. `InteropTests/Protocols/InboundDispatchDiTests.cs`:
  - `UseInboundDispatch_with_WebSocket_and_built_in_protocols_resolves_without_a_cycle_or_hang` (bounded 10 s; resolves `DidCommClient`, `ITransportRouter`, `WebSocketDidCommTransport`, `IInboundFrameConsumer`, `ProtocolDispatcher`)
  - `ReceiveInbound_without_UseInboundDispatch_fails_resolution_with_an_actionable_message`
  - `A_host_consumer_registered_first_wins`
  - `UseWebSocketTransport_resolves_without_logging_registered`
  - `Disposing_the_container_ends_session_loops_without_unobserved_exceptions` (`await using`)
- [x] G10. Regression: every #91 suite, `DidCommClientSendAsyncTests`, `AllRecipientKeysRoutingTests`, `AllRecipientKeysTests`, `MediatorEndpointExpanderTests`, `MessageBuilderReturnRouteTests`, `RedactionAuditTests`, the Samples smoke tests, and the DX gates. Any test that breaks gets an L-020 sentence before it is touched.
- [ ] G11. **(Deferred to #93/#94/#96 — the handlers do not exist yet.)** Deferred and recorded (needs the #93/#94/#96 handlers): the FR-RR-09 acceptance chain "discover-features → mediate-request → recipient-update → delivery-request → messages-received → status 0" over HTTP and WebSocket. The subset that exists is covered in G5/G6 (ping, a pickup-typed recording handler, a header-less 4.0 ack).

## H. Docs

- [x] H1. `CHANGELOG.md [Unreleased]`:
  - **"Added — return-route client side (FR-RR-07, FR-RR-09, #92, epic #88)"**: every public member above, with the contracts from A–F.
  - **"Changed — return-route client side (#92)"**:
    - with `Forward`, the service entry is read before packing, still exactly once;
    - a builder-set `all`/`thread` to a routed recipient is now refused at send;
    - `UseWebSocketTransport` uses a factory registration;
    - session replies and pushes are stamped (JC-17);
    - the WebSocket transport reads return-route session sockets.
  - **Upgrader note**, following the L-045 template (`ProtocolContext` 1.6 note and `VerifiedKeyBinding` 1.4.0): `SendOptions`, `SendResult`, `TransportRequest` and `TransportResult` gain members that take part in the synthesized `Equals` / `GetHashCode` / `ToString`, and `ReadOnlyMemory<byte>` members compare by memory identity, not content. Binary and API compatible: no constructor, `Deconstruct` or member changed (package validation against 1.5.0 is clean).
  - **"Security — return-route client threat model (#92)"**: the threat-model bullets, and the known limitations:
    - a send racing the peer's close may be accepted and lost (WebSocket has no acknowledgement);
    - a consumer or handler that waits for another inbound on the same session deadlocks it until its own timeout;
    - unpacking `Returned` resolves the sender key's DID, like any receive path;
    - one gate per (E, L, P) is kept until dispose.
- [x] H2. `docs/codebase-architecture.md`:
  - New section "Return-route client side (Phase 7, #92)": wiring; `SendAsync` steps; HTTP read/cap; the `Returned` rule; session sockets (key, routing, retire, loop, dispose); the inbound consumer and admission; threat model; limitations.
  - Rewrite "Outbound flow" steps 1–5: snapshot/stamp, effective header, single service read and refusal, affinity, `Returned`.
  - Packages table: Core Transports public `IInboundFrameConsumer` / `InboundFrame`; internal `ReturnRouteHeaderWriter`, `OutgoingCreatedTime`, `ReturnRouteIdentity`, `InboundReplayGate`; DI `UseInboundDispatch` + internal `InboundDispatchConsumer`; WebSocket session sockets / `ReceiveInbound`.
  - "Return-route receive side": the intro sentence and step 6 point at `InboundReplayGate` and the #92 consumer; mention `SessionOutbound` stamping.
  - "Phase 7: not built yet": drop #92.
- [x] H3. Cookbook Section R: remove `.WithCreatedTime(...)` and the "stamped by hand … does not stamp automatically yet" sentence. Replace it with one sentence: `SendAsync` stamps `created_time` (FR-RR-09) because the header puts the ping in the receiver's replay scope.
- [x] H4. DX allowlists. Run `PublicApiCoverageTests` and `PublicApiExecutionTests`, then add **exactly** the entries they report: both gates fail on stale entries, and some new members execute indirectly (e.g. the 4-parameter constructor via the 3-parameter one). Reason text: "Return-route client side (#92): demonstrated by cookbook sections CC–FF / sample 11 (#97); delete this entry when #97 lands." Candidates: the public list in the header block.
- [x] H5. **(Narrowed by L2: only the FR-RR-07 over-cap sentence was edited; the rest are listed as proposed PRD clarifications.)** PRD amendments. **Flagged replanning items, orchestrator to confirm.** One contract across XML docs, PRD and CHANGELOG (L-045/L-052):
  - **FR-RR-07:**
    - over-cap: replace "(an over-cap body is discarded and logged; the send still counts as accepted)" with "(an over-cap body is never buffered: the transport reports the send accepted with no payload and `ResponseDiscarded`, and logs it; `SendAsync`, having asked for the return route, then throws `TransportException` with status 200, the same outcome as an unpack failure)" (JC-2);
    - add "A send with `ReturnRoute` `All`/`Thread` whose `Recipients` name more than one DID is refused with `ArgumentException` before any resolution" (JC-4);
    - add "An anoncrypt send may carry `all`/`thread` (stamped per FR-RR-09) but opens no session socket and never yields `Returned`" (JC-5);
    - the `Returned` rule becomes "an authcrypt sender key id (FR-RR-03) whose DID subject equals the addressed DID, decrypted by a key of the sending DID" (JC-6);
    - the WebSocket loop sentence becomes "every session socket is read by its own loop; with `ReceiveInbound` each pushed message, capped at `MaxReceiveBytes` (an oversized one closes the socket with 1009), is handed to the inbound consumer one at a time; `UseInboundDispatch()` admits it under FR-RR-08 and dispatches it with no session; a handler's reply is never sent automatically" (JC-7/8/9).
  - **FR-RR-09:** append "Messages a receiver sends over a return-route session (`IReturnRouteSession.TrySendAsync`) are stamped by the same rule, so a mediator's replies and pushes pass the client's FR-RR-08 admission" (JC-17).
- [x] H6. L-045 grep across `src/` XML docs, `docs/`, `CHANGELOG.md`, `samples/` and `README.md` for `delivery-only`, `one-way`, `does not stamp`, `no body`, `discards the response`, `return_route`, `created_time`, `is #92`. Restate every hit.

## I. Gates, workflow, implementation order

- [x] I1. **Chunk A — Core, facade, HTTP, server stamping:** A1–A11, B1–B6, C1–C3, D1–D2, tests G1–G5, the G7 HTTP part, G10. Gates green, then checkpoint commit `feat(return-route): client model, FR-RR-09 stamping, refusal before packing, HTTP Returned (#92)` (explicit paths only).
- [x] I2. **Chunk B — WebSocket and DI:** E1–E12, F1–F4, tests G6, G7 (consumer and flood), G8, G9. Gates green. Loop the new socket and session tests 10× with `DOTNET_PROCESSOR_COUNT=2`: `for i in $(seq 10); do DOTNET_PROCESSOR_COUNT=2 dotnet test tests/DidComm.InteropTests -c Release --no-build --filter "FullyQualifiedName~ReturnRouteClient|FullyQualifiedName~WebSocketSessionSocket|FullyQualifiedName~InboundDispatchDi" || break; done`. Checkpoint commit.
- [x] I3. **Chunk C — docs:** H1–H6. Run the full gates (`build /warnaserror`, `test --no-build`, `pack --no-build`, validation against 1.5.0), then checkpoint commit.
- [x] I4. QA pass (separate agent): every G row, plus the negative paths.
- [x] I5. Adversarial pass (separate agent; compare with `main`, L-051): the G7 list, plus free-form: get a frame from a peer other than P dispatched for L; make the client send a reply on S; exceed any bound; overlap a close and a send; keep a socket open past dispose; make a loop or consumer fault go unobserved; make `Returned` accept another DID; leak a DID into a log line.
- [x] I6. Fill the Review section; update `tasks/lessons.md` if a correction happened. Every commit runs `b=$(git branch --show-current); [ "$b" = feat/return-route-client-92 ] && git commit …` (L-019); no history rewrite (L-053).

## J. Replanning notes for #88 (post on the epic after merge)

- **#93 (CM 3.0):** replies and pushes are stamped by `SessionOutbound` (JC-17). `MediationClient` over WebSocket needs `UseInboundDispatch()` + `ReceiveInbound = true`; grants arrive by dispatch (an `IInboundCorrelator` is recommended). The Discover Features bootstrap uses `SendOptions.ReturnRoute = All` (HTTP: `Returned`). Handlers never re-check replay.
- **#94 (pickup 3.0):** the client admits every pickup-typed push only with a fresh `created_time` (stamped server-side by JC-17). A push the client drops is redelivered after its lease; client pushes are dispatched with `Session == null`.
- **#95 (`DidComm.Mediator`):** relays and direct pushes use `DidCommClient.SendPackedAsync`: the guard is always applied, the media type must be a DIDComm type, and no response body is read. `ResponseDiscarded` semantics as in A4.
- **#96 (recipient client):**
  - over HTTP: `SendResult.Returned`; `TransportException` with `HttpStatusCode == 200` means accepted but unreadable or over the cap (never silent loss);
  - over WebSocket: inbound dispatch with `ReceiveInbound`; header-less acks ride S automatically;
  - no manual timestamps;
  - a handler must not wait for another inbound on the same session;
  - one `DidCommClient` in DI unpacks pushes for every local DID it holds.
- **#97:** delete the "#92 … until #97" allowlist entries; cookbook CC–FF demonstrate `SendOptions.ReturnRoute`, `Returned`, `UseInboundDispatch`, `ReceiveInbound`, `SendPackedAsync`; sample 04 switches to `SendPackedAsync`.
- **#98:** add the G7/G8 adversarial items, the stalled-body timeout, a flood on S1 with traffic on S2, and dispose with a consumer that ignores cancellation. Keep DD-13.
- **#99 / #100:** CM 2.0 and pickup 4.0 are stamped by the PIURI clause on both sides; the header-less 4.0 ack over S is tested here with a test handler.
- **Follow-ups outside #88 (pre-existing, not this diff):**
  - `WebSocketDidCommTransport` implements only `IAsyncDisposable`, so synchronous container disposal throws (L-035);
  - `HttpDidCommTransport`'s Debug line logs the endpoint URI;
  - the `ObserverDelivery` token recheck;
  - the fixed 200 ms delay in `WebSocketTransportBehaviorTests`.

## K. Judgment calls

- **JC-1 (kept).** Every new record member is init-only and non-positional, so constructors and `Deconstruct` are unchanged. The value-equality note goes in the CHANGELOG.
- **JC-2 (kept, extended).** An over-cap body, a stalled body or a failed body read → the transport returns `Accepted = true`, `ResponseDiscarded = true` and logs one Warning. `SendAsync` (which asked for the return route) throws `TransportException(200)`, the same as an unpack failure. PRD amendment H5.
- **JC-3 (kept, strengthened).** The transport reads a body only when the request carries `MaxResponseBytes`, which the facade sets only for an effective `all`/`thread`. An unsolicited body costs no allocation, no unpack and no resolution.
- **JC-4 (kept).** `all`/`thread` with more than one recipient DID → `ArgumentException` before any I/O.
- **JC-5 (kept).** Anoncrypt `all`/`thread` is stamped and sent, but gets no session socket (no L) and no `Returned`.
- **JC-6 (kept).** One internal `ReturnRouteIdentity` helper serves the facade and the consumer: `TryGetPeer`, then both compares always run, combined with `&`.
- **JC-7 (refined).** Every session socket gets a receive loop even with `ReceiveInbound` off; the flag gates only surfacing. Reasons: notice eviction and close, complete the close handshake (retire would otherwise wait for its backstop), and avoid stalling the receiver's writer on a socket nobody reads. Pooled sockets are still never read. Under STOMP there are no sessions.
- **JC-8 (overturned in part).** The consumer seam is kept (`IInboundFrameConsumer` + `InboundFrame`, `UseInboundDispatch()`, no hosting, lazy resolution through `IServiceProvider`). The **shared bounded drop-newest queue is not built**: each session's loop hands frames to the consumer itself, one at a time. A single shared lossy queue lets one peer's flood drop another peer's grant (L-037, cross-peer starvation). Per-socket handling gives per-peer backpressure, no client-side loss, no extra reader task, and per-session FIFO order. `IInboundMessageSource` was rejected because it needs an external single reader that something must start (a hosted service, or start-on-resolution).
- **JC-9 (kept).** A handler reply to a push is never auto-sent (Debug line with the id).
- **JC-10 (kept, specified in E4–E10).** Routing is re-decided per attempt; retire happens under the gate; a send queued behind a retired S re-routes; dispose covers active and retiring session sockets and their loops; `Disconnected` names the endpoint, once per socket.
- **JC-11 (kept).** An over-cap push → 1009 close-output under the gate, then retire and discard mode. Nothing beyond the cap plus one read is buffered.
- **JC-12 (kept, refined).** No wire value is logged before identity is established: unpack and identity drops log no message id.
- **JC-13 (kept).** New wire inputs and their caps:
  - the HTTP body (`MaxReceiveBytes`, then FR-API-09 inside unpack);
  - the `Content-Length` / `Content-Type` headers (compared, never echoed);
  - the WebSocket frame size (`_inboundCap`);
  - `SenderKid` / `RecipientKid` / `SignerKid` of returned and pushed messages (unpack caps them; `DidSubjectOf` refuses anything over 4096 characters);
  - `return_route` / `return_route_thread` (`TryRead`: compared in place, at most 2048 characters);
  - `id` (the replay guard's `MaxMessageIdLength` of 512);
  - `created_time` (the guard's arithmetic).
- **JC-14 (new).** The service entry is read once, always first on the `Forward` path, right after the recipient's keys. The refusal uses `MediatorEndpointExpander.ForwardLayerCount` (L-041); `ExpandAsync` consumes the same list (L-039).
- **JC-15 (new).** The header override and stamping happen on a JSON snapshot only when needed. The caller's instance is never mutated, and the effective header is read from the outgoing message.
- **JC-16 (new).** The HTTP cap travels on `TransportRequest.MaxResponseBytes`, so no HTTP constructor change and no MS.DI ambiguity. The WebSocket transport takes `MaxReceiveBytes` from the `coreOptions` its constructor already accepts.
- **JC-17 (new; affects #93/#94).** `SessionOutbound.TryPackAsync` stamps `created_time` with the same Core rule. Without it, the client's FR-RR-08 admission drops every mediator-service reply or push. PRD FR-RR-09 amendment H5.
- **JC-18 (new).** `LocalDid` / `PeerDid` are set only for direct routes (0 forward layers) and authcrypt sends to one DID. A routed or anoncrypt send never touches a session socket.
- **JC-19 (new).** `UseWebSocketTransport` uses an explicit factory registration. With type activation, the new 4-parameter constructor would be skipped whenever `ILogger<T>` is not registered, so a consumer would silently not be wired.
- **JC-20 (new).** `SendPackedAsync` always applies the guard, refuses the queue URI and non-DIDComm media types, returns `TransportResult`, and never asks for a body.
- **JC-21 (new).** Session sockets live in the existing per-key gate dictionaries under `PoolKey + "\n" + L + "\n" + P`, so the dispose drain covers them unchanged. Gates are never removed before dispose (L-028).
- **JC-22 (new).** No `InternalsVisibleTo` is added for the transports. Neither transport evaluates identity or admission: the facade (Core) and the DI consumer (which already has `InternalsVisibleTo`) do.


## L. Plan review and amendments by the per-issue orchestrator (2026-11-11)

Reviewed against issue #92, the three #88 "#92" sections, PRD FR-RR-01..09 / FR-TRN-03/09/10 / FR-API-05/07/09, DD-11..14, §15.2 and D1–D7. The plan covers every scope item and acceptance criterion; the deviations below are accepted or amended.

- **L1 (accepts JC-8's deviation from the issue body).** The issue body sketched one bounded drop-newest channel (`IInboundMessageSource`). The plan hands each session socket's frames to the consumer inline, one at a time. Accepted: a single shared lossy queue lets one peer's flood drop another peer's grant (L-037), and an `IInboundMessageSource` needs an external reader something must start (hosting, or start-on-resolution). Per-socket inline consumption gives per-peer backpressure with no client-side loss; the receiver's `MaxPendingPushes` is the bound on the other side. The issue's "drop-newest" acceptance becomes "a flood on S1 neither drops nor delays S2" (G7). The deadlock caveat (a handler must not wait for another inbound on the same session) is documented on `IInboundFrameConsumer` and in the CHANGELOG.
- **L2 (amends H5: PRD edits narrowed).** The orchestrator brief allows PRD edits only where an FR cannot be met as written, flagged as replanning items. Only ONE PRD edit is made: FR-RR-07's parenthetical "(an over-cap body is discarded and logged; the send still counts as accepted)" conflicts with the orchestrator's test matrix ("over-cap response → `TransportException`") and the #88 #96 note ("never silent loss"). It is reworded to the JC-2 behaviour (transport: accepted, no payload, `ResponseDiscarded`, logged; `SendAsync`, having asked for the return route, throws `TransportException` with status 200, as for an unpack failure; a stalled or failed body read is treated the same). Flagged as a replanning item in the report and the PR body. The other H5 bullets (JC-4 multi-DID refusal, JC-5 anoncrypt, JC-6 local-DID check, JC-7/8/9 WebSocket loop wording) are refinements inside the PRD's latitude: they go to CHANGELOG + `docs/codebase-architecture.md` + XML docs, and are listed as proposed PRD clarifications (replanning items) — the PRD is not edited for them.
- **L3 (JC-17 needs no PRD amendment).** FR-RR-09's first sentence already reads "Every outgoing message that falls under the FR-RR-08 scope carries `created_time`". A receiver's session reply or push of a mediator-service type (or carrying `all`/`thread`) is such an outgoing message, and the #92 client applies FR-RR-08 to it. `SessionOutbound.TryPackAsync` stamping implements that sentence for the receiver; no PRD edit. It stays the one-line AspNetCore change in D2 and is recorded for #93/#94.
- **L4 (scope guard).** Sample 04 is not touched (#97 rewrites it onto `SendPackedAsync`). No CM / pickup handlers are added beyond test handlers. The PRD acceptance chain in G11 is deferred to #93/#94/#96 and reported as such.
- **L5 (implementation constraints for every implementer).** No fixed sleeps (L-044); synchronous rendezvous tests raise the thread-pool floor (CI has 2 cores). Logs: message id and enum/exception type names only, never an exception object, DID, kid, thread id, endpoint or body. No wire value in exception text. Single-pass, branch-flat identity compares. Every new public member fully XML-documented (L-003). Never change an existing public constructor or record primary constructor (Package Validation vs 1.5.0). `git add` explicit paths only; never touch `tasks/*review*.md`.

## Review (2026-11-11, per-issue orchestrator)

**Outcome.** Every scope item of #92 and the three #88 "#92" sections is built, except the FR-RR-09 acceptance chain that needs the #93/#94/#96 handlers (G11, deferred). Commits: `34e4968` (Chunk A: Core model, facade, HTTP, receiver stamping), `368d6d9` (Chunk B: WebSocket session sockets, inbound consumer, DI), `1bca0cf` (QA matrix), `fd6f32d` (QA + red-team fixes), `6b8a8b1` (WebSocket factory cycle guard), `ab641cc` (docs), plus the test-only flake fix below.

**Gates.** `dotnet build DidComm.sln -c Release /warnaserror` 0 warnings · `dotnet test DidComm.sln -c Release --no-build` Core 1504 → 1812, Interop 492 → 645, 0 skipped, 0 failed · `dotnet pack DidComm.sln -c Release --no-build` exit 0, Package Validation vs 1.5.0 clean. Stability: the 353 return-route / session / dispatch / WebSocket tests looped with `DOTNET_PROCESSOR_COUNT=2` — the first 10× loop was 10/10; a 20-run loop then exposed one pre-existing #91 flake (fixed, see below), after which 20/20 runs passed.

**Deviations from the plan (all recorded in commits and docs).**
- `MaxResponseBytes` and `Returned` only for an authcrypt `all`/`thread` send whose identities are set (JC-5 strengthened); identities only for DIDs ≤ 2048 characters (QA-F02, a regression vs `main` otherwise).
- The WebSocket transport refuses invalid identities only on a send that would open a session (QA-F02).
- Backstop is a dedicated timer calling the guarded `StopLoop`, not `CancelAfter` on the consumer-visible token (ADV-01: a throwing cancellation callback crashed the process); non-dispose closes use a finite close grace (QA-F01).
- HTTP: declared lengths above 64 KiB grow as data arrives (ADV-04); every non-cancellation body failure is `ReadFailed` (QA-F03).
- DI: the facade factory and the WebSocket transport factory are cycle-guarded (ADV-02 + the consumer → transport cycle this PR introduced, L-046).
- Docs agent restated three sample comments that called every transport delivery-only (L-045), beyond Section R.

**QA (216 tests) and red team.** QA-F01..F03 fixed with red/green regression tests. ADV-01, ADV-02, ADV-04, ADV-07 (= QA-F02) fixed with red/green regression tests. Won't-fix, documented: ADV-03 (`Returned` is identity-checked, not replay-admitted — FR-RR-07 specifies the identity rule only, and admitting would drop every unstamped in-scope reply from third-party mediators; callers correlate on `thid`), ADV-05 (a multi-recipient envelope whose first held key is another local DID's is discarded — FR-RR-03 session sends are single-recipient), ADV-06 (unpack resolves the named sender DID before the identity check — pre-existing class shared with every receive endpoint).

**Pre-existing flake fixed (test only).** `ReturnRouteWebSocketTests.Many_concurrent_sessions_for_distinct_DIDs_each_receive_only_their_own_pushes` (#91, unchanged by this branch) failed 1 in 20 runs on 2 cores: its overlap assertion depended on the scheduler. A rendezvous now forces two pushers inside the counted region (L-044).

**Found during review, not fixed here (replanning).** FR-RR-08 refuses `created_time > now + ExpiresClockSkew` in whole seconds and `ExpiresClockSkew` defaults to 0, so a message stamped by a host whose clock runs even slightly ahead is refused as `FutureDated` (failure rate ≈ the offset in seconds). It is #91's pinned contract; documented on `DidCommOptions.ExpiresClockSkew` and in the CHANGELOG limitations, raised for #98 / the epic.

## Approval

Plan approved by the per-issue orchestrator on behalf of the user per the standing autonomous-loop instruction of 2026-11-10. Approved with amendments L1–L5 above (2026-11-11).
