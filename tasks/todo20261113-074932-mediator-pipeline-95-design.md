# #95 design (architect pass, 2026-11-13): `DidComm.Mediator` pipeline, `AddMediator()` and `MapDidCommMediator`

Appendix to `todo20261113-074932-mediator-pipeline-95.md`; where the plan's judgement calls differ, the plan wins.

I read the binding sources (issue #95, both owner-decision blocks, every #88 update through "after #105", lessons L-001..L-060, PRD §3.1/§8/§8a–c/§12, the architecture sections) and traced the code. Branch `feat/mediator-pipeline-95` is at `add80ab`.

**What is released.** I checked v1.5.1 with `git show v1.5.1:…`. These are released:
- `ForwardProcessor`.
- `ForwardProcessorOptions`, a positional record `(Mode, ExtraRecipientRoutingKeys)`.
- `ForwardProcessingResult` with its 4 positional members. `NotBefore` is #90's and unreleased.
- `DidCommDiagnostics`, `DidCommReceiveOptions`, and the registry-aware `MapDidCommEndpoint(pattern)` / `MapDidCommWebSocket(pattern)`.

Not in v1.5.1, so free to change: `ForwardHandler`, `IForwardSink`, `SendPackedAsync`, `ReplayProtectionOptions`, `InMemoryReplayGuard`, `MessagePickupQueue`, `MessagePickupOptions` and every other #90–#105 type.

---

## 1. Public API, per assembly

### DidComm.Core

**1. `ForwardProcessorOptions.MaxDelay`** — `public TimeSpan? MaxDelay { get; init; }`. It is not a positional member.
- XML intent: "The longest `delay_milli` honoured (FR-ROUTE-10). A positive hint longer than this is shortened to it. For a negative hint, `|n|` is clamped to it *before* the uniform draw in `[0, min(|n|, MaxDelay)]`. Nothing is refused because of its delay. `null` (default) is the library's hard bound, `int.MaxValue` ms, which is the 1.5.1 behaviour. `AddMediator()` sets it from `MediatorOptions.MaxForwardDelay`."
- `Validate()` gains one rule: `null`, or within `[0, TimeSpan.FromMilliseconds(int.MaxValue)]`; otherwise `ArgumentException(nameof(MaxDelay))`.
- **Why it is binary-compatible:**
  - Adding an init-only property to a sealed record adds two accessors.
  - The primary constructor, `Deconstruct` (positional members only), and the `Mode` / `ExtraRecipientRoutingKeys` accessors keep their signatures. ApiCompat sees additions only.
  - Equality, `GetHashCode` and `ToString` now include `MaxDelay`. Every instance built by 1.5.1-compiled code has `null`, so its equality results do not change.
  - CHANGELOG wording follows L-045: "binary/API compatible; value equality now includes MaxDelay".
- **Why it goes here (least API, and "clamped before the draw" holds):**
  - The draw happens in Core. `ForwardHandler.HandleAsync` calls `ForwardProcessor.ExtractDelay(message)` inline (`ForwardHandler.cs:252`). The public `ProcessAsync` overloads draw in `ResolveSchedule` (`ForwardProcessor.cs:294-298`).
  - The handler already holds the processor, so the cap must reach the processor's options. No new type is needed, and sample-04-style hosts get the same cap.
  - Rejected alternative: the sink re-drawing from the raw header. That draws twice, reconstructs the receipt time, and gives a point mass at the cap if it clamps after the draw.

**2. `DidCommDiagnostics.MeterName`** — `public const string MeterName = "didcomm-dotnet";`
- This adds a member to a released static class.
- Its XML doc lists the instruments and the closed tag vocabulary (§10).

**3. `DidComm.Resolution.DidCommServiceBuilder`** — new, public sealed. It lifts `samples/_shared/PeerIdentityFactory.cs:96-113`.
```csharp
public static DidCommServiceBuilder ForEndpoint(Uri endpoint);       // absolute http/https/ws/wss, no userinfo; stores AbsoluteUri (L-029)
public static DidCommServiceBuilder ForMediator(string mediatorDid); // bare DID ≤ 2048 (FR-MED-07 recipient entry: uri = routing_did); DID URL refused
public DidCommServiceBuilder WithId(string id);                      // default "#didcomm"; "#frag" or absolute DID URL with fragment
public DidCommServiceBuilder WithRoutingKeys(params string[] routingKeys); // each a DID or DID URL within IdentifierLimits; order kept;
                                                                     // refuses a route longer than MediatorEndpointExpander.MaxForwardLayers (counting ForMediator's implicit layer)
public DidCommServiceBuilder WithAccept(params string[] profiles);   // default ["didcomm/v2"]; zero profiles = omit "accept"
public NetDid.Core.Model.Service Build();                            // {Id, Type="DIDCommMessaging", ServiceEndpoint=ServiceEndpointValue.FromMap({uri, accept?, routingKeys?})}
```
- Refusal messages never quote values.
- #96 adds `ForMediation(MediationGrant)` in DidComm.Mediator, because `MediationGrant` lives there. It can be a C# 14 static extension that wraps `ForMediator(grant.RoutingDids[0])`.

**4. `InMemoryReplayGuard.ClockBehindRefusalCount`** — `public long ClockBehindRefusalCount => Interlocked.Read(ref _behind);`
- The counter already exists (`InMemoryReplayGuard.cs:95`). It is the count behind the "behind the latest purge instant" Warning (`:213-214`). The type is unreleased.

**Core internals:**
- `ForwardMetrics` — internal sealed, `IDisposable`. Constructor `ForwardMetrics(IMeterFactory?)`; members `Record(string outcome, long count = 1)` and `Meter`.
- `ForwardOutcomes` — internal constant strings.
- `ForwardProcessor.ResolveDelay(Message)` — internal instance method that applies `_options.MaxDelay`.
- `ForwardProcessor.ExtractDelay(Message, TimeSpan? maxDelay)` — internal static.
- One internal `ForwardHandler` constructor overload that takes `ForwardMetrics`. The public constructor is unchanged and creates and owns its own `ForwardMetrics`.

### DidComm.Extensions.DependencyInjection
No public change. `AddRoutingMediator` (`DidCommBuilder.cs:200-220`) now, before `AddProtocol<ForwardHandler>()`:
- TryAdds `ForwardMetrics` as `sp => new ForwardMetrics(sp.GetService<IMeterFactory>())`.
- TryAdds a `ForwardHandler` factory that uses the internal constructor.

The pattern is the same as `MessagePickupBuilderExtensions.cs:89-93`.

### DidComm.AspNetCore
No public change.
- The body of `MapDidCommWebSocket(pattern)` (`DidCommEndpointRouteBuilderExtensions.cs:577-613`) moves into `internal static IEndpointConventionBuilder MapDidCommWebSocketCore(IEndpointRouteBuilder endpoints, string pattern, bool mediatorEndpoint)`. The public method calls it with `false`.
- With `true`, the receive options read at `:599` are replaced by `receiveOptions.ForMediatorEndpoint()`. This is a new internal copy method on `DidCommReceiveOptions` with `AllowSameSocketReplies = true` and `UseStomp = false`; `AcceptedMediaTypes` and `ReceiveRejectionFloor` are copied.
- csproj: `<InternalsVisibleTo Include="DidComm.Mediator.AspNetCore" />`.
- **Why InternalsVisibleTo rather than a public overload:**
  - It adds no public surface to a released package for one caller.
  - Precedent: Core already grants InternalsVisibleTo to the sibling packages `DidComm.AspNetCore` and `DidComm.Mediator`, and all packages ship in lockstep from one `DidCommVersion`.
  - Residual: version skew between packages could break an internal call. RELEASING records that the packages ship together.

### DidComm.Mediator (unreleased)
New public types:
- `DidComm.Mediator.MediatorOptions` (sealed) — see §6.
- `DidComm.Mediator.MediatorBuilderExtensions.AddMediator(this DidCommBuilder builder, Action<MediatorOptions>? configure = null)`.
- `DidComm.Mediator.IRecipientEndpointDirectory { Uri? Find(string grantPeerDid); }`
  - XML intent: the http(s) endpoint to push the grant's messages to, or `null`. Host singleton, thread-safe. It is called only on the direct-push workers, never on the request or the forward pump. A throw means "skip this push".
- `DidComm.Mediator.Coordination.MediatorIdentity : IMediatorIdentity`.
  - Constructor `(IEnumerable<string> routingDids)` requires at least one entry; each must be a `BareDid` of at most 2048 characters. Duplicates are removed and the first-occurrence order is kept, because first = primary (#99 decision).
  - The list is copied to a `ReadOnlyCollection`; `RoutingDids` returns it.
  - It is immutable, so reading it is cheap, thread-safe and never throws. `ArgumentException` does not quote the DID.
- `MessagePickupOptions.MaxMessageAge` — `TimeSpan?`, default `TimeSpan.FromDays(30)`, `null` = off. `Validate`: `null` or `> 0`.
- `MessagePickupQueue.RevokeAndPurgeAsync(string peerDid, CancellationToken ct = default)` → `ValueTask<GrantRevocation>`.
- `public readonly record struct GrantRevocation(bool Revoked, int Purged)`, with a `ToString` that prints no DID.

Changed:
- `MessagePickupQueue` public constructor loses `IMessageIdGenerator? idGenerator` (`MessagePickupQueue.cs:40-52`). It becomes `(IMessageStore, IMediationStore, IReturnRouteSessionRegistry, IOptions<MessagePickupOptions>, IOptions<DidCommOptions>, ILogger<MessagePickupQueue>? logger = null)`.
- A new internal constructor adds `Func<string>? newId` as a test seam.

Docs only:
- `QueuedMessage.ExpiresAt` (`IMessageStore.cs:171`), the `IMessageStore` remarks, the `InMemoryMessageStore` remarks, `PickupDeliveryOptions.NotBefore`.
- `IMediatorIdentity` and `CoordinateMediationOptions.RoutingDids` remarks: "AddMediator requires ≥ 1".

### DidComm.Mediator.AspNetCore (new package)
```csharp
public static class MediatorEndpointRouteBuilderExtensions
{
    public const string InvitationOobId = "mediator";
    public static IEndpointConventionBuilder MapDidCommMediator(this IEndpointRouteBuilder endpoints, string pattern);
}
```

---

## 2. Files

**Core:**
- Edit `src/DidComm.Core/Protocols/Routing/{ForwardProcessorOptions.cs, ForwardProcessor.cs, ForwardHandler.cs, ForwardProcessingResult.cs (Delay doc)}`.
- Add `src/DidComm.Core/Protocols/Routing/{ForwardMetrics.cs, ForwardOutcomes.cs}`.
- Edit `src/DidComm.Core/Diagnostics/DidCommDiagnostics.cs`.
- Add `src/DidComm.Core/Resolution/DidCommServiceBuilder.cs`.
- Edit `src/DidComm.Core/Transports/ReturnRoute/InMemoryReplayGuard.cs`.

**DI:** edit `src/DidComm.Extensions.DependencyInjection/DidCommBuilder.cs`.

**AspNetCore:** edit `src/DidComm.AspNetCore/{DidCommEndpointRouteBuilderExtensions.cs, DidCommReceiveOptions.cs, DidComm.AspNetCore.csproj}`.

**Mediator:**
- Edit `src/DidComm.Mediator/DidComm.Mediator.csproj`:
  - add `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />`;
  - add `<InternalsVisibleTo Include="DidComm.Mediator.AspNetCore" />`;
  - update the Description.
- Edit `Directory.Packages.props`: `<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.14" />`.
- Add `src/DidComm.Mediator/{MediatorOptions.cs, MediatorBuilderExtensions.cs, IRecipientEndpointDirectory.cs}`.
- Add `src/DidComm.Mediator/Coordination/MediatorIdentity.cs`; edit `IMediatorIdentity.cs` and `CoordinateMediationOptions.cs` (docs).
- Add `src/DidComm.Mediator/Pipeline/{MediatorForwardSink.cs, DirectPushDispatcher.cs, MediatorHostedService.cs, RoutingDidValidator.cs, MediatorMetrics.cs, MediatorMarker.cs, MediatorInvitationHolder.cs}`. All internal; namespace `DidComm.Mediator.Pipeline`.
- Edit `src/DidComm.Mediator/Pickup/{MessagePickupOptions.cs, PickupCoordinator.cs, MessagePickupQueue.cs, MessagePickupBuilderExtensions.cs, IMessageStore.cs, InMemoryMessageStore.cs}`.
- Add `src/DidComm.Mediator/Pickup/{PickupIds.cs, PickupIdSource.cs}`.

**Mediator.AspNetCore:**
- Add `src/DidComm.Mediator.AspNetCore/DidComm.Mediator.AspNetCore.csproj`. Copy `DidComm.Mediator.csproj`, including `EnablePackageValidation=false` and its comment. Add `FrameworkReference Microsoft.AspNetCore.App`, ProjectReferences to `DidComm.Mediator` and `DidComm.AspNetCore`, and `InternalsVisibleTo DidComm.InteropTests`.
- Add `MediatorEndpointRouteBuilderExtensions.cs`, `CompositeEndpointConventionBuilder.cs` (internal) and `MediatorInvitationStore.cs` (internal `IOobInvitationStore` adapter over `MediatorInvitationHolder`).
- Add the project to `DidComm.sln`.

**Samples/tools:**
- `samples/_shared/PeerIdentityFactory.cs`: `BuildDidCommService` delegates to `DidCommServiceBuilder`, keeping its output (`WithAccept()` with no arguments when `spec.Accept` is null).
- `tools/InteropCli/{Program.cs, InteropCli.csproj}`.
- `tools/interop-live/{run-python-leg.sh, run-jvm-leg.sh, README.md}`.

**Tests:**
- `tests/DidComm.Core.Tests/Protocols/Routing/{ForwardDelayCapTests.cs, ForwardHandlerOutcomeMetricsTests.cs}` (new); adjust existing ForwardHandler log tests (see §14).
- `tests/DidComm.Core.Tests/Resolution/DidCommServiceBuilderTests.cs`.
- `tests/DidComm.Mediator.Tests/Pipeline/` (new): `MediatorOptionsTests`, `AddMediatorDiTests`, `MediatorIdentityTests`, `MediatorForwardSinkTests`, `DirectPushDispatcherTests`, `MediatorHostedServiceTests`, `RoutingDidValidatorTests`, `PipelineTestSupport` (`ManualTimeProvider`, `TestMeterFactory`, recording directory/transport).
- `tests/DidComm.Mediator.Tests/Pickup/` new: `PickupRandomIdTests`, `PickupMessageAgeTests`, `PickupRevokeAndPurgeTests`. Migrate the id-generator seams listed in §5.
- `tests/DidComm.InteropTests/Mediator/` (new): `MediatorTestServer.cs`, `MediatorEndToEndHttpTests`, `MediatorEndToEndWebSocketTests`, `MediatorDirectPushTests`, `MediatorTwoHopTests`, `MediatorExpiryAndDelayTests`, `MediatorAgeLimitTests`, `MediatorAcknowledgementOrderingTests`, `MediatorUnknownNextTests` (incl. timing), `MediatorRewrapAndRoutingKeysTests`, `MediatorStartupTests`, `MediatorInvitationEndpointTests`, `MediatorRevokeAndPurgeTests`, `MediatorLogHygieneTests`.
- `tests/DidComm.InteropTests/DidComm.InteropTests.csproj`: add a ProjectReference to `DidComm.Mediator.AspNetCore`. The DX gate needs the dll in bin.
- `tests/DidComm.InteropTests/DxCoverage/CoverageComputation.cs`: add `"DidComm.Mediator.AspNetCore"` to `ShippedAssemblies` (`:14-22`).
- `coverage-allowlist.json` / `execution-allowlist.json`: add the new entries and re-key the `MessagePickupQueue::.ctor` entries (coverage `:1269`, execution `:1009`). Both currently carry the old `IMessageIdGenerator` signature and would fail as stale.

**Docs:** `docs/didcomm-dotnet_PRD.md`, `docs/codebase-architecture.md`, `CHANGELOG.md`, `README.md`, `RELEASING.md`, and the plan file `tasks/todo<ts>-mediator-pipeline-95.md`.

---

## 3. The sink and direct push

### `MediatorForwardSink.AcceptAsync(result, context, ct)`

It runs on `ForwardHandler`'s single pump, after the 202 and after the pump's own expiry check (`ForwardHandler.cs:452`). `ct` is the handler's shutdown token.

**Constructor:** `(MessagePickupQueue queue, DirectPushDispatcher pushes, ForwardMetrics metrics, IOptions<MediatorOptions> options, ILogger<MediatorForwardSink>? logger)`.
- `DirectPush` is copied at construction (L-060).
- No registry, dispatcher, handler or `DidCommClient` (L-017). The queue factory is already cycle-guarded (`MessagePickupBuilderExtensions.cs:66-85`).

Steps:
1. `id = MediatorLog.LogId(context.Received.Message.Id)`. This is the forward's id; the queue record id is never logged.
2. `subject = DidSubject.DidSubjectOf(result.NextHop)`. `null` (defensive; the handler already screened it) → `Dropped-Unknown`.
3. `delivery = await queue.DeliverAsync(subject, result.OnwardPacked, new PickupDeliveryOptions(result.NotBefore, result.ExpiresTime, AllowLive: true), ct)`. Inside it (`PickupCoordinator.cs:288-378`):
   1. Size bound.
   2. Forward expiry on `DidCommOptions.Clock` with `ExpiresClockSkew` (`ExpiryInstant`, `:675-687`). Never `ReplayProtectionOptions.ClockSkew`.
   3. `BareDid`.
   4. **The delivery-time re-lookup** `FindGrantByRecipientDidAsync` (`:315`). Its `grant.PeerDid` is the live-session key.
   5. Live target, only when due.
   6. `EnqueueAsync` with a random id and `ExpiresAt = min(forward expiry, now + MaxMessageAge)`.
   7. **New: a post-enqueue re-check** (§9).
   8. Live push.
   
   It never throws except OCE on `ct`.
4. Map the outcome:

| `DeliverAsync` outcome | Forward outcome | Log |
|---|---|---|
| `PushedLive` | `Delivered-Live` | Debug |
| `Queued` | `Queued` | Debug; then the direct-push step below |
| `NotRegistered` | `Dropped-Unknown` | rate-limited Warning |
| `Expired` | `Dropped-Expired` | rate-limited Warning |
| `PayloadTooLarge` | `Dropped-TooLarge` | rate-limited Warning |
| `QueueFull` | `Dropped-Quota` | rate-limited Warning |
| `Faulted` | `Faulted` | rate-limited Error |

   The transport answer was already 202. Nothing goes back toward the sender.
5. Direct-push step (only for `Queued`): if `DirectPush`, and `result.NotBefore` is `null` or `<= context.Options.Now()`, call `pushes.TryEnqueue(new PushJob(delivery.GrantPeerDid!, subject, delivery.MessageId!, result.OnwardPacked, forwardId))`. It never blocks.
   - A not-yet-due message is not pushed. It waits in the queue and is picked up once due, the FR-PICK-08 rule applied to step 3.
6. `catch (OperationCanceledException) when (ct.IsCancellationRequested)` → rethrow. Any other exception → `Faulted`, logged by type name only.

The direct push **must be detached**, on bounded workers.

**Why detached.** Inline on the single pump, one slow or tarpitting recipient endpoint would hold the pump for up to `DirectPushTimeout`, plus the HTTP transport's own up to 4 attempts × 30 s (`HttpTransportOptions`). Every other forward then waits. At 1024 outstanding / 64 MiB the handler drops new forwards for everyone, so one endpoint becomes a mediator-wide outage.

L-059 asks for bounded background work with a stated saturation policy, and the policy here is safe without the work, because the record is already queued.

**L-037 / L-036:**
- The egress target is never derived from the message, only from the host's directory.
- Only records already admitted under quota are pushed.
- The ratio is 1 forward → 1 POST (× the transport's retry count).
- Direct push is opt-in (`DirectPush = false`).

### `DirectPushDispatcher` (internal; `IAsyncDisposable` + `IDisposable`, L-035)

**Constructor:** `(IServiceProvider sp, IOptions<MediatorOptions>, MessagePickupQueue, IMediationStore, MediatorMetrics, ILogger, TimeProvider)`.
- `DidCommClient` and `IRecipientEndpointDirectory` are resolved **lazily on the first push** via `sp` and cached. This follows L-017 and the brief: no cycle through the facade, router or transports, and no client is built when push is off.

**Bounds:**
- `DirectPushConcurrency` workers (default 4), started under `ExecutionContext.SuppressFlow`, only when `DirectPush` is on.
- A bounded channel of 1024 jobs.
- A 64 MiB budget of pinned payload bytes, internal constants matching `ForwardHandler.DefaultCapacity` / `DefaultByteBudget`.
- At most 16 pending or in-flight jobs per grant, so one recipient cannot monopolise the backlog.
- **Saturation policy:** `TryEnqueue` fails → `Push-Skipped-Saturated`; the message stays queued for pickup.

**Worker, per job:**
1. `endpoint = directory?.Find(grant)`. No directory, `null`, or a throw → `Push-Skipped-NoEndpoint`.
2. The scheme must be `http`/`https` (`OrdinalIgnoreCase`). Otherwise → `Push-Skipped-Scheme`, with a rate-limited Warning. See below.
3. Re-check registration: `(await mediation.FindGrantByRecipientDidAsync(job.RecipientDid))?.PeerDid == job.GrantPeerDid`. Otherwise → `Push-Skipped-Unregistered`; nothing is pushed after a revoke/remove (§9).
4. `result = await client.SendPackedAsync(job.Payload, endpoint, MediaTypes.Encrypted, token)`.
   - `token` = the dispatcher's shutdown token linked with a guarded timer that fires at `DirectPushTimeout` (default 30 s). Use `TimeProvider.CreateTimer` with a callback that does `try { cts.Cancel(); } catch { }`. Never `CancelAfter`: a `CancelAfter` token handed to host code can crash the process (#92 follow-up 7).
   - `SendPackedAsync` always runs `OutboundEndpointGuard.Validate` (`DidCommClient.cs:864`) and refuses the queue URI (`:860`). The HTTP transport also pins connections at connect time against redirects and DNS rebinding (L-015).
5. **"2xx":** `result is { Accepted: true, HttpStatusCode: >= 200 and <= 299 }`. `HttpDidCommTransport` returns `Accepted` only for 2xx (`HttpDidCommTransport.cs:104-117`) and throws `TransportException` otherwise.
   - On success: `await queue.RemoveAsync(grant, messageId)` → `Pushed`. A `false` from it (already acknowledged or purged) still counts as `Pushed`.
   - Anything else — `TransportException`, timeout, a custom transport returning a `null` status — → `Push-Failed`. The record stays queued: an accepted message is never lost.
   - Logs carry the forward id and the exception type name. Never the endpoint or the exception message, because `TransportException` text contains the URL.
6. Release the reservation.

**Why ws(s) endpoints are never pushed (my decision).**
- The WebSocket transport reports `Accepted: true, HttpStatusCode: null` once the frame is written (`WebSocketDidCommTransport.cs:456`). There is no receipt.
- Counting that as delivery would turn a dropped connection into silent loss.
- Recipients that want push over a socket use pickup live mode, which is acknowledged.

**Shutdown.** Dispose completes the channel and cancels the workers. Pending jobs are discarded: their messages stay queued, so this fails safe. It waits up to 5 s for in-flight pushes; a push in flight may complete.

**Ordering is queue first, then push.**
- D6 puts live first; the #94 note says pass `AllowLive: false` only if push goes first.
- Queueing first means a crash mid-push loses nothing.
- Cost: a recipient that also polls may see the message twice. Delivery is already at-least-once.

---

## 4. Delay cap and age limit

**Delay cap.** It lives in Core, in `ForwardProcessor.ExtractDelay(Message, TimeSpan? maxDelay)`. It is reached through `ForwardProcessor.ResolveDelay` (handler, `:252`) and `ResolveSchedule` (public overloads), so the clamp precedes the only draw. Arithmetic, all in `long`, with no overflow:
```
capMs = maxDelay is {} c ? (long)Math.Clamp(Math.Floor(c.TotalMilliseconds), 0, int.MaxValue) : int.MaxValue
millis < 0:  magnitude = millis == long.MinValue ? long.MaxValue : -millis
             bound = Math.Min(Math.Min(magnitude, capMs), int.MaxValue - 1)   // clamp |n| BEFORE the draw
             sample = RandomNumberGenerator.GetInt32(0, (int)bound + 1)        // uniform in [0, bound]
millis >= 0: delay = Math.Min(millis, capMs)
NotBefore = receivedAt + delay   (receivedAt = the handler's single clock read, unchanged)
```
- Cap 0 means every hint becomes "due at once", which the MAY allows.
- `MediatorOptions.MaxForwardDelay` (`TimeSpan`, default 1 h, range `[0, int.MaxValue ms]`) becomes `ForwardProcessorOptions.MaxDelay`.
- **Interaction with `ExpiresClockSkew`:** none. `NotBefore` is the mediator's clock and expiry stays on `ExpiresClockSkew`. A message whose `NotBefore` is later than its expiry is stored and swept as expired. Expired records never count against bounds, so it cannot be used to occupy quota.
- Consequence, documented as the owner accepted: a sender can occupy a recipient's quota unseen for at most the cap after it stops.

**Age limit.** It lives on `MessagePickupOptions.MaxMessageAge`, because it is a queue property: it also binds `DeliverAsync` callers and hosts that use only `AddMessagePickup`.
- `PickupCoordinator` copies it at construction.
- In `DeliverAsync`:
```
expiresAt = ExpiryInstant(options.ExpiresTime)                        // forward expiry, ExpiresClockSkew
agedOutAt = maxAge is {} a ? (a.Ticks >= (DateTimeOffset.MaxValue - now).Ticks ? DateTimeOffset.MaxValue : now + a) : null  // mediator clock, no skew
record.ExpiresAt = Min(expiresAt, agedOutAt)    // null-aware; the "Expired at admission" check keeps using expiresAt only
```
- The saturating add matters: `MessagePickupAdversarialResourceBoundsTests.cs:200` sets the clock to `MaxValue − 1 s`.
- Every existing expiry path then applies the age limit unchanged: the lazy per-grant sweep, the store-full sweep, `PurgeExpiredAsync` (`InMemoryMessageStore.cs:104-112, 364-452`), lease selection, and `status` exclusion.
- It applies to queued, leased and passed-over records alike, and depends only on time, never on a size (L-056).

**`IMessageStore` contract: no signature changes.** Only the meaning of `QueuedMessage.ExpiresAt` widens: "the first instant at which the record must no longer be delivered and may be removed — the earlier of the forward's expiry and the mailbox age limit, counted from `ReceivedAt` on the mediator's clock". `InMemoryMessageStore` needs no code change.

**Consequence of this choice.** The limit in force at admission is fixed on the record. A host that lowers the limit and restarts on a persistent store does not shorten records already stored. This is listed in §15 for the owner, who expected a contract change.

**Cross-validation.** `MediatorOptions.Validate` refuses `Pickup.MaxMessageAge <= MaxForwardDelay` when both are set; otherwise a delayed message would age out before it is due. The silent loss for a recipient offline longer than the limit goes in the docs and CHANGELOG.

---

## 5. Random queue ids

**Mechanism.** `internal static string PickupIds.NewRandomId()`:
```csharp
Span<byte> b = stackalloc byte[16];
RandomNumberGenerator.Fill(b);
return Convert.ToHexStringLower(b);
```
- That is 128 bits, 32 characters of `[0-9a-f]`: all unreserved, and within `PickupSizing.MaxMintedIdLength` (64).

**Wiring.**
- `PickupCoordinator` swaps `IMessageIdGenerator _ids` (`:75`, `:107`) for `Func<string> _newId`, defaulting to `NewRandomId`.
- `NewMessageId` (`:662-668`) keeps its `IsUsableId` check, so the faulty-generator path stays testable.
- `MessagePickupQueue` drops the `IMessageIdGenerator` parameter from its public constructor.
- `BuildQueue` (`MessagePickupBuilderExtensions.cs:99-109`) stops calling `sp.GetService<IMessageIdGenerator>()`. It calls `sp.GetService<PickupIdSource>()?.Next` instead. `PickupIdSource` is internal (`internal sealed class PickupIdSource(Func<string> next)`), so only code with InternalsVisibleTo can register it, and no public path lets a host's generator reach queue ids.

**Test migration** (register `new PickupIdSource(...)` instead of `IMessageIdGenerator`):
- `MessagePickupAdversarialResourceSeamTests.cs:264-265`
- `MessagePickupLiveDeliverTests.cs:595-596, 631-632, 652-653, 665-666, 682-683, 959-960`
- `MessagePickupStoreSizingTests.cs:249`
- `MessagePickupLiveIdGenerator` and `MessagePickupWireFixedIds` gain a `Func<string>` adapter.

**New regression test.** A container-registered sequential `IMessageIdGenerator` never appears in attachment ids: ids are 32 hex characters and distinct across 1000 deliveries.

---

## 6. Options composition

```csharp
public sealed class MediatorOptions
{
    public CoordinateMediationOptions CoordinateMediation { get; }   // RoutingDids, MaxPageSize, MaxGrants (1 000), MaxRecipients (10 000) …
    public MessagePickupOptions Pickup { get; }                      // MaxQueueMessages, MaxQueueBytes, LeaseTimeout, MaxDeliveryBatch, MaxDeliveryBytes, MaxAckIds, MaxMessageAge …
    public ReplayProtectionOptions Replay { get; }                   // Window, ClockSkew (60 s), MaxEntries …
    public ReturnRouteSessionOptions Sessions { get; }               // MaxSessionsPerPeer, MaxSessions, MaxPendingPushes …
    public RewrapMode RewrapMode { get; set; } = RewrapMode.PassThrough;
    public TimeSpan MaxForwardDelay { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromMinutes(5);   // [1 s, 24 h]
    public bool DirectPush { get; set; }
    public int DirectPushConcurrency { get; set; } = 4;                       // [1, 64]
    public TimeSpan DirectPushTimeout { get; set; } = TimeSpan.FromSeconds(30); // (0, 5 min]
    public void Validate();   // the four sub-Validate()s + the ranges above + defined RewrapMode + MaxMessageAge > MaxForwardDelay
}
```
- Issue scope item 3's per-grant quota, lease, batch and page-size settings are reached through the sub-objects. Nothing is aliased, so no option type overlaps another.

**Projection: one delegate reaches each role's `IOptions<T>`.**
- An internal factory `MediatorOptions.Projecting(cm?, pickup?, replay?, sessions?)` builds a view whose slot for T is the *real* T instance being configured; the other slots are scratch.
- `AddMediator` registers one Configure per role: `T => configure(MediatorOptions.Projecting(T: t))`. For CM and pickup this goes through `AddCoordinateMediation(cm => …)` / `AddMessagePickup(p => …)`, so their probes and validation run too.
- It also registers `AddOptions<MediatorOptions>().Configure(configure)` for the pipeline scalars only.
- The delegate therefore touches only the properties the host sets. Options run delegates in registration order:
  - a host `Configure<T>` registered earlier keeps every property the mediator delegate does not set;
  - one registered later wins for the properties it sets.

  This is why copying whole objects was rejected: it would clobber earlier host settings with defaults.
- Documented rule: the delegate may run several times, on different instances, so it must only assign values. That is the contract `AddCoordinateMediation`'s probe already has.
- At run time no role reads its settings through `IOptions<MediatorOptions>`. Its sub-objects are configuration views.

**Validation.**
- At registration: `var probe = new MediatorOptions(); configure?.Invoke(probe); probe.Validate();`. This sees only the delegate's values, so it is best-effort for cross-role rules.
- At first use: every role validates its effective options at construction (CM handler, queue, guard, registry, `ForwardProcessor`).
- At startup: the hosted service forces all of them (§7). The `MaxMessageAge > MaxForwardDelay` rule is re-checked there on the effective values.

**Repeated `AddMediator` calls.** Delegates compose. Role registrations are idempotent. `ForwardProcessorOptions` is a factory reading `IOptions<MediatorOptions>`, so later calls' `RewrapMode` / `MaxForwardDelay` also compose; the conflict checks recognise our own registrations via the `MediatorMarker` descriptor.

**Host also calls the role methods.**
- `AddCoordinateMediation` / `AddMessagePickup`, before or after: fine.
- `AddRoutingMediator` before `AddMediator`: `AddMediator` throws `InvalidOperationException` ("AddMediator() configures the routing mediator itself…").
- `AddOptions<ReplayProtectionOptions>()`: composes by order as above.

**Ordering with `AddDidComm`.**
- `AddMediator` runs inside the `AddDidComm` callback (`DidCommServiceCollectionExtensions.cs:65`), before `AddDidComm`'s `AddOptions` and TryAdds of the registry and guard (`:78-85`).
- It only adds Configure delegates for those two option types, so `AddDidComm`'s guard and registry factories read the composed values on first resolution. The guard copies its settings then (#105).
- A host that registers its own guard or registry must do so before `AddDidComm` (TryAdd). `MediatorOptions.Replay` / `Sessions` reach it only if it reads `IOptions<T>`; this is documented.

---

## 7. `AddMediator()`, the hosted service, identity

**Registration sequence:**
1. Null check, then probe + `Validate()`.
2. Conflict checks, at registration and before anything is added:
   - a `ForwardProcessorOptions` or `ForwardHandler` descriptor already present and not ours → `InvalidOperationException`;
   - a non-mediator `IForwardSink` → `InvalidOperationException`;
   - `RewrapMode.ReanoncryptToNext` with a host `IRecipientRoutingKeyProvider` registered → `ArgumentException` (it would be silently ignored, `ForwardProcessor.cs:224-228`);
   - the probe has 0 routing DIDs, and there is no `IMediatorIdentity` and no `IConfigureOptions<CoordinateMediationOptions>` descriptor → `ArgumentException`. Otherwise the check defers to first resolution and startup, which avoids false refusals.
3. `TryAddSingleton<MediatorMarker>()`.
4. `AddOptions<MediatorOptions>().Configure(configure)`, plus the projected `Configure` for `ReplayProtectionOptions` and `ReturnRouteSessionOptions`.
5. `TryAddSingleton<IMediatorIdentity>(sp => new MediatorIdentity(sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.RoutingDids))`. This must come before step 6, whose TryAdd it pre-empts (`CoordinateMediationBuilderExtensions.cs:63-64`).
6. `builder.AddCoordinateMediation(projected)`. **Must precede step 9**: it registers the store as the forward directory, and the reverse order throws.
7. `builder.AddMessagePickup(projected)`.
8. TryAdd the remaining singletons:
   - `ForwardProcessorOptions` as a factory: `new(m.RewrapMode) { MaxDelay = m.MaxForwardDelay }`;
   - `MediatorMetrics`;
   - `DirectPushDispatcher`;
   - `MediatorForwardSink` and `IForwardSink → it`;
   - `MediatorInvitationHolder`.
9. `builder.AddRoutingMediator(new ForwardProcessorOptions(probe.RewrapMode) { MaxDelay = probe.MaxForwardDelay })`. It validates; its TryAdds of options and sink are no-ops, and it adds the processor, handler and metrics.
10. `builder.AddBuiltInProtocols()`. This is idempotent and gives the Trust Ping responder plus the Discover Features handler that FR-MED-01 / FR-PICK-01 disclosure and #96's version negotiation need.
11. `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MediatorHostedService>(sp => new MediatorHostedService(sp, IOptions<MediatorOptions>, logger, sp.GetService<TimeProvider>() ?? TimeProvider.System)))`.

**`MediatorHostedService : IHostedLifecycleService, IAsyncDisposable, IDisposable`.**
- Why lifecycle: in `WebApplication`, user hosted services' `StartAsync` runs after the web server's. `StartingAsync` runs before any `StartAsync`, so the checks finish before Kestrel listens.
- **`StartingAsync(ct)`.** Any exception fails host start.
  1. Identity: read `IMediatorIdentity.RoutingDids` once and copy it. Require ≥ 1 and that each is a `BareDid`. Otherwise `InvalidOperationException`, naming the index, never the DID. This is how "≥ 1" is enforced for a host's own identity.
  2. The resolved `IForwardSink` must be a `MediatorForwardSink`. A sink registered later would replace the pipeline silently.
  3. Resolve `ProtocolDispatcher`. This builds the handlers: the CM handler (directory check), the pickup handler (resolves `MessagePickupQueue`, which validates `MaxDeliveryBytes` and logs the admissible size), and `ForwardHandler` (`ForwardProcessor` validates `MaxDelay`).
  4. Resolve `IInboundReplayGuard` and `IReturnRouteSessionRegistry`. Bad replay or session settings now fail at startup, which closes #105 follow-up 1 for mediator hosts.
  5. Re-check `MaxMessageAge > MaxForwardDelay` on the effective values.
  6. FR-MED-07 via `RoutingDidValidator`, per DID, each bounded by `task.WaitAsync(30 s, timeProvider, ct)` (no host-token callbacks):
     - `IServiceEndpointResolver.ResolveAsync(did)` must return ≥ 1 entry. A missing resolver fails with "AddMediator() needs DID resolution (UseNetDidResolver())".
     - The **first** entry is the one senders use (`MediatorEndpointExpander.cs:126`). Its `Uri` must be absolute, with scheme http/https/ws/wss, and must not be a DID or the queue URI.
     - `RoutingKeys` on that entry → Warning (spec SHOULD NOT; senders ignore them, `:136-139`).
     - Another entry with a DID uri → Warning.
     - `IDidKeyService` keyAgreement must have ≥ 1 key, and `ISecretsResolver.FindPresentAsync(kids)` must find ≥ 1. Otherwise forwards to that DID could never be decrypted.
     - Resolution failure, unsupported method or timeout → fail startup. The message names the index and the exception type. This is fail-closed: senders resolve the same DID, so a mediator whose routing DID does not resolve is unreachable anyway.
  7. Warning if `DirectPush` is on and no `IRecipientEndpointDirectory` is registered.
  8. Build the OOB invitation into `MediatorInvitationHolder` (§8).
  9. Register the observable instruments (§10).
  10. Write one Information line (below).
- **`StartedAsync`:** start the loop task `PurgeLoopAsync`: `using var t = new PeriodicTimer(PurgeInterval, timeProvider); while (await t.WaitForNextTickAsync(stop)) await PurgeOnceAsync(stop);`.
  - **`internal PurgeOnceAsync`** (the test seam):
    - `removed = await queue.PurgeExpiredAsync(stop)`. The judgement uses `DidCommOptions.Clock` (`PickupCoordinator.cs:397`).
    - Record the `purged` counter; Information if removed > 0.
    - Compare `SaturationCount`, `ClockBehindRefusalCount` and `DroppedNotifications` with the previous tick. If one moved, log a Warning with the delta, the total and the remedy; this is naturally rate-limited by the tick.
    - A store fault is caught and logged as a rate-limited Warning by type name, and the loop continues.
    - OCE on stop exits.
  - Tests need no sleeps: purge logic via `PurgeOnceAsync` and the injected `DidCommOptions.Clock`; scheduling via a hand-rolled `ManualTimeProvider` (overrides `CreateTimer`). No new test package.
- **`StoppingAsync`:** guarded cancel. **`StopAsync(ct)`:** await the loop with `WaitAsync(ct)`, swallowing OCE. **Dispose:** cancel and dispose the CTS.
- **Information line**, no DIDs: "Mediator ready: {RoutingDidCount} routing DID(s) checked (FR-MED-07); forward delay honoured up to {MaxForwardDelay}; mailbox age limit {MaxMessageAge}; direct push {DirectPush}; purge every {PurgeInterval}."
- **Outside a generic host** (a plain `ServiceCollection`) nothing starts: the checks run lazily at first use and nothing is purged. Documented.

**Where routing DIDs are configured.** Only `MediatorOptions.CoordinateMediation.RoutingDids`, or a host `IMediatorIdentity` registered before `AddMediator`. No second list.

**`AddMediator()` XML remarks (operations guidance):**
- Put rate limiting or admission control in front of `MapDidCommMediator`: `app.MapDidCommMediator("/didcomm").RequireRateLimiting("mediator")`. Fresh-DID floods saturate the replay budget (W-2), and entries stamped at the future bound live `Window + 2 × ClockSkew`.
- Use an allow-list or rate-limiting `IMediationPolicy`; grants never expire, and self-made grants fill `MaxGrants` / `MaxRecipients` and the store-wide queue bounds.
- A host clock stepping back more than `Window + ClockSkew` (6 min at defaults) refuses in-scope traffic until it catches up or restarts (logged).
- Size `Replay.MaxEntries` to accepted in-scope rate × (`Window + ClockSkew`), about 91 msg/s at defaults.
- Session sizing: a stalled live session holds up to `MaxPendingPushes` packed deliveries of ≤ `MaxDeliveryBytes`. Packed frames correspond to stored records, so they are bounded store-wide by about 16/9 × `MaxStoreBytes`.
- Relay-time and queued expiry use `ExpiresClockSkew`, never `ClockSkew`.
- Direct push is one attempt; a push-only recipient must also pick up (§15 Q1).
- `UseWebSockets()` must precede mapping.
- Every collaborator must be registered before `AddMediator` (TryAdd).

---

## 8. `MapDidCommMediator(pattern)`

**Routes.** `pattern` must start with `/`; `base = pattern.TrimEnd('/')`, so `"/"` gives `""`.
- `POST {pattern}`: public `MapDidCommEndpoint(pattern)`, unchanged.
- `GET {pattern}`: `MapDidCommWebSocketCore(endpoints, pattern, mediatorEndpoint: true)`.
- `GET {base}/oob`: `MapDidCommOobEndpoint(base + "/oob", new MediatorInvitationStore(holder))`.

POST and GET share a pattern; ASP.NET Core routes by method. The OOB GET cannot share the WebSocket GET, hence `/oob`.

The return value is an internal `CompositeEndpointConventionBuilder` forwarding `Add` / `Finally` to all three, so `.RequireRateLimiting` / `.RequireHost` apply to the whole mediator.

**Fail-fast at map time.** `endpoints.ServiceProvider.GetService<MediatorMarker>() is null` → `InvalidOperationException("MapDidCommMediator requires AddMediator()")`.

**Map-time Information line**, a path only: "Mediator endpoints: POST/GET {Pattern}, invitation GET {OobPath}?_oobid=mediator".

**`AllowSameSocketReplies`.** It is forced on for this WebSocket endpoint only, through the internal core overload. Global `DidCommReceiveOptions` are untouched, so a host's other `MapDidCommWebSocket` keeps FR-TRN-10 one-way.

**STOMP.** This endpoint always uses bare framing and ignores a global `UseStomp`. STOMP sessions can never push (`DidCommReceiveOptions.cs:44-56`), so pickup over WebSocket would be silent. A host that wants STOMP maps its own WebSocket endpoint elsewhere. Documented.

**Acknowledgement and admission are kept on every endpoint.**
- HTTP is the unchanged public method:
  - TCS created before dispatch (`DidCommEndpointRouteBuilderExtensions.cs:242-250`);
  - 6-argument `DispatchAsync` with a real acknowledgement and the session (`:273`);
  - `TrySetCanceled` on abort (`:297-301`) and on the post-handler 400 (`:309-311`);
  - `InboundAdmission.Evaluate` (`:264`).
- WebSocket runs the same `ReceiveLoopWithDispatchAsync` (`:626`) with `Task.CompletedTask` and admission. Only the options object differs.
- Tests below prove both.

**OOB invitation.**
- Built in `StartingAsync`, after FR-MED-07 passed, so only validated DIDs are advertised:
  `await client.PackPlaintextAsync(OutOfBand.CreateInvitation(from: routingDids[0], goal: "RequestMediate", goalCode: "request-mediate", accept: ["didcomm/v2"], id: <random>).Message)`.
- This is the Identus / Blocktrust mediator invitation shape. Those are #98's external mediators.
- It is stored in the holder. `MediatorInvitationStore.Retrieve(id)` returns it only for `id == InvitationOobId`; `Store` / `Remove` throw `NotSupportedException`.
- **URL discovery:** the operator publishes `OutOfBand.ToShortUrl($"{publicBase}{base}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId)`. The map-time log line gives the path.
- The id is a fixed public constant. It reveals only the public routing DID, which is in every recipient's DID document anyway.

---

## 9. Revoke and purge

**`MessagePickupQueue.RevokeAndPurgeAsync(peerDid, ct)`:**
1. `BareDid` check → `ArgumentException` without echoing the value.
2. `revoked = await mediation.RevokeAsync(peerDid)`.
3. `purged = await store.PurgeGrantAsync(peerDid)`.
4. Return `new GrantRevocation(revoked, purged)`.

Store faults propagate; it is an admin call, like `PurgeExpiredAsync`. It is idempotent: retrying after a failed purge revokes nothing and purges again.

**Race (L-057).** A forward admitted before the revoke can reach `DeliverAsync` and store a record *after* the purge. Close it inside `DeliverAsync`: after `EnqueueAsync` succeeds, call `FindGrantByRecipientDidAsync(recipientDid)` again.
- If it no longer names `peerDid`, call `AcknowledgeAsync(peerDid, [ourId])`, removing exactly the record we stored, and return `NotRegistered`. No live push.
- Interleavings:
  - enqueue before the purge → the purge removes it;
  - enqueue after the purge → the revoke happened earlier, so the re-check removes it.
- If the re-check throws, keep the record and return `Queued`: never lose; the age limit or expiry reclaims it.

The direct-push worker repeats the check before sending (§3).

**Residuals, documented:**
- A push or live delivery already in flight completes.
- If the same peer is granted mediation again *during* the call, records stored for the new grant before the purge runs are removed too. Deny the peer in `IMediationPolicy` before revoking.

---

## 10. Observability

**Meter `didcomm-dotnet`** (`DidCommDiagnostics.MeterName`).
- `ForwardMetrics` and `MediatorMetrics` take `IMeterFactory?` from DI. `WebApplication` registers it. Without one, each owns its own `new Meter(name)` and disposes it.
- Tests use a hand-rolled `IMeterFactory` (Meter `Scope`) to isolate measurements from parallel tests.

**Instruments.** All tags are low-cardinality and none carries a DID.
- **`didcomm.forward.outcomes`** — Counter<long>, unit `{forward}`, tag `didcomm.forward.outcome`. Exactly one outcome per forward, whichever stage ended it:
  - From `ForwardHandler`: `Dropped-Malformed`, `Dropped-NotEncrypted`, `Dropped-Expired` (inline and "expired while queued"), `Dropped-Unknown` (next not a DID / not registered), `Dropped-Saturated`, `Dropped-Unacknowledged`, `Dropped-Shutdown` (bulk), `Faulted` (screening fault; processor `Crypto`/`DidResolution` fault). Processor `Consistency`/`Malformed` faults count as `Dropped-Malformed`.
  - From the sink: `Delivered-Live`, `Queued`, `Dropped-Unknown`, `Dropped-Expired`, `Dropped-TooLarge`, `Dropped-Quota`, `Faulted`.
  - This must live in Core. The issue's `Dropped-Expired` / `Dropped-Unknown` mostly happen in the handler, before any sink, so a Mediator-only counter would miss the main drop paths (L-052).
- **`didcomm.mediator.direct_push.outcomes`** — tag `didcomm.push.outcome`: `Pushed`, `Push-Failed`, `Push-Skipped-Saturated`, `Push-Skipped-NoEndpoint`, `Push-Skipped-Scheme`, `Push-Skipped-Unregistered`. Kept separate so a pushed message is not double-counted.
- **`didcomm.mediator.queue.purged`** — Counter.
- **Observable instruments**, each only when the registered instance is the in-memory type, with try/catch in the callbacks:
  - `didcomm.replay.saturated` and `didcomm.replay.clock_behind_refusals` (ObservableCounters, from `InMemoryReplayGuard`);
  - `didcomm.return_route.dropped_notifications`;
  - ObservableGauges `didcomm.mediator.queue.messages` / `.bytes` (from `InMemoryMessageStore.MessageCount` / `TotalBytes`).

**Logs.**
- Categories: `MediatorForwardSink`, `DirectPushDispatcher`, `MediatorHostedService`.
- Rules:
  - message id via `MediatorLog.LogId`;
  - exception type names only, never the object or its message;
  - no DID, record id or endpoint;
  - drop and failure lines through `MediatorLog.RateLimited` per outcome: first and every 1000th at Warning (Error for `Faulted`), Debug otherwise;
  - successes at Debug.
- **`ForwardHandler`'s per-forward drop Warnings** (`:223-275, 363`) move to the same per-reason rate limit, with identical text. These are stranger-triggerable flood lines, and the change also brings the unknown and known inline paths closer in cost.

---

## 11. Threat model (L-031)

**Registration surface: who registers what, and when.**
- Only host code at startup registers `IRecipientEndpointDirectory`, the stores, the policy, the identity and the routing-key provider (TryAdd before `AddMediator`).
- Conflict checks refuse a foreign `IForwardSink` or an earlier `AddRoutingMediator` at registration. Startup refuses a sink registered later.
- In-process host code is trusted. Its boundary: the directory is called only off the request path and the pump, with type-only logs, and a throw means "skip".

**Who can call what.**
- Anyone can POST or WS-send forwards and fetch the OOB invitation.
- Only authcrypt peers that pass replay admission enrol or pick up (#93/#94).
- Forwards are relayed only for registered `next` values.
- `RevokeAndPurgeAsync` and `PurgeExpiredAsync` are host APIs.

**Quota exhaustion.**
- **Via delays:** bounded by the cap. Residual: unseen occupation for at most 1 h after the sender stops (owner-accepted).
- **Via ids:** 128-bit random ids cannot collide or be predicted; host sequential generators are unreachable.
- **Via the age limit:** it only removes. Residual: silent loss after 30 days offline (owner-accepted, documented).
- **Store-wide bounds via self-made grants:** policy guidance. Residual: availability for everyone (stated limit, as W-2).

**Forward sink and direct push as amplifier or SSRF lever.**
- The target comes only from the host directory.
- `SendPackedAsync` always runs the SSRF guard plus connect-time pinning.
- Only stored records are pushed, 1:1, after the 202 and off the pump.
- Concurrency, backlog, bytes and per-grant pending are bounded; each push has its own guarded timeout.
- Saturation means "stays queued".
- Residuals:
  - The HTTP transport's retries (up to 4 POSTs on 5xx).
  - The transport's global circuit breaker: a failing recipient endpoint can open it and stall all of the mediator's HTTP egress for `CircuitBreakerOpenDuration`. Messages stay queued.
  - Anyone can make the mediator relay blobs of at most `MaxAdmissiblePayloadBytes` to registered recipients' push endpoints, as they could post to those endpoints directly.
  - A host directory pointing at the mediator itself enables bounded onion re-entry (misconfiguration; each layer costs the sender bytes).

**Enrolment junk against store-wide bounds:** allow-list policy guidance. The grant bound is `mediate-deny`.

**Timing oracles on `next`.**
- The 202 is written before processor and sink.
- The inline lookup is constant-work (`MediationForwardRecipientRegistry`).
- Inline work differs only by the reservation versus a rate-limited log; this is measured (§12) against ratio < 3.0.
- Direct push and the re-check run after the 202, so they cannot affect response time.
- Residual: microsecond-scale differences, dominated by unpack (FR-MED-08 measured about 1.1×).

**Log injection.** Message ids are validated to unreserved URI characters at unpack and bounded to 128 characters by `LogId`. All logs use structured templates. No DID, endpoint or exception text is logged; a test checks this with `LeakingLogLines`.

**DI lifetimes and cycles.**
- Everything is a singleton.
- The sink, dispatcher and metrics take no registry, dispatcher, handler or `DidCommClient` at construction.
- `DidCommClient` and the directory are resolved lazily on push workers. The hosted service resolves in `StartingAsync`, not its constructor.
- The queue factory is already cycle-guarded.
- Tests: a directory that takes `ProtocolDispatcher` in its constructor works; a host `IForwardSink` makes `AddMediator` throw.

**The new package's endpoint composition.**
- The three routes, the same-socket switch scoped to the mediator socket, and STOMP disabled on it.
- Sessions open only for authcrypt + `return_route: all` + admitted messages (#91 rules, unchanged).
- The OOB GET discloses only the public routing DID.
- Conventions apply to all three routes.

**Revocation race.** Closed for the pipeline by the post-enqueue re-check. Residuals: in-flight deliveries, and the re-grant edge (§9).

**Clock.**
- Aging is judged by each caller's reading inside the store. A request read milliseconds before a sweep can deliver a just-aged record: a residual of reading skew only (L-060), never resurrection, because removal is by the lock holder.
- Settings are copied at construction everywhere (L-060).

**Adversarial checklist for the red-team pass (L-054..L-060):**
- every collaborator throwing on every path that reads it (directory, client resolution, re-check lookup, identity in `StartingAsync`, the observable callbacks);
- a parked `DeliverAsync` between lookup and enqueue against `RevokeAndPurgeAsync`;
- a gated slow endpoint while forwards for other recipients keep flowing;
- churn on push saturation;
- out-of-order clock readings around the age limit;
- options changed after validation.

---

## 12. Test plan

**Harness: `MediatorTestServer`**, in `tests/DidComm.InteropTests/Mediator/`, modelled on `ReturnRouteTestHost`:
- A TestServer `WebApplication`: `AddDidComm(UseNetDidResolver, UseSecretsResolver(mediator), Configure(Clock = TestClock), AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediatorDid) …))`, then `UseWebSockets`, `MapDidCommMediator("/")`, and an optional `/bob-inbox` recording callback endpoint.
- The mediator DID is minted with `DidCommServiceBuilder.ForEndpoint(new Uri(server.BaseAddress, "/"))`, so FR-MED-07 passes. `OutboundEndpointPolicy.AllowedHosts` gets the TestServer host.
- The mediator's HTTP transport uses a primary handler that routes by request host to the right TestServer handler. This is how two-mediator and direct-push tests reach each other; the pattern comes from `ReturnRouteClientHarness.cs:84-85`.
- Reused helpers: `TestPeer`, `TestClock`, `CapturingLoggerProvider`, `LeakingLogLines`, created-time stamping from the server clock, the enrolment helpers from `MessagePickupEndpointSupport` / `CoordinateMediationHttpTests`, and `ForwardHandler.FlushAsync` (internal).

**SpecActorRegistry mediator1/mediator2 are not used.** Their fixtures have no DIDCommMessaging service, so FR-MED-07 would refuse them at startup, and they share Bob's key bytes (`SpecActorRegistry.cs:31-35`). The tests mint did:peer mediators instead.

| Acceptance line | Test(s) |
|---|---|
| E2E over HTTP | `MediatorEndToEndHttpTests`. Bob, as peer P, enrols with hand-rolled `CoordinateMediation.CreateMediateRequest` (`return_route: all`, stamped). He mints did:peer B with `DidCommServiceBuilder.ForMediator(grant.RoutingDids[0])` and registers B with `CreateRecipientUpdate`. Alice runs `SendAsync(Forward: true)` with no override and resolves B. Bob sends `delivery-request` → one attachment that unpacks directly with B's keys (this proves one forward layer, FR-MED-07 / FR-ROUTE-04 Example 1). Ack → `status.message_count == 0`, and the `Queued` outcome count is 1. |
| Live WebSocket | `MediatorEndToEndWebSocketTests`. Global options have `AllowSameSocketReplies = false` **and** `UseStomp = true`, which proves the endpoint override. `live-delivery-change{true}` → `status{live_delivery:true}`. Alice sends → `delivery` push; the store holds 1 (leased) until the ack, then 0; `Delivered-Live` = 1. |
| DirectPush (sample 04 parity) | `MediatorDirectPushTests`. The directory maps P → `/bob-inbox`. The inbox receives the payload, Bob unpacks it, and the store goes to 0 (`Pushed`). Also: failing endpoint (500) → stays queued and picked up later; ws endpoint → `Push-Skipped-Scheme`; not-yet-due → not pushed; revoked between queue and push → `Push-Skipped-Unregistered`; saturation with a gated endpoint → `Push-Skipped-Saturated` and the message stays; a gated slow endpoint does not stop live/queue delivery for another recipient (L-059); shutdown with a push in flight loses nothing. |
| Two-mediator route | `MediatorTwoHopTests`. M2 enrols at M1 as peer did:m2 and registers did:m2; Bob enrols at M2. B's service is `ForMediator(did:m1).WithRoutingKeys("did:m2")`. Alice sends; M1 queues under did:m2. **(a) Via queue:** the test acts as M2's pickup client at M1 and POSTs each attachment to M2's endpoint; M2 queues for Bob; Bob picks up. **(b) Via push:** M1 has `DirectPush` and a directory mapping did:m2 → M2's URL. |
| Expiry | Dispatcher-level: a synthetic `UnpackResult` with past `expires_time` → `Dropped-Expired`, nothing queued, log has the id only. Over HTTP the same forward is refused at unpack by FR-API-05 (uniform 400, nothing queued); the existing `MediatorEndpointRelayTests.cs:174` pins this behaviour. Queued `expires_time = t0+60`, clock moved to t0+61 → `status` 0; `PurgeOnceAsync` → 1 removed; store 0. |
| Delay | E2E: a hand-built forward (`ForwardMessage.Create` + header `delay_milli: 500`, anoncrypted to the mediator) → 202. At t0+499 ms `status` is 0 and `delivery-request` → `status`; at t0+500 ms it is deliverable. Cap: with `MaxForwardDelay = 1 h`, 3 599 999 / 3 600 000 / 3 600 001 / `long.MaxValue` give `NotBefore` at +1 h−1 ms / +1 h / +1 h / +1 h (asserted by deliverability at exact clock points). Negative values: unit test of `ExtractDelay` with `|n| = 10 h`, cap 1 h, 1000 draws, all ≤ cap and **< 1 % exactly equal to the cap**. Clamp-after-draw would give about 90 % equal to the cap, so this distinguishes them. Also cap 0, `long.MinValue`, `Validate` range. |
| Age limit | `PickupMessageAgeTests` (PickupFixture clock) and `MediatorAgeLimitTests`. At age−1 tick: counted and deliverable. At age: not counted, not leased, purged by `PurgeExpiredAsync`. Store-full sweep frees aged records of another grant. Leased and passed-over records age out; a later ack is ignored. `null` = never. Saturating add near `MaxValue`. `MaxMessageAge <= MaxForwardDelay` is refused. |
| Unknown `next` | 202, store 0, no request reaches `/bob-inbox` even with DirectPush on, `Dropped-Unknown` = 1. **Timing:** `MediatorUnknownNextTests.Response_time_does_not_tell_known_from_unknown_next` uses the pattern of `InMemoryMediationStoreTests.cs:510-570` over HTTP: warm-up, interleaved batches of about 20 requests, flush between batches, medians, ratio < 3.0, and `MaxQueueMessages` raised so quota never changes the path. |
| Rewrap / extra keys | `RewrapMode = ReanoncryptToNext`: Bob unpacks the anoncrypted self-addressed forward, peels `next == B` and unpacks Alice's message (the approach of `AliceMediatorBobRoundTripTests.cs:63-99`). Extra keys: a test `IRecipientRoutingKeyProvider` returns the key of a DID whose secrets Bob holds; he peels the extra layer. Registering it together with rewrap → `ArgumentException`. |
| Acknowledgement ordering | The middlewares of `MediatorEndpointRelayTests.cs:385-399` around `MapDidCommMediator`. With `OnStarting` gated, the sink's internal `Accepted` counter is 0 and the store 0 until release, then 1. With abort-after-endpoint: 0 forever, after `FlushAsync`, and `Dropped-Unacknowledged` = 1. Replay: the same stamped `status-request` twice over the mediator's HTTP and WebSocket endpoints → the second is dropped. |
| Startup failures | `MediatorStartupTests`: `MaxReceiveBytes = 16 KiB` (`MaxDeliveryBytes` unsatisfiable) → `app.StartAsync()` throws `ArgumentException`. FR-MED-07 matrix: uri is a DID (`ForMediator(otherDid)`), no service, ftp scheme, unresolvable method, no held keyAgreement secret → `InvalidOperationException` naming the index and never the DID. A ws-scheme service passes. Routing-keys-on-own-service → Warning. Host identity with 0 DIDs fails. Foreign sink registered later fails. |
| Revoke and purge | Unit: `(true, 3)`, then `status` 0, then a new forward → `Dropped-Unknown`. **Parked race:** decorators park `DeliverAsync` between lookup and enqueue; revoke+purge; release → the record is removed by the re-check (`NotRegistered`), store 0. Re-grant variant keeps the message. Fault in the re-check → `Queued`. |
| Composition / DI | `AddMediatorDiTests`: sequence, conflicts, repeated calls, host `Configure<ReplayProtectionOptions>` before and after, a directory taking the dispatcher in its constructor resolves fine, `AddDidComm` without a host guard uses the composed values. |
| Metrics / logs | `TestMeterFactory`: per-outcome counts per path; `LeakingLogLines` empty across every e2e test; rate limit (first and every 1000th Warning). |
| Live harness | §12a. |

**Injected-clock technique.**
- Server `DidCommOptions.Clock = () => TestClock.Now`; peers stamp `created_time` from the server clock (`ReturnRouteTestHost.cs:234`).
- No real sleeps, except bounded condition polls for the pump; `FlushAsync` is preferred.
- Push timeouts use `ManualTimeProvider`.

**12a. Live-harness cell.** Keep the CLI stateless. Add one verb:

`InteropCli mediator-pickup --mediator <med.json> --recipient <rcpt.json> --in <envelope> --out <unpacked.json>`

1. Start an in-process Kestrel mediator on `127.0.0.1:0`: `AddMediator` + `MapDidCommMediator("/didcomm")`, the mediator's secrets, routing DID = med. `StartAsync` runs FR-MED-07.
2. A recipient client (`UseHttpTransport`, http allowed) enrols itself with `SendAsync(…, ReturnRoute: All, ServiceEndpointOverride)`: `mediate-request`, then `recipient-update add <rcpt DID>`.
3. A raw `HttpClient` POSTs the counterpart-built forward bytes (`application/didcomm-encrypted+json`); assert 202.
4. Poll `delivery-request` (≤ 10 s) → a single attachment → `UnpackAsync` → write the `unpack` verb's `{message, metadata}` JSON → ack → assert `status` 0 (exit 1 otherwise).

This exercises the real endpoint, handler, pump, sink, queue and pickup on the dotnet side, against a forward built by the counterpart library.

- **csproj:** add `FrameworkReference Microsoft.AspNetCore.App` and ProjectReferences to `DidComm.Mediator.AspNetCore` and `DidComm.Transports.Http` (L-012: it has no redundant `Microsoft.Extensions.*` references).
- **Each leg:** mint `"$INTEROP_CLI" mint --out dotnet-medsvc.json --endpoint https://mediator.invalid/didcomm`. FR-MED-07 checks the scheme only; the URL is never dialled.
- **New cell:** `"authcrypt via dotnet mediator (pickup)"`, inbound only.
  - python: `pack --mode authcrypt --to dotnet-base --route dotnet-medsvc.json` (the existing `wrap_in_forward` path, `interop_peer.py:377-382`);
  - then `mediator-pickup --mediator dotnet-medsvc.json --recipient dotnet-base.json`;
  - then python `assert`.
  - The jvm leg is identical, via `InteropPeer.java --route`.
- README: Routing row gains "through a running didcomm-dotnet mediator (pickup)".

---

## 13. Docs to change

**PRD:**
- §3.1: Mediator row drops "MapDidCommMediator / #95 decides" and moves `DidCommServiceBuilder` to the Core row; new row `DidComm.Mediator.AspNetCore` | `MapDidCommMediator` (HTTP + WebSocket with same-socket replies + OOB invitation) | `DidComm.Mediator`, `DidComm.AspNetCore`.
- FR-ROUTE-09: acceptance filled: dispatcher-level `Dropped-Expired`; HTTP 400 at unpack; queued expiry purge and `status`.
- FR-ROUTE-10: the cap, its default, and the clamp-before-draw rule as in §4; acceptance as in §12.
- FR-ROUTE-11: acceptance adds ack ordering and timing through `MapDidCommMediator`.
- FR-MED-07: startup validation as built (first entry, schemes, held keyAgreement key, resolution failure fails startup, routingKeys Warning); acceptance filled.
- FR-PICK-03: "until it expires, reaches the mailbox age limit (FR-PICK-10), or the host purges it".
- FR-PICK-04: ids come from the queue's own CSPRNG (16 bytes, 32 lowercase hex), never `IMessageIdGenerator`.
- FR-PICK-09: as built — queue first; HTTP 2xx semantics; ws(s) not pushed; due-only; detached and bounded; re-check; SSRF guard; one attempt.
- FR-PICK-10: the delay MUST is met by the FR-ROUTE-10 cap (unseen occupation ≤ cap); `MaxMessageAge` (30 days, `null` off, from `ReceivedAt`, mediator clock, expiry paths, silent loss); `RevokeAndPurgeAsync`.
- §12 Phase 7: tick the #95 items.

**Architecture doc:**
- New section "Mediator service: pipeline, AddMediator, MapDidCommMediator (Phase 7, #95)".
- Extend "Mediator routing core": delay cap, outcome metrics, rate-limited drops.
- Extend "Message Pickup 3.0": random ids, age fold, re-check, revoke-and-purge.
- Update the Packages and Tests and tools tables, and shrink "Phase 7: not built yet".

**CHANGELOG [Unreleased]:**
- **Added:** mediator service, new package `DidComm.Mediator.AspNetCore`, `DidCommServiceBuilder`, `MediatorIdentity`, `RevokeAndPurgeAsync`, meter.
- **Changed:**
  - random ids;
  - `MessagePickupQueue` constructor;
  - age limit, with the silent-loss note;
  - delay cap;
  - `ForwardProcessorOptions.MaxDelay`, "binary/API compatible; value equality now includes MaxDelay";
  - `ForwardHandler` drop logs rate-limited.
- **Security:** threat model and residuals.

**README:** package list and DX paragraph count (now 8 shipped assemblies). **RELEASING:** table row, "eight packages", the lockstep InternalsVisibleTo note, and the second new package id.

**XML docs:** every new public member, plus the remarks listed in §1. The `AddMediator` and `MediatorOptions` remarks carry the operations guidance.

---

## 14. Ordered steps and the three-engineer split

**Step 0 (one engineer, one commit): contract stubs**, compiling with `NotImplementedException` bodies:
- `ForwardProcessorOptions.MaxDelay`;
- internal `ForwardMetrics.Record` / `ForwardOutcomes`;
- `MessagePickupOptions.MaxMessageAge`;
- `MessagePickupQueue.RevokeAndPurgeAsync` / `GrantRevocation`;
- `MediatorOptions`;
- internal `MediatorMarker` / `MediatorInvitationHolder { string? PlaintextJson }`;
- internal `MapDidCommWebSocketCore`;
- `DidCommServiceBuilder` signatures;
- the plan file `tasks/todo<ts>-mediator-pipeline-95.md`.

**Engineer A — Core and queue.**
Files: `src/DidComm.Core/Protocols/Routing/*`, `src/DidComm.Core/Diagnostics/DidCommDiagnostics.cs`, `src/DidComm.Core/Resolution/DidCommServiceBuilder.cs`, `InMemoryReplayGuard.cs`, `DidCommBuilder.cs`, `src/DidComm.Mediator/Pickup/*`, `samples/_shared/PeerIdentityFactory.cs`, `tests/DidComm.Core.Tests/**`, `tests/DidComm.Mediator.Tests/Pickup/**`.
Commits:
- A1 delay cap;
- A2 forward outcome metrics + rate-limited drop logs (adjust tests that count Warnings: keep the text, and match Debug lines too);
- A3 `DidCommServiceBuilder` + `PeerIdentityFactory` lift;
- A4 random ids + seam migration;
- A5 age limit. Fix the #94 tests that assert `ExpiresAt == null` for "no expiry", per L-020: they pinned pre-decision behaviour. Set `MaxMessageAge = null` in fixtures that advance the clock past 30 days (forward-progress / pass-over suites);
- A6 post-enqueue re-check + `RevokeAndPurgeAsync`. Adjust tests that count `FindGrantByRecipientDidAsync` calls per `DeliverAsync`.

**Engineer B — pipeline.**
Files: `src/DidComm.Mediator/{MediatorOptions.cs, MediatorBuilderExtensions.cs, IRecipientEndpointDirectory.cs, DidComm.Mediator.csproj}`, `Coordination/MediatorIdentity.cs` (+ doc edits in `IMediatorIdentity.cs` / `CoordinateMediationOptions.cs`), `Pipeline/**`, `Directory.Packages.props`, `tests/DidComm.Mediator.Tests/Pipeline/**`.
Commits: B1 options + identity + registration; B2 sink; B3 direct push; B4 hosted service + validator + metrics.

**Engineer C — ASP.NET, integration, harness, docs.**
Files: `src/DidComm.AspNetCore/{DidCommEndpointRouteBuilderExtensions.cs, DidCommReceiveOptions.cs, .csproj}`, `src/DidComm.Mediator.AspNetCore/**`, `DidComm.sln`, `tests/DidComm.InteropTests/{.csproj, Mediator/**, DxCoverage/**}`, `tools/InteropCli/**`, `tools/interop-live/**`, all docs.
Commits: C1 internal WebSocket core + InternalsVisibleTo (with a regression test that the public overload is unchanged); C2 new package + `MapDidCommMediator` + gate list; C3 e2e suites (after B1–B4); C4 CLI verb + leg cells; C5 docs + allowlists.

**Single owners for shared files:**
- C owns the allowlists, CHANGELOG, PRD and architecture doc. A and B hand over their member lists and notes.
- B owns `Directory.Packages.props`.
- A owns `DidCommBuilder.cs`.

**Then:** QA and adversarial passes (AGENTS.md; §11 checklist), fixes, review.

**Every item is verified with:**
- `dotnet build DidComm.sln -c Release /warnaserror`
- `dotnet test DidComm.sln -c Release --no-build`
- `dotnet pack DidComm.sln -c Release --no-build`
- both live legs, run locally.

---

## 15. Scope, risk and open decisions

**Size.** Roughly 2–2.5k lines of source plus about 5–6k of tests, harness and docs. That is reviewable as one PR only commit by commit.

**I recommend two sequential PRs** (process call for the owner; either works):
- **95-1, "forward core and queue hardening":** A1–A6 plus their PRD rows (FR-ROUTE-10, FR-PICK-03/04/10). These are self-contained changes to existing #90/#94 behaviour that reviewers should judge in isolation.
- **95-2, "mediator service":** B, C. It closes #95.

**Risks:**
- #94 tests that assume `ExpiresAt == null` or long clock advances (A5).
- Tests that count lookups (A6).
- Rate-limited handler logs breaking Warning-count tests (A2).
- Route matching of `"/"` vs `"/x"` patterns — test both.
- `WebApplication` hosted-service ordering, which is why the checks are in `StartingAsync`.
- Allowlist re-keying of the `MessagePickupQueue` constructor.

**Decisions I made** (reasons given above):
- the cap on `ForwardProcessorOptions`, Core default unbounded;
- age folded into `ExpiresAt`;
- random hex ids via an internal seam;
- projection-based options;
- `AddBuiltInProtocols` inside `AddMediator`;
- the meter in Core;
- detached bounded direct push, ws(s) excluded;
- fail-closed FR-MED-07 on resolution failure;
- InternalsVisibleTo over a public overload;
- the mediator WebSocket endpoint ignores `UseStomp`;
- a fixed `InvitationOobId`;
- `RevokeAndPurgeAsync` on `MessagePickupQueue` plus the post-enqueue re-check.

**Genuine owner-level questions:**
1. **What direct push guarantees** (FR-ROUTE-08 SHOULD "retry later").
   - As designed, each message gets one push attempt. Failed, skipped and not-yet-due messages wait in the pickup queue.
   - A push-only recipient (sample-04 style) that never picks up therefore accumulates leftovers until the age limit. After 100 of them its quota is full and new forwards are dropped silently.
   - Options:
     - (a) keep one attempt and document "push-only recipients must also pick up";
     - (b) add a retry drain: on each purge tick, lease a bounded batch of each recently failed grant's records under a mediator-owned lease, push, then acknowledge or release, using existing store APIs, with a bounded set of grants to retry;
     - (c) push first, without storing.
   - Recommendation: (a) in #95 plus a follow-up issue for (b). (c) breaks "never lose an accepted message".
2. **Age-limit semantics (confirm).** The owner anticipated "the store contract change".
   - My design changes no `IMessageStore` signature: the limit is fixed on each record at admission via `ExpiresAt`.
   - Consequence: lowering `MaxMessageAge` and restarting on a persistent store does not shorten records already stored.
   - A dynamic limit would need the store to take the age on every time-dependent call, a real contract change for custom stores.
   - Recommendation: keep the fold.
3. **PR split**, as above.

### Critical Files for Implementation
- /Users/moises/Projects/didcomm-dotnet/src/DidComm.Core/Protocols/Routing/ForwardHandler.cs
- /Users/moises/Projects/didcomm-dotnet/src/DidComm.Mediator/Pickup/PickupCoordinator.cs
- /Users/moises/Projects/didcomm-dotnet/src/DidComm.Extensions.DependencyInjection/DidCommBuilder.cs
- /Users/moises/Projects/didcomm-dotnet/src/DidComm.AspNetCore/DidCommEndpointRouteBuilderExtensions.cs
- /Users/moises/Projects/didcomm-dotnet/src/DidComm.Mediator/Coordination/CoordinateMediationBuilderExtensions.cs (pattern for the new `MediatorBuilderExtensions.cs` / `Pipeline/*`)