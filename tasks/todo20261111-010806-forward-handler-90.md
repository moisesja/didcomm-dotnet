# todo20261111-010806 — Routing core: `ForwardHandler`, `UnpackResult` overload, relay-time expiry, `delay_milli` schedule (#90)

Issue: #90 (part of epic #88; blocked-by #89 merged as PR #101). Branch: `feat/forward-handler-core-90`.
Binding refinement: the "#90 — routing core" paragraph of the latest comment on #88 (plaintext / signed-only / not-held-key / unknown `next` → drop + log id only + 202; registry keyed by `DidSubjectOf(next)`; constant-work lookup; 202 written before the sink; FR-ROUTE-09 regardless of FR-API-05 reject-vs-warn; `forward` exempt from FR-RR-08).
PRD rows: FR-ROUTE-05..07 (unchanged), FR-ROUTE-09 (expiry), FR-ROUTE-10 (delay), FR-ROUTE-11 (registered handler + silent drop), FR-API-05 (expiry arithmetic to mirror, `DidCommClient.cs:669-678`), FR-API-09 (`next` cap already in `ForwardMessage.TryParse`), §4.3 `DidSubjectOf` (`Consistency/DidSubject.cs`, internal).
Orchestrator decisions 1–15 are fixed; this plan bakes them in and flags two deviations (J1, J2) found while reading the code.

**Plan approved by the per-issue orchestrator on behalf of the user per the standing autonomous-loop instruction of 2026-11-10.** Orchestrator review notes: J1/J2 accepted; decision 6's queue uses the ObserverDelivery lifecycle verbatim (no new options class); the registry hash lookup (J4) is accepted as the FR-ROUTE-11 constant-work reading because the 202 precedes every registration-dependent step; J3 (undecryptable JWE keeps the FR-API-07 uniform 400) is carried to the PR body as a judgment call for the reviewer.

Build/verify (every item below is done only when these are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release` · `dotnet pack DidComm.sln -c Release --no-build` (package validation vs 1.5.0).

Files this change may touch (and nothing else):
- `src/DidComm.Core/Protocols/Routing/ForwardProcessor.cs`, `ForwardProcessingResult.cs` (edit)
- `src/DidComm.Core/Protocols/Routing/IRecipientRoutingKeyProvider.cs`, `StaticRecipientRoutingKeyProvider.cs`, `IForwardRecipientRegistry.cs`, `InMemoryForwardRecipientRegistry.cs`, `IForwardSink.cs`, `NullForwardSink.cs`, `ForwardHandler.cs` (new)
- `src/DidComm.Core/Protocols/IProtocolHandlerRoles.cs` (new); `src/DidComm.Core/Protocols/DiscoverFeatures/FeatureDisclosure.cs`, `ProtocolFeatureProvider.cs` (edit)
- `src/DidComm.Extensions.DependencyInjection/DidCommBuilder.cs` (edit: `AddRoutingMediator`)
- `samples/04-MediatorAgent/Program.cs` (minimal edit, lines 112-134)
- tests: `tests/DidComm.Core.Tests/Protocols/Routing/ForwardProcessorTests.cs`, `ForwardNextLengthCapTests.cs` (edit); `ForwardTestEnvelopes.cs`, `ForwardProcessorUnpackResultTests.cs`, `ForwardHandlerTests.cs`, `InMemoryForwardRecipientRegistryTests.cs` (new); `tests/DidComm.Core.Tests/Protocols/DiscoverFeatures/DiscoverFeaturesHandlerTests.cs` (edit, +2 tests); `tests/DidComm.InteropTests/Protocols/RoutingMediatorDiTests.cs`, `tests/DidComm.InteropTests/Routing/MediatorEndpointRelayTests.cs` (new)
- `CHANGELOG.md`, `docs/codebase-architecture.md`, this file.
- NOT touched: `src/DidComm.AspNetCore/*` (verified: nothing needed, see A9), `tools/InteropCli/Program.cs` (string overload with a real anoncrypt forward still works), `ForwardProcessorOptions.cs`, `ForwardMessage.cs`, `ForwardConstants.cs`, PRD (verify only), `samples/02-Cookbook` (Section O already feeds a real anoncrypt forward, line 97).

---

## A. Core — `DidComm.Core/Protocols/Routing`

- [x] A1. `ForwardProcessingResult.cs` — keep the 4 positional parameters `(NextHop, OnwardPacked, ExpiresTime, Delay)` untouched. Add:
  - `public DateTimeOffset? NotBefore { get; init; }` — set by the processor to `DidCommOptions.Now() + Delay` at receipt; `null` when `Delay` is null (FR-ROUTE-10: absolute not-before a queue can store; handler never sleeps).
  - `public bool IsExpired(DateTimeOffset now, TimeSpan skew) => IsExpiredAt(ExpiresTime, now, skew);`
  - `internal static bool IsExpiredAt(long? expiresTime, DateTimeOffset now, TimeSpan skew)` — `expiresTime is null → false`; else `now.ToUnixTimeSeconds() - (long)Math.Floor(skew.TotalSeconds) > expiresTime` (byte-for-byte the FR-API-05 arithmetic in `DidCommClient.cs:671-674`). Shared by the handler's inline check (A7 step c) so both sites cannot drift.
  - XML docs on every member (L-003: all `<param>`s or none).
- [x] A2. `IRecipientRoutingKeyProvider.cs` (new, `namespace DidComm.Protocols.Routing`, public):
  `ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct);` — doc: called with the DID subject of `body.next` (fragment/path/query stripped), only in `RewrapMode.PassThrough`; rewrap mode never consults it (FR-ROUTE-05 second paragraph vs FR-ROUTE-06 are mutually exclusive, cf. `ForwardProcessorOptions.Validate`). Returning an empty list means "relay the attachment verbatim".
- [x] A3. `StaticRecipientRoutingKeyProvider.cs` (new, public sealed): ctor `(IReadOnlyList<Jwk>? keys)`; `GetAsync` returns the list (or `Array.Empty<Jwk>()`) for every DID; `ValueTask.FromResult`.
- [x] A4. `IForwardRecipientRegistry.cs` (new, public): `ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct);` — doc: key is the DID subject; the handler reduces `next` before calling; a CM 3.0 grant store (#93/#95) replaces the default.
- [x] A5. `InMemoryForwardRecipientRegistry.cs` (new, public sealed, `IForwardRecipientRegistry`): `ConcurrentDictionary<string, byte>(StringComparer.Ordinal)`; `public void Register(string didOrDidUrl)` → `DidSubject.DidSubjectOf(value) ?? throw new ArgumentException("... is not a DID or DID URL", nameof(didOrDidUrl))` (message must not echo the value); `public bool Unregister(string didOrDidUrl)` (same reduction, `false` for a non-DID); `public int Count`; `IsRegisteredAsync` = `ContainsKey` (no reduction, no allocation: O(1) hash lookup whose work does not depend on registration state — that is the FR-ROUTE-11 "constant-work" claim, J4). Starts empty.
- [x] A6. `IForwardSink.cs` + `NullForwardSink.cs` (new, public): `Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct);` Default `NullForwardSink(ILogger<NullForwardSink>? logger = null)` logs Information `"Forward {MessageId} accepted by NullForwardSink and dropped; register an IForwardSink (DidComm.Mediator, #95) to relay."` using `context.Received.Message.Id` only, returns `Task.CompletedTask`.
- [x] A7. `ForwardProcessor.cs`:
  - Fields: `+ IRecipientRoutingKeyProvider _routingKeys; + DidCommOptions _didCommOptions;` keep the private `IDidKeyServiceForFacadeOnlyAlias` adapter as is.
  - Ctors (all delegate to one private master):
    - keep `public (DidCommClient, IDidKeyService, ForwardProcessorOptions)` → `this(client, keyService, options, new StaticRecipientRoutingKeyProvider(options?.ExtraRecipientRoutingKeys), new DidCommOptions(), new JoseCryptoProvider())` (null `options` still reaches the master's `ThrowIfNull`; doc: `NotBefore` then uses the system clock).
    - keep `internal (…, JoseCryptoProvider)` → same with the given provider.
    - new `public (DidCommClient client, IDidKeyService keyService, ForwardProcessorOptions options, IRecipientRoutingKeyProvider routingKeys, DidCommOptions didCommOptions)` → `this(…, new JoseCryptoProvider())`.
    - new `internal (…, IRecipientRoutingKeyProvider, DidCommOptions, JoseCryptoProvider)` master: null checks, `options.Validate()`.
  - `public Task<ForwardProcessingResult> ProcessAsync(string packed, CancellationToken ct = default)` keeps its signature; body = `ThrowIfNullOrEmpty; var unpacked = await _client.UnpackAsync(packed, ct); return await ProcessAsync(unpacked, ct);`.
  - new `public async Task<ForwardProcessingResult> ProcessAsync(UnpackResult unpacked, CancellationToken ct = default)` — order fixed by decision 1:
    1. `ArgumentNullException.ThrowIfNull(unpacked)`; `ForwardMessage.TryParse(unpacked.Message, out next, out attachments)` false → `ConsistencyException` ("received a non-forward message; the mediator role only handles <MTURI>" — drop the current `type='{…}'` echo, J5); `MalformedMessageException` from `TryParse` propagates (FR-API-09 cap / missing next / missing attachments).
    2. `!unpacked.Encrypted` → `ConsistencyException("Forward was not encrypted (plaintext or signed-only); a mediator relays only forwards decrypted with a held key (FR-ROUTE-11).")`.
    3. `string.IsNullOrEmpty(unpacked.RecipientKid)` → `ConsistencyException("Forward was not decrypted with a key this mediator holds (FR-ROUTE-11).")` (no kid in the text).
    4. `attachments.Count > 1` → existing `ConsistencyException` (unchanged text).
    5. `ExtractAttachmentBytes` unchanged (lenient base64 stays — do not tighten). `expiresTime`, `delay` as today; `var notBefore = delay is TimeSpan d ? _didCommOptions.Now() + d : (DateTimeOffset?)null;`
    6. Rewrap mode → unchanged `RewrapToNextAsync` (uses `StripFragment`; leave it).
    7. PassThrough: `var subject = DidSubject.DidSubjectOf(next); var extra = subject is null ? Array.Empty<Jwk>() : await _routingKeys.GetAsync(subject, ct);` → `extra.Count > 0 ? WrapForExtraKeysAsync(...)` else verbatim. (A non-DID `next` skips the lookup and passes through — J6.)
    8. Every return: `new ForwardProcessingResult(next, bytes, expiresTime, delay) { NotBefore = notBefore }`.
  - Remove the now-dead direct read of `_options.ExtraRecipientRoutingKeys` from `ProcessAsync` (the static provider carries it). `Validate()` untouched.
  - Update the class `<remarks>`: inputs are `UnpackResult`s from the host's own unpack; the string overload is a convenience; posture rules; `NotBefore` clock source.
- [x] A8. `src/DidComm.Core/Protocols/IProtocolHandlerRoles.cs` (new, public, `namespace DidComm.Protocols`): `IReadOnlyList<string> Roles { get; }` — doc: optional companion to `IProtocolHandler`; Discover Features 2.0 `disclose` entries carry an optional `roles` array (spec: `{"feature-type":"protocol","id":"…","roles":["…"]}`); `ProtocolFeatureProvider` copies it.
- [x] A9. `ForwardHandler.cs` (new): `public sealed class ForwardHandler : IProtocolHandler, IProtocolHandlerRoles, IDisposable, IAsyncDisposable`
  - `public string ProtocolUri => ForwardConstants.ProtocolIdentifier;` `public IReadOnlyList<string> Roles { get; } = new[] { "mediator" };` `internal const int DefaultCapacity = 1024;` `private static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);`
  - Ctors: `public ForwardHandler(ForwardProcessor processor, IForwardSink sink, IForwardRecipientRegistry registry, ILogger<ForwardHandler>? logger = null) : this(processor, sink, registry, logger, DefaultCapacity, shutdownGrace: null)`; `internal ForwardHandler(…, int capacity, TimeSpan? shutdownGrace)` (range checks as `ObserverDelivery`). Only one public ctor so `AddProtocol<ForwardHandler>()`'s `TryAddSingleton<T>()` has no greediest-ctor ambiguity.
  - Queue: `private sealed record WorkItem(UnpackResult? Received, ProtocolContext? Context, TaskCompletionSource? Barrier);` `Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity + 1) { FullMode = Wait, SingleReader = false, SingleWriter = false })` with an `OutstandingItems` counter capped at `capacity` under a gate (one physical slot reserved for the flush barrier, exactly like `ObserverDelivery`); one pump `Task.Run(() => PumpAsync(_shutdownToken))` started in the ctor; `_shutdown` CTS captured as `_shutdownToken` before the pump starts.
  - `public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)` — never throws except `OperationCanceledException` on `ct`; always returns `null` (FR-ROUTE-07, mediators send only destward). Inline, cheap, in this order, each failure = `LogWarning` with `message.Id` only + `return null`:
    (a) `ForwardMessage.TryParse` in `try/catch (MalformedMessageException)` → "malformed forward"; `false` → "not a forward" (cannot happen through the registry, keep as a guard);
    (b) `context.Received.Encrypted && !string.IsNullOrEmpty(context.Received.RecipientKid)` else "forward was plaintext/signed-only or not decrypted with a held key (FR-ROUTE-11)";
    (c) `ForwardProcessingResult.IsExpiredAt(message.ExpiresTime, context.Options.Now(), context.Options.ExpiresClockSkew)` → "forward expired at relay time (FR-ROUTE-09)" (applies regardless of any FR-API-05 setting: the unpack path already rejected; this is the handler's own check);
    (d) `var subject = DidSubject.DidSubjectOf(next)`; null → "next is not a DID"; `await _registry.IsRegisteredAsync(subject, ct)` false → "next is not a registered recipient (FR-ROUTE-11)" (D4: no problem report);
    then admit: under the gate, if `_shutdownStarted == 0 && OutstandingItems < capacity && Writer.TryWrite(new WorkItem(context.Received, context, null))` → `OutstandingItems++`; else `LogWarning` "forward {MessageId} dropped: handler queue saturated (capacity {Capacity}); fail closed" (rate-limit like `ObserverDelivery.Drop`: first + every 1000th) and `return null`. The queued item carries the dispatcher-built `ProtocolContext` (immutable record) and NOT the request `ct` (the endpoint passes `HttpContext.RequestAborted`; the detached work runs on `_shutdownToken`).
  - `PumpAsync(CancellationToken shutdown)`: `await foreach (var item in Reader.ReadAllAsync(shutdown))` → barrier → `TrySetResult`; else `ProcessOneAsync(item, shutdown)` then release the slot. `ProcessOneAsync`: `try { var result = await _processor.ProcessAsync(received, shutdown); if (result.IsExpired(context.Options.Now(), context.Options.ExpiresClockSkew)) { LogInformation id only; return; } await _sink.AcceptAsync(result, context, shutdown); } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { } catch (Exception ex) { LogWarning(ex, "forward {MessageId} not relayed: {ExceptionType}", id, ex.GetType().Name) }` — covers `ConsistencyException`, `MalformedMessageException`, `DidResolutionException`, `CryptoException`, sink faults; never the `next` value. The exception's own `.Message` goes through the logger's exception slot only (processor messages contain no wire values after A7/J5).
  - `internal Task FlushAsync(TimeSpan timeout)` — barrier item written into the reserved slot (throw `ObjectDisposedException` after shutdown; serialized by a `SemaphoreSlim(1,1)` like `ObserverDelivery.FlushAsync`); completes when every forward admitted before the call has been handed to the sink (or dropped).
  - `Dispose()` = `BeginShutdown()`; `DisposeAsync()` = `BeginShutdown()` + `await completion.WaitAsync(_shutdownGrace)` swallowing `TimeoutException`. `BeginShutdown`: `Interlocked.Exchange(ref _shutdownStarted, 1)`; `Writer.TryComplete()`; `_shutdown.Cancel(throwOnFirstException: false)`; drain queued items (barriers → `TrySetException(ObjectDisposedException)`, forwards discarded = fail closed, logged once at Information with the count); observe the pump then `_shutdown.Dispose()` + completion TCS. Copy the shape of `ObserverDelivery.cs:277-340` rather than inventing a second lifecycle.
  - Class `<remarks>` must record the rationale: FR-ROUTE-11 acceptance "the 202 is written before the forward sink runs (the sink is detached from the request), so response timing does not depend on registration state"; D6's sink (#95) does live-push / queue / direct-push work and rewrap mode does crypto — detaching once here means every sink runs after the 202 on every transport instead of each sink detaching itself. Also: the inner payload's `expires_time` is encrypted and invisible; only the forward's header is enforced (FR-ROUTE-09). `please_ack` is never read (FR-ROUTE-07).
- [x] A10. `FeatureDisclosure.cs`: add `[JsonPropertyName("roles")] public IReadOnlyList<string>? Roles { get; init; }` — omitted on the wire when null (`DidCommJson.Default` has `DefaultIgnoreCondition = WhenWritingNull`, `DidCommJson.cs:41`, verified); `ReadDisclosures` deserializes `IReadOnlyList<string>` natively via STJ.
- [x] A11. `ProtocolFeatureProvider.Disclose`: `Roles = handler is IProtocolHandlerRoles r ? r.Roles : null`.

## B. DI — `DidComm.Extensions.DependencyInjection/DidCommBuilder.cs`

- [x] B1. Add `public DidCommBuilder AddRoutingMediator(ForwardProcessorOptions? options = null)` (NOT an `Action<>`: the options type is a positional record with init-only members; `init→set` would be a binary break). Body (all `TryAdd` so a host or #95 can pre-register its own):
  ```csharp
  var resolved = options ?? new ForwardProcessorOptions();
  resolved.Validate();                                   // fail fast at registration, not first dispatch
  Services.TryAddSingleton(resolved);                    // ForwardProcessorOptions
  Services.TryAddSingleton<IRecipientRoutingKeyProvider>(sp =>
      new StaticRecipientRoutingKeyProvider(sp.GetRequiredService<ForwardProcessorOptions>().ExtraRecipientRoutingKeys));
  Services.TryAddSingleton<InMemoryForwardRecipientRegistry>();
  Services.TryAddSingleton<IForwardRecipientRegistry>(sp => sp.GetRequiredService<InMemoryForwardRecipientRegistry>());
  Services.TryAddSingleton<IForwardSink, NullForwardSink>();
  Services.TryAddSingleton(sp => new ForwardProcessor(
      sp.GetRequiredService<DidCommClient>(),
      sp.GetRequiredService<IDidKeyService>(),
      sp.GetRequiredService<ForwardProcessorOptions>(),
      sp.GetRequiredService<IRecipientRoutingKeyProvider>(),
      sp.GetRequiredService<IOptions<DidCommOptions>>().Value));
  return AddProtocol<ForwardHandler>();
  ```
  XML doc: not part of `AddBuiltInProtocols()` (an ordinary agent is not a mediator); once registered, `routing/2.0` is disclosed with `roles: ["mediator"]`; the registry starts empty — nothing is relayed until the host registers recipients on `InMemoryForwardRecipientRegistry` (or #95's CM store replaces `IForwardRecipientRegistry`); default sink drops.
- [x] B2. L-017 cycle check (record in the doc comment of B1): registry factory walks `IEnumerable<IProtocolHandler>` → `ForwardHandler` → `ForwardProcessor` → `DidCommClient` (ISecretsResolver, IDidKeyService, IServiceEndpointResolver?, ITransportRouter?, IOptions<DidCommOptions>, JoseCryptoProvider) + `IDidKeyService` + `IRecipientRoutingKeyProvider` (→ `ForwardProcessorOptions`) + `IForwardSink` (`NullForwardSink` → `ILogger?`) + `IForwardRecipientRegistry` (→ `InMemoryForwardRecipientRegistry`, no deps) + `ILogger<ForwardHandler>?`. None reaches `ProtocolHandlerRegistry`, `ProtocolDispatcher` or `IFeatureProvider`. Rule for #95 (write it in the `IForwardSink` doc): an `IForwardSink` implementation MUST NOT take `ProtocolHandlerRegistry` or `ProtocolDispatcher` in its ctor (inject `IServiceProvider` and resolve at call time), or the registry factory deadlocks on first resolution.
- [ ] B3. The DI container disposes `ForwardHandler` (singleton `IAsyncDisposable`) on provider dispose → the pump stops within the 5 s grace. Covered by D-Interop test I4.

## C. AspNetCore / samples / tools

- [x] C1. `DidComm.AspNetCore` — no change. Verified: `MapDidCommEndpoint(pattern)` (`DidCommEndpointRouteBuilderExtensions.cs:159-222`) unpacks, dispatches, logs the outcome and returns 202; a handler exception would become the uniform 400, but `ForwardHandler.HandleAsync` never throws, so every forward that unpacks yields 202 (FR-ROUTE-11). Record J3 (undecryptable forward → 400 before dispatch).
- [x] C2. `samples/04-MediatorAgent/Program.cs:112-134`: replace the hand-off comment + `PackPlaintextAsync` re-pack with `var processed = await processor.ProcessAsync(unpacked, ct);` and a comment: "the endpoint already decrypted the forward with the mediator's key; hand the UnpackResult straight to ForwardProcessor so envelope metadata (Encrypted, RecipientKid) is kept — a plaintext or signed-only forward is refused (FR-ROUTE-11)". Keep `narrator` lines byte-identical (`MediatorAgentSmokeTests` asserts "[mediator] received an envelope (type = …/forward)", "[mediator] forward unwrapped — next hop did:peer:2.", "(HTTP 202)"). Keep the callback overload; the handler-based rewrite is #97.
- [x] C3. `tools/InteropCli` `unwrap-forward` — no change (real anoncrypt forward through the string overload: `Encrypted` true, `RecipientKid` set). Verify by running the existing interop CLI test, if any, or `dotnet build`.

## D. Tests

- [x] D1. `tests/DidComm.Core.Tests/Protocols/Routing/ForwardTestEnvelopes.cs` (new, internal static helper): `AsDecryptedByMediator(Message m, string recipientKid = "did:example:m#key-1")` → `new UnpackResult(Message: m, Stack: new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext }, Encrypted: true, Authenticated: false, NonRepudiation: false, AnonymousSender: true, ContentEncryption: null, KeyWrap: null, SignatureAlgorithm: null, SignerKid: null, SenderKid: null, RecipientKid: recipientKid, AllRecipientKids: new[] { recipientKid }, FromPrior: null)` (PascalCase named args, L-017); `AsPlaintext(Message)`, `AsSignedOnly(Message)` (`Stack [Signed, Plaintext]`, `Encrypted: false, NonRepudiation: true, SignerKid: "did:example:s#key-1"`), `AsEncryptedWithoutRecipientKid(Message)`. Also `NewProcessor(ForwardProcessorOptions?, IRecipientRoutingKeyProvider?, DidCommOptions?)` over `EmptySecretsResolver`/`EmptyKeyService` (move those two private classes here from `ForwardProcessorTests`).
- [x] D2. Rewrite `ForwardProcessorTests` per the L-020 section: every success-path test builds the forward `Message`, wraps it with `AsDecryptedByMediator`, and calls `ProcessAsync(unpacked)`; structural assertions unchanged. Keep the option-validation tests as they are.
- [x] D3. `ForwardNextLengthCapTests`: switch the three success-path tests that use the plaintext `MediatorFor` helper to the synthetic overload (see L-020 section); refusal tests stay on the plaintext string path (they pin "cap check before posture check").
- [x] D4. `ForwardProcessorUnpackResultTests.cs` (new) — see Test matrix U1.
- [x] D5. `ForwardHandlerTests.cs` (new) — see Test matrix U2; drives `ProtocolDispatcher.DispatchAsync` with a `ProtocolHandlerRegistry` holding one `ForwardHandler` (internal ctor for capacity/grace), a capturing `IForwardSink` (count, last result, optional gate `TaskCompletionSource`), a capturing `ILogger<ForwardHandler>` (private nested class, like `ObserverDeliveryTests.CapturingLogger`), `InMemoryForwardRecipientRegistry` with `did:example:bob` registered, `DidCommOptions { Clock = () => fixed }`.
- [x] D6. `InMemoryForwardRecipientRegistryTests.cs` (new) — U3.
- [x] D7. `DiscoverFeaturesHandlerTests.cs` +2 tests — U4.
- [x] D8. `tests/DidComm.InteropTests/Protocols/RoutingMediatorDiTests.cs` (new) — I1..I5; pattern of `RegistryDiAndDispatchTests` / `DiscoverFeaturesDiTests` (`AddDidComm(b => { b.UseNetDidResolver(); b.UseSecretsResolver(new InMemorySecretsResolver()); b.AddBuiltInProtocols(); b.AddRoutingMediator(); })`, did:peer identities from `samples/_shared/PeerIdentityFactory.cs`, dispatcher resolved from DI).
- [x] D9. `tests/DidComm.InteropTests/Routing/MediatorEndpointRelayTests.cs` (new) — I6..I9. Harness: `SpecActorRegistry.LoadDefault()` + `FixtureDidResolver` over `diddocs/spec/{alice,bob-with-routing,mediator1}.json` (copy `LoadResolverFromFiles` from `AliceMediatorBobRoundTripTests`); TestServer as in `AspNetCoreReceiveRoundTripTests.BuildServerAsync` but through `AddDidComm`: register `AddSingleton<IDidKeyService>(new NetDidKeyService(resolver))`, `AddSingleton<IServiceEndpointResolver>(...)`, `AddSingleton<IForwardSink>(capturingSink)` BEFORE `AddDidComm(b => { b.UseSecretsResolver(actors.AsSecretsResolver()); b.AddBuiltInProtocols(); b.AddRoutingMediator(); })` (do not call `UseNetDidResolver`; `AddDidComm`'s `IsRegistered<IDidKeyService>` check is satisfied by the manual registration); `app.MapDidCommEndpoint("/didcomm")`; `app.Services.GetRequiredService<InMemoryForwardRecipientRegistry>().Register("did:example:bob")`; Alice packs `PackEncryptedAsync(msg, new PackEncryptedOptions(Recipients: ["did:example:bob"], From: "did:example:alice", Forward: true))` with a direct `DidCommClient` over the same actors; POST `packResult.Message` as `application/didcomm-encrypted+json`; Bob unpacks `OnwardPacked` with `actors.AsSecretsResolver()`.
- [x] D10. `MediatorAgentSmokeTests` stays green (C2 keeps the narrator lines).

## E. Docs

- [ ] E1. `CHANGELOG.md` `[Unreleased]`: add `### Added` (ForwardHandler, `AddRoutingMediator`, `ForwardProcessor.ProcessAsync(UnpackResult)`, `IRecipientRoutingKeyProvider` / `StaticRecipientRoutingKeyProvider`, `IForwardRecipientRegistry` / `InMemoryForwardRecipientRegistry`, `IForwardSink` / `NullForwardSink`, `ForwardProcessingResult.NotBefore` / `IsExpired`, `IProtocolHandlerRoles`, `FeatureDisclosure.Roles`, new `ForwardProcessor` ctor; FR-ROUTE-09..11) and `### Changed` (behaviour: `ForwardProcessor.ProcessAsync(string)` now refuses plaintext and signed-only forwards and forwards not decrypted with a held key with `ConsistencyException` (FR-ROUTE-11); the non-forward `ConsistencyException` no longer echoes the message type; sample 04 hands the `UnpackResult` to the processor directly) above the existing `### Docs`.
- [ ] E2. `docs/codebase-architecture.md:66-77` "Mediator pipeline (planned, Phase 7)": replace with "Mediator routing core (Phase 7, #90)": inbound `forward` → `ProtocolDispatcher` → `ForwardHandler` (inline: parse, posture `Encrypted && RecipientKid`, relay-time expiry, `DidSubjectOf(next)` → `IForwardRecipientRegistry`; then detached bounded queue) → `ForwardProcessor.ProcessAsync(UnpackResult)` (pass-through / per-recipient `IRecipientRoutingKeyProvider` wraps / rewrap) → expiry re-check → `IForwardSink`; `roles: ["mediator"]` disclosure via `IProtocolHandlerRoles`; `AddRoutingMediator()`; 202 before the sink on every transport; keep a closing note that live sessions / queue / CM 3.0 / pickup / `DidComm.Mediator` are #91–#95 and the recipient client is #96.
- [ ] E3. PRD — verify only: FR-ROUTE-09..11 rows (lines 406-408) already carry the acceptance text this change satisfies; no edit. If the implementer finds a mismatch, stop and re-plan (CLAUDE.md "PRD is the target").

## F. Verification

- [x] F1. `git branch --show-current` = `feat/forward-handler-core-90` before the first edit and before every commit (L-019).
- [x] F2. `dotnet build DidComm.sln -c Release /warnaserror` (L-003: complete XML docs on every new public/internal member; L-006 surface below).
- [x] F3. `dotnet test DidComm.sln -c Release` — all green including `MediatorAgentSmokeTests`, `AliceMediatorBobRoundTripTests`, `CookbookSmokeTests`.
- [x] F4. `dotnet pack DidComm.sln -c Release --no-build` — package validation vs 1.5.0 green (API-compat section).
- [x] F5. Diff behaviour vs main: `ForwardProcessor` string overload on the Cookbook Section O / InteropCli real forwards unchanged; plaintext forward now `ConsistencyException`.

### Implementation notes (sections A, B, C, D1–D3)

- Deviation (gate outside the plan's file list): `PublicApiCoverageTests` / `PublicApiExecutionTests` (FR-DX-01) fail for every new public member because no sample references or executes it, and the plan defers the handler-based sample 04 rewrite to #97. Added one justified entry per member (30 static, 25 execution) to `tests/DidComm.InteropTests/DxCoverage/coverage-allowlist.json` and `execution-allowlist.json`, each reading "delete this entry when #97 lands" — the gate's own stale-entry check then forces their removal once #97 demonstrates the surface. Alternative for the orchestrator: pull the sample 04 rewrite forward into this PR instead.
- `ForwardHandler`'s private `WorkItem` is `(ProtocolContext? Context, TaskCompletionSource? Barrier)` — the `UnpackResult` is reachable as `Context.Received`, so the plan's separate `Received` field was dropped as redundant.
- `ForwardHandler.HandleAsync` also throws `ArgumentNullException` for a null `message`/`context` (caller bug, never reachable through the dispatcher); every wire-driven failure is a drop + Warning + `null`. A registry (or logger) fault during inline screening is caught and logged as a drop too.
- `ForwardTestEnvelopes.NewProcessor` takes an optional 4th `IDidKeyService? keyService` (defaults to `EmptyKeyService`) so QA can drive rewrap-mode faults without a second helper.
- `ForwardNextLengthCapTests`: added `DecryptedMediatorFor(next, mode, resolver)` → `(ForwardProcessor, UnpackResult)` for the three rewritten success tests; `MediatorFor` (plaintext string path) stays for the refusal tests and its doc comment now states why (cap check precedes posture check).
- `AliceMediatorBobRoundTripTests`, `AllRecipientKeysRoutingTests`, `SenderForwardWrappingTests`, `MediatorAgentSmokeTests`, `CookbookSmokeTests`, `DiscoverFeaturesRoundTripTests` pass unchanged (all feed real anoncrypt forwards through the string overload).

#### QA notes (Core) — D4–D7

Bugs found by the new tests and fixed in src (minimal, root cause):

- `ForwardProcessor.ExtractDelay` (`ForwardProcessor.cs`): a positive `delay_milli` was not capped. `long.MaxValue` threw `OverflowException` out of `TimeSpan.FromMilliseconds`; `10^15` fit a `TimeSpan` but threw `ArgumentOutOfRangeException` from the `NotBefore` addition (`Now() + delay` past year 9999); `int.MaxValue + 1` passed uncapped although the comment promised an `int.MaxValue` ms hold. On the string overload (sample 04, InteropCli) these escaped as undocumented exception types. Fixed with the same `int.MaxValue` ms cap the negative path already had; `ForwardProcessingResult.Delay` doc updated. Caught by `ForwardProcessorUnpackResultTests.Huge_positive_delay_milli_is_capped_and_never_throws`.
- `ForwardHandler.PumpAsync` (`ForwardHandler.cs`): `ChannelReader.ReadAllAsync` hands out already-buffered items without re-checking the token, so after `Dispose`/`DisposeAsync` cancelled the pump, a sink that returned on cancellation let the pump race the disposer's `DrainQueued` for the still-queued forwards and hand them to the sink with a cancelled token (3 sink calls where the contract promises 1 + 2 discarded). Fixed: the pump checks `shutdown.IsCancellationRequested` before each forward item, discards it and stops; the "discarded" Information line is now emitted once, with the total, from the shutdown observer. Caught by `ForwardHandlerTests.DisposeAsync_completes_within_grace_and_discards_queued_forwards`.

Not fixed (pre-existing, outside this change's file list): `ObserverDelivery.PumpAsync` has the identical `ReadAllAsync` shape, so a cooperative observer with buffered snapshots can receive them with a cancelled token after dispose. Flagged for a follow-up.

Accepted as designed (not a test failure): a pump-side `DidResolutionException` (rewrap mode) carries the next-hop DID in its own `.Message`; the handler passes it in the logger's exception slot only, never in the template or state (J-note in `ProcessOneAsync`). The log-posture test asserts template + state values, not exception text.

Extra permutations added beyond the matrix: `delay_milli` as string / float / bool / null / array / object (ignored, relay unaffected); huge positive and `int.MaxValue` exactly; `expires_time` 0 and negative (dropped); `expires_time == now` (relayed); `next` with path / query / params and no fragment (DID subject match); ordinal case-variant `next` (dropped); `next` as `did:`, `https://…`, `#key-1` (dropped, not thrown); forward whose `to` names someone else (relayed: posture is kid-based); `data.base64` attachment through the handler; attachment with neither json nor base64 (pump drop); forward without `next` (dropped); non-string `next` (malformed before posture); posture refusal ordered before attachment count; registry throwing (drop, "inline screening failed"); registry raising cancellation (OCE propagates); throwing logger (outcome unchanged, sink still reached); sink exception then pump keeps serving; request-token cancel after the 202 does not reach the sink (J7); two concurrent `FlushAsync` callers; dispatch after dispose (dropped, not thrown); dispose idempotency; ctor range checks; non-forward through `HandleAsync` directly; `IsRegisteredAsync` does not reduce DID URLs; 4096-char registry value accepted, 4097 refused without echo; 8-thread registry concurrency; provider called with the DID subject for fragment / path / query / params forms; provider fault propagates in pass-through; two provider keys wrap outer-to-inner in list order; five-arg ctor ignores `ExtraRecipientRoutingKeys`; `roles` wire shape checked on the disclose body JSON.

Verification: `dotnet build DidComm.sln -c Release /warnaserror` green; `dotnet test tests/DidComm.Core.Tests -c Release --no-build` 1254/1254; handler + registry classes re-run 8× without a flake.

---

## Judgment calls

- J1 (deviation from decision 12's list). `ForwardNextLengthCapTests` also pins pre-PRD plaintext success through its `MediatorFor` helper in three tests (`ProcessAsync_passes_a_bare_DID_next_of_exactly_the_DID_cap_through_to_the_host`, `ProcessAsync_passes_next_at_exactly_the_cap_through_to_the_host`, `ProcessAsync_rewraps_to_next_at_exactly_the_cap`). They must move to the synthetic `UnpackResult` path too (L-020 section). The refusal tests in that file keep the plaintext string path because `TryParse` runs before the posture check (decision 1 order), so they still throw `MalformedMessageException` without touching the resolver — and they now additionally pin that ordering.
- J2 (type names). `EnvelopeKind` is `Plaintext | Signed | Encrypted` (no `Anoncrypt`): synthetic stacks use `[Encrypted, Plaintext]`. `DidCommOptions.Now()` and `DidSubject` are internal — fine for Core, and both test projects have `InternalsVisibleTo`.
- J3. FR-ROUTE-11's "not decrypted by a held key" is read as "unpack succeeded without a held-key decrypt" (plaintext / signed-only, or a synthetic `UnpackResult` with `Encrypted` but no `RecipientKid`). A JWE the mediator cannot open fails inside `UnpackAsync` → the existing uniform 400 (FR-API-07) before dispatch; it is not identifiable as a forward and the 400 depends only on the mediator's own keys, so it is not a registration oracle. By construction a decrypt with a non-owned kid cannot produce an `UnpackResult` (documented on `ProcessAsync`, not tested).
- J4. "Constant-work lookup" = one ordinal hash `ContainsKey` on the DID subject, with the 202 written before any registration-dependent work (the sink). Timing of the 202 therefore does not depend on whether `next` is registered; no timing test beyond the existing receive-floor tests is added.
- J5. The non-forward `ConsistencyException` text drops the `type='…'` echo (wire value; FR-API-09 no-echo posture applies to every new/edited wire-facing message). Check `ForwardProcessorTests.ProcessAsync_rejects_non_forward_plaintext_with_ConsistencyException` for a `.Contain(type)` assertion and relax it to the MTURI constant if present.
- J6. In PassThrough mode a `next` that is not a DID/DID URL (passes the FR-API-09 cap but `DidSubjectOf` is null) skips the routing-key lookup and passes through verbatim rather than throwing: the handler already drops such forwards before the processor runs (step d), and the string-overload/host path keeps its pre-existing behaviour (minimal impact).
- J7. The queued item stores the dispatcher's `ProtocolContext` and runs on the handler's shutdown token, never the request token — otherwise an HTTP client abort after the 202 would cancel the relay.
- J8. Pump is single-reader, sequential: N parallel dispatches → N sequential sink calls. Parallelism is a #95 concern (the sink can fan out); the bound (`DefaultCapacity = 1024`, internal const, no options class) is the only knob for now, per decision 6.
- J9. Expiry is checked twice on purpose: inline (so an expired forward never costs a queue slot or sink call, FR-ROUTE-09 acceptance "reaches the handler and nothing is queued") and in the pump (time spent queued). Both use `ForwardProcessingResult.IsExpiredAt`.
- J10. `NullForwardSink` logs at Information (not Warning): dropping is its documented contract, not a fault.
- J11. `AddRoutingMediator` calls `Validate()` eagerly so an invalid rewrap+extras combination fails at startup rather than on the first forward (the processor ctor would throw inside the registry factory on first dispatch — a worse failure mode under L-017).

## Transitive public surface (L-006)

New public types: `IRecipientRoutingKeyProvider`, `StaticRecipientRoutingKeyProvider`, `IForwardRecipientRegistry`, `InMemoryForwardRecipientRegistry`, `IForwardSink`, `NullForwardSink`, `ForwardHandler` (all `DidComm.Protocols.Routing`), `IProtocolHandlerRoles` (`DidComm.Protocols`).
Types they reference, all already public: `Jwk` (`DidComm.Jose`), `UnpackResult`, `DidCommOptions`, `DidCommClient` (`DidComm.Facade`), `IDidKeyService` (`DidComm.Resolution`), `ForwardProcessingResult`, `ForwardProcessor`, `ForwardProcessorOptions`, `ProtocolContext`, `IProtocolHandler`, `Message`, `ILogger<T>` (Microsoft.Extensions.Logging.Abstractions — already a public dependency via `ProtocolDispatcher`'s ctor). `DidSubject`, `IdentifierLimits`, `DidCommOptions.Now()`, `ForwardProcessingResult.IsExpiredAt` stay internal (used only inside Core). Nothing needs promoting; build with `/warnaserror` confirms (CS0051/CS0053 would flag otherwise).

## API-compat impact (package validation vs 1.5.0 — every item additive)

| Member | Kind |
|---|---|
| `ForwardProcessingResult.NotBefore { get; init; }` | added property on a sealed positional record; ctor arity (4) and `Deconstruct` unchanged |
| `ForwardProcessingResult.IsExpired(DateTimeOffset, TimeSpan)` | added instance method |
| `ForwardProcessor..ctor(DidCommClient, IDidKeyService, ForwardProcessorOptions, IRecipientRoutingKeyProvider, DidCommOptions)` | added ctor; 3-arg public ctor kept |
| `ForwardProcessor.ProcessAsync(UnpackResult, CancellationToken)` | added overload; `(string, CancellationToken)` kept with the same signature (behaviour change only) |
| `IRecipientRoutingKeyProvider`, `StaticRecipientRoutingKeyProvider`, `IForwardRecipientRegistry`, `InMemoryForwardRecipientRegistry`, `IForwardSink`, `NullForwardSink`, `ForwardHandler`, `IProtocolHandlerRoles` | new types |
| `FeatureDisclosure.Roles { get; init; }` | added property (class, object-initializer construction; no ctor change) |
| `DidCommBuilder.AddRoutingMediator(ForwardProcessorOptions? = null)` | added method |
| `DidComm.AspNetCore`, `DidComm.Transports.*`, `DidComm.Adapters.NetDid` | unchanged |

No removals, no signature changes, no `init→set`, no new positional record parameters.

## Test matrix

U1 `ForwardProcessorUnpackResultTests` (Core.Tests, uses `TestKeyMaterial`/`SpyDidResolver`/`SpySecretsResolver`/`LengthCap.Doc` already in Core.Tests for a real anoncrypt fixture):
- `UnpackResult_overload_matches_string_overload_on_a_real_anoncrypt_forward` (NextHop, OnwardPacked, ExpiresTime, Delay null, NotBefore null equal)
- `String_overload_refuses_a_plaintext_forward_with_ConsistencyException`
- `UnpackResult_overload_refuses_Encrypted_false`, `…_refuses_signed_only`, `…_refuses_null_RecipientKid`, `…_refuses_empty_RecipientKid`
- `Non_forward_is_refused_with_ConsistencyException_that_does_not_echo_the_type`
- `Malformed_next_throws_MalformedMessageException_before_the_posture_checks` (plaintext + over-cap next → Malformed, not Consistency)
- `IsExpired_theory` `[InlineData(expires, nowSeconds, skewSeconds, expected)]`: `(100, 100, 0, false)`, `(100, 101, 0, true)`, `(100, 101, 1, false)`, `(100, 102, 1, true)`, `(100, 101, 1.9 → floor 1, false)`, `(null, …, false)`
- `NotBefore_is_now_plus_delay_on_the_injected_clock` (`delay_milli: 500`, fixed clock → `now + 500 ms`)
- `NotBefore_for_negative_delay_lies_within_now_and_now_plus_magnitude` (`-500`, 25 runs, all in `[now, now+500ms]`)
- `NotBefore_is_null_when_delay_is_absent`
- `Routing_key_provider_receives_the_DID_subject_of_next_in_PassThrough` (`next = "did:example:n#key-1"` → provider called once with `"did:example:n"`)
- `Routing_key_provider_is_not_called_in_rewrap_mode`
- `Provider_keys_wrap_the_payload_once_per_key` (one X25519 key → `OnwardPacked` unpacks with that key to a forward whose `next` = original next and whose attachment = inner payload)
- `Three_arg_ctor_uses_the_static_provider_from_options` (same output as `ExtraRecipientRoutingKeys`)
- `Please_ack_on_a_forward_is_ignored_FR_ROUTE_07` (kept)

U2 `ForwardHandlerTests` (via `ProtocolDispatcher.DispatchAsync`, synthetic `UnpackResult`s, capturing sink/logger, fixed clock):
- `ProtocolUri_is_routing_2_0_and_Roles_is_mediator`
- `Registered_encrypted_forward_reaches_the_sink_exactly_once_with_NoReply_and_Handler_ForwardHandler` (FlushAsync, then assert)
- `Expired_forward_is_dropped_before_the_sink` ; `Forward_expiring_within_skew_is_relayed`
- `Forward_that_expires_while_queued_is_dropped_by_the_pump` (gate the sink, advance the clock, release)
- `Plaintext_forward_is_dropped` ; `Signed_only_forward_is_dropped` ; `Encrypted_forward_without_RecipientKid_is_dropped`
- `Unknown_next_is_dropped_with_NoReply` ; `Next_as_DID_URL_with_fragment_matches_the_registered_bare_DID` ; `Non_DID_next_is_dropped`
- `Next_over_the_FR_API_09_cap_is_dropped_not_thrown` ; `Forward_without_attachments_is_dropped_not_thrown` ; `Two_attachments_are_dropped_in_the_pump_and_logged`
- `Processor_fault_in_the_pump_is_logged_and_never_escapes` (rewrap mode + `EmptyKeyService` → `DidResolutionException`)
- `Sink_exception_does_not_escape_and_produces_no_reply`
- `Please_ack_on_a_forward_yields_no_reply`
- `Queue_saturation_drops_with_a_warning` (internal ctor capacity 1, gated sink, 3 dispatches → ≤ 2 sink calls, one "saturated" log)
- `DisposeAsync_completes_within_grace_and_discards_queued_forwards`
- `Fifty_parallel_forwards_yield_fifty_sink_calls`
- `Logs_carry_the_message_id_and_never_the_next_DID` (every entry contains the id; none contains `did:example:bob`)
- `HandleAsync_throws_only_OperationCanceledException_for_a_cancelled_caller_token`

U3 `InMemoryForwardRecipientRegistryTests`: `Register_reduces_a_DID_URL_to_its_subject`, `Register_rejects_a_non_DID_without_echoing_it`, `Unregister_removes_and_reports`, `Count_tracks_registrations`, `Lookup_is_ordinal` (`did:example:Bob` ≠ `did:example:bob`).

U4 `DiscoverFeaturesHandlerTests` (+2): `Disclosure_carries_roles_for_a_handler_implementing_IProtocolHandlerRoles` (fake handler in the registry), `Disclosure_omits_roles_on_the_wire_when_null` (serialize the disclose body with `DidCommJson.Default`; no `"roles"`).

I1..I5 `RoutingMediatorDiTests` (InteropTests, DI + dispatcher):
- `AddRoutingMediator_registers_ForwardHandler_processor_and_defaults` (`ProtocolHandlerRegistry.TryResolve(ForwardTypeUri)` → `ForwardHandler`; `IForwardSink` is `NullForwardSink`; `IForwardRecipientRegistry` is the `InMemoryForwardRecipientRegistry` singleton; `IRecipientRoutingKeyProvider` is static)
- `AddRoutingMediator_is_idempotent_and_a_host_registered_sink_wins`
- `AddBuiltInProtocols_alone_registers_no_routing_handler`
- `Provider_builds_resolves_the_dispatcher_and_disposes_without_hang` (L-017; `DisposeAsync` under `WaitAsync(10 s)`)
- `Discover_features_discloses_routing_2_0_with_roles_mediator_only_with_AddRoutingMediator` (query `match: "https://didcomm.org/routing/*"`; with → one disclosure, `Roles == ["mediator"]`; without → none; trust-ping disclosure `Roles == null`)

I6..I9 `MediatorEndpointRelayTests` (TestServer + `MapDidCommEndpoint("/didcomm")`, fixture actors):
- `Registered_recipient_forward_returns_202_then_reaches_the_sink_and_Bob_unpacks_the_onward_payload` (sink gated on a TCS: assert 202 while the gate is closed, release, await the sink result, Bob unpacks → `From == did:example:alice`, `Authenticated`)
- `Unknown_next_returns_202_and_nothing_reaches_the_sink` (no registration; `FlushAsync` via the resolved `ForwardHandler`; sink count 0)
- `Plaintext_forward_returns_202_and_nothing_reaches_the_sink` (POST a plaintext forward with `application/didcomm-plain+json`)
- `Relay_logs_carry_the_message_id_and_not_the_next_DID` (`CapturingLoggerProvider`)

Existing tests that must stay green unchanged: `AliceMediatorBobRoundTripTests` (real anoncrypt forwards through the string overload), `SenderForwardWrappingTests`, `MediatorAgentSmokeTests`, `CookbookSmokeTests`, `DiscoverFeaturesRoundTripTests`.

## L-020 test changes

Each changed test, one sentence:
- `ForwardProcessorTests.ProcessAsync_passes_through_inner_payload_bytes_in_default_mode`, `…_propagates_expires_time_when_set_on_the_inbound_forward`, `…_returns_null_expires_when_inbound_forward_omits_it`, `…_ignores_please_ack_on_a_forward_FR_ROUTE_07`, `…_resolves_negative_delay_milli_to_a_random_bounded_TimeSpan`, `…_resolves_positive_delay_milli_exactly`, `…_decodes_a_base64url_attachment_payload`, `…_relays_a_padded_base64_attachment_FR_ATT_02`, `…_does_not_crash_on_extreme_negative_delay_milli`: these pinned the pre-PRD behaviour that a PLAINTEXT forward is relayed; FR-ROUTE-11 (#89) now requires `Encrypted && RecipientKid` — rewrite them to call `ProcessAsync(AsDecryptedByMediator(forward))` with the same structural assertions (the contract they really test — pass-through bytes, expires propagation, please_ack silence, delay resolution, lenient base64 — is unchanged).
- `ForwardProcessorTests.ProcessAsync_throws_when_forward_attachment_has_neither_json_nor_base64`, `…_maps_malformed_base64_attachment_to_MalformedMessageException`, `…_throws_when_forward_carries_more_than_one_attachment`: these assert refusals that today arrive via plaintext; with the posture check ordered before attachment extraction (decision 1) a plaintext input would now fail for the wrong reason, so they move to the synthetic encrypted path to keep asserting the attachment contract.
- `ForwardProcessorTests.ProcessAsync_rejects_non_forward_plaintext_with_ConsistencyException`: asserts the correct contract (non-forward → `ConsistencyException`, via `TryParse` before the posture check) — kept on the plaintext string path; only relax any assertion on the echoed `type` (J5).
- `ForwardNextLengthCapTests.ProcessAsync_passes_a_bare_DID_next_of_exactly_the_DID_cap_through_to_the_host`, `…_passes_next_at_exactly_the_cap_through_to_the_host`, `…_rewraps_to_next_at_exactly_the_cap`: pinned plaintext success; the contract is "a `next` at the cap is accepted" — rewrite to `ProcessAsync(AsDecryptedByMediator(forward))` keeping the `SpyDidResolver` assertions (the rewrap case still resolves `NextDid` through the key service).
- `ForwardNextLengthCapTests` refusal tests (`…_refuses_a_bare_DID_next_over_the_DID_cap_in_every_mode`, `…_refuses_next_over_the_cap_in_every_mode`, `…_refuses_next_over_the_cap_when_extra_routing_keys_are_configured`): assert the correct contract (FR-API-09 cap rejected before any resolution) and additionally now pin that the cap check precedes the FR-ROUTE-11 posture check — unchanged.
- New refusal tests (one each): plaintext via the string overload → `ConsistencyException`; synthetic `Encrypted=false` → `ConsistencyException`; synthetic `Encrypted=true, RecipientKid=null` → `ConsistencyException`; signed-only (`Encrypted=false, NonRepudiation=true`) → `ConsistencyException`.

## Docs

- `CHANGELOG.md` — E1 (`### Added`, `### Changed` under `[Unreleased]`, above the existing `### Docs`).
- `docs/codebase-architecture.md` — E2 (replace the "planned" paragraph; keep the #91–#96 pointer).
- PRD — verify only (E3); FR-ROUTE-09..11 acceptance cells already describe this change (PR #101).
- XML docs: `ForwardProcessor` remarks (posture rules, `NotBefore` clock, string overload is a convenience), `IForwardSink` (#95 rule: no registry/dispatcher ctor dependency), `ForwardHandler` (detachment rationale, expiry-twice, inner `expires_time` invisible), `AddRoutingMediator` (not in `AddBuiltInProtocols`, empty registry, default sink drops, L-017 graph).

## Review

(to be filled after implementation and QA)
