# todo20261114-091033 — Recipient-side client: `MediationClient`, `PickupClient`, `DidCommServiceBuilder.ForMediation` (#96)

Issue: #96 (epic #88; after #89 PR #101 … #95 PR #109 merge `f2efeb6`, then the owner's review-record commit `61f7898`). Branch: `feat/mediator-client-96` in worktree `.claude/worktrees/orch-96` (L-019: re-verify the branch before every commit).
Normative sources: DIDComm v2.1 §Routing (Rewrapping), https://didcomm.org/coordinate-mediation/3.0/, https://didcomm.org/coordinate-mediation/2.0/, https://didcomm.org/messagepickup/3.0/. PRD rows (the target): FR-MED-09, FR-MED-10 (client side), FR-PICK-11, FR-ROUTE-06 (receiver side), FR-RR-09 (evidence), §3.1, §12 Phase 7. D1–D7 unchanged.
Binding refinements: the "#96" paragraphs of the "after #89 / #91 / #92 / #93 / #94 / #105 / #95" planning updates on #88 and the owner decisions there. In short: no manual timestamps (FR-RR-09, asserted); correlate on `thid` with a type + mediator-DID fallback; `Returned` proves identity only (correlate, stay idempotent); over-cap / stalled / unreadable HTTP reply = `TransportException` status 200; `SetLiveDeliveryAsync` refused over HTTP before sending; the client must not need ASP.NET Core; WS needs `UseInboundDispatch()` + `ReceiveInbound`; session handlers never await another inbound on the same session; "no answer" = timeout, retry idempotently (a mediator > 60 s ahead has its WS answers dropped); clocks must agree within a minute by default; delivery is at-least-once, deduplicate by inner message id; queue ids are opaque 32-hex strings; poll in live mode (a delayed message is never pushed when due); open rewrapped deliveries (FR-ROUTE-06 receiver side); `ForMediation` is a static extension around Core's `ForMediator(grant.RoutingDids[0])`; routing keys bare DID or DID + fragment, at most 8 forward layers; our mediator's invitation is at `{base}/oob?_oobid=mediator`.
The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`. Interop suite runs with `git submodule update --init` (fixtures ≈ 150 files).

## Orchestrator reconciliation of the architect pass (my calls, each with a reason)

The architect's design follows below verbatim except where these notes override it.

- R-1. **`IInboundCorrelator` / `InboundMessageSnapshot` stay internal** (architect D-1, agreed). Reason: a public inline hook would see every decrypted inbound and could stall the receive path (L-031, L-037); promotion cascades through the snapshot's binding types (L-006); `DidComm.Mediator` already has `InternalsVisibleTo` from Core.
- R-2. **Live acknowledgement at enumeration end — overrides D-16.** An item is acknowledged when the consumer asks for the next one (the next `MoveNextAsync`). When the enumeration ends (break, return, an exception out of the loop body, cancellation, dispose) the last yielded item is **not** acknowledged and **not** recorded in the duplicate filter: it comes back later (at-least-once). A consumer that wants it gone before stopping calls `PickupClient.AckAsync(item.Id)`. Reason: `await foreach` cannot tell a break from a failed loop body, and an acknowledgement is irreversible (L-056: destroy only on a fact, not a guess about whether the app handled it). The issue's "acks each item after the consumer's `MoveNextAsync` returns" holds: no item is ever acknowledged before it was handed over.
- R-3. **Dispatcher log noise — decided in this PR (L-046), overrides §11 owner item 7.** `IInboundCorrelator.OnInbound` (internal) returns `bool` = "claimed by a pending request or a live stream". When at least one correlator claimed a message and no handler is registered for it, the dispatcher's "No protocol handler registered" line is written at Debug instead of Information. `DiscoverFeaturesClient`'s explicit implementation returns whether it completed a query. Reason: without it a recipient-only host writes one Information line per mediator answer and push, a log flood this diff causes.
- R-4. **§14.2 snippets CC–FF stay with #97** (overrides §11 owner item 8). Reason: the binding #88 note after #89 assigns them to #97.
- R-5. **`CoordinateMediationVersion` pin option kept** (§11 owner item 2). Reason: Identus has no Discover Features (#88 body), so the #98 gate needs a way to skip negotiation; a DF timeout still fails (no guessing), per FR-MED-09's "negotiates through Discover Features first".
- R-6. **No `FetchAsync` opt-out of acknowledgement** (§11 owner item 3). Reason: FR-PICK-11 and the issue say fetch acknowledges what unpacked; an opt-out is surface nobody asked for. A caller who needs at-least-once to the application uses `ReceiveLiveAsync` or acknowledges itself through `AckAsync`.
- R-7. **HTTP "no answer" → immediate `TimeoutException` naming the request id** (D-8, agreed). Reason: over HTTP the response is the only channel for the answer (FR-RR-04: at most one message); the issue's acceptance requires `TimeoutException` with the request id for "no answer".
- R-8. **Duplicate filter on by default** (D-15, agreed).
- R-9. **Packaging (owner-level, not blocking).** The issue and PRD §3.1 place the client in `DidComm.Mediator` (namespace `DidComm.Mediator.Client`), and the PRD's constraint (no ASP.NET Core) holds: the package references `Microsoft.Extensions.Hosting.Abstractions` only. So this is built as the issue says. The architect's point — a wallet then also carries the mediator roles; a split is cheapest before `DidComm.Mediator` is first published — goes to the owner in the #88 update as an awareness item with options, not as a blocker.
- R-10. **Thid-less `delivery` over WebSocket goes only to a live stream** (§4.2, agreed). Interop residual for #98: a third-party mediator that answers `delivery-request` without `thid` works over HTTP (the exchange binds the body) but not over WebSocket; observe in the live gate.

## Checklist

### A0. Contract stubs (engineer A0, merged before A/B/C branch)
- [ ] A0.1 Every §2 public type/member with XML docs and `NotImplementedException` bodies; internal `IMediatorExchange`, `MediatorRoute`, `MediatorExchangeSpec`, `LiveRegistration`, `DeliveredOutcome`, a stub `MediatorClientChannel`; Core internal signatures (`Pthid` / `Verified` on the snapshot implemented outright; `OnInbound` → `bool` per R-3).
- [ ] A0.2 C# 14 `extension(DidCommServiceBuilder)` for `ForMediation` proven under `/warnaserror` and the DX scanner (or the fallback, recorded here).
- [ ] A0.3 `TODO(#97)` entries in both allowlists (ids from the gate's failure output); counts recorded.
- [ ] A0.4 `tests/DidComm.InteropTests/Mediator/Client/MediatorClientHarness.cs` (HTTP or WS client container against `MediatorTestServer`).
- [ ] A0.5 Build / test / pack green.

### A. Core + channel + DI (engineer A)
- [ ] A1 `InboundMessageSnapshot` `Pthid`/`Verified`; `ReturnRouteEligibility` snapshot overload + parity test.
- [ ] A2 `DidCommClient.ResolveDirectEndpointAsync` + `UnpackDeliveredAsync` (rewrap opening, ≤ 8 layers); `ForwardProcessor.ExtractAttachmentBytes` internal.
- [ ] A3 `MediatorClientChannel` (exchange, matcher table §4.2, pending map, guarded deadline timer, HTTP no-answer, dispose).
- [ ] A4 `DuplicateFilter`, `DeliveredMessageUnpacker` (timeout, concurrency 4, abandoned ≤ 16).
- [ ] A5 `MediatorClientOptions` + `Validate`, `MediationProtocolException`, `AddMediatorClient()`; dispatcher Debug downgrade (R-3).

### B. MediationClient, CM 2.0, ForMediation (engineer B)
- [ ] B1 Negotiation (DF with `ReturnRoute: All`, cache, pin, `MediationNotSupportedException`).
- [ ] B2 CM 3.0 + internal CM 2.0 codec; fixtures `coordinate-mediation-2.0/*.json`; answer validation (§4.5); page walker.
- [ ] B3 `ForMediation`; fake CM 2.0 mediator; end-to-end enrolment tests (HTTP, WS, deny, 2.0, neither).

### C. PickupClient + live (engineer C)
- [ ] C1 Status / fetch (per-attachment reading, `limit` bound, filter, dedupe, commit point, ack) / ack chunks / live toggle.
- [ ] C2 `LiveDeliveryStream` (cycle, drain, push buffer, ack-on-next per R-2, backoff, live-off at end).
- [ ] C3 End-to-end: HTTP and WS acceptance, partial unpack, delayed message in live mode, rewrap, mediator + recipient in one host, clock skew.

### D. Verification and hardening (orchestrator)
- [ ] D1 Integrate A, B, C; full build/test/pack green.
- [ ] D2 Two independent QA passes (permutation list from the brief); fixes; per-component defect tally (L-063).
- [ ] D3 One or more independent adversarial passes; fixes; break-it on non-trivial fixes.
- [ ] D4 Docs: CHANGELOG, PRD (FR-MED-09, FR-MED-10, FR-PICK-11, FR-ROUTE-06, FR-RR-09 evidence, §12), architecture doc (new #96 section, Packages row, Tests list, "still to build"), XML docs; DX totals reported.
- [ ] D5 Negative controls for silent gates (DX gate, interop fixtures, pack/package validation).
- [ ] D6 PR opened; CI checked once.

## Defect tally (L-063)

| Component | Pass | Defects confirmed | Fixed in |
|---|---|---|---|

## Progress log

- (empty)

---

# Architect design (Plan agent, 2026-11-14), reconciled above


Worktree read: `/Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96` (branch `feat/mediator-client-96`, at `origin/main` `61f7898`). Binding inputs used: issue #96 body; every "#96" paragraph of the #88 planning updates (after #89, #91, #92, #93, #94, #105, #95) and the owner decisions; PRD §3.1, §8/8a/8b/8c, §12 Phase 7, §14.2. Where the issue body and #88 disagree, #88 wins (noted in §11).

---

#### 0. What is already there (facts the design relies on)

- `IInboundCorrelator` is internal (`src/DidComm.Core/Protocols/IInboundCorrelator.cs:21`). The dispatcher calls correlators inline and guarded, before any handler (`ProtocolDispatcher.cs:234-240`, `:284-300`). Only the internal ctor wires them (`:96-110`). DI passes `sp.GetServices<IInboundCorrelator>()` (`DidCommBuilder.cs:77-83`). The DF client is registered as a correlator (`DidCommBuilder.cs:129-132`).
- `InboundMessageSnapshot` is internal. It has `Id/Type/Thid/From/To` and the trust fields, but **no `Pthid`** and no "verified vs fallback" flag (`InboundMessageSnapshot.cs:79-94`; fallback path `:149-185`). Every `UnpackAsync` registers a verified snapshot (`EnvelopeReader.cs:296`), so `SendResult.Returned.Message` has one.
- `DidComm.Core` already grants `InternalsVisibleTo DidComm.Mediator` and `DidComm.Mediator.Tests` (`DidComm.Core.csproj:42-43`). The DI assembly grants nothing to Mediator, so `DependencyCycleGuard` is not reachable from it.
- `DiscoverFeaturesClient` is released in 1.5.1 (public ctor, `QueryFeaturesAsync`). It **cannot** negotiate for us: its send delegate drops `SendResult` (`DiscoverFeaturesClient.cs:45`, `:187-193`), it sets no `ReturnRoute`, and its timeout is a `CancelAfter`-driven token handed to host code (`:174`, the class of the #92 follow-up 1).
- `SendAsync`: one service-entry read, `entries[0]` (`MediatorEndpointExpander.cs:88`), routed return-route refused (`DidCommClient.cs:421-427`). Affinity identities are set only with 0 forward layers and `From` (`:699-712`). `Returned` is identity-checked only (`:729-731`, `:781-816`). `DidCommClient` is `sealed partial` (`:33`) and already exposes `internal Options` (`:887`) and `RoutedReturnRouteRefusal` (`:892`).
- With `ServiceEndpointOverride`, the pre-send SSRF guard is skipped (`DidCommClient.cs:669-688`). Both transports still pin at connect time (`HttpDidCommBuilderExtensions.cs:48-57`, `WebSocketDidCommTransport.cs:212-215`).
- The WS inbound path: `InboundDispatchConsumer` does unpack → `ReturnRouteIdentity` → `InboundReplayGate` → `DispatchAsync(session: null)`, one frame at a time per socket (`InboundDispatchConsumer.cs:73-156`; contract `IInboundFrameConsumer.cs:12-22`).
- Rewrap: `RewrapToNextAsync` builds `forward{to = next = nextDid}` anoncrypted to `nextDid` (`ForwardProcessor.cs:414-448`). The mediator opens such layers with `PeelAsync` + `UnpackAnoncryptedPlaintextAsync` (`ForwardProcessor.cs:269-284`, `DidCommClient.cs:935`). It allows at most `MaxSelfAddressedLayers = 8` (`ForwardHandler.cs:159`, `:690-799`), and `IsSelfAddressed` is `next` subject == decrypting subject (`:803-805`). `ExtractAttachmentBytes` is private (`ForwardProcessor.cs:307`). **Nothing on the recipient path opens a rewrapped delivery today.**
- Wire helpers already exist: `CoordinateMediation.Create*/TryParse*`, where `TryParseGrant` accepts a string or an array (`CoordinateMediation.cs:225-232`). `MediationBodies.ReadRecipients(listName, …)` already takes `keys` (`MediationBodies.cs:219`). `MessagePickup.Create*/TryParse*` exist, and `TryParseDelivery` is strict: one bad attachment refuses the whole delivery (`MessagePickup.cs:270-279`, `PickupBodies.cs:222-240`). Live framing is a `delivery` with no `thid` (`MessagePickupHandler.cs:128-131`). The problem report uses `pthid` (`ProblemReport.cs:63-102`). Re-sending `live-delivery-change{true}` keeps the first arrival mark and wakes the grant (`PickupCoordinator.cs:303-337`).
- The mediator handlers own their PIURIs (last registration wins, `ProtocolHandlerRegistry.cs:27-34`) and ignore response types (`CoordinateMediationHandler.cs:123`, `MessagePickupHandler.cs:144`).
- `DidCommServiceBuilder` is unreleased (absent at `v1.5.1`). `DidComm.Mediator` is unpublished (`EnablePackageValidation=false`). `SendResult.Returned` is unreleased. **Nothing in this design touches a released public member.**
- The DX scanner skips types whose name starts with `<` (`PublicApiSurface.cs:392-394`). `DidComm.Mediator` is in the shipped list (`CoverageComputation.cs:14-24`).

---

#### 1. Decisions (one-line reasons)

| # | Decision | Reason |
|---|---|---|
| D-1 | `IInboundCorrelator` and `InboundMessageSnapshot` **stay internal**. The correlator is an internal class in `DidComm.Mediator` reached through the existing IVT. The public clients do not implement it. | A public inline hook would see every decrypted inbound and could stall the receive path (L-031/L-037). Promotion would cascade (L-006: snapshot, bindings, addressing). The only consumer is library code, and the IVT exists. |
| D-2 | One internal **`MediatorClientChannel`** per container (correlator, bounded pending map, matcher, route resolution, live-stream registry, duplicate filter, unpack limiter). `MediationClient` and `PickupClient` are thin facades over it, through an internal interface `IMediatorExchange`. | Gives one pending map, one bound, one correlator registration and one identity rule. Both clients share the WS session socket S(E,L,M), so they must share correlation. The interface lets B and C work in parallel against a fake. |
| D-3 | **Route resolved once per operation** by a new Core internal `DidCommClient.ResolveDirectEndpointAsync(peerDid)`. It mirrors SendAsync's direct return-route selection: `entries[0]`, forward layers refused, queue URI refused, absolute URI, `OutboundEndpointGuard`. The client then sends with `ServiceEndpointOverride` = that URI. A caller-supplied override is trusted and not guarded, as in `SendOptions`. | The WS/HTTP decision and the send use one read of one entry, selected by the same rule (L-041/L-039). The guard skipped by the override path is applied by the same helper. |
| D-4 | WS routes (`ws`/`wss`) are refused with `InvalidOperationException` before sending when the channel cannot receive (no correlator registration, i.e. non-DI; or no `IInboundFrameConsumer` registered). The message names `UseWebSocketTransport(o => o.ReceiveInbound = true)` + `UseInboundDispatch()`. | Turns a silent timeout into a clear error. Checked with `IServiceProviderIsService`, so nothing is resolved. |
| D-5 | Every request is sent with `ReturnRoute = All`, built with `UuidV4MessageIdGenerator.Instance`, and never stamped by hand. | FR-MED-03/FR-PICK-01 need `all` on every HTTP POST, and on WS it is harmless (FR-RR-05: mode unchanged). FR-RR-09 stamping is relied on and asserted. Request ids never come from a host's (possibly sequential) generator. |
| D-6 | Correlation is **one matcher** `Offer(snapshot, boundTo?)`, used for both the WS-dispatched snapshot (unbound) and the HTTP `Returned` snapshot (bound to its request). §4.2 has the rules. | Same identity, `thid` and type rule for both transports. The only difference is the HTTP exchange binding a thid-less answer. |
| D-7 | **Identity before lookup**, comparisons combined with non-short-circuit `&`. The snapshot must be `Verified`, i.e. not a hand-built fallback. | A stranger's message never touches the pending map (no `thid` timing signal, and a forgery cannot claim or cancel anything). Fallback snapshots carry caller claims (L-040). |
| D-8 | Over HTTP, a response that carries no answer for the request (202, identity-refused, or not correlating) fails the request **at once** with `TimeoutException` naming the request id. | HTTP returns at most one message (FR-RR-04), so waiting is pointless. One exception type means "no answer, retry idempotently" on both transports. |
| D-9 | `mediate-deny` → `RequestMediationAsync` returns `null`. A grant returns the existing `MediationGrant(PeerDid: from, RoutingDids)`. | "Deny is a result" with zero new surface. `ForMediation` takes the same record. |
| D-10 | CM version: `MediatorClientOptions.CoordinateMediationVersion` = `Negotiate` (default) / `V3` / `V2`. Negotiate = a DF query (`ReturnRoute: All`) whose answer is cached per mediator DID subject: at most 64 entries, TTL 1 h, no insert when full. | PRD needs negotiation. The pin is needed because Identus has no Discover Features (#88 body). The cache saves a round trip per call and is bounded. |
| D-11 | "Neither version" → `MediationNotSupportedException : MediationProtocolException` with a sanitized `DisclosedProtocols` property. The message text quotes no wire value. | Reconciles "name the disclosed features" with D7's no-echo rule: the features are in a property, not the text. |
| D-12 | CM 2.0 is an **internal** codec `Coordination/CoordinateMediation20` with client-direction builders and parsers only. | Does not pre-empt #99, which adds the mediator-direction pieces and decides whether to make it public. |
| D-13 | Rewrap opening is a **Core internal** `DidCommClient.UnpackDeliveredAsync`. It uses the mediator's acceptance rule (anoncrypt-of-plaintext forward, one attachment, `next` subject == decrypting subject) and opens at most 8 layers. | One shape rule beside the mediator's (L-041). `UnpackAsync` (released) is not changed, and #110 can reuse it. |
| D-14 | `FetchAsync` acknowledges what unpacked, plus duplicates, **inside the call** (per FR-PICK-11). The last cancellation point is before the acknowledgement. Ack failures do not throw. | An unpacked message is never lost by throwing after the ack was sent. A failed ack comes back and is filtered as a duplicate. |
| D-15 | Duplicate key = SHA-256 truncated to 128 bits. Authenticated messages: over (authenticated sender DID subject, decrypting DID subject, inner message id). Anonymous messages: over the innermost packed bytes. Bounded FIFO, `DedupeCapacity` (default 4096; 0 = off). An entry is recorded only at the **commit point** (FetchAsync: right before acks; live: when the item is confirmed taken). | Message ids are unique per sender, so a stranger cannot suppress someone else's message. Only fixed-size hashes are retained (L-062). Recording at commit keeps at-least-once. |
| D-16 | **Overridden by R-2.** `ReceiveLiveAsync` acknowledges an item when the consumer asks for the next one, or when the enumeration ends (break, return, exception out of the loop body, cancellation). Buffered or drained items that were never yielded are never acknowledged. | "Taken" semantics; a process crash keeps at-least-once. `await foreach` cannot tell break from exception, so ending the enumeration counts as taken (owner-flagged, §11). |
| D-17 | Live stream cycle = `live-delivery-change{true}` → `status`, **then** drain (`delivery-request` until a `status`). It runs on start and every `LivePollInterval` (default 1 min), and after a stream send failure with backoff 1 s → 30 s. | Respects the #95 arrival-mark rule (nothing falls between drain and mark). It is idempotent, self-heals after a reconnect, and reaches delayed messages, which are never pushed when due (FR-PICK-08). |
| D-18 | Pushes reach the stream through a bounded `Channel` (8, drop-newest, never blocks the receive loop). A dropped push is not acknowledged. | The correlator must not wait. A dropped push stays leased to the session and comes back after close or lease timeout, then the next cycle drains it: at-least-once safe. |
| D-19 | Per-attachment unpack: one deadline (`UnpackTimeout`, default 30 s) covering all layers; at most 4 concurrent per fetch; at most 16 abandoned unpacks still running, beyond which new unpacks fail fast as failures. The live stream unpacks sequentially. | The inner sender controls unpack latency (L-061): the caller's own await pays it, bounded. Abandoned work is bounded (L-059). |
| D-20 | `recipientDid` filter is enforced: an unpacked message whose innermost decrypting DID subject ≠ the filter is a failure (left queued). | Prevents cross-tenant delivery inside a multi-tenant recipient host when the mediator mislabels. |
| D-21 | `ForMediation` is a C# 14 static extension member, in `DidComm.Mediator.Client.MediationServiceBuilderExtensions`. | Gives `DidCommServiceBuilder.ForMediation(grant)` as #88 asks. It compiles to a plain static implementation method, which is the DX-gate member (§6.4). |
| D-22 | No new metrics. | Minimal surface. Logs are enough for a client; a later issue can add a meter. |

---

#### 2. Public API (namespace `DidComm.Mediator.Client`, assembly `DidComm.Mediator`)

All result types are sealed classes with **internal** constructors: fewer DX entries and no fabricated results. Exceptions also have internal constructors.

```csharp
public enum CoordinateMediationVersion { Negotiate = 0, V3 = 1, V2 = 2 }

public sealed class MediatorClientOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);   // (0, 10 min]; one exchange: send + answer (route resolution counts toward the first exchange)
    public int MaxPendingRequests { get; set; } = 64;                           // [1, 4096]; over it → InvalidOperationException before sending
    public CoordinateMediationVersion CoordinateMediationVersion { get; set; } = CoordinateMediationVersion.Negotiate; // defined value
    public int DedupeCapacity { get; set; } = 4096;                             // [0, 1_000_000]; 0 = off
    public TimeSpan LivePollInterval { get; set; } = TimeSpan.FromMinutes(1);   // [1 s, 24 h]; keep ≤ the mediator's delay cap (1 h by default)
    public TimeSpan UnpackTimeout { get; set; } = TimeSpan.FromSeconds(30);     // (0, 10 min]; per delivered attachment, every layer
    public void Validate();                                                     // ArgumentOutOfRangeException naming the property and rule
}
// Copied and validated once when the channel is built (L-060).

public sealed class MediationClient
{
    public MediationClient(DidCommClient client, MediatorClientOptions? options = null, ILogger<MediationClient>? logger = null); // non-DI: HTTP routes only (D-4)
    // internal MediationClient(IMediatorExchange exchange, ILogger<MediationClient>? logger)  — DI

    public Task<MediationGrant?> RequestMediationAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public Task<IReadOnlyList<RecipientUpdateResult>> UpdateRecipientsAsync(string mediatorDid, string from, IReadOnlyList<RecipientUpdate> updates, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public Task<RecipientQueryResult> QueryRecipientsAsync(string mediatorDid, string from, RecipientQueryPaging? paginate = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public IAsyncEnumerable<string> QueryAllRecipientsAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default); // [EnumeratorCancellation]
}

public sealed class RecipientQueryResult
{
    public IReadOnlyList<string> Dids { get; }
    public RecipientPagination? Pagination { get; }
}

public sealed class PickupClient
{
    public PickupClient(DidCommClient client, MediatorClientOptions? options = null, ILogger<PickupClient>? logger = null);       // non-DI: HTTP only
    public Task<PickupStatus> GetStatusAsync(string mediatorDid, string from, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public Task<PickupBatch> FetchAsync(string mediatorDid, string from, int limit, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public Task<PickupStatus> AckAsync(string mediatorDid, string from, IReadOnlyList<string> ids, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public Task<PickupStatus> SetLiveDeliveryAsync(string mediatorDid, string from, bool liveDelivery, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
    public IAsyncEnumerable<PickupItem> ReceiveLiveAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default);
}

public sealed class PickupBatch
{
    public IReadOnlyList<PickupItem> Items { get; } // delivery order; successes (acknowledged) and failures (left queued); duplicates removed (acknowledged)
    public bool Drained { get; }                    // the mediator had nothing deliverable for this request: a `status` answer, or a delivery without attachments
    public PickupStatus? Status { get; }            // the `status` when it answered with one; null otherwise
}

public sealed class PickupItem
{
    public string Id { get; }              // the mediator's attachment id: opaque, never parsed (unreserved chars, ≤ 2048)
    public UnpackResult? Unpacked { get; } // innermost message (rewrap layers opened); null when it did not unpack
    public Exception? Error { get; }       // non-null exactly when Unpacked is null; the item was not acknowledged
}

public class MediationProtocolException : DidCommException
{
    public string RequestId { get; }       // our own request id (a UUID we minted) — safe to echo
    public string? ProblemCode { get; }    // body.code of an in-thread problem report, only if ≤ 128 chars of [a-z0-9._-] and ProblemCode.TryParse; else null
}
public sealed class MediationNotSupportedException : MediationProtocolException
{
    public IReadOnlyList<string> DisclosedProtocols { get; } // ≤ 32 distinct entries, each ≤ 256 chars of 0x21–0x7E and PIURI-parseable
}

public static class MediationServiceBuilderExtensions
{
    extension(DidCommServiceBuilder)
    {
        public static DidCommServiceBuilder ForMediation(MediationGrant grant); // ArgumentNullException; ArgumentException when RoutingDids is null/empty;
                                                                               // otherwise DidCommServiceBuilder.ForMediator(grant.RoutingDids[0]) (its own bare-DID check)
    }
}

public static class MediatorClientBuilderExtensions
{
    public static DidCommBuilder AddMediatorClient(this DidCommBuilder builder, Action<MediatorClientOptions>? configure = null);
}
```

**Argument validation** happens first, before any I/O (L-008), and never quotes a value:
- `mediatorDid`, `from`: `BareDid.IsBareDid`, otherwise `ArgumentException`.
- `updates`: 1..1024 entries, each a bare-DID `recipient_did` with a defined action. The 1024 bound is the mediator's `MaxUpdatesPerMessage` default; it is one message, with no client-side chunking or partial-failure semantics. The exception names the index.
- `paginate`: `Limit` 0..1000, `Offset` ≥ 0.
- `limit`: 1..100.
- `recipientDid`: bare DID or null.
- `ids`: 1..10 000 entries, each non-empty, ≤ 2048 and `UnreservedUriChars.IsUnreserved`; sent in chunks of 100 (the mediator's `MaxAckIds` default), returning the last `status`.
- `serviceEndpointOverride`: absolute URI without userinfo.

**Exceptions** (FR-API-07):
- `ArgumentException` / `ArgumentOutOfRangeException`: arguments.
- `InvalidOperationException`: a WS route that cannot receive; `SetLiveDeliveryAsync` or `ReceiveLiveAsync` on a non-WS route, before anything is sent; pending bound; a second live stream for the same pair; a routed mediator; no resolver.
- `TimeoutException`: no answer, naming the request id and the message name, never a DID.
- `MediationProtocolException`: an in-thread problem report; an answer that is malformed (inner `MalformedMessageException`) or inconsistent (§4.5).
- `MediationNotSupportedException`: see D-11.
- `TransportException`: propagated as is. Over HTTP with status 200 it means "accepted, reply over the cap, stalled or unreadable" (FR-RR-07); retry idempotently.
- Pack-stage exceptions of `SendAsync` (`DidResolutionException`, `UnsupportedDidMethodException`, `SecretNotFoundException`) propagate unchanged.
- `OperationCanceledException`; `ObjectDisposedException` after the container is disposed.

**Typical HTTP use without DI:** `new MediationClient(didComm).RequestMediationAsync(m, me)` → `DidCommServiceBuilder.ForMediation(grant).Build()` → `UpdateRecipientsAsync` → `new PickupClient(didComm).FetchAsync(...)` in a loop until `Drained`.

---

#### 3. Internal architecture

#### 3.1 Core changes (all internal, engineer A)

1. `InboundMessageSnapshot`: add `internal string? Pthid` (filled from `message.Pthid` in `RegisterVerified`, `cloned.Pthid` in `CreateFallback`) and `internal bool Verified` (true only on the `RegisterVerified` path).
2. `ReturnRouteEligibility`: refactor into a private core over the primitive fields and add `TryGetPeer(InboundMessageSnapshot, out peer, out local, out reason)` beside the existing `UnpackResult` overload. Same rule (L-041); a parity test checks both overloads on the same unpack results.
3. New partial `src/DidComm.Core/Facade/DidCommClient.Mediated.cs`:
   - `internal Task<Uri> ResolveDirectEndpointAsync(string peerDid, CancellationToken ct)`:
     1. No `_serviceResolver` → the existing "Forward = true requires an IServiceEndpointResolver" `InvalidOperationException`.
     2. `entries = ResolveAsync(peerDid)`. Empty → `DidResolutionException(peerDid, "no DIDCommMessaging service entries available")` (the same text as `ExpandAsync`).
     3. `ForwardLayerCount(entries[0]) > 0` → `InvalidOperationException(RoutedReturnRouteRefusal)`.
     4. Queue URI → `TransportException(QueueTransport.RefusalMessage)`.
     5. Not absolute → `TransportException`.
     6. `new OutboundEndpointGuard(_options.OutboundEndpointPolicy).Validate(uri)`.
     7. Return `uri`.
   - `internal Task<DeliveredUnpack> UnpackDeliveredAsync(string packed, CancellationToken ct)`, returning `readonly record struct DeliveredUnpack(UnpackResult Result, string InnermostPacked, int LayersOpened)` (§4.6).
4. `ForwardProcessor.ExtractAttachmentBytes`: `private` → `internal`. No behaviour change; it is the single attachment-decoding rule.

#### 3.2 `MediatorClientChannel : IInboundCorrelator, IMediatorExchange, IDisposable, IAsyncDisposable` (engineer A)

`src/DidComm.Mediator/Client/Internal/`. State: `ConcurrentDictionary<string, PendingRequest>` keyed by request id; `_active` (pending count + live streams) for a fast path; live registry `ConcurrentDictionary<string, LiveRegistration>` keyed `mediatorSubject + "\n" + localSubject`; `DuplicateFilter`; `NegotiatedVersionCache` (owned by B, held here); `DeliveredMessageUnpacker`; copied options; `TimeProvider`; `receivesInbound` flag.

```csharp
internal interface IMediatorExchange
{
    MediatorClientOptions Options { get; }
    TimeProvider Time { get; }
    Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct);
    Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct);
    LiveRegistration RegisterLiveStream(string mediatorDid, string localDid);          // InvalidOperationException: one exists, or 16 already
    Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct); // deadline, concurrency, abandoned bound
    DuplicateFilter Duplicates { get; }
    NegotiatedVersionCache Versions { get; }
}
internal sealed record MediatorRoute(Uri Endpoint, bool IsHttp, bool IsWebSocket);
internal sealed record MediatorExchangeSpec(string Name, MessageTypeUri[] Expected, bool ExpectsDelivery); // static instances per request type and CM version
```

`PendingRequest` copies DF's `PendingQuery` terminal CAS (`DiscoverFeaturesClient.cs:50-94`): states Waiting / Completed / Abandoned / Failed, and a `TaskCompletionSource<InboundMessageSnapshot>(RunContinuationsAsynchronously)`. It retains only our request id, the two DID subjects (≤ 2048, caller-supplied), and a reference to the static spec.

#### 3.3 The two facades

- `MediationClient` (engineer B): negotiation (§4.4), CM 3.0 via `CoordinateMediation.*`, CM 2.0 via `CoordinateMediation20`, result validation (§4.5), the page walker.
- `PickupClient` (engineer C): fetch (§4.7), ack, status, live toggle, and `LiveDeliveryStream` (§4.8). Delivery parsing is per attachment and lenient, through a new internal `PickupBodies.ReadAttachmentsEach(attachments, max, msg)`. It returns `(id?, payload?, error?)` per attachment, so one bad attachment becomes one failure item instead of refusing the whole delivery (as `TryParseDelivery` does).

---

#### 4. Algorithms

#### 4.1 Exchange (one request, one answer)

1. Count check (`MaxPendingRequests`).
2. `pending = new PendingRequest(request.Id, spec, M, L)`. Registered with `TryAdd` before sending, because a WS answer can arrive before `SendAsync` returns. A duplicate id → `InvalidOperationException`. Increment `_active`.
3. Deadline: a `CancellationTokenSource` cancelled by `TimeProvider.CreateTimer` at `RequestTimeout`, whose callback cancels inside a `try` (the `PushDispatcher.CancelQuietly` pattern, `PushDispatcher.cs:364`, `:518-529`; never `CancelAfter` on a token host code sees). Linked with `ct`. A registration abandons the pending entry and removes the exact `KeyValuePair`.
4. `result = SendAsync(request, new SendOptions([M], From: L, ServiceEndpointOverride: route.Endpoint) { ReturnRoute = All }, linked)`.
5. If `result.Returned` is set → `Offer(snapshotOf(Returned), boundTo: pending)`.
6. If `route.IsHttp` and the pending entry is still Waiting → `TryFail(NoAnswer)` and throw `TimeoutException("Mediator request '{id}' ({spec.Name}) got no answer in the HTTP response; the mediator may still have acted on it — retry idempotently.")`.
7. `await pending.Completion.Task`, then `await Task.CompletedTask.ConfigureAwait(ForceYielding)`. Parsing never runs on a receive loop or inside `Offer`.
8. The answer is a problem report (any sorter) → throw `MediationProtocolException(requestId, sanitized code)`.
9. Our deadline fired while the caller's `ct` did not → `TimeoutException("Mediator request '{id}' ({name}) was not answered within {timeout}.")`. `TransportException` propagates.
10. `finally`: abandon, remove the exact entry, decrement `_active`, dispose the timer.

A response arriving after timeout or cancellation finds no entry or a non-waiting one and is dropped (Debug, rate-limited). A late `delivery` is never acknowledged, so it comes back after its lease. A late grant or update was applied: the retry is idempotent (a re-sent `remove` answers `client_error`, which is documented).

#### 4.2 The matcher `Offer(S, boundTo)`; the correlator calls `Offer(S, null)`

0. Fast path: `_active == 0` → return. A host that is also a mediator pays one volatile read per inbound while the client is idle.
1. Type: `MessageTypeUri.TryParse(S.Type)` once. Not one of {DF disclose; CM 3.0 and 2.0 grant/deny/update-response/recipient/keylist-update-response/keylist; pickup status/delivery; report-problem 2.x} → return.
2. Identity (no lookup yet; all booleans computed, combined with `&`):
   `S.Verified`; `ReturnRouteEligibility.TryGetPeer(S, out peer, out local, out _)` (authcrypt sender kid, signer agrees, not a forward); `fromOk = DidSubjectOf(S.From) == peer`; `toOk = any of the first 64 S.To entries has DidSubjectOf == local`. Fail → return silently (other traffic; no log).
3. Thread: `t = S.Thid`; for a problem report, `t = S.Pthid ?? S.Thid`. A `t` longer than 64 characters is never looked up (our ids are 36).

| Snapshot (identity passed) | Unbound (WS / host endpoint) | Bound to HTTP request R |
|---|---|---|
| `t` = pending P.id, P's pair = (peer, local) (`&` of both compares), type ∈ P.Expected or problem report | complete P | complete P (P may be ≠ R) |
| `t` present, no matching pending | drop (Debug, rate-limited "late or unknown") | drop; R fails (step 6 above) |
| no `t`, type `delivery` | to the live stream registered for (peer, local), `TryWrite`; none, or full → drop, not acknowledged | complete R if R expects `delivery`, else drop; **never** to a stream |
| no `t`, problem report | drop (`pthid` is REQUIRED) | complete R (transport-bound) |
| no `t`, other expected type | exactly one Waiting P with the same pair expecting this type → complete; 0 → drop; ≥ 2 → refuse all (they stay pending; Debug, rate-limited "ambiguous") | complete R if R expects the type, else drop |

So a thid-less live push can never answer a pending `delivery-request`. A `delivery` with a `thid` is never treated as a live push. On HTTP, the exchange itself binds the body, so thid-less answers from third-party mediators still work there.

#### 4.3 Routes

`ResolveRouteAsync(M, override, requireWebSocket)`:
- endpoint = override ?? `client.ResolveDirectEndpointAsync(M)`, bounded by `RequestTimeout`;
- `IsHttp` = scheme `http`/`https`; `IsWebSocket` = `ws`/`wss`; any other scheme is treated like WS (wait for the correlator);
- `IsWebSocket && !receivesInbound` → `InvalidOperationException` (D-4);
- `requireWebSocket && !IsWebSocket` → `InvalidOperationException` ("live delivery needs a WebSocket route"), before anything is sent.

A mediator whose first DID-document entry is `https` is reached over HTTP. A client that wants WS passes `serviceEndpointOverride` (no automatic "prefer wss" pick: L-041).

#### 4.4 CM version negotiation (MediationClient)

1. Pinned version → use it.
2. Otherwise use the cache, keyed by DID subject of M (TTL on `TimeProvider.GetTimestamp`).
3. Otherwise send `DiscoverFeatures.CreateQuery(from, M, {protocol, "https://didcomm.org/coordinate-mediation/*"})` through the exchange (spec: disclose). Then:
   - read at most the first 256 `disclosures` entries;
   - keep `feature-type == protocol` whose `id` parses as a PIURI (`ProtocolIdentifier.TryParse`);
   - skip entries whose `roles` is present and lacks `mediator` (OrdinalIgnoreCase);
   - match `MatchesProtocolAndMajor` against CM 3.0 and CM 2.0;
   - prefer 3.0, else 2.0;
   - neither → `MediationNotSupportedException` with `DisclosedProtocols` sanitized (§2), message "The mediator disclosed {n} protocol(s), none of them Coordinate Mediation 3.0 or 2.0 in the mediator role (request '{id}'); see DisclosedProtocols."
4. Cache the result (no insert when the 64 entries are full).
5. A DF timeout → `TimeoutException` (no guessing). The XML docs point to the pin for mediators without Discover Features.

#### 4.5 MediationClient answers

- **Grant.** Validate before allocating: `routing_did` is a string, or an array of length ≤ 16 (counted on the `JsonElement` first, L-055). Each entry is a bare DID ≤ 2048. Otherwise `MediationProtocolException`. A deny → `null`.
- **`recipient-update-response` / `keylist-update-response`.** The count must equal the request's, and each entry's `recipient_did` (ordinal) and `action` must equal the request entry at the same index. Otherwise `MediationProtocolException`.
- **`recipient` / `keylist`.**
  - `dids.Count` ≤ `paginate?.Limit ?? 1000`;
  - if `pagination` is present: `count == dids.Count`, `offset ==` the requested offset (0 without `paginate`), `remaining ≥ 0`;
  - otherwise `MediationProtocolException`.
- **Page walker.**
  - Page size 100.
  - `offset += dids.Count`.
  - Stop on `pagination == null`, `remaining == 0` or `dids.Count == 0` (progress guard), or after 1000 pages.
  - Yields DID strings.

CM 2.0 shapes: string `routing_did`; `keylist-update` / `keylist-update-response` / `keylist-query` / `keylist` with `keys`; same entry fields. The internal codec writes through `MediationBodies.WriteUpdates/WritePaginate` and reads through `ReadRoutingDids`, `ReadUpdated` and `ReadRecipients("keys")`. **#99 reuses** the type constants and these codecs and adds `CreateGrant(string)`, `CreateKeylistUpdateResponse`, `CreateKeylist` and the request parsers.

#### 4.6 Rewrap opening (Core `UnpackDeliveredAsync`)

```
r = await UnpackAsync(text)
for opened = 0; ; opened++:
    if !ForwardMessage.TryParse(r.Message, out next, out atts): return (r, text, opened)   // TryParse may throw Malformed → failure
    selfShape = r.Encrypted & r.AnonymousSender & !r.Authenticated & empty(r.SenderKid) & empty(r.SignerKid)
                & r.FromPrior is null & atts.Count == 1
                & DidSubjectOf(next) is {} n & DidSubjectOf(r.RecipientKid) is {} d & n == d
    if !selfShape: throw ConsistencyException("A forward that is not a delivery rewrapped for this agent cannot be opened (FR-ROUTE-06); it is not a message for this agent.")
    if opened == MaxForwardLayers (8): throw MalformedMessageException("more than 8 forward layers")
    text = UTF8(ForwardProcessor.ExtractAttachmentBytes(atts[0]))      // relaxed base64, data.json, empty → malformed
    r = await UnpackAsync(text)                                          // full rules per layer: size cap, FR-API-05 expiry, consistency
```

- At most 8 self-addressed layers are opened per attachment, so at most 9 unpacks.
- Only the DID that decrypted a layer counts as "this agent", as on the mediator. A forward whose `next` is someone else (or another local DID), or that is authcrypt, signed or carries `from_prior`, is a **failure** item (left queued, reported), never a message.
- A pass-through delivery returns after the first unpack with 0 layers.
- Inner layers use the normal unpack, because the innermost message may legitimately be authcrypt. The cost of resolving a sender DID is paid on the caller's await, under `UnpackTimeout`.

#### 4.7 `FetchAsync`

1. Validate the arguments.
2. Resolve the route.
3. Exchange `delivery-request{limit, recipient_did?}`; the spec expects `delivery` and `status`.
4. `status` → parse (malformed → `MediationProtocolException`). Check the `recipient_did` echo: present and different from the filter → `MediationProtocolException`. Return `Drained = true` with `Status`.
5. `delivery` → `DeserializeMessage()`, check the echo.
   - Zero attachments → `Drained = true`.
   - Take the first `limit` attachments only; the excess is never decoded or unpacked and not acknowledged (count logged). A poisoned excess attachment proves this (L-055).
   - A duplicate attachment id: the first wins, later ones are ignored.
   - An attachment without a usable id is ignored (cannot be acknowledged; counted).
   - A decode error → failure item.
6. Unpack through the limiter (D-19), results in delivery order.
7. Apply the filter rule (D-20).
8. Classify in order: a key already in `Duplicates`, or seen earlier in this batch → **duplicate** (acknowledge, not returned); otherwise **new**.
9. `ct.ThrowIfCancellationRequested()`. This is the last cancellation point: nothing has been acknowledged yet, so everything comes back.
10. **Commit**: add the new keys to `Duplicates`.
11. Acknowledge `new ∪ duplicates` (≤ 100, one message) on its own deadline (`RequestTimeout`, not the caller's `ct`). Await the `status`. Any failure → Warning by exception type name; continue.
12. Return the `PickupBatch`.

**Drain loop property (documented and tested):** a failed item stays leased for the mediator's lease (60 s at ours) and is not delivered again within it. A caller that loops `FetchAsync` until `Drained` therefore reaches everything behind the failures. A caller polling slower than the lease, with more failures than `limit`, sees only the failures until it discards them with `AckAsync` (L-058; never silently acknowledged).

#### 4.8 `ReceiveLiveAsync` (`LiveDeliveryStream`, engineer C)

A non-iterator wrapper validates the arguments eagerly and returns the iterator. At the first `MoveNextAsync`:
- `route = ResolveRouteAsync(…, requireWebSocket: true)`, which refuses before any send;
- `reg = RegisterLiveStream(M, L)` (one per pair, at most 16 streams);
- per stream: `ready` (≤ one batch plus pushes), `taken` (confirmed, not yet acknowledged, ≤ 100), and an in-flight id set (bounded: buffer 8 + ready + taken).

```
cycle():                         // on start; every LivePollInterval (monotonic) after the last; immediately (backoff 1 s→30 s) after a stream send failure
    status = exchange(live-delivery-change{true})      // sets or keeps the mark; LiveDelivery == false → Warning once, continue polling only
    drainRounds = 0; draining = true
loop:
    confirm previous yielded item (if any): Duplicates.Add(key); taken.Add(id); inFlight.Remove(id)
    if taken.Count ≥ 10 | nothing ready now | oldest taken ≥ 10 s: flushAcks()   // messages-received ≤ 100 ids, await status (RequestTimeout); failure → keep ids (drop oldest beyond 100), schedule cycle with backoff
    if cycle due: cycle()
    if ready empty:
        if reg.Pushes.TryRead(out s): ready += parseAndUnpack(s)              // ≤ 10 attachments per push
        else if draining: b = exchange(delivery-request{10}); status → draining = false; else ready += parseAndUnpack(b); ++drainRounds == 50 → draining = false
        else await (reg.Pushes.WaitToReadAsync | next cycle time | ct)
        continue
    item = ready.Dequeue()
    skip if id ∈ inFlight (already buffered/taken) — not unpacked, not acknowledged
    duplicate key in Duplicates → taken.Add(id) (acknowledged), not yielded
    yield item   // successes and failures; failures are never confirmed, never acknowledged, and leave the in-flight set when yielded (re-reported when they come back)
finally (enumeration ended, any reason):
    confirm the last yielded item; flushAcks(); exchange(live-delivery-change{false});   // total bounded by RequestTimeout, independent of ct; errors logged
    reg.Dispose()                // exact-entry removal; buffered pushes are not acknowledged → back after session close or lease
```

- **Cycle failures.** A cycle failing with `TimeoutException` / `TransportException` is retried with backoff. After 5 consecutive failed cycles the stream throws the last exception. A `MediationProtocolException` (e.g. `e.m.live-mode-not-supported`) is thrown at once.
- **Quota.** Outstanding unacknowledged items per stream ≤ 8 buffered + 10 drained + 10 taken, far below the mediator's 100-message quota.
- **Order.** Best-effort FIFO within each source, no global ordering guarantee.
- **Reconnect.** A dropped socket is reopened by the next send (`ReturnRoute = All`). The new session starts with live off, and the next cycle's `live-delivery-change{true}` turns it back on. Detection latency is ≤ `LivePollInterval`, or immediate after a failed send.

#### 4.9 Duplicate filter and unpack limiter

- `DuplicateFilter`: a lock, a `HashSet<UInt128>`, and a `Queue<UInt128>` for FIFO eviction. Retained memory is O(capacity × 16 B) whatever the wire id lengths (L-062). Keys are as in D-15; the local DID is in the key, so the scope is per local DID.
- `DeliveredMessageUnpacker`:
  - semaphore 4 per fetch call;
  - a global abandoned counter: at ≥ 16, a new unpack fails fast with `TimeoutException("delivered message not unpacked: unpack capacity exhausted")`;
  - the deadline is a guarded timer plus `WaitAsync`;
  - a task that does not honour cancellation keeps the counter until it ends.

---

#### 5. DI, lifetimes, cycles, non-DI

`AddMediatorClient(configure?)`:
- runs `configure` on a probe and calls `Validate()` (failures come out as `ArgumentException`), then `AddOptions<MediatorClientOptions>().Configure(configure)`;
- `TryAddSingleton<MediatorClientChannel>(sp => new(sp.GetRequiredService<DidCommClient>(), options.Value, receivesInbound: sp.GetService<IServiceProviderIsService>()?.IsService(typeof(IInboundFrameConsumer)) == true, sp.GetService<TimeProvider>() ?? TimeProvider.System, sp.GetService<ILoggerFactory>()))`;
- `TryAddEnumerable(ServiceDescriptor.Singleton<IInboundCorrelator, MediatorClientChannel>(sp => sp.GetRequiredService<MediatorClientChannel>()))`, the DF pattern;
- `TryAddSingleton` of `MediationClient` and `PickupClient` through the internal constructors.

Repeated calls are harmless and their option delegates compose. Neither `UseInboundDispatch()` nor any transport is added implicitly.

**Cycle walk (L-017).**
- `ProtocolDispatcher` factory → correlators → channel → `DidCommClient` → router → transports → WS transport → `IInboundFrameConsumer` (`InboundDispatchConsumer`: provider and logger only, resolving the dispatcher at call time). Nothing leads back to the dispatcher, the registry or a handler.
- App handlers may take `MediationClient`/`PickupClient`: handler → client → channel → `DidCommClient`, no cycle.
- A host consumer that illegally takes the dispatcher re-enters the guarded `DidCommClient` and dispatcher factories and throws, as today.
- The channel constructor resolves nothing (the `IsService` check only reads registrations).

**Disposal.** The channel implements `IDisposable` and `IAsyncDisposable` (L-035): pending requests fail with `ObjectDisposedException`, and live writers complete so open streams end. The public clients are not disposable: a non-DI channel has no background work, and its timers are per request.

**Mediator and recipient in one host.** The mediator handlers own the CM and pickup PIURIs and ignore responses, and the correlator runs first, so `AddMediator()` + `AddMediatorClient()` compose. Both roles share one replay cache, keyed (sender subject, id), so they do not collide.

**Non-DI.** `new MediationClient(didComm)` / `new PickupClient(didComm)` each own a private channel that is not a correlator (`receivesInbound = false`). HTTP works fully; WS routes are refused (D-4).

**Documented rule.** Never call these clients from a protocol handler or observer that runs on a session socket's receive loop for the same session: the loop reads the next frame only after the handler returns (`IInboundFrameConsumer` contract). An `AsyncLocal` guard was considered and rejected: it flows into child tasks and would refuse legitimate fire-and-forget use.

---

#### 6. Logging, docs and gates

#### 6.1 Log lines

- Categories: `DidComm.Mediator.Client.MediationClient`, `…PickupClient`, `…Internal.MediatorClientChannel`.
- Content: our request ids (UUIDs), message names (constants), counts, enum names and exception type names only. Never a DID, a key id, an attachment or queue id, a mediator message id, an exception object or its text.
- **Rate-limited** (first and every 1000th at Warning, Debug otherwise, `MediatorLog.RateLimited`): unclaimed or late answers, ambiguous thid-less answers, live pushes dropped (no stream or buffer full), attachments ignored (beyond `limit`, no id), unpack failures (count plus type names), ack failures, problem reports received.
- Not logged at all: identity failures (other traffic).
- Debug: request sent and answered (id, name, elapsed ms).
- All calls go through `MediatorLog.Write`, so a throwing provider changes nothing.

#### 6.2 DX gate

New public members get `TODO(#97)` entries in **both** `tests/DidComm.InteropTests/DxCoverage/coverage-allowlist.json` and `execution-allowlist.json`. Reason text: `"TODO(#97): recipient-side client (#96): demonstrated by sample 11 / cookbook sections CC–FF (#97); delete this entry when #97 lands."` That is about 42 coverage and about 31 execution entries: types, ctors, methods and properties of the §2 types; enum members are exempt. The exact member ids are copied from the gate's failure output in the stub commit.

#### 6.3 Build

`/warnaserror` and XML docs: document every parameter or none (L-003); put a `<summary>` on the `extension` block and on `ForMediation`.

#### 6.4 C# 14 extension vs the gates

The extension block compiles to:
- a static implementation method `MediationServiceBuilderExtensions::ForMediation(DidComm.Mediator.Coordination.MediationGrant)`, which `DidCommServiceBuilder.ForMediation(grant)` call sites reference in IL;
- a compiler grouping type `<G>$…` with skeleton members, which the scanner skips (`IsPublicSurfaceType`: names starting with `<`).

So the gate counts two members: the static class and the method. The **stub commit must prove this**: build `/warnaserror`, run both DX tests, and confirm the two ids appear and nothing from `<G>$` does. Two failure modes and their fixes:
- the skeleton surfaces → extend the scanner's name/attribute exemption and add a scanner test;
- the implementation method is `[CompilerGenerated]` → add an explicit scanner test so the hole is visible.

Fallback if SDK 10.0.100's docs or extension emission misbehave under `/warnaserror`: a plain static `MediationServiceBuilderExtensions.ForMediation(MediationGrant)` (same id shape), recorded in the plan. Consumers below C# 14 (F#, VB, older LangVersion) call the static method directly; `DidComm.Mediator` is `net10.0`-only, so C# consumers default to 14.

---

#### 7. Threat model (L-031)

**Registration surface.**
- Host code at startup only (`AddMediatorClient`): no runtime seam, no callbacks, no public correlator.
- Options are copied at construction. The internal correlator sees every verified inbound snapshot (as the DF client does), performs no I/O and does no body parsing; it does one `TryWrite` / `TrySetResult`.

**Hostile or compromised mediator.** It is authoritative for its own answers and cannot exceed these bounds:
- **Size:** every answer ≤ `MaxReceiveBytes`.
- **Pending work:** pending map ≤ `MaxPendingRequests`.
- **Deliveries:** attachments processed ≤ `limit` (excess not unpacked, not acknowledged); live buffer 8 (drop, not acknowledged); live attachments per push ≤ 10.
- **Ids and lists:** routing DIDs ≤ 16, bare, ≤ 2048; `dids` page bound; pagination consistency; walker ≤ 1000 pages with a progress guard; update-response shape equality; DF disclosures ≤ 256 inspected, ≤ 32 reported, sanitized; problem code sanitized; attachment ids validated (unreserved, ≤ 2048) before they are echoed in `messages-received`, and never logged.
- **Acknowledgements:** the ack set is exactly what unpacked plus duplicates, so a mediator cannot make the client acknowledge a failure or an id it never delivered.
- **Unsolicited messages:** an unsolicited `delivery` with a `thid` is dropped; a thid-less one reaches only a registered stream, is unpacked like any delivery and is acknowledged only when it unpacked. Unsolicited `mediate-grant` / `recipient-update-response` / `status` are dropped unless a pending request matches (thid, or exactly one fallback candidate).
- **Misaddressed payloads:** attachments addressed to someone else fail to unpack and are reported. Payloads for another local DID are refused under a filter (D-20). Without a filter this is a stated residual: a multi-tenant host checks `Unpacked.RecipientKid` or uses the filter.
- **Withheld or delayed answers:** `RequestTimeout`.
- **Granted routing DID:** it is the mediator's word (residual; publish only for a mediator you trust).

**Third parties.**
- Cannot write on S(E,L,M): only E writes to it.
- Via the host's own receive endpoints:
  - post-sign tampering → unpack fails, nothing dispatched (HTTP body tampering → `TransportException` status 200; WS → frame dropped → timeout);
  - a self-consistent forgery (Mallory's authcrypt, `from` = Mallory, our pending `thid`) → identity fails before lookup, and the real answer still completes;
  - `from` = M with Mallory's skid → refused at unpack (FR-CONSIST);
  - anoncrypt, signed-only or `anoncrypt(sign)` → identity fails;
  - authcrypt from M to another local DID → pair mismatch;
  - a hand-built `UnpackResult` dispatched by host code → `Verified = false`, never completes.
- Replays:
  - a captured genuine answer can match only a request id that is pending now (fresh UUID v4 per request), so in practice none;
  - a captured thid-less live `delivery` replayed to a host endpoint → shared FR-RR-08 cache (duplicate, or too old); if ever admitted → duplicate filter, acknowledged again harmlessly.

**Resource exhaustion.**

| Resource | Bound |
|---|---|
| Pending requests | ≤ 64 |
| Live streams | ≤ 16, each ≤ 8 snapshots of ≤ `MaxReceiveBytes` |
| In-flight / taken ids per stream | ≤ ~30 ids of ≤ 2048 chars |
| Duplicate filter | ≤ 4096 × 16 B |
| Version cache | ≤ 64 |
| Abandoned unpacks | ≤ 16 |
| Correlator cost when idle | one volatile read |

**Timing.** Identity is computed before any dictionary probe, with `&`-combined compares (D-7). The `thid` probe only runs for messages authcrypted by a mediator to us.

**Peer-controlled latency (L-061).**
- The correlator never awaits. The WS receive loop is never held by the client.
- Inner-sender DID resolution runs on the caller's own await (`FetchAsync`) or on the stream consumer's, bounded by `UnpackTimeout`, 4-way concurrency (fetch) and the abandoned bound.
- Mediator latency is bounded by `RequestTimeout`. Mediator-DID resolution is bounded by the exchange deadline.
- Residual: a sender whose DID resolves slowly delays a live stream by up to `UnpackTimeout` per message it sends, and its failures return once per lease period until the app discards them.

**Clock.**
- A mediator more than `ReplayProtectionOptions.ClockSkew` (60 s) ahead has its WS answers dropped (`FutureDated`) although it acted → `TimeoutException`; retries are idempotent; live pushes return after the lease.
- A client more than about 6 min behind is refused `TooOld` → timeouts.
- Documented: clocks must agree within a minute by default.
- Over HTTP, `Returned` is not admitted, so skew does not matter there.

**Interop risks for #98.**
- A mediator that does not stamp `created_time` on WS answers → dropped by the client's admission.
- Raw-push live framing (DD-13) → the client drops it (peer mismatch) and the stream degrades to polling.
- A mediator that sets `thid` on live pushes → dropped, picked up by polling.
- A mediator without DF → pin the version.

---

#### 8. Work split (three staff engineers, parallel worktrees)

**A0. Contract stub commit** (engineer A, merged before anyone branches):
- every §2 public type and member with XML docs and `NotImplementedException` bodies;
- `IMediatorExchange`, `MediatorRoute`, `MediatorExchangeSpec`, `LiveRegistration`, `DeliveredOutcome`, a stub `MediatorClientChannel`;
- the Core internal signatures (`Pthid`/`Verified` implemented outright, they are trivial);
- both allowlists' `TODO(#97)` entries;
- the C# 14 / DX-gate verification (§6.4);
- `tests/DidComm.InteropTests/Mediator/Client/MediatorClientHarness.cs`: a client container over HTTP or WS against `MediatorTestServer`, the `QaSender` pattern with `UseInboundDispatch` + `ReceiveInbound` + `AddMediatorClient`, shared clock, logs into `mediator.Logs`; frozen afterwards, changes via C;
- the plan `tasks/todo{ts}-mediator-client-96.md`.

Gate for A0: build `/warnaserror`, tests and pack all green.

| Engineer | Owns (src) | Owns (tests) |
|---|---|---|
| **A — Core + channel + DI** | `src/DidComm.Core/Protocols/InboundMessageSnapshot.cs`, `src/DidComm.Core/Transports/ReturnRoute/ReturnRouteEligibility.cs`, `src/DidComm.Core/Facade/DidCommClient.Mediated.cs` (new), `src/DidComm.Core/Protocols/Routing/ForwardProcessor.cs` (visibility only), `src/DidComm.Mediator/Client/Internal/{MediatorClientChannel, IMediatorExchange, PendingRequest, MediatorExchangeSpec, MediatorRoute, LiveRegistration, DuplicateFilter, DeliveredMessageUnpacker}.cs`, `src/DidComm.Mediator/Client/{MediatorClientOptions, MediatorClientBuilderExtensions, MediationProtocolException}.cs` | `tests/DidComm.Core.Tests/Facade/{UnpackDeliveredTests, ResolveDirectEndpointTests}.cs`, `tests/DidComm.Core.Tests/Transports/ReturnRouteEligibilitySnapshotParityTests.cs`, `tests/DidComm.Mediator.Tests/Client/{ClientTestSupport, MediatorClientChannelTests, MediatorResponseMatcherTests, MediatorClientTimeoutRaceTests, DuplicateFilterTests, DeliveredMessageUnpackerTests, MediatorClientDiTests, MediatorClientOptionsTests}.cs` |
| **B — MediationClient, CM 2.0, ForMediation** | `src/DidComm.Mediator/Client/{MediationClient, RecipientQueryResult, MediationNotSupportedException, MediationServiceBuilderExtensions, CoordinateMediationVersion}.cs`, `src/DidComm.Mediator/Client/Internal/{CoordinateMediationWire, FeatureNegotiation, NegotiatedVersionCache}.cs`, `src/DidComm.Mediator/Coordination/CoordinateMediation20.cs` (internal, new), `src/DidComm.Mediator/Coordination/MediationBodies.cs` (additive: routing-DID count bound) | `tests/DidComm.Mediator.Tests/fixtures/coordinate-mediation-2.0/*.json` (seven, from the didcomm.org 2.0 page, normalized to valid JSON; DIDs replaced by did:peer examples because the page's are corrupted; provenance comment), `tests/DidComm.Mediator.Tests/Coordination/{CoordinateMediation20CodecTests, CoordinateMediation20FixtureTests}.cs`, `tests/DidComm.Mediator.Tests/Client/{MediationClientTests, MediationClientNegotiationTests, MediationClientRecipientsTests, MediationServiceBuilderExtensionsTests}.cs`, `tests/DidComm.InteropTests/Mediator/Client/{FakeCoordinateMediation20Mediator, MediatorClientEnrolmentTests, MediatorClientCoordinateMediation20Tests}.cs` |
| **C — PickupClient + live** | `src/DidComm.Mediator/Client/{PickupClient, PickupBatch, PickupItem}.cs`, `src/DidComm.Mediator/Client/Internal/{LiveDeliveryStream, DeliveryReader}.cs`, `src/DidComm.Mediator/Pickup/PickupBodies.cs` (additive `ReadAttachmentsEach`) | `tests/DidComm.Mediator.Tests/Client/{PickupClientFetchTests, PickupClientAckTests, PickupClientLiveStreamTests, PickupClientLiveCycleTests}.cs`, `tests/DidComm.InteropTests/Mediator/Client/{PickupClientHttpEndToEndTests, PickupClientWebSocketEndToEndTests, PickupClientRewrapTests, MediatorAndRecipientHostTests, MediatorClientClockSkewTests}.cs` |
| **Lead, after integration** | — | `CHANGELOG.md`, `docs/didcomm-dotnet_PRD.md`, `docs/codebase-architecture.md`, allowlist reconciliation, plan review section |

B and C unit-test against a fake `IMediatorExchange` until A lands.

**Integration order:**
1. A0.
2. A, B and C in parallel.
3. Merge A.
4. Merge B (its end-to-end tests now run on the real channel).
5. Merge C.
6. Full suite.
7. Independent QA passes.
8. Break-it adversarial passes (brief them per L-039/L-044/L-055/L-057/L-059/L-061/L-062 and the AGENTS.md tamper/forgery rule; compare with `main` per L-051).
9. Docs.
10. PR.

---

#### 9. Test plan outline

### Unit, without handlers (fake send seam; `ManualTimeProvider` from `tests/DidComm.Mediator.Tests/Pipeline/PipelineTestSupport.cs:23`; real envelopes from `HandlerTestSupport` peers)

**Channel and matcher (A)**
- Identity matrix: anoncrypt, signed-only, `anoncrypt(sign)`, authcrypt from another DID, decrypted by another local DID, `from` ≠ skid (post-sign tamper vs a self-consistent forgery), local missing from `to`, forward type, fallback snapshot.
- Every row of the §4.2 table, for both unbound and bound offers.
- The ambiguous fallback is refused and both requests time out.
- A thid-less `delivery` never completes a `delivery-request`; a thid'd `delivery` never reaches a stream.
- A problem report by `pthid`, and by `thid`.
- Fast path: no work when idle; a 200 000-character `thid` is never probed.
- Pending bound reached → refused before sending.
- `TimeoutException` names the request id and contains no DID.
- A deterministic race between response and timeout (rendezvous, L-044): the CAS winner is respected and a late answer is dropped.
- HTTP no-answer fails at once, without waiting.
- No CancelAfter-driven token is handed to the send: a throwing registered callback cannot crash anything.
- Dispose fails pending requests.

**Core (A)**
- `UnpackDeliveredAsync`: 0, 1, 2 and 8 layers open; the 9th is refused; `next` = another DID → failure; authcrypt, signed or `from_prior` layer → failure; two attachments; empty or invalid base64; `data.json`; an expired layer; a layer over `MaxReceiveBytes`.
- `ResolveDirectEndpointAsync`: parity with `SendAsync` selection (same `entries[0]`, same refusals, guard applied).

**Duplicate filter and unpack limiter (A)**
- A stranger with the same message id is not suppressed; anonymous messages are keyed by bytes; FIFO eviction; capacity 0 = off.
- Retained size is bounded with 200 k-character ids (L-062).
- The unpack limiter: a gated resolver that ignores cancellation keeps abandoned unpacks ≤ 16, and the 17th fails fast (L-059).

**MediationClient (B)**
- Negotiation: 3.0 preferred; 2.0 fallback; neither → sanitized property and wire-free message; roles filter; hostile disclosures (10 000 entries, control characters, 64 KiB ids); cache bound and TTL; pin skips DF; DF timeout → `TimeoutException`.
- Deny → `null`. Grant validation: a 17-entry `routing_did` refused from the count, with a poisoned 17th entry (L-055); DID URL refused; string accepted.
- Update-response mismatches (count, order, DID, action).
- Paging: inconsistencies, offset ignored by the mediator → refused; walker progress guard and page cap.
- 1025 updates refused before sending.
- CM 2.0 codec round-trips the fixtures byte-exact.

**PickupClient (C)**
- Fetch:
  - partial failure: the ack is exactly the ids that unpacked (L-057);
  - `limit + 1` attachments with a poisoned last one → not unpacked, not acknowledged (L-055);
  - duplicate ids (first wins); attachments without id;
  - filter enforcement;
  - cancellation before the commit (nothing acknowledged, nothing recorded) and after it (results returned);
  - ack failure keeps the results and the redelivery is filtered;
  - `AckAsync` chunks of 100;
  - the drain-loop property with more failures than `limit`, inside and beyond the lease (L-056/L-058).
- Live:
  - acknowledgement on the next `MoveNextAsync` and at enumeration end (break, exception, cancellation);
  - buffered-but-not-yielded items are never acknowledged;
  - buffer overflow drops without acknowledging;
  - cycle order (live-on, then drain) on a frozen clock;
  - periodic cycle on `ManualTimeProvider`; re-arm after a send failure with backoff;
  - 5 failed cycles → throws; `e.m.live-mode-not-supported` → throws;
  - a second stream for the same pair refused; HTTP refused before any send (send-seam count 0);
  - live-off at the end;
  - failures yielded, not acknowledged, and re-reported.

**DI (A)**
- Registrations and singletons; a repeated `AddMediatorClient` registers one correlator.
- `AddMediator()` + `AddMediatorClient()` resolve the dispatcher with no cycle or hang.
- WS without a consumer → `InvalidOperationException` naming the fix.
- Options failures at registration.

**ForMediation (B)**
- First routing DID; `null`; empty list; DID URL → `ArgumentException`; `.WithRoutingKeys` with the 8-layer bound.

**Logs (all)**
- No DID, kid or attachment id in any client line (reuse the `LeakingLogLines` approach); the rate limits work.

### End-to-end on the #95 TestServer (`tests/DidComm.InteropTests/Mediator/Client/`)

- **HTTP:**
  - enrol → `ForMediation` DID document → Alice `SendAsync(Forward: true)` → `FetchAsync` → acknowledged → `status` 0;
  - the full chain DF → `mediate-request` → `recipient-update` → `delivery-request` → `messages-received` → `status` 0, with `created_time` == the injected clock on every request (FR-RR-09);
  - policy deny → `null`;
  - a faulting mediation store (no reply, 202) → immediate `TimeoutException` naming the id;
  - partial unpack (a forward to Bob carrying a payload for Carol) → the good ones returned, the bad one still counted in `status`;
  - tampered HTTP body → `TransportException` status 200.
- **WS** (mediator identity minted with a `ws://` service, and separately an `http` identity plus a `ws` override):
  - the same flow;
  - `ReceiveLiveAsync`: messages present before live-on arrive by the drain, later ones by push;
  - acknowledgement per item; live-off at the end;
  - two local DIDs over one transport on separate sockets;
  - the socket evicted → re-armed;
  - a delayed forward (`delay_milli`) arrives through the periodic cycle (FR-PICK-08);
  - `SetLiveDeliveryAsync` over HTTP refused with zero server requests.
- **Rewrap:** `RewrapMode.ReanoncryptToNext` → Bob's items are Alice's messages; a two-mediator route.
- **CM 2.0:** the fixture-driven fake (registry-aware `MapDidCommEndpoint` + `AddBuiltInProtocols`, so DF discloses only 2.0) with and without `thid`; a DF-only host → `MediationNotSupportedException`.
- **Mediator and recipient in one host:** grants and statuses are correlated, the host's mediator handlers ignore them, and its relaying continues while client requests are pending.
- **Clock skew:** the mediator 61 s ahead over WS → timeout, then the idempotent retry succeeds once the clock is fixed; 30 s ahead → works.

---

#### 10. Docs, PRD, CHANGELOG, allowlists

- **PRD:**
  - FR-MED-09: "as built" detail (route rule, pin, cache, deny `null`, HTTP no-answer, bounds) and the evidence test names;
  - FR-MED-04: acceptance evidence for "completes on a thid-less grant";
  - FR-MED-10: the client speaks 2.0; the mediator role stays #99;
  - FR-PICK-11: as built (fetch, drain property, live cycle, acknowledgement semantics, duplicate filter, filter enforcement);
  - FR-ROUTE-06: a recipient-side sentence (same acceptance rule, at most 8 layers, a non-self-addressed forward is a failure);
  - FR-RR-09: the full chain now evidenced over HTTP and WS;
  - §12 Phase 7 status line: #96 in;
  - §14.2 CC/DD/EE snippet text, if accepted (§11).
- **Architecture doc:** a new section "Recipient-side client: `MediationClient`, `PickupClient` (Phase 7, #96)" after #95's (wiring; channel and matcher table; routes; negotiation; CM 2.0; fetch; live cycle; rewrap opening; duplicate filter; threat model; limits). Also update the `DidComm.Mediator` Packages row (Client namespace), the Tests list, the #93 "Not here yet" line, and "Phase 7: still to build" (drop #96). Extend, do not replace.
- **CHANGELOG `[Unreleased]`:** "Added — recipient-side client (FR-MED-09, FR-PICK-11, #96, epic #88)"; "Security — recipient-client threat model and residuals (#96)"; "Changed (internal) — snapshot `Pthid`/`Verified`; `ExtractAttachmentBytes` visibility" (one line, no public change).
- **Allowlists:** §6.2.

---

#### 11. Issues with the spec, and owner-level questions

### Wrong or under-specified, with the resolution used here

1. The issue's `FetchAsync → IReadOnlyList<(id, UnpackResult)>` cannot report failures or "nothing deliverable". Resolution: `PickupBatch` with `Items` / `Drained` / `Status`.
2. The issue's `IAsyncEnumerable<UnpackResult>` cannot carry the id or failures. Resolution: `PickupItem`.
3. The issue says "First look at `Returned`, else await the correlator". Over HTTP that waits out the timeout for nothing. Resolution: D-8.
4. `DiscoverFeaturesClient` cannot negotiate with `ReturnRoute: All` over HTTP, and its timeout pattern hands a CancelAfter-driven token to host code. It is released, so it is left unchanged and the client sends its own DF query. The DF client's timeout pattern is a pre-existing follow-up, not #96 debt.
5. The snapshot has no `Pthid`, which problem-report correlation needs (FR-PICK-07), and no verified flag. Both added (internal).
6. "Name the disclosed features" vs D7 "no echo of wire values". Resolution: D-11.
7. "Fallback on type + mediator DID" is unsafe without the local DID (multi-tenant) and without an ambiguity rule. Resolution: pair = (mediator, local), exactly one candidate, never `delivery`.
8. The #92 note "send `messages-received` without awaiting a reply" applies to handlers on the receive loop. The client's acknowledgements are awaited from caller threads, which is safe.
9. FR-MED-09 gives no pending bound or timeout default. Resolution: 64 and 30 s.
10. The DID document's first entry decides HTTP vs WS. Real mediators often publish `https` first. Resolution: per-call `serviceEndpointOverride` (the DF client's precedent), no automatic wss pick.
11. "At most 8 forward layers in total" for the recipient is stated precisely as: at most 8 self-addressed layers opened per attachment, so at most 9 unpacks.

### Owner-level (flagged, not decided; the default named is what this design builds)

1. **Packaging, and time-sensitive.** The client lives in `DidComm.Mediator` (D1, §3.1), so wallets pull in the mediator roles and `Hosting.Abstractions`. A `DidComm.Mediator.Client` package split is cheap only **before the first publish** of `DidComm.Mediator`.
2. **`CoordinateMediationVersion` pin option** (needed for Identus, which has no DF). This is a PRD wording extension. Also: on a DF timeout, fail (built) or guess 3.0?
3. **`FetchAsync` acknowledges inside the call** (PRD-mandated): at-most-once for the app once it returns. Add an opt-out (`acknowledge: false`)?
4. **Live acknowledgement on enumeration end even when the loop body throws** (D-16).
5. **HTTP no-answer → immediate `TimeoutException`**, rather than waiting for an out-of-band reply.
6. **Duplicate filter on by default** (4096).
7. **On a recipient-only host, the dispatcher logs one Information "No protocol handler registered" line per mediator reply or push** (`ProtocolDispatcher.cs:366-373`). Accept it and document it, or make a small internal change (the correlator reports "claimed" and the line drops to Debug)? L-046: decide in this PR.
8. **§14.2 snippets CC–EE in #96 or #97.** The PRD stub says they "land with the APIs"; the #88 note assigns them to #97. Recommendation: #96 writes the PRD snippets, #97 the cookbook sections and FF.

---

#### 12. Scope check

Estimate: about 2.5 k lines of src (channel and matcher about 850, live stream about 450, PickupClient about 400, MediationClient about 350, CM 2.0 about 150, results, options, exceptions and DI about 300, Core about 150) and 5–6 k lines of tests. That is comparable to PR #107 and #109.

Recommendation: **one PR** with ordered commits (A0 → Core → channel → MediationClient/CM 2.0 → PickupClient fetch → live stream → end-to-end → docs).

Pre-agreed split if review asks:
- PR-1: Core internals + channel + MediationClient + `ForMediation` + CM 2.0 client codec (FR-MED-09, FR-MED-10 client side).
- PR-2: PickupClient, live stream and rewrap opening (FR-PICK-11, FR-ROUTE-06 recipient side).

L-063 escape hatch: if `ReceiveLiveAsync` draws a second post-fix break-it defect, offer to defer only the live stream to its own issue, keeping `SetLiveDeliveryAsync` and `FetchAsync`. Its surface is two members and one internal file, so removing it is one clean change.

### Critical Files for Implementation
- /Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96/src/DidComm.Mediator/Client/Internal/MediatorClientChannel.cs (new: correlator, pending map, matcher, routes, live registry)
- /Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96/src/DidComm.Core/Facade/DidCommClient.Mediated.cs (new partial: `ResolveDirectEndpointAsync`, `UnpackDeliveredAsync`)
- /Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96/src/DidComm.Core/Protocols/InboundMessageSnapshot.cs (`Pthid`, `Verified`)
- /Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96/src/DidComm.Mediator/Client/PickupClient.cs (new, with `Internal/LiveDeliveryStream.cs`)
- /Users/moises/Projects/didcomm-dotnet/.claude/worktrees/orch-96/src/DidComm.Mediator/Client/MediationClient.cs (new, with `Coordination/CoordinateMediation20.cs`)