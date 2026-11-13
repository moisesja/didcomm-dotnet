# Changelog

All notable changes to didcomm-dotnet are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — mediator service: delivery pipeline, AddMediator(), MapDidCommMediator in the new DidComm.Mediator.AspNetCore package (FR-ROUTE-09..11, FR-MED-07, FR-PICK-09/10, #95, epic #88)

The roles of #93 and #94 now make a working mediator: every forward the routing core relays reaches
the recipient's pickup queue, and two calls set it up —
`services.AddDidComm(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediatorDid)))`
and `app.MapDidCommMediator("/didcomm")`. Still to come in the epic: the recipient-side client
(#96), sample 11 and the cookbook sections (#97), the interop and adversarial gate (#98),
Coordinate Mediation 2.0 (#99) and Pickup 4.0 (#100).

- **`AddMediator(configure?)`** (`DidComm.Mediator`) registers Coordinate Mediation 3.0, the routing
  mediator, Message Pickup 3.0, the built-in protocols (Trust Ping for health checks, Discover
  Features for the role disclosures) and the delivery pipeline, under one settings object. Before
  adding anything it refuses an earlier `AddRoutingMediator()` or an `IForwardSink` already
  registered (`InvalidOperationException`: the mediator configures both itself);
  `RewrapMode.ReanoncryptToNext` together with a host `IRecipientRoutingKeyProvider`, whose keys
  re-wrapping would ignore (`ArgumentException`); a configuration with no routing DID and nothing
  that could supply one (`ArgumentException`); and any invalid setting (`ArgumentException`, the
  replay and session settings included, with the role's own exception inside). Calling it again is
  harmless. Collaborators a host supplies (stores, policy, identity, endpoint directory,
  routing-key provider) are registered before it.
- **`MediatorOptions`**: the settings of each role as sub-objects — `CoordinateMediation`, `Pickup`,
  `Replay`, `Sessions`, the same types the roles read on their own — plus the pipeline's:
  `RewrapMode` (`PassThrough`), `MaxForwardDelay` (1 hour), `PurgeInterval` (5 minutes; 1 second to
  24 hours), `DirectPush` (off), `PushConcurrency` (4; 1 to 64) and `PushTimeout` (30 seconds; at
  most 5 minutes) — the push workers and their per-push deadline, for live and direct pushes alike
  — and `Validate()`. The `AddMediator()` delegate reaches each role's own options and composes with
  the host's `Configure<T>` calls in registration order; it may run several times and must only
  assign values. The remarks of `AddMediator()` and `MediatorOptions` carry the operations guidance:
  rate limiting in front of the endpoint, an allow-list or rate-limiting mediation policy, replay
  sizing (about 91 in-scope messages a second at the defaults), what a host clock stepping back
  does, the memory a stalled live session holds, and why a recipient in live mode should still ask
  for its queued messages after a reconnect or a slow period.
- **The delivery pipeline** (FR-ROUTE-11, FR-PICK-09). The mediator's forward sink runs on the
  forward handler's background pump, after the sender has its 202, and only stores: it calls
  `MessagePickupQueue.DeliverAsync` for the DID of `next`, with the forward's `delay_milli` as a
  not-before time, its `expires_time` and `AllowLive: false`, and never waits on a recipient. A
  stored message is counted `Queued`; a recipient with live delivery on then gets it pushed by the
  push workers (below), otherwise it waits for a `delivery-request`. Every other outcome (unknown
  recipient, expired, too large, over quota, a fault) is a logged drop, counted, with nothing sent
  back to the sender.
- **Live pushes on the push workers** (FR-PICK-08/09). A stored message that is already due is
  handed to a small pool of background workers when its recipient has, at that moment, a live
  session with live delivery on, or when direct push is on; the hand-over never blocks the pump. A
  worker checks the registration again, then pushes the message over the recipient's newest live
  session — taking exactly that stored message under a lease owned by the session and sending it as
  a one-message `delivery` through the session's size-bounded send; the session keeps it leased
  until `messages-received`, its close or `LeaseTimeout` — and, when no live session took it and
  direct push is on, pushes it to the recipient's endpoint (below); otherwise it stays queued for
  pickup. A message a `delivery-request` took first is pushed by neither path. **What a live
  recipient can rely on:** every message stored, already due, while it is live is pushed to it, in
  the order it was stored, one at a time — unless its push fails or reaches the deadline, in which
  case that message, and any stored during the cooldown that follows a deadline, stays queued for
  pickup; delivery is at-least-once. The work is bounded and fair: `PushConcurrency` workers, one
  `PushTimeout` deadline per push that holds even against a session, transport or store that ignores
  cancellation, recipients served in turn with at most one push in flight each, and a backlog sized
  from the pickup quotas (`MaxQueueMessages` per grant, `MaxStoreMessages` in all), so a slow
  recipient holds one worker, never all, and every message the queue stores can have its push; a
  push beyond a bound (possible only with a host store that admits more than the pickup settings
  say, or behind jobs of messages a pickup already removed) is skipped and its message stays queued.
  A recipient whose push reaches the deadline has its pushes paused for 2 × `PushTimeout` (its
  messages wait for pickup meanwhile), so a peer that stalls its own pushes holds a worker at most
  once every three push timeouts. A push job holds a few bounded ids (two DIDs, the queue's record
  id, the forward's id shortened for logs) and no payload. `MessagePickupQueue.DeliverAsync` called
  by a host with `AllowLive: true`, the default, still pushes live itself, on the caller's thread.
- **Direct push** (opt-in: `MediatorOptions.DirectPush` and a host `IRecipientEndpointDirectory`).
  After a message is queued, if it is already due and no live session of the recipient took it, it
  is pushed by the push workers to the endpoint the host's directory names for the recipient's
  grant — never one taken from the message — through `DidCommClient.SendPackedAsync`, so the
  outbound endpoint guard always applies. Just
  before sending, the push takes exactly that stored message under a lease of its own (new
  `IMessageStore.LeaseByIdAsync`), so it never sends a message that has expired or aged out, is
  being fetched by the recipient, or was acknowledged or purged meanwhile
  (`Push-Skipped-NotHeld`), and it sends the payload the queue holds. Only an HTTP 2xx answer counts
  as delivered and removes the message; anything else gives the message back to pickup at once.
  Only `http` and `https` endpoints are pushed to, because a WebSocket write gives no receipt, and a
  plain `http` endpoint also needs `"http"` in `HttpTransportOptions.AllowedSchemes` (the HTTP
  transport sends to `https` only by default). One attempt per message, and it is a single request:
  one POST to exactly the directory's endpoint, no retry, no redirect followed (a 3xx is a failed
  push), outside the HTTP transport's circuit breaker, so one recipient's failing endpoint stops
  neither the pushes to the others nor the host's other HTTP sends. A recipient that relies on push
  must also pick up, and should drop messages whose id it has already seen (see Security). The
  push deadline holds even against a transport or store that ignores cancellation: the call is
  abandoned and the message given back to pickup, and an abandoned send may still land later. The
  bounds, the fairness and the cooldown are the push workers' (above).
- **Start-up checks** (FR-MED-07). In a generic host (`WebApplication`, `Host`) a hosted service
  checks, before the server starts listening, that there is at least one routing DID and that each
  resolves to a document whose first `DIDCommMessaging` entry names an `http`, `https`, `ws` or
  `wss` URI (not a DID, not the queue URI) and that has a `keyAgreement` key whose private key the
  secrets resolver holds. A DID that cannot be resolved, an unsupported method and a check that
  takes longer than 30 seconds fail start-up as well: senders resolve the same DID. Routing keys on
  the mediator's own entry are a Warning. It also checks that the sink in effect is the mediator's,
  builds every handler (an unsatisfiable `MaxDeliveryBytes` fails here), builds the replay guard and
  session registry, and checks all settings together — including the forward settings in effect,
  however the container was composed: a `ForwardProcessorOptions` registered after `AddMediator()`
  without the `MaxForwardDelay` cap or with another `RewrapMode`, re-wrapping together with a host
  routing-key provider, and a forward recipient directory that is still the routing mediator's empty
  default all fail start-up. Messages name the DID's position in the list, never the DID. It then builds the mediator's out-of-band invitation and, every `PurgeInterval`,
  removes expired and aged-out messages and reports replay saturation, clock-behind refusals and
  dropped session notifications as Warnings with the remedy. Outside a generic host none of this
  runs: the roles check their own settings when first used, but routing DIDs are not checked, no
  invitation is built and nothing is purged unless the host calls `PurgeExpiredAsync`.
- **New package `DidComm.Mediator.AspNetCore`: `MapDidCommMediator(pattern)`** maps three endpoints
  under one pattern: `POST {pattern}` (the registry-aware HTTP endpoint, unchanged — the 202 is
  committed before a forward is relayed), `GET {pattern}` (the WebSocket endpoint, with same-socket
  replies on and STOMP off for this endpoint only, so pickup and live delivery work there while the
  host's other WebSocket endpoints stay one-way) and `GET {base}/oob?_oobid=mediator` (the
  mediator's out-of-band invitation from its primary routing DID, goal code `request-mediate`;
  `InvitationOobId` = `"mediator"`). Conventions on the returned builder, such as
  `RequireRateLimiting`, apply to all three. It throws at map time without `AddMediator()`.
- **Also new:** `IRecipientEndpointDirectory`; `MediatorIdentity` (a fixed routing-DID list: at least
  one, bare DIDs, duplicates removed, the first is the primary one);
  `MessagePickupQueue.RevokeAndPurgeAsync(peerDid)` → `GrantRevocation(Revoked, Purged)`, which
  revokes a peer's grant and drops everything held under it in one call (a message being stored for
  the peer meanwhile is removed again); `MessagePickupOptions.MaxMessageAge` and
  `ForwardProcessorOptions.MaxDelay` (both under Changed, below);
  `InMemoryReplayGuard.ClockBehindRefusalCount`; and `IMessageStore.LeaseByIdAsync`, which takes one
  record by id under the same rules as `LeaseAsync` — the mediator's live and direct pushes take
  their message with it (an interface member on an unreleased type: a custom store has to add it).
- **A single-attempt send** (`DidComm.Core`): `SendPackedOptions { SingleAttempt }` and the overload
  `DidCommClient.SendPackedAsync(packed, endpoint, mediaType, SendPackedOptions, ct)`, which sets the
  new init-only `TransportRequest.SingleAttempt`. The HTTP transport then makes exactly one POST to
  exactly that endpoint — no retry, no redirect followed (a 3xx is a `TransportException`), neither
  counted nor refused by its circuit breaker — bounded by `RequestTimeout`; the outbound endpoint
  guard and the connect-time address checks still apply. Other sends are unchanged (retries, 307
  following, the breaker). Another transport may ignore the flag. `TransportRequest` is a released
  record: the change is binary/API compatible; value equality now includes `SingleAttempt`
  (instances built by 1.5.1-compiled code have `false` there, so their comparisons do not change).
- **`DidCommServiceBuilder`** (`DidComm.Core`, namespace `DidComm.Resolution`) builds the
  `DIDCommMessaging` service entry a DID publishes: `ForEndpoint(uri)` for a DID reached directly,
  `ForMediator(mediatorDid)` for one reached through a mediator, then `WithId`, `WithRoutingKeys`,
  `WithAccept` and `Build()`. Every value is checked when it is given and a refusal never quotes it;
  a route longer than eight forward layers is refused.
- **Metrics** on the meter `didcomm-dotnet` (`DidCommDiagnostics.MeterName`, created through the
  container's `IMeterFactory` when there is one): `didcomm.forward.outcomes`, one count per forward
  that reaches a routing mediator's forward handler (one refused earlier, at unpack, is not
  counted), tagged `didcomm.forward.outcome` (`Queued` — every stored forward, whatever happens to
  its push —, `Dropped-Malformed`, `Dropped-NotEncrypted`, `Dropped-Expired`, `Dropped-Unknown`,
  `Dropped-Saturated`, `Dropped-Unacknowledged`, `Dropped-Shutdown`, `Dropped-TooLarge`,
  `Dropped-Quota`, `Faulted` — `Dropped-Unknown` also counts a rewrap next hop that cannot be
  reached, and `Faulted` is kept for faults of the mediator's own collaborators);
  `didcomm.mediator.push.outcomes`, one count per stored message handed to the push workers or
  refused by them, tagged `didcomm.push.outcome` (`Pushed-Live`, `Pushed`, `Push-Failed`,
  `Push-Skipped-Saturated`, `Push-Skipped-NoEndpoint`, `Push-Skipped-NoLiveSession`,
  `Push-Skipped-Scheme`, `Push-Skipped-Unregistered`, `Push-Skipped-NotHeld`,
  `Push-Skipped-Cooldown`, `Push-Skipped-Shutdown`); `didcomm.mediator.queue.purged`; and, for the
  library's in-memory types, `didcomm.replay.saturated`, `didcomm.replay.clock_behind_refusals`,
  `didcomm.return_route.dropped_notifications`, `didcomm.mediator.queue.messages` and
  `didcomm.mediator.queue.bytes`. No tag carries a DID. Earlier builds of this change named the push
  settings `DirectPushConcurrency` / `DirectPushTimeout` and the push counter
  `didcomm.mediator.direct_push.outcomes`, and counted a live push as a `Delivered-Live` forward;
  none of these was released.
- DX gate: `DidComm.Mediator.AspNetCore` joins the shipped-assembly list (eight assemblies); the new
  public members are allow-listed with a `TODO(#97)` marker until sample 11 demonstrates them, and
  the two `MessagePickupQueue` constructor entries follow its new signature.

Tests: `AddMediatorDiTests`, `MediatorOptionsTests`, `MediatorIdentityTests`,
`MediatorForwardSinkTests`, `DirectPushDispatcherTests`, `MediatorHostedServiceTests`,
`RoutingDidValidatorTests` and `MediatorGenericHostTests` (`DidComm.Mediator.Tests/Pipeline`);
`PickupRandomIdTests`, `PickupMessageAgeTests` and `PickupRevokeAndPurgeTests` (`Pickup`);
`ForwardDelayCapTests`, `ForwardHandlerOutcomeMetricsTests`, `ForwardHandlerDiTests` and
`DidCommServiceBuilderTests` (`DidComm.Core.Tests`); `MapDidCommMediatorTests` and
`MediatorWebSocketCoreTests` (`DidComm.InteropTests`). End to end over the real endpoints:
`MediatorEndToEndTests` (HTTP pickup with exactly one forward layer, live WebSocket),
`MediatorAcknowledgementTests`, `MediatorDirectPushTests`, `MediatorExpiryDelayAndAgeTests`,
`MediatorRewrapAndRoutingKeysTests`, `MediatorStartupTests`, `MediatorTwoHopTests` and
`MediatorUnknownNextTests` (the known/unknown `next` timing, run alone in a serial collection).
Two independent QA passes added the `*QaTests` suites (component and end-to-end permutations);
`MessageStoreLeaseByIdTests`, `ForwardSelfAddressedTests` and `ForwardRewrapNextTests` cover the
fixes they led to. Two adversarial passes added the `*AdvTests` suites (`ForwardPeelAdvTests`,
`MediatorPeelEndpointAdvTests`, `MediatorEgressAdvTests`, `MediatorIdentityAndLogsAdvTests`,
`MediatorLifetimeAdvTests`, `OutboundGuardAdvTests`); `HttpTransportSingleAttemptTests`,
`ForwardHandlerPumpContextTests` and `LogValuesTests` cover the fixes they led to. The PR #109
review's fixes added `LivePushTests` (`DidComm.Mediator.Tests/Pipeline`) and
`MediatorLiveIsolationTests` (over the real endpoints: a live recipient whose key resolution stalls
holds up no other recipient's storage or pickup), and a third adversarial pass on them added
`LivePushAdv4Tests` and `MediatorLivePushAdv4Tests`.

### Changed — mediator service (#95)

- **Queue ids are random.** The id of a queued message — the attachment `id` a `delivery` carries
  and `messages-received` sends back — is now 16 bytes from the operating system's CSPRNG as 32
  lowercase hex characters, minted by the queue itself. The container's `IMessageIdGenerator` is no
  longer used for them, so a host's sequential or guessable generator cannot make one recipient's
  ids predictable from another's. `MessagePickupQueue`'s public constructor loses its
  `IMessageIdGenerator? idGenerator` parameter (the type is unreleased).
- **Mailbox age limit.** New `MessagePickupOptions.MaxMessageAge`, **default 30 days** (`null`
  turns it off): a queued message older than this, counted from when the queue accepted it on the
  mediator's clock, is never delivered and is removed the way an expired message is — waiting,
  delivered-but-unacknowledged and passed-over messages alike. **A recipient that stays offline
  longer than the limit loses those messages without notice**: nothing tells the sender or the
  recipient. Set the limit to the longest absence your recipients must survive. The limit is
  written onto each record when it is stored (`QueuedMessage.ExpiresAt` is now the earlier of the
  forward's expiry and the limit), so a custom `IMessageStore` needs no change, and the limit in
  force at that moment stays with the record: lowering it later, or restarting with another value
  over a persistent store, does not shorten what is already held. Under `AddMediator()` it must be
  longer than `MaxForwardDelay`.
- **The honoured `delay_milli` is capped.** New `ForwardProcessorOptions.MaxDelay` (`TimeSpan?`,
  init-only): a longer positive delay is shortened to it, and for a negative one (a random delay of
  up to its magnitude) the magnitude is capped before the one random draw, so the draw stays
  uniform over what is honoured. Whole milliseconds; zero makes every message due at once; no
  forward is refused because of its delay. `null` — the default for `AddRoutingMediator()` and for
  a processor a host builds — keeps the 1.5.1 bound of `int.MaxValue` milliseconds; `AddMediator()`
  sets it from `MediatorOptions.MaxForwardDelay`, 1 hour by default. This also bounds how long a
  sender can fill a recipient's quota with messages it cannot see yet (the #94 limitation below).
  `ForwardProcessorOptions` is a released record: the change is binary/API compatible; value
  equality now includes `MaxDelay` (instances built by 1.5.1-compiled code have `null` there, so
  their comparisons do not change).
- **`ForwardHandler`'s drop lines are rate-limited per reason**: the first and every 1000th at
  Warning ("expired while queued" at Information), the rest at Debug with the same text. Any stranger
  can make the handler drop a forward, so these lines could be flooded. An alert that counts these
  Warnings now sees the first and every 1000th of each reason. The handler also counts every forward
  it ends on `didcomm.forward.outcomes`, and `AddRoutingMediator()` now registers that counter and
  builds the handler through a cycle-guarded factory that shares it.
- **`MessagePickupQueue.DeliverAsync` looks the recipient up again after storing.** When the DID no
  longer belongs to the grant it was stored under (the grant was revoked meanwhile), exactly that
  record is removed and the outcome is `NotRegistered`; when that lookup fails, the message is kept
  and reported `Queued` but not pushed live. A custom `IMediationStore` therefore sees two
  `FindGrantByRecipientDidAsync` calls per stored message.
- **`DeliverAsync` reports `Queued` once the message is stored**, also when the call is cancelled or
  fails after that point (the message stays queued, under no lease); `OperationCanceledException`
  leaves only when the call is cancelled before anything is stored. Before, a cancellation after the
  store threw, so a forward whose message was stored while the mediator shut down was counted
  `Dropped-Shutdown`. A delivery already in flight, a live push included, completes even if a
  concurrent acknowledgement or `RevokeAndPurgeAsync` removes the record.
- **`DidComm.Mediator` now depends on `Microsoft.Extensions.Hosting.Abstractions` 10.0.14** (for the
  mediator's hosted service). It still has no ASP.NET Core reference.
- **`DidComm.AspNetCore`**: no public change. The body of the registry-aware
  `MapDidCommWebSocket(pattern)` moved into an internal core that `DidComm.Mediator.AspNetCore` calls
  with the mediator's receive options, through `InternalsVisibleTo`; ship the packages at one
  version. The WebSocket return-route session's bounded send looks at its token once more just
  before handing the frame to the socket writer, so a send cancelled while it was packing queues
  nothing (it throws `OperationCanceledException`, as `IReturnRouteSession` documents).
- **`PickupDeliveryOptions.AllowLive` says what `true` costs.** With `true`, the default,
  `MessagePickupQueue.DeliverAsync` pushes live itself, on the caller's thread, and packing that
  `delivery` resolves the recipient's DID, so the call can take as long as the resolution.
  `AddMediator()`'s pipeline passes `false` and pushes from its own workers.
- Samples: the shared helper `PeerIdentityFactory` builds its `DIDCommMessaging` service entry
  through `DidCommServiceBuilder`; the DIDs it mints are unchanged.

### Fixed — found by the #95 QA and adversarial passes; present before #95 (#95)

- **A two-mediator route behind a rewrapping mediator now delivers** (FR-ROUTE-06, receiver side;
  on `main` since #90). Rewrapping hands the next hop a forward whose `next` is that hop itself
  (DIDComm v2.1 §Routing, "Rewrapping"); `ForwardHandler` looked its own DID up as a recipient and
  dropped the forward. A forward whose `next` is the DID that decrypted it is now opened inline, on
  the sender's own request and before anything is reserved — so the sender pays for opening what it
  built and the shared forward pump never runs a stranger's decryption or DID resolution — and the
  forward inside is screened like an arriving one (a forward, not expired, `next` registered) and
  relayed. Such a layer must be exactly an anoncrypt envelope around a plaintext message without
  `from_prior` — what rewrapping produces and what a sender's forward layers are; an authcrypt
  layer, a signature or another envelope inside, a plaintext or signed layer, and `from_prior` are
  refused as `Dropped-Malformed` before any DID is resolved, so opening never resolves a DID the
  sender chose. At most 8 such layers are opened per received forward, each within
  `MaxReceiveBytes` (one more is `Dropped-Malformed`); only the DID that decrypted a layer counts as
  the mediator itself; anything inside that is not a forward is dropped (a mediator is not the final
  recipient); a layer that cannot be opened, whatever the reason, is `Dropped-Malformed` (or
  `Dropped-Expired`), never `Faulted`; the relayed forward's not-before is the latest of the holds
  of the received forward and every opened layer, each capped, never added, and its
  `expires_time` the earliest of every layer opened; the received forward is counted once.
- **Rewrap mode addresses the DID subject of `next`** (FR-ROUTE-06; in 1.5.1). It dropped only a
  fragment, so a `next` given as a DID URL with a path or a query was resolved as it was; a `next`
  with no DID part is now `MalformedMessageException`.
- **`NotBefore` no longer overflows** (FR-ROUTE-10; on `main` since #90). With the clock close to
  `DateTimeOffset.MaxValue`, receipt time plus the hold threw: `ForwardHandler` dropped the forward
  as `Faulted`, and the public `ProcessAsync` overloads threw an undocumented exception. The sum is
  now pinned to `DateTimeOffset.MaxValue`.
- **A forward attachment that decodes to no bytes is malformed** (FR-ROUTE-05; in 1.5.1). A
  `data.base64` of white space or `=` decoded to nothing, and `ForwardProcessor.ProcessAsync`
  returned an empty onward payload; behind `AddMediator()` the queue then refused it and a
  stranger's malformed forward was counted `Faulted` with an Error line. It now throws
  `MalformedMessageException` (already its documented type for bad attachment data), which
  `ForwardHandler` counts as `Dropped-Malformed`.
- **The HTTP transport's log line no longer carries the endpoint** (in 1.5.1; #92 follow-up). Its
  Debug line after a successful POST printed the URI, which for a direct push is a host secret (it
  can be a capability URL) or names a recipient; it now carries the scheme and the status only.
  `IHttpClientFactory`'s own request logging (categories `System.Net.Http.HttpClient.*`) still
  writes the full URI at Information: filter those categories where endpoints must not reach the
  logs. A `TransportException`'s message names the endpoint; log it by type.
- **A throwing logging provider no longer turns a delivered HTTP send into a failure** (in 1.5.1).
  The HTTP transport's Debug line after a 2xx was unguarded, so a provider that threw made the
  send fail after the recipient had it — a caller could send it again, and a direct push counted it
  failed and left the message for a second delivery. The line is now guarded.
- **The outbound endpoint guard classifies two more IPv6 forms** (in 1.5.1). The IPv4-translated
  form `::ffff:0:a.b.c.d` (RFC 2765 / RFC 6052) is judged by the IPv4 address it embeds, and the
  RFC 8215 local-use NAT64 prefix `64:ff9b:1::/48` is refused as a whole (where the IPv4 sits in it
  depends on the prefix length a network chose). Before, both were treated as public, so a loopback
  or metadata address could be reached through them.
- **The receive endpoints no longer log a rejected message's exception text** (in 1.5.1). The HTTP
  rejection path, both WebSocket receive loops, the callback overloads and the STOMP malformed-frame
  line logged the whole exception, whose message can quote the sender's values (an id, a DID, a
  line break), so a stranger could put text of its choosing into a Warning. They now log exception
  type names, and the message id bounded to 128 characters where there is one.
- **Message ids are written bounded on the whole receive path, and a message type never** (in
  1.5.1 for the dispatcher; on `main` since #90 / #91 for `ForwardHandler`, `InboundAdmission`, the
  return-route sessions and the endpoints' return-route lines).
  `ForwardHandler`, `ProtocolDispatcher`, `InboundAdmission` and the return-route sessions wrote
  ids raw, at any length the receive limit allows; every line now writes them through one Core rule
  shared with `DidComm.AspNetCore` and `DidComm.Mediator`: up to 128 characters as it is, a longer
  one as "(id of N characters)". The dispatcher's no-handler Information line wrote the message
  type as it arrived — a valid type URI can carry line breaks and terminal escapes in its protocol
  name, so any stranger could forge a log line with one plaintext POST; it now names the bounded id
  only (the type stays on the `didcomm.handle` span's tag). The dispatcher's Warning for a pure ACK
  that also requests an ACK (a peer's FR-THR-04 rule-2 violation) is now rate-limited: the first and
  every 1000th at Warning, the rest at Debug.
- **The forward pump starts without the caller's execution context** (on `main` since #90), so it
  carries no ambient state (an `AsyncLocal`, an activity) of whoever first resolved the handler.
- **A live lease can no longer be missed by a closing session** (on `main` since #94; found by a
  code read in the PR #109 review's adversarial pass). A live push writes the session's leasing
  flag and then reads its closed mark; the close writes the closed mark and then reads the leasing
  flag, so that one of the two always sees the other and the close gives back every lease. Both
  writes were volatile writes, which release/acquire ordering allows to move after the read that
  follows them, so a lease could be left until its timeout. Both are now full fences
  (`Interlocked.Exchange`). No deterministic test can force the reordering; the close and lease
  tests still pass.

### Security — mediator service threat model and residuals (#95)

- **Who can call what.** Anyone who reaches the mediator's endpoints can post forwards — relayed
  only to a registered `next` — and fetch the invitation, which shows only the primary routing DID.
  Only authcrypt peers that pass replay admission can enrol or pick up (unchanged).
  `RevokeAndPurgeAsync` and `PurgeExpiredAsync` are host APIs, not reachable from the wire.
- **Where pushes go, and what.** Direct push sends only to the endpoint the host's directory names,
  never one from the message; only `http`/`https`; always through the outbound endpoint guard; only
  a message the queue still holds for delivery, taken by id under the push's own lease just before
  it is sent, so an expired, aged-out, acknowledged or purged message is never pushed and a message
  a recipient is fetching is not sent twice; one push per message, after the 202 and off the
  forward pump, as one request to exactly that endpoint — no retry, no redirect followed (a
  redirect could otherwise send the push to a host the directory never named), outside the HTTP
  transport's circuit breaker (which one recipient's failing endpoint could otherwise open for every
  send of the host). Concurrency, backlog, per-grant jobs and the time of each push are bounded —
  the deadline holds even against a callee that ignores cancellation — recipients are served in
  turn with one push in flight each, and a skipped push leaves the message queued. Residuals: a push
  abandoned at its deadline may still land later (delivery is at-least-once); anyone can make the mediator
  post up to `MaxAdmissiblePayloadBytes` to a registered recipient's push endpoint, as they could
  post there directly; a directory that points at the mediator itself allows a bounded onion
  re-entry (a misconfiguration); `IHttpClientFactory`'s request logging writes push endpoints at
  Information unless the host filters it (see Fixed, above).
- **No recipient holds up the others** (PR #109 review). The forward pump only stores. Earlier
  builds of this change made the live push on the pump, and packing a `delivery` resolves the
  recipient's DID — a network method lets the recipient decide how long that takes — so one live
  recipient whose DID resolved slowly left every other recipient's acknowledged forwards unstored,
  and could get them dropped once the forward backlog filled. Live and direct pushes now run on the
  push workers, per grant, one in flight each, under the push deadline. A third adversarial pass on
  that design found two more gaps, both fixed: a per-grant backlog of 16 left part of a live
  recipient's burst never pushed (the backlog is now sized from the pickup quotas), and grants
  whose resolution stalls could hold every worker push after push (they are now paused for
  2 × `PushTimeout` after a deadline). Residual: the workers are shared, so with K grants that stall
  every push and W workers a healthy recipient's push can wait about ceil(K / W) × `PushTimeout`,
  and once K reaches 3 × W the stalled grants, each back after its pause, can keep every worker busy
  and that wait recurs; nothing is lost and pickup always works; an allow-list `IMediationPolicy`
  closes it (grants are free under the default policy).
- **Push jobs hold bounded values** (PR #109 review). A push job outlives the forward handler's
  byte reservation. Earlier builds of this change kept the forward's id as it arrived, which the
  message model does not bound: 16 jobs with 524 288-character ids kept 16 MiB alive while the store
  counted only the payloads, up to about 1 GiB across the backlog those builds allowed. A job now
  keeps two bare DIDs of at most 2 048 characters, the queue's record id (at most 64) and the
  forward's id shortened for logs (at most 128), and refuses anything wider.
- **A push abandoned at its deadline.** A direct push gives its lease back at once. A live push's
  lease is the session's: it is given back when the abandoned send ends without the message going
  out (at once for the library's WebSocket session, which honours the cancellation); a host session
  that ignores the cancellation and reports the message sent later keeps it leased until
  `messages-received`, its close or the lease timeout. An abandoned send may still reach the
  recipient, which may then see the message twice (at-least-once).
- **Filling a recipient's quota.** Delays are capped (residual, accepted by the owner: a sender can
  keep a recipient's quota occupied with messages it cannot see for at most `MaxForwardDelay` after
  it stops sending). Queue ids are 128-bit random, so they cannot be predicted from one grant to
  another and a host's id generator is never involved. The age limit only removes (residual,
  accepted: a recipient offline longer than the limit loses those messages silently). Under open
  enrolment, self-made grants can still use up the store-wide bounds: use an allow-list or
  rate-limiting `IMediationPolicy` (an availability limit, like W-2). A forward is outside replay
  admission (FR-RR-08), so a captured forward posted again is relayed again (residual): without
  direct push the copies wait within the recipient's quota; with direct push on each replay is
  pushed again, and since a delivered push frees its place, the quota does not limit how often —
  recipients should drop messages whose id they have already seen. The delay cap cannot be lifted
  by a later registration: start-up refuses forward settings without it.
- **Timing on `next`.** The 202 is committed before the forward processor and the sink run, the
  inline recipient lookup does the same work either way, and the inline paths for a known and an
  unknown `next` differ by a queue reservation against a log line that is now rate-limited; the
  re-check and the pushes run after the 202.
- **Revocation race.** Closed for the pipeline: a forward admitted before `RevokeAndPurgeAsync` that
  stores its record after the purge removes that record itself, and a push, live or direct, checks
  the registration again and takes the record by id before sending, so it never sends what the
  purge removed. Residuals: a delivery or push already being sent completes; a
  peer granted mediation again during the call loses what was stored for the new grant before the
  purge (deny it in the policy first); if the second lookup or the removal fails, the record stays
  until it expires or ages out.
- **Logs.** Every message id on the receive path is written bounded to 128 characters ("(id of N
  characters)" beyond), and a stranger's message type never reaches a log line; the mediator's, the
  forward handler's and the receive endpoints' lines carry ids, counts, setting values and
  exception type names — never a DID, a queue record id, an endpoint or exception text (Fixed,
  above). Rate-limited, the first and every 1000th at their level and the rest at Debug: the forward
  handler's drop lines (per reason), the mediator sink's drops, `InboundAdmission`'s
  malformed-`return_route` and replay-refusal lines, and the dispatcher's FR-THR-04 rule-2 drop.
  Not rate-limited — one line per message, so a stranger can make them grow, though never with text
  of its choosing: the dispatcher's no-handler Information line, the endpoints' Warning for an
  envelope that cannot be unpacked ("rejecting inbound envelope"), the consumed pure-ACK Information
  line and the opt-in Trace 2.0 line. The library's HTTP transport no longer logs the endpoint
  (Fixed, above).
- **Start-up fails closed.** A routing DID that does not resolve, or whose endpoint or keys are
  unusable, stops the host from starting instead of advertising a mediator nobody can reach; so do
  forward settings in effect that are not the mediator's.
- **Forwards addressed to the mediator itself.** They are opened inline, on the sender's request and
  before any reservation, so the sender's own request pays for its layers and the shared forward
  pump never does (the first #95 version opened them on the pump, where a stranger's slow DID held
  up other recipients' relays). Only an anoncrypt envelope around a plaintext message without
  `from_prior` is opened, refused otherwise from its shape before any DID is resolved, so opening
  resolves no DID the sender chose (the next version opened any shape and resolved the sender and
  signer DIDs inner layers named). One request costs at most 8 decryptions after the endpoint's
  own, every layer within `MaxReceiveBytes`; a chain of layers is held at most the delay cap (the
  holds are not added); a layer that fails to open is a malformed drop, so a stranger cannot raise
  the `Faulted` count, and neither can a recipient whose DID a rewrap cannot resolve
  (`Dropped-Unknown`);
  skipping the registry lookup for the decrypting DID tells a sender nothing it could not read from
  the mediator's public DID document, and the response does not tell a self-addressed `next` from
  any other by status, headers, body or time within the tested tolerance.
- **Not changed, for #98.** A captured, not-yet-sent authcrypted `live-delivery-change` sent first
  on a fresh socket holds the victim's live session: the message is authenticated, the channel is
  not bound to it (the same on `main`).
- **Dependency graph.** Everything is a singleton; the sink, the push dispatcher and the metrics
  take nothing that leads back to the dispatcher, the handlers or the facade; `DidCommClient` and
  the directory are resolved on the first direct push; the hosted service resolves when it starts;
  the forward handler's factory is cycle-guarded.
- **Endpoint composition.** Same-socket replies are on only for the mediator's WebSocket endpoint,
  with STOMP off there; sessions open only for authcrypt messages with `return_route: all` that
  passed replay admission (the #91 rules); HTTP keeps the real transport acknowledgement, and both
  endpoints keep replay admission.
- **Clock.** Aging is judged by each caller's clock reading inside the store, so a request that read
  the clock just before a sweep can still deliver a record that has just aged out (reading skew
  only; a removed record never comes back). Settings are copied when each part is built.

### Changed — replay admission has its own clock-skew tolerance, 60 seconds by default (FR-RR-08, #105, epic #88)

**Behaviour change (against earlier `[Unreleased]` builds; FR-RR-08 is new since 1.5.1):** an
in-scope message — authcrypted with `return_route` other than `none`, or a Coordinate Mediation /
Message Pickup message — whose `created_time` is up to **60 seconds ahead** of the receiver's clock
is now **admitted**. Before, the replay guard took its tolerance from
`DidCommOptions.ExpiresClockSkew`, which defaults to zero, so a peer whose clock ran even a second
fast had such messages dropped silently (HTTP `202`, no reply) — between two real hosts,
intermittent failures of mediation and pickup.

- **New:** `ReplayProtectionOptions.ClockSkew` (`TimeSpan`, default 60 s): how far ahead of this
  host's clock a `created_time` may be (refused as `FutureDated` beyond it), and how much longer than
  `Window` an admitted entry is kept (`created_time + Window + ClockSkew`). Whole seconds, floored.
  `Validate()` refuses a negative value and one over `int.MaxValue` seconds.
- **`DidCommOptions.ExpiresClockSkew` is unchanged (default 0) and now governs expiry only**
  (FR-API-05, and the FR-ROT-05 / FR-ROUTE-09 checks that share its arithmetic). It no longer
  affects replay admission, and `ClockSkew` never lets an expired message through. A host that
  raised `ExpiresClockSkew` to its clock offset, as the #92 notes advised, should move that value to
  `ReplayProtectionOptions.ClockSkew` and set `ExpiresClockSkew` back: it now only accepts expired
  messages for longer.
- **Where it applies:** the receive endpoints (HTTP and WebSocket, with `AddDidComm`'s guard or the
  endpoints' per-application fallback) and the client's inbound consumer for messages pushed on a
  WebSocket session socket. One container has one value for both roles. An HTTP
  `SendResult.Returned` is not replay-admitted (FR-RR-07, #92), so no replay setting decides it.
  A host-registered `IInboundReplayGuard` applies its own rule.
- **Cost:** an entry for a message stamped now is kept 6 minutes instead of 5, so the defaults
  sustain about 91 admitted in-scope messages per second (`MaxEntries`) and 2.8 per sender
  (`MaxEntriesPerPeer`) instead of 109 and 3.4. A sender that stamps `now + ClockSkew` holds each
  entry `Window + 2 × ClockSkew` (7 minutes), which is also how long a fresh-DID flood can keep the
  budget full. Memory bounds are unchanged.
- **`InMemoryReplayGuard`** no longer depends on `DidCommOptions`: its constructors are
  `(ReplayProtectionOptions)` and `(ReplayProtectionOptions, ILogger<InMemoryReplayGuard>?)` (the
  type is unreleased). It now validates and copies its settings when it is built, so a later change
  to the options object has no effect (before, `Window` was read on every call, and raising it
  opened a gap between retention and age rejection).

### Security — a captured request is never re-admitted when clock readings arrive out of order (FR-RR-08, #105)

Found by the #105 red-team and QA passes; present since #91, on `main` too. The endpoints read the
clock before the replay guard takes its lock, and the guard purged with whichever reading reached
the lock. So if another request read the clock later but took the lock first and purged a
captured request's entry, the replay — read earlier, at the last second of its window — was
admitted again and its handler re-ran. At `ExpiresClockSkew` 0 (the old default) a one-second
inversion across a second boundary was enough; a host clock stepping back did the same.

- The guard now remembers the latest instant it purged at and refuses as `TooOld` any timestamped
  message whose entry would already have expired by then (`created_time + Window + ClockSkew`
  earlier than that instant): it may have been forgotten. With clock readings in order this is the
  ordinary age rule. A reading D seconds behind refuses in-window messages older than
  `Window + ClockSkew − D` by that reading; a message stamped at the reading itself is refused only
  once D exceeds `Window + ClockSkew`.
- This also closes the #91 limitation W-1 (a clock that jumps forward past an entry's retention and
  back re-admitted the request). The price, stated in the PRD and the XML docs: after a clock step
  back of more than `Window + ClockSkew`, fresh in-scope messages are refused as `TooOld` until the
  clock is back within that span of the latest purge instant, or the process restarts —
  availability, never integrity. Such refusals are logged as a rate-limited Warning with the lag in
  seconds, so a guard left behind by a far-future clock reading is visible.
- A second red-team pass on the fix ran a 92-million-call randomized search (non-monotonic clocks,
  saturation, timestamp-free entries, extreme settings and instants) against an oracle written from
  the documented rule: no message was admitted twice. The same oracle finds thousands of double
  admissions in the guard as it was before this fix.

Tests: the guard's boundaries at the default (59 and 60 s ahead admitted, 61 refused) and at
`ClockSkew` 0 (the old rule, compared line by line with `main`), second-by-second sweeps through
retention for future-dated `created_time`, out-of-order readings and clock steps (unit and over the
TestServer endpoint, with a guard that parks the replay between its clock read and the lock), a
random-walk comparison with an independent model of the rule, concurrency, validation, settings
changed after construction; `ExpiresClockSkew` no longer changing admission, and `ClockSkew` never
changing FR-API-05, FR-ROT-05 or FR-ROUTE-09 expiry; the client's inbound consumer; and two hosts
whose clocks are 30 s apart (and 59, 60, 61, 90, 300, 301 s) completing trust ping with
`return_route: all`, enrolment, pickup and live delivery over HTTP and WebSocket in both
directions, with `ClockSkew = 0` negative controls that fail where `main` failed.

### Added — Message Pickup 3.0, mediator role: queue, deliveries, acknowledgements, live mode (FR-PICK-01..10, #94, epic #88)

A mediator can now hold messages for the recipients enrolled with it and hand them over when they
ask (didcomm.org [messagepickup 3.0](https://didcomm.org/messagepickup/3.0/), PRD §8c). It is the
second piece of the mediator service, in the `DidComm.Mediator` package under the namespace
`DidComm.Mediator.Pickup`, still without any ASP.NET Core reference. What puts forwarded messages
into the queue — the delivery pipeline — is #95 (above); the recipient-side client is #96; Pickup
4.0 is #100 and reuses the internals built here.

- **`AddMessagePickup(configure?)`** on `DidCommBuilder` registers `MessagePickupHandler`,
  `MessagePickupQueue` and, unless the host registered its own first, `InMemoryMessageStore`.
  Discover Features then discloses `https://didcomm.org/messagepickup/3.0` with
  `roles: ["mediator"]` (FR-PICK-01). It needs the mediation registry: call
  `AddCoordinateMediation()` too, before or after; without an `IMediationStore` the first use throws
  `InvalidOperationException` naming the fix. Over a WebSocket the receive endpoint must have
  `AllowSameSocketReplies` on, or pickup stays silent there. Bad settings throw `ArgumentException`
  at registration.
- **`MessagePickupHandler`** answers the four requests of the protocol:
  - `status-request` → `status` with `message_count`, `total_bytes`, `live_delivery`, the
    `recipient_did` of the request when it had one, and — when something is waiting —
    `longest_waited_seconds`, `newest_received_time` and `oldest_received_time` in whole UTC epoch
    seconds (FR-PICK-02). Delivered-but-unacknowledged messages are still counted; messages that
    are not yet due are not.
  - `delivery-request` → one `delivery` whose attachments carry the queued messages byte for byte,
    in arrival order, at most `limit` of them and at most `MaxDeliveryBatch` (default 10); or
    `status` when nothing is waiting or `limit` is 0 (FR-PICK-03). Attachment data is base64url
    without padding. A negative, fractional or non-numeric `limit` is malformed; one of any size is
    clamped.
  - `messages-received` → the listed messages are removed and a `status` comes back (FR-PICK-04).
    Ids the peer's grant does not hold are ignored, never reported and never echoed.
  - `live-delivery-change` → `status` showing the new state; or, when `true` arrives on a connection
    that cannot push (HTTP, or no session), the Report Problem 2.0 message with code
    `e.m.live-mode-not-supported` and comment `Connection does not support Live Delivery`
    (FR-PICK-07). `false` there is simply answered `status`.
  - Replies carry `thid` = the request's `thid`, or its `id` when it has none — on `status` too,
    which the 3.0 page does not show and 4.0 requires. The problem report carries it as `pthid`.
- **Who may see what (FR-PICK-01/02).** Only a request with an authcrypt layer is acted on; the
  peer is the DID of the authcrypt sender key, never the plaintext `from`. Anything else is
  dropped before its type or body is read: nothing removed, nothing changed, no reply, one
  rate-limited log line with the message id. A peer sees what is held under its own mediation grant
  and nothing else. A `recipient_did` it has not registered — another peer's, nobody's, or a DID
  URL — is answered exactly like an empty queue, so the reply says nothing about other recipients.
- **A delivery stays within a byte budget (FR-PICK-03).** `MessagePickupOptions.MaxDeliveryBytes`
  (default: `DidCommOptions.MaxReceiveBytes`, 1 MiB) is the largest packed `delivery` the mediator
  sends. A batch is chosen by arithmetic (a delivered message is base64-encoded twice, so the
  packed size is about 16/9 of the payload plus fixed allowances) and then checked on the real
  bytes: the handler pushes the `delivery` through the session with a size bound, and when the
  packed message is over it, takes the last message out and sends again. A message that could not
  be delivered alone within the budget is not accepted into the queue in the first place:
  `MessagePickupQueue.MaxAdmissiblePayloadBytes` is 564 525 bytes (about 551 KiB) at the defaults,
  and is logged when the role is first used. Five 300 KiB messages therefore travel one per
  `delivery`, over HTTP and over a WebSocket.
- **Leases (FR-PICK-04).** A delivered message is not deleted and not delivered again until its
  lease ends: by `messages-received` (removed), after `LeaseTimeout` (default 60 s), or — over a
  WebSocket — when the connection closes. It is always the same stored record with the same id;
  nothing is copied or moved. Delivery is therefore at-least-once: a recipient can see a message a
  second time and acknowledges by id.
- **Live mode (FR-PICK-06/08).** After `live-delivery-change{true}` on a WebSocket session, a
  message arriving for that recipient is pushed as a `delivery` with one attachment and no `thid`,
  and kept under a lease until acknowledged. The flag belongs to the connection and ends with it
  (the session's own `Closed` signal, not the registry's notifications); a new connection starts
  with live mode off. Messages already queued are not pushed. With several live connections of one
  recipient, the newest gets the push.
- **`MessagePickupQueue`** is what a delivery pipeline (and a host) calls:
  `DeliverAsync(recipientDid, payload, options?)` looks up the grant the DID is registered under,
  stores the message under it and reports `PushedLive`, `Queued`, `NotRegistered`, `Expired`,
  `PayloadTooLarge`, `QueueFull` or `Faulted` — an outcome, never an exception, and never something
  to show the message's sender (FR-PICK-09). `RemoveAsync` takes a message out again (for a
  pipeline that delivered it another way) and `PurgeExpiredAsync` drops what has expired. One call
  per recipient gives each its own record and id (FR-PICK-05).
- **`IMessageStore`** and **`InMemoryMessageStore`**: the queue's storage. Every operation is
  confined to one grant, and a message id means something only under its grant, so one recipient
  can never acknowledge or probe another's messages. Enqueue is one check-and-insert against the
  bounds; leasing is one select-and-mark; a lease is a mark with its own id, and giving messages
  back names that id, so a late clean-up cannot undo a lease someone else has taken since. A
  message that could not be delivered for now is passed over: a second mark, with no owner, that
  only time ends. Time is passed in by the caller. Only the in-memory store ships; the interface's remarks state the
  contract a durable one has to keep.
- **Bounds (FR-PICK-10).** Per grant: `MaxQueueMessages` (100) and `MaxQueueBytes` (8 MiB). For the
  in-memory store as a whole: `MaxStoreMessages` (5 000) and `MaxStoreBytes` (128 MiB) — modest on
  purpose, the in-memory store being for samples, tests and small hosts; about 170 MiB in the worst
  case at the defaults. Waiting, delivered-but-unacknowledged, live-pushed and not-yet-due messages
  all count; expired ones never keep a new message out. A `messages-received` may carry at most
  `MaxAckIds` ids (default: batch × 10); a longer list is malformed and is refused from its length
  before any entry is read. A `recipient_did` or id longer than 2048 characters is refused by a
  length check.
- **Removed DIDs and revoked grants.** Registration is checked when messages are delivered: what is
  queued for a DID that no longer belongs to the peer's grant is not delivered, and is removed as
  an unfiltered `delivery-request` comes to it (exactly the records that request looked at). A peer
  whose grant was revoked gets an empty `status`. The store has `PurgeRecipientAsync` and
  `PurgeGrantAsync` for the host. **End a peer's mediation with
  `MessagePickupQueue.RevokeAndPurgeAsync`** (#95, above), which revokes the grant and purges what
  it holds in one call. A host that revokes through `IMediationStore.RevokeAsync` alone must call
  `PurgeGrantAsync` itself, or the revoked grant's leftovers stay in memory — not delivered while
  the peer has no grant, and delivered again if the same peer is granted mediation again.
- **Failures stay inside.** A message store, mediation store, session registry or session that
  throws, or returns something outside its contract, never throws through the
  dispatcher: the request gets no reply, the leases it took are released, and a delivery to the
  queue reports `Faulted`. Log lines carry the request's message id (replaced when longer than 128
  characters), exception type names and counts — never a DID, a `recipient_did`, a queue message
  id, a thread id or exception text. A malformed body from an authenticated peer is a
  `MalformedMessageException` whose text names the member and the rule only.
- **`MessagePickup`**: the six message type URIs, a `Create…` builder and a strict `TryParse…` for
  each message, for both sides of the protocol. The `delivery` parser decodes attachment data
  leniently (padded or not, either base64 alphabet, or `data.json`) and does not inspect the
  payload.

Things to know before running a mediator on this:

- **Acknowledge live deliveries.** A pushed message stays stored and counted until
  `messages-received` names it (the Pickup 4.0 rule, applied under 3.0 where the page is silent). A
  recipient that never acknowledges fills its own quota, and further messages for it are refused.
- **A recipient whose DID document lists many keys gets smaller batches.** Each key adds an entry
  to the envelope. When the envelope outgrows the allowance (about 32 KiB), batches are trimmed.
  A message that does not fit such a recipient even alone is neither delivered nor removed: the
  request passes it over (a warning in the log) and goes on to what is queued behind it. Passing
  over is a mark the store keeps on the message itself (`IMessageStore.PassOverAsync`,
  `QueuedMessage.PassOvers`): the message is left out for `LeaseTimeout` the first time and for
  twice as long each time it is tried again and still does not fit. Because the mark is not a
  lease, it survives lease expiry and reconnects, so however many such messages there are and
  however rarely the recipient asks, its requests get past them to what can be delivered. Such a
  message stays stored, counted in `status` and in the quota, until it expires, reaches the mailbox
  age limit (#95, above) or the host purges it — so a recipient should not loop on
  `message_count` alone. Raise `MaxDeliveryBytes` (and the recipients' `MaxReceiveBytes`) if you
  serve such recipients.
- **One request does a bounded amount of work.** At most 2 × `MaxDeliveryBatch` lease rounds and as
  many packs, whatever the queue holds; a request that runs out answers `status` and the next one
  continues.
- **Messages that are not yet due use quota without showing in `status`.** A sender that asks for
  long delays can therefore fill a recipient's quota with messages the recipient cannot see or
  fetch until they come due. Under `AddMediator()` (#95, above) the honoured delay is capped at
  `MaxForwardDelay`, 1 hour by default, so this lasts at most that long after the sender stops; a
  host that calls `DeliverAsync` itself sets its own not-before times.
- **A live connection whose peer stops reading** can hold up to `MaxPendingPushes` packed
  deliveries (16 by default, each up to `MaxDeliveryBytes`) until it closes.
- **Clean-up after a closed connection is bounded.** Ending the leases of a closed WebSocket is a
  store call made in the background, one at a time, from a backlog of at most 1 024 connections;
  a connection that never took a lease queues nothing. When the backlog is full (a store that
  cannot keep up with connection churn) further closes are not queued: their leases end by
  `LeaseTimeout` instead. Closing never waits for the store.
- **A request without a return-route session gets nothing over the library endpoints.** A
  `delivery-request` that arrives without `return_route: all` leases nothing; the handler's
  `status` reply is logged, not sent.
- **`messages-received` and `live-delivery-change` need no grant.** Both touch only the caller's own
  state (its own key in the store, its own connection).
- **Expiry is lazy.** Expired messages are never delivered or counted, but their memory is only
  given back when their grant is next touched, when the store runs full, or when the host calls
  `MessagePickupQueue.PurgeExpiredAsync` — which `AddMediator()`'s hosted service does every
  `PurgeInterval` (#95, above).

### Added — a size-bounded send on return-route sessions (FR-RR-04/05, #94)

- **`IReturnRouteSession.TrySendAsync(message, maxPackedBytes, ct)`** returns
  `ReturnRouteSendResult.Sent`, `NotSent` or `TooLarge`. The bound is compared with the packed
  envelope — the bytes the HTTP response or the WebSocket frame would carry — after packing, so it
  is exact. A message over the bound is neither held nor queued, and the HTTP slot or WebSocket
  push slot it took is given back, so a smaller message can follow on the same session. The
  two-argument `TrySendAsync` is unchanged (it is the bounded send with no bound). Message Pickup
  uses it to keep every `delivery` within `MaxDeliveryBytes`: the component that packs is the one
  that measures. `IReturnRouteSession` has not been released yet, so the new member is a plain
  interface member; a custom implementation has to add it.

### Changed — default bounds of the in-memory mediation store (#93 follow-up, owner decision)

- `CoordinateMediationOptions.MaxGrants` is now 1 000 (was 10 000) and `MaxRecipients` 10 000 (was
  100 000): the in-memory stores are for samples, tests and small hosts, so their defaults are
  modest. Worst-case key memory at the defaults is about 45 MB instead of about 450 MB.
  `MaxRecipientsPerGrant` (256), `MaxUpdatesPerMessage` (1024) and `MaxPageSize` (100) are
  unchanged. Neither value has shipped in a release.

### Added — Coordinate Mediation 3.0, mediator role: the new `DidComm.Mediator` package (FR-MED-01..06, FR-MED-08, #93, epic #88)

An agent can now act as a mediator that recipients enrol with: a recipient asks for mediation, is
told which mediator DID to publish as its endpoint, and registers the DIDs it wants forwards
accepted for (didcomm.org [coordinate-mediation 3.0](https://didcomm.org/coordinate-mediation/3.0/),
PRD §8b). This is the first piece of the mediator service and ships in a **new package,
`DidComm.Mediator`** (namespace `DidComm.Mediator.Coordination`), which references `DidComm.Core` and
`DidComm.Extensions.DependencyInjection` and nothing from ASP.NET Core. Message pickup is the
entry above (#94); the delivery pipeline (#95), the recipient-side client (#96) and the 2.0
compatibility handler (#99) follow. No existing package changes behaviour.

- **`AddCoordinateMediation(configure?)`** on `DidCommBuilder` registers the handler and, unless the
  host registered its own first, an in-memory store, a grant-everyone policy and the routing-DID
  list from the options. Discover Features then discloses
  `https://didcomm.org/coordinate-mediation/3.0` with `roles: ["mediator"]` (FR-MED-01).
  - It also makes the store the forward recipient directory (`IForwardRecipientRegistry`), so
    `AddRoutingMediator()` relays a forward exactly when its `next` is a registered recipient DID.
    **Call it before `AddRoutingMediator()`.** A directory that is already registered makes the call
    throw `InvalidOperationException`; one registered afterwards is caught, with the same exception,
    when the handler is first built. Either way the mediator cannot end up silently dropping every
    forward for the recipients it enrols. `CoordinateMediationOptions.UseAsForwardRecipientRegistry
    = false` opts out for a host that keeps its own directory.
  - Calling it more than once is harmless: nothing is registered twice and the option delegates
    compose. Bad settings (a bound below 1, a routing DID that is not a bare DID of at most 2048
    characters) throw `ArgumentException` at registration; the DID is not quoted.
- **`CoordinateMediationHandler`** answers the three requests of the protocol (FR-MED-02..06):
  - `mediate-request` → `mediate-grant` carrying `routing_did` as an array, or `mediate-deny`. A peer
    that already holds a grant gets the same grant again with its registrations kept, and the
    policy is not asked a second time. `mediate-deny` comes from the policy, or from a store that
    cannot hold another grant. With no routing DID configured the grant carries the DID that
    decrypted the request.
  - `recipient-update` → `recipient-update-response` with one result per entry, in the order sent:
    `success`; `no_change` (the peer already registered that DID); `client_error` (not a bare DID —
    a DID URL is refused, never reduced to its DID — or held by another peer, or a removal of
    something this peer does not hold, or one of the mediator's own DIDs, or refused by the policy,
    or over the per-peer limit, or the peer has no grant); `server_error` (the store or the policy
    failed, or the mediator-wide limit is reached). The mediator's own DIDs are the DID that
    decrypted the request, the routing DIDs in the peer's grant, the routing DIDs configured now
    (they may be newer than the grant) and `DidCommOptions.OwnIdentifiers`. If that list cannot be
    read — the `IMediatorIdentity` throws — the guard fails closed: every entry of the message gets
    `server_error` and nothing is applied.
  - `recipient-query` → `recipient` with one page of the peer's own DIDs in registration order.
    `limit` is clamped to `MaxPageSize` (default 100); zero or less gives an empty page; an offset
    past the end gives an empty page with `remaining: 0`; without `paginate` the first page is
    returned. `pagination` is always present.
  - Replies go from the DID that decrypted the request to the peer, with `thid` set to the
    request's `thid`, or its `id` when it has none (FR-MED-04). The reply is produced whether or not
    the request opened a return-route session; the endpoint decides how it travels (FR-MED-03).
- **Who counts as a peer (FR-MED-05).** Only a request with an authcrypt layer is acted on, and the
  peer is the DID of the authcrypt sender key, never the plaintext `from`. A plaintext, anoncrypt,
  signed-only or `anoncrypt(sign)` request is dropped before its body is read: nothing is granted
  or changed and nothing is sent back. The rule is the one that decides whether a message may open
  a return-route session, so the two cannot disagree.
- **`CoordinateMediation`**: the seven message type URIs, a `Create…` builder for each message and a
  strict `TryParse…` for each (FR-MED-02). A parser returns `false` for a different message type and
  throws `MalformedMessageException` for the right type with a bad body: an unknown `action` or
  `result`, a missing or non-array `updates`, a `paginate` without `limit` or `offset`, a string or
  fractional or negative-offset paging value, a DID member that is not a string or is longer than
  2048 characters. The text names the member and the rule and never quotes a value from the
  message. A `routing_did` given as a single string (the 2.0 shape) is accepted; the library always
  writes the array. `mediate-request` and `mediate-deny` are written with `body: {}` and accepted
  without a body. Members the protocol does not define are ignored. Each builder takes an optional
  `IMessageIdGenerator`.
- **`IMediationStore`** and **`InMemoryMediationStore`**: grants and the DIDs registered under them.
  A DID belongs to the first grant that registers it, and the check and the insert are one step
  under one lock, so two peers can never both get the same DID. The store reports what happened to
  each update as a `RecipientStoreOutcome`; the handler turns that into the protocol's `result`, so
  a custom store never deals in wire values. Three bounds, all configurable: `MaxGrants` (1 000),
  `MaxRecipientsPerGrant` (256), `MaxRecipients` (10 000) — modest on purpose, since the in-memory
  store is for samples, tests and small hosts; its keys take about 45 MB in the worst case at those
  defaults. The lookup the forward path uses,
  `FindGrantByRecipientDidAsync`, is one probe that takes no lock and does the same work whether or
  not the DID is registered. `RevokeAsync` ends a grant and frees its DIDs. Only the in-memory store
  ships; a durable one is the host's (epic decision 1), and the interface's remarks state the
  contract it has to keep.
- **`IMediationPolicy`** (default `DefaultMediationPolicy`: grant and allow everything): the host's
  say in who gets mediation (`OnMediateRequestAsync`) and what they may register
  (`MayRegisterAsync`) — an allow-list, a rate limit, or proof that the peer controls the DID. It is
  a gate in front of the store, not a replacement for the store's one-owner rule.
  `MediationDecision.Deny` is the zero value, so a decision nobody set denies.
- **`IMediatorIdentity`**: where the granted routing DIDs come from; the default reads
  `CoordinateMediationOptions.RoutingDids`.
- **Bounds on one message (FR-MED-08).** A `recipient-update` may carry at most
  `MaxUpdatesPerMessage` entries (default 1024). A longer list is malformed and is refused from its
  length alone, before the body is re-read and before any entry is decoded or allocated, so
  whatever sits beyond the bound is never looked at. A `recipient_did` longer than 2048 characters
  is refused by a length check before anything parses it. A peer without a grant costs one store
  read.
- **Failures stay inside.** A store, policy or identity provider that throws, or returns something
  unusable, never throws through the dispatcher: the affected `recipient-update` entries get
  `server_error`, and a `mediate-request` or `recipient-query` gets no reply (the protocol defines
  no error for them). The handler's log lines carry the message id (replaced when longer than 128
  characters) and exception type names only — never a DID, a key id, a body value or exception
  text. A logging provider that throws changes nothing.

Things to know before running a mediator on this:

- **The protocol asks for no proof that a peer controls the DID it registers.** The first peer to
  register a DID gets it. Require proof in `MayRegisterAsync` if that matters to you.
- **A peer with a grant can find out that a DID is registered by someone else** (`client_error` on
  `add`; the protocol fixes the result values). Callers without a grant learn nothing.
- **Open enrolment can be filled.** The default policy grants any authenticated sender and grants do
  not expire, so self-made DIDs can use up `MaxGrants` and `MaxRecipients`. New peers are then
  denied and new registrations fail; existing ones keep working. A public mediator should use an
  allow-list or a rate limit.
- **With no routing DID configured, the peer chooses which of the mediator's DIDs becomes its
  `routing_did`** (the one it addressed), and nothing yet checks that DID's document for a usable
  endpoint. Configure `RoutingDids`; `AddMediator()` (#95, above) requires at least one and checks
  each one's DID document when the mediator starts.
- **Revoking a grant stops new forwards, not ones already admitted.** A forward the forward handler
  accepted before the revocation (for example one waiting on `delay_milli`) still reaches the sink.
- `DidComm.Mediator` has no released version yet, so SDK package validation is off for it until the
  first release names a baseline. The FR-DX-01 sample-coverage gate now scans `DidComm.Mediator` too
  (eight assemblies since #95 added `DidComm.Mediator.AspNetCore`); the
  new package's members are allow-listed with a `TODO(#97)` marker until sample 11 demonstrates them.

### Added — return-route client side (FR-RR-07, FR-RR-09, #92, epic #88)

This agent can now ask a peer to send messages back over the connection it used, and take them
in: as the HTTP response to its request, or pushed on a WebSocket it opened (the DIDComm
return-route extension, PRD §8a; the receiving side is #91 below). Messages that fall under the
receiver's replay protection are timestamped automatically. The mediator clients (#93, #94, #96)
build on it. The new types live in `DidComm.Transports.ReturnRoute`.

- **`SendOptions.ReturnRoute`** (`ReturnRouteMode?`) and **`SendOptions.ReturnRouteThread`**
  (`string?`), both init-only (FR-RR-07). `null` (the default) adds nothing and leaves a header the
  message already carries (`MessageBuilder.WithReturnRoute`) as it is; `None` sends an explicit
  `"none"` (the downgrade shape); `All` and `Thread` send those values and replace a header the
  message carries. `ReturnRouteThread` is required with `Thread` and invalid with any other value,
  `null` included; it must be a possible `thid` (RFC 3986 unreserved characters, FR-MSG-11) of at
  most 2048 characters. An undefined mode throws `ArgumentOutOfRangeException` and a bad thread
  value `ArgumentException` (the value is not quoted), both before anything is resolved. The
  header is written by the same code as `MessageBuilder.WithReturnRoute`, so both produce
  byte-identical headers, and it is written on a copy: the caller's `Message` is never modified.
- **What `all` / `thread` does to `SendAsync`.** "The effective header" is the one the message goes
  out with, from `SendOptions.ReturnRoute` or from the message itself; a malformed value counts as
  `none`, as the receiver reads it (FR-RR-01). With an effective `all` or `thread`:
  - recipients naming more than one DID → `ArgumentException`, before anything is resolved (replies
    come back over one connection, from one peer);
  - a recipient reached through routing keys or a mediator (its selected service entry needs at
    least one forward layer) → `InvalidOperationException`, after the recipient's keys and service
    entry are read and before any mediator or routing-key DID, sender key or secret is looked up and
    before anything is packed (FR-RR-02). The text names no DID. With `ServiceEndpointOverride`
    there is no route, so there is no refusal;
  - over HTTP, an authcrypt send (`From` set) has the transport read a `200` DIDComm response
    body, at most `DidCommOptions.MaxReceiveBytes`, and returns the reply in `SendResult.Returned`;
  - over WebSocket, an authcrypt send opens a session socket of its own (below);
  - an anoncrypt send (no `From`) carries the header and is timestamped, but reads no response and
    opens no session socket: there is no sending DID to verify a reply against.
- **`created_time` stamping** (FR-RR-09). `SendAsync` stamps `created_time` (Unix seconds, from
  `DidCommOptions.Clock`) on its copy when the message has none and falls under the receiver's
  FR-RR-08 scope: its effective `return_route` is `all` or `thread`, or its `type` belongs to a
  mediator-service protocol (Coordinate Mediation 2.0 / 3.0, Message Pickup 3.0 / 4.0, any minor).
  A value the caller set is sent unchanged, however old: a stale request is refused by the
  receiver, never refreshed into a fresh one. A message that needs neither a header nor a stamp is
  sent as it is, with no copy. `PackEncryptedAsync` and `SendPackedAsync` stamp nothing. The return-route sessions of the
  AspNetCore endpoints stamp their replies and pushes by the same rule (Changed, below).
- **`SendResult.Returned`** (`UnpackResult?`, init-only): the reply returned in the HTTP response.
  Non-null only when the message went out with `all` / `thread` from an authcrypt sender, the
  response was `200` with a DIDComm `Content-Type`, and its body unpacked as authcrypt whose sender
  key id names the DID the send addressed and whose decrypting key id names the sending DID (DID
  subjects of the verified key ids, PRD §4.3; a signer, if any, bound to the same subject; not a
  Routing 2.0 `forward`). A reply that fails that rule is discarded, `Returned` stays `null`, and a
  Warning (EventId 6) carries the id of the message the caller sent and the reason name only. A body
  that was over the cap, did not arrive in time, failed mid-read or does not unpack fails the send
  with a `TransportException` whose `HttpStatusCode` is `200` (the request itself was delivered),
  never a raw parser exception (FR-API-07). `202`, a non-DIDComm content type or an empty body →
  `null`. The reply is neither dispatched nor replay-admitted: the caller asked for it, and what it
  proves is limited (Security, below). Over WebSocket it is always `null`: replies arrive through
  the inbound consumer.
- **`TransportRequest.ReturnRoute`** (the effective header as sent; `null` when absent, `None` when
  malformed), **`.LocalDid`** / **`.PeerDid`** (the sending and addressed DID subjects, set only for
  an authcrypt send delivered directly — no forward layer — to recipients naming one DID, when both
  are at most 2048 characters; `null` otherwise; a transport uses them for connection affinity) and
  **`.MaxResponseBytes`** (`int?`; `null` means "do not read an in-band response"; set to
  `MaxReceiveBytes` only when the send asks for `all` / `thread` and `LocalDid` is set; a transport
  reads at most that many bytes). All init-only and set only by `SendAsync`; a transport that does
  not implement the extension may ignore them.
- **`TransportResult.ResponsePayload`** (`ReadOnlyMemory<byte>?`, the raw and unverified body),
  **`.ResponseMediaType`** (the canonical `MediaTypes` constant the response's `Content-Type`
  matched) and **`.ResponseDiscarded`** (`true` when the peer returned a DIDComm body that was not
  read: over the cap, not received in time, or the read failed; `Accepted` stays `true`), all
  init-only.
- **HTTP response read** (`HttpDidCommTransport`). The body is read only when the request carries
  `MaxResponseBytes` and the final response (after a `307`, the last hop's) is `200` with a DIDComm
  `Content-Type` (FR-MSG-06 normalization); otherwise it is never touched, as before. A declared
  `Content-Length` over the cap is discarded without reading a byte; a declared length of at most
  64 KiB is read into a buffer of exactly that size; a larger declared length, or none (chunked),
  is read in 16 KiB chunks into a buffer that grows only as bytes arrive, stops as soon as the
  total passes the cap, and must match a declared length exactly. So memory follows the bytes
  received, never the length a peer declares: at most about twice the cap plus one chunk. The read
  has its own `HttpTransportOptions.RequestTimeout` budget (the resilience pipeline's timeout ends
  with the headers) and is never retried. Over the cap, late, or failed (anything the response
  stream throws, other than the caller's own cancellation, which propagates) → `Accepted` with
  `ResponseDiscarded` and one Warning naming the reason (`OverCap`, `ReadTimeout`, or `ReadFailed`
  with the exception type name) and the limit, never the URL or a body byte.
- **WebSocket session sockets** (`WebSocketDidCommTransport`). A request carrying `all` / `thread`
  and both identities opens, or reuses, a session socket S(E, L, P) of its own, keyed by endpoint,
  local DID and peer DID and never by endpoint alone (the receiver binds a socket to its first
  authenticated sender, FR-RR-05). While S exists, every send to the same (E, L, P) travels over
  it, with or without a header, so a header-less acknowledgement reaches the session it
  acknowledges. An explicit `none` is sent over S and then retires it: S stops taking sends, a
  close frame is sent, and the messages already on their way are still read until the peer's close
  frame; later sends use the endpoint's pooled socket until another `all` / `thread` opens a fresh
  S. Ordering, for the caller: a send that starts after an `all` / `thread` send to (E, L, P)
  returned uses S while S exists; one that starts after a `none` send returned uses the pool;
  concurrent sends are not ordered. A send that fails on S drops it, and its retry opens a fresh S
  (`all` / `thread`) or uses the pool (anything else). Requests without both identities keep the
  pool. Under `UseStomp` no session socket is opened (one Information line). A request that would
  open S with an identity that is not a `did:` value of at most 2048 characters without whitespace
  or control characters → `ArgumentException` (the value is not repeated); any other request with
  such an identity uses the pool. `Lifecycle` reports `Connected` when S opens and `Disconnected`
  once per socket, naming its endpoint, also from the socket's receive loop.
- **Reading session sockets.** Every session socket is read by its own loop, which sends nothing
  but close frames and never throws. A whole message (fragments reassembled, FR-TRN-09) longer than
  `DidCommOptions.MaxReceiveBytes` closes S with `1009`, whether or not `ReceiveInbound` is on:
  nothing past the cap plus one read is kept, S stops taking sends, and every later message on it
  is discarded. A close the peer starts (a receiver eviction) is answered. The retire close, the
  `1009` close and the close answer are bounded by the close grace — one `SendTimeout`, or 30
  seconds when that is infinite or not positive — after which a retiring socket stops being read.
  Pooled sockets are still never read.
- **`WebSocketTransportOptions.ReceiveInbound`** (`bool`, default `false`, read once at
  construction). On: each whole pushed message is handed to the registered `IInboundFrameConsumer`.
  Off: session sockets are still read, so the transport notices a close or an eviction and
  completes the close handshake, but every pushed message is discarded (logged once). On without a
  consumer → `InvalidOperationException` at construction (under DI, on first resolution), naming
  what to register.
- **`WebSocketDidCommTransport(IOptions<WebSocketTransportOptions>, ILogger<WebSocketDidCommTransport>?, IOptions<DidCommOptions>?, IInboundFrameConsumer?)`**,
  a new constructor with all four parameters required (pass `null` for none). The 1.5.0
  three-parameter constructor is unchanged and delegates with no consumer, so existing calls bind
  to it as before; it throws `InvalidOperationException` when `ReceiveInbound` is set. In both,
  `coreOptions` now also supplies `MaxReceiveBytes`, the cap on a pushed message (the default when
  `null`).
- **`IInboundFrameConsumer`** (`ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct)`)
  and **`InboundFrame(Uri Endpoint, string LocalDid, string PeerDid, ReadOnlyMemory<byte> Payload)`**
  (a sealed record whose equality compares `Payload` by memory identity). The contract: called from
  one session socket's receive loop, one message at a time — the socket's next message is read only
  after the call returns — while different sockets may be consumed concurrently. So a consumer, or
  a handler it runs, must not wait for another inbound message on the same session: that session
  deadlocks until the wait's own timeout. The token is the session's own: cancelled when the
  transport is disposed, when a send on the socket fails or finds it no longer open, and when a
  retiring socket has not finished closing within the close grace; a callback registered on it
  runs on the transport thread that cancels it, must be short and must not throw (a throw is
  swallowed). An exception from the consumer is logged by its type name (rate-limited) and the
  loop goes on. `Payload` is untrusted bytes, at most the receive cap; `LocalDid` and `PeerDid` are
  the transport's attribution (the identities of the send that opened the socket), not evidence.
  An implementation must not take `DidCommClient`, `ITransportRouter`, a transport,
  `ProtocolDispatcher`, `ProtocolHandlerRegistry` or a protocol handler in its constructor: it
  resolves them through an `IServiceProvider` at call time.
- **`DidCommBuilder.UseInboundDispatch()`** registers the built-in consumer (`TryAdd`: a consumer
  the host registered first wins); pair it with `UseWebSocketTransport(o => o.ReceiveInbound =
  true)`. For each pushed message it resolves the facade, the dispatcher, the replay guard and the
  options (at call time, never at construction), unpacks the message, requires authcrypt from the
  session's peer to its local DID (the `Returned` rule), applies FR-RR-08 replay admission with the
  receive side's own rule and the `IInboundReplayGuard` from DI, and dispatches it with
  `ProtocolContext.Session` `null`. Anoncrypt, signed-only, `anoncrypt(sign)`, plaintext,
  `forward`, another sender and another local DID are dropped before admission, so they consume no
  replay budget. A handler's reply is never sent (a Debug line says so): reply with an explicit
  `SendAsync`. Handlers never re-check replay. Nothing runs in the background. Every drop is
  logged — the first and every 1000th of each kind at Warning with the count, the rest at Debug —
  with no message id before the identity rule passed and the id after it, plus enum or exception
  type names; never a DID, key id, thread id, endpoint or body.
- **`DidCommClient.SendPackedAsync(ReadOnlyMemory<byte> packed, Uri endpoint, string mediaType, CancellationToken ct = default)`**
  → `Task<TransportResult>`: deliver bytes that are already packed (for example a mediator relaying
  a forward, #95) through the registered router. It always applies the SSRF guard
  (`DidCommOptions.OutboundEndpointPolicy`) and refuses the queue URI (`TransportException`); it
  requires a non-empty payload, an absolute endpoint and one of the three DIDComm media types (the
  `application/` prefix may be omitted; the canonical constant is sent) (`ArgumentException`), and a
  router (`InvalidOperationException`). Nothing is packed, stamped, resolved or read back: the
  request carries no return-route fields. Same span and failure log as `SendAsync`.
- DX gate: the new surface is allowlisted in `coverage-allowlist.json` / `execution-allowlist.json`
  until cookbook sections CC–FF / sample 11 (#97) demonstrate it. Cookbook section R no longer
  stamps its ping's `created_time` by hand: `SendAsync` does.

### Changed — return-route client side (#92)

- **With `Forward`, the service entry is read before packing.** `PackEncryptedAsync` (and so
  `SendAsync` without an endpoint override) reads the recipient's DIDCommMessaging service entry
  once, right after the recipient's keys and before the sender's keys, the signer and the pack;
  any mediator or routing-key DID is still resolved after the pack, from that same entry. The only
  observable difference: when both the service resolver and the sender-key or secret lookup would
  fail, the resolver's exception now surfaces first. An empty service list still fails after the
  pack, as before.
- **A builder-set `all` / `thread` now takes effect in `SendAsync`.** A message built with
  `MessageBuilder.WithReturnRoute(ReturnRouteMode.All)` (or `Thread`) used to be sent like any
  other. Now `SendAsync` refuses it for a routed recipient (`InvalidOperationException`, before
  packing) and for recipients naming more than one DID (`ArgumentException`), stamps its
  `created_time` when missing, and, for an authcrypt send: over HTTP reads a `200` DIDComm response
  into `SendResult.Returned`, where a body over the cap, late or unreadable now fails the send with
  `TransportException` (status `200`) instead of returning `Accepted`; over WebSocket (without
  STOMP) opens a session socket instead of using the pooled one, and later sends to the same peer
  from the same DID at that endpoint travel over it until a `none`.
- **`UseWebSocketTransport` registers the transport through an explicit factory** instead of type
  activation. It passes the same values activation resolved before (the options, the logger and
  `IOptions<DidCommOptions>` when registered) plus the registered `IInboundFrameConsumer`, so the
  consumer is wired whatever else is registered.
- **DI cycles through the facade or the WebSocket transport now fail fast.** `AddDidComm`'s
  `DidCommClient` factory and `UseWebSocketTransport`'s transport factory are cycle-guarded, like
  the dispatcher, registry and handler factories since #91: a dependency cycle through the facade or
  the WebSocket transport factory (for example an `IInboundFrameConsumer` that takes
  `DidCommClient`, a transport or the router in its constructor, or a custom `IDidCommTransport`
  that takes `DidCommClient`) fails first resolution with `InvalidOperationException` instead of
  deadlocking it. A WebSocket transport the host builds in its own factory has no such guard. The
  guard's exception text changed: it now also says that an `IInboundFrameConsumer` or an
  `IDidCommTransport` must not take `DidCommClient`, the dispatcher, the registry or a handler,
  because the facade depends on every transport; the WebSocket transport factory's guard has a
  message of its own. Code that matched the old text must be updated.
- **Return-route session replies and pushes are timestamped.** A reply or push sent through
  `IReturnRouteSession.TrySendAsync` (the HTTP `200` body or a WebSocket push, internally
  `SessionOutbound`) that falls under the FR-RR-09 scope — a mediator-service type, or `return_route`
  `all` / `thread` — now gets `created_time` from `DidCommOptions.Clock` when it has none, on the
  library's own snapshot (a value already set is kept), so a client's FR-RR-08 admission accepts
  it. Other replies, such as `ping-response`, are unchanged.
- **The WebSocket transport reads return-route session sockets** (above); it used to read no
  socket at all, and pooled sockets are still never read. Each open session socket costs one
  receive-loop task and an 8 KiB pooled read buffer. `DisposeAsync` now also closes active and
  retiring session sockets and waits for their loops (a loop held by a consumer that ignores
  cancellation is abandoned, its socket already disposed): the bound becomes
  `DrainBudget + 2 × CloseBudget` (`ConnectTimeout + SendTimeout`, plus two `SendTimeout`s instead
  of one; each clamped to a finite wait, an infinite one counting as zero). Its close-failure Debug
  line now carries the exception type name, not the exception object.
- `TransportResult`, `TransportRequest` and `IDidCommTransport` no longer describe every transport
  as delivery-only: an HTTP transport may hand back a response body, and a WebSocket transport
  hands pushed messages to its consumer, when the return-route extension is in effect.
  `MessageBuilder.WithReturnRoute` and the receive side's replay admission now share their code with
  the client (`ReturnRouteHeaderWriter`, `InboundReplayGate`); their behaviour is unchanged.
- **Upgrader note — value equality of the send records.** `SendOptions`, `SendResult`,
  `TransportRequest` and `TransportResult` are positional records, so their new init-only members
  take part in the synthesized `Equals` / `GetHashCode` / `ToString`: two instances that agree on
  every other member but differ on a new one no longer compare equal — for example two
  `TransportResult`s for the same `200` that carry different response buffers, which compared
  equal before this release. `ReadOnlyMemory<byte>` members (`TransportResult.ResponsePayload`,
  like the existing `TransportRequest.Payload` and the new `InboundFrame.Payload`) compare by
  memory identity — the same buffer and range — not by content; `SendResult.Returned` compares
  through `UnpackResult`'s own equality. Binary and API compatible — no constructor,
  `Deconstruct` or existing member changed (package validation against 1.5.0 is clean); code that
  compared these records should compare the members it means.

### Security — return-route client threat model and known limitations (#92)

- **Who registers a consumer.** The host, at startup only: `UseInboundDispatch()`, its own
  `IInboundFrameConsumer` registration, or the four-parameter transport constructor. Nothing adds
  or removes one at run time.
- **What a consumer sees and can do.** Only the bytes a peer pushed on a session socket this client
  opened itself, with the transport's (endpoint, local DID, peer DID) attribution — the client's own
  routing facts from its own request, never wire claims. A consumer holds no socket handle and
  cannot write to the transport.
- **Trust comes from authcrypt, not from the socket.** `Returned` and the built-in consumer accept
  only authcrypt whose sender key id names the addressed peer and whose decrypting key id names the
  sending DID: DID subjects of verified envelope key ids, never the plaintext `from` / `to`, with
  both compares always run (no early exit). Anything else is dropped before replay admission. A
  dispatched push gets no session and a handler's reply to it is never sent, so a peer cannot use
  the client as a reflector.
- **Bounded input.** A response body is read only for a send that asked for a return route, never
  buffered past the cap (memory follows the bytes received, not a declared length), and
  time-bounded; a pushed message is capped at `MaxReceiveBytes` (`1009` beyond it); unpacking
  applies the FR-API-09 caps.
- **Flood isolation.** Each session socket hands its own messages to the consumer, one at a time: a
  peer that floods or stalls its session slows only that socket, and no other peer's message is
  dropped or delayed.
- **SSRF.** `SendPackedAsync` always applies the outbound guard; the routed-recipient refusal comes
  before any mediator or routing-key DID is resolved.
- **Audit.** The new log lines carry message ids (on the inbound side only after the identity rule
  passed) and enum or exception type names, never a DID, key id, thread id, endpoint, body or
  exception object; lines a peer can repeat are rate-limited; all are written through guarded
  calls, so a throwing logging provider changes nothing.
- **Tested adversarially** (`ReturnRouteClientAdversarialTests`,
  `ReturnRouteClientRedTeamRegressionTests` and the QA suites): wrong-peer, wrong-local and
  unauthenticated pushes; a replayed push; an in-scope push without `created_time`; a 100 MB
  response, declared and chunked; a large declared length that never arrives; a stalled body;
  garbage under a DIDComm content type; a flood on one session beside traffic on another; retire,
  close and send races; dispose with a consumer that ignores cancellation; throwing cancellation
  callbacks; consumers and transports that close a DI cycle; a log scan for DIDs, key ids, thread
  ids and bodies.
- **Known limitations:**
  - `Returned` proves one thing: the reply was authcrypted by the addressed DID to the sending DID.
    It is not FR-RR-08 replay-admitted and it is not bound to the request — a reply the same peer
    authcrypted earlier, for another request, passes the same rule. Correlate it with the request
    on its `thid` (DD-11) and handle it idempotently. Over HTTPS only the endpoint the client chose
    (or whoever terminates TLS for it) can return one. (ADV-03, won't fix: PRD FR-RR-07 specifies
    identity only, and admitting it would drop the unstamped in-scope replies of third-party
    mediators.)
  - A message encrypted to several recipients whose first recipient key this client holds belongs
    to another local DID than the sending one is attributed to that DID and discarded, by
    `Returned` and by the consumer, even when a later key is the sending DID's. A receiver's
    return-route replies and pushes are always encrypted to exactly one peer. (ADV-05.)
  - Unpacking a hostile response or push resolves the DID its sender key id names before the
    identity rule runs, as every receive endpoint does (ADV-06, a pre-existing class). It is bounded
    by the FR-API-09 caps and `MaxReceiveBytes`, and reachable only through a return-route send or
    a session socket the client opened.
  - A send racing the peer's close may be reported accepted and still be lost: WebSocket has no
    acknowledgement.
  - A consumer or handler that waits for another inbound message on the same session deadlocks
    that session until the wait's own timeout.
  - One send gate per (endpoint, local DID, peer DID) the application ever sent to is kept until
    the transport is disposed.
  - A sending or addressed DID longer than 2048 characters (FR-API-09) gets no session socket and
    no `Returned`: the send is delivered as before return routes, without reading a response. No
    reply between such DIDs could pass the identity rule anyway.
  - **Clock skew.** FR-RR-08 admission — on the receiver, and now in the client's inbound consumer —
    refuses `created_time > now + ReplayProtectionOptions.ClockSkew`, compared in whole seconds
    (default 60 s since #105; before it, the guard used `DidCommOptions.ExpiresClockSkew`, default
    zero). Over WebSocket the mediator's replies and pushes are judged by the client's own setting;
    an HTTP `SendResult.Returned` is not replay-admitted, so no replay setting decides it.

### Added — return-route receive side (FR-RR-01..06, FR-RR-08, #91, epic #88)

A peer that authenticates itself can now ask this agent to send messages back over the connection
it used — in the HTTP response, or pushed on its WebSocket — instead of needing an inbound endpoint
of its own (the DIDComm return-route extension, PRD §8a). Mediator clients (Coordinate Mediation,
Message Pickup) depend on it; the sending side is #92 (above) and the mediator protocols #93–#95.
Requests that can open such a session or change mediator state are now protected against replay.
The new types live in `DidComm.Transports.ReturnRoute`.

- **Reading and writing the header** (FR-RR-01, DD-12). `ReturnRouteMode` (`None`, `All`,
  `Thread`) and `ReturnRouteHeader`, the parsed `return_route` / `return_route_thread` pair with the
  wire constants and `MaxThreadLength` (2048, the DID length cap). `ReturnRouteHeader.TryRead`
  never throws (except for a null message) and never fails a message: an absent header or `none`
  is `None`, `all` is `All`, `thread` with a sibling top-level `return_route_thread` is `Thread`;
  anything else — a non-string value, any other or differently-cased string, or `thread` whose
  thread value is missing, not a string, over 2048 characters or not made of RFC 3986 unreserved
  characters (the FR-MSG-11 `thid` alphabet) — reads as `None` and `TryRead` returns `false`, so
  the endpoint logs the message id. The mode is compared in place, and a thread value whose raw
  JSON is already too long to fit the cap is refused without being copied out of the message.
  `ReturnRouteHeader.Read` is the silent form. `MessageBuilder.WithReturnRoute(mode, thread?)`
  writes byte-exact `"return_route":"all"`, an explicit `"none"` (the downgrade shape),
  or `"thread"` plus `return_route_thread` (validated like a `thid`; leaving `Thread` removes a
  stale thread value).
- **`IReturnRouteSession`** (FR-RR-03): the binding between an authenticated peer and the
  connection its message arrived on. It exposes `PeerDid`, `LocalDid`, `Mode`, `Thread`,
  `Transport`, `IsLive`, `Closed` and one operation, `TrySendAsync(Message, CancellationToken)` —
  never the socket, the raw bytes or the inbound. A transport opens one only for an inbound that
  was encrypted with an authcrypt layer (`authcrypt(…)` or `anoncrypt(authcrypt(…))`: a sender key
  id, not merely a verified signature), whose sender and decrypting key ids are DIDs (and whose
  signer key id, if any, names the sender's DID), that carries `return_route: all`, is not a
  Routing 2.0 `forward` (FR-RR-02) and passed replay admission (below). `PeerDid` is the DID
  subject of the sender key id — never the plaintext `from`, never the connection; `LocalDid` is
  the subject of the key that decrypted the inbound. `TrySendAsync` snapshots the message
  (serialize → deserialize, so a caller still mutating it cannot race the check), requires
  `from` = `LocalDid` and `to` = exactly `[PeerDid]` (compared as DID subjects, so a DID URL of the
  same subject is accepted), and authcrypts it from `LocalDid` to `PeerDid` without forward
  wrapping. `true` means held or queued, not delivered. `false` — never an exception, except
  `ArgumentNullException` or cancellation of the caller's own token — when the session is
  downgraded or closed, an HTTP session already holds its one message, a WebSocket session has no
  free push slot (checked before the snapshot, so such a call costs no snapshot, key resolution or
  encryption), the addressing is refused, or the snapshot or the pack failed (logged by exception
  type name).
- **HTTP** (FR-RR-04), on the registry-aware `MapDidCommEndpoint(pattern)` — no switch. An eligible
  request gets a request-scoped session (`IsLive == false`; never registered, so it raises no
  registry events and does not count against the bounds). If the handler returns a reply, or
  pushes one message through `context.Session`, the response is `200` with the packed message as
  its body and `Content-Type: application/didcomm-encrypted+json`; otherwise `202` with an empty
  body, as before. At most one message per request: a second push returns `false`, and when a
  handler both pushed and returned a reply the push wins and the reply is dropped (logged). A reply
  that is not from the decrypting DID to exactly the authenticated sender is not sent (logged with
  the refusal name; `202`); a pack failure answers `202`, never `400`; a handler that throws after
  pushing gets the unchanged uniform `400` and its push is discarded. The 400 / 413 / 415 paths and
  the rejection-timing floor are unchanged, and the #90 `ProtocolContext.Acknowledged` completes
  once the `200` or the `202` is committed.
- **Session-aware HTTP callback**:
  `MapDidCommEndpoint(pattern, Func<UnpackResult, IReturnRouteSession?, CancellationToken, Task<Message?>>)`,
  the callback counterpart of the above. Same envelope handling and admission; the callback gets
  the session (or `null`), and a non-null message it returns goes back through the session as the
  `200` body. A callback that throws is logged by exception type name and answered `202` (never
  `500`), and anything it pushed is discarded. It carries no acknowledgement task (there is no
  `ProtocolContext`). A two-parameter lambda still binds the existing callback overload.
- **WebSocket** (FR-RR-05), on the registry-aware `MapDidCommWebSocket(pattern)`, only when
  `DidCommReceiveOptions.AllowSameSocketReplies` is `true` and never under STOMP (there, one
  Information line per connection instead). The connection becomes a session at its first eligible
  message and is registered under the peer; the peer and local DID are fixed from then on. Handler
  replies and `TrySendAsync` pushes are written as one binary WebSocket message each by a single
  writer task — the only code that sends on the socket — through a bounded queue. A push reserves
  one of `MaxPendingPushes` slots before it is snapshotted and packed and keeps it until the writer
  takes its frame off the queue (a push that fails or is cancelled gives it back); with no free slot
  it answers `false` at once — no snapshot, key resolution or encryption, and the handler never
  blocks. A later message from the same peer to the same local DID without the header leaves the
  mode unchanged; `none`, `thread` or a malformed value stops pushes (the session stays registered)
  and `all` resumes them; no new frame starts once the receive loop has processed the downgrade. A message authenticated by another DID on the
  socket is dispatched without a session and its reply is not written; it never opens or re-binds
  one, and when the registry refused the first eligible message's session only that same sender
  may try again. Closing the socket unregisters the session (`SessionClosed`, reason
  `Unregistered`) and discards queued pushes. An evicted session's socket gets a normal close frame
  ("return-route session evicted"); the connection keeps reading — dispatching, without a session,
  what the peer sent before it saw the close — until the peer answers, is cancelled after a
  5-second grace if it never does, and never opens a new session. When the total bound refuses a
  session the message is dispatched without one.
- **`IReturnRouteSessionRegistry` / `InMemoryReturnRouteSessionRegistry`**: the host's live
  (WebSocket) sessions keyed by peer DID subject. `Register` returns a `SessionRegistration`
  (`SessionRegistrationOutcome.Registered` with the evicted session, if any; `RefusedTotalBound`;
  `AlreadyRegistered`); `Unregister`; `Find(did)` (a DID or DID URL; a snapshot, oldest first;
  empty for a non-DID); `Count`; `SessionOpened` / `SessionClosed` events
  (`ReturnRouteSessionEventArgs`, `SessionCloseReason`: `None`, `Unregistered`, `Evicted`). A peer
  at `MaxSessionsPerPeer` that registers one more replaces its own oldest session — even when the
  registry is full, since the total does not grow; a registration that would grow the total past
  `MaxSessions` is refused and nothing is evicted, so a flood of fresh DIDs never displaces an
  existing peer. The registry forgets an evicted session; closing its transport is the
  registrant's job (the endpoint does it). One lock, O(1) eviction. Notifications are best-effort
  and never run on the caller's thread: each `Register` / `Unregister` that changed something
  writes that call's events — `SessionClosed(Evicted)` then `SessionOpened` for a registration —
  to a bounded queue inside the lock and returns, and one notifier per registry (started without
  any request's `ExecutionContext`; none flows from the callers either) delivers them one at a time
  in the order of the state changes, each subscriber's exception isolated and logged by type name.
  A subscriber that blocks stalls only that registry's later notifications and holds one pool
  thread — never a registration, an eviction, `Find` or the closing of the evicted socket. While
  subscribers lag, at most `MaxPendingNotifications` notifications wait; a call that finds them all
  taken drops the oldest — already superseded by the newer ones — and counts it in
  `DroppedNotifications` (a Warning with the running total for the first and every 1000th drop).
  Per-session state belongs on `IReturnRouteSession.Closed`, the authoritative per-session signal.
  The registry is `IDisposable` (the container disposes `AddDidComm`'s): `Dispose` stops the
  notifier without waiting for a subscriber, drops what was waiting, and leaves `Register`,
  `Unregister` and `Find` working. Log lines carry counts only.
- **`ReturnRouteSessionOptions`** (validated; configure with
  `services.AddOptions<ReturnRouteSessionOptions>().Configure(...)`): `MaxSessionsPerPeer` = 8 —
  several devices or tabs per DID (Coordinate Mediation 3.0 opens one socket per device) without
  one peer holding a meaningful share of the total; `MaxSessions` = 4096 — each live session is a
  socket plus its push queue, which keeps the registry's bookkeeping to a few megabytes, well inside
  a small host's connection range; `MaxPendingPushes` = 16 pushes per socket being packed or
  queued (besides the one being sent) — live delivery keeps at most one unacknowledged delivery per
  session, so this is slack, not a working set; `MaxPendingNotifications` = 1024 lifecycle
  notifications held for lagging subscribers (besides the one being delivered), each with at most
  two session references — a reconnect burst of about a thousand sessions fits without a drop, and
  a stalled subscriber can make the registry retain about 2048 sessions at most.
- **Replay protection** (FR-RR-08): `IInboundReplayGuard.TryAdmit(peerDidSubject, messageId,
  createdTime, now)`, the default `InMemoryReplayGuard`, and `ReplayAdmission` (`Admitted`,
  `Duplicate`, `TooOld`, `FutureDated`, `MissingCreatedTime`, `Saturated`, `MessageIdTooLong`). The
  registry-aware endpoints run it after unpack and before dispatch for the in-scope messages (scope
  under Changed below). Anything but `Admitted` is dropped — HTTP `202` with an empty body, nothing
  on a WebSocket — and is neither dispatched nor given a session; it is logged with the message id
  and the outcome only (the first and every 1000th per outcome at Warning, Debug otherwise), and a
  host-registered guard that throws counts as a drop. Rules: `created_time` must lie in
  `[now − Window, now + ClockSkew]` (the replay rule's own skew since #105); a message without one is
  refused unless `RequireCreatedTime` is off, in which case it is remembered for `Window`; admission
  is one atomic check-and-insert on (sender DID subject, message id), so concurrent duplicates admit
  exactly one; an entry is kept until `created_time + Window + ClockSkew` inclusive while age
  rejection starts at `now − Window`, so a captured request is never both forgotten and young
  enough to pass (since #105 also when clock readings arrive out of order). At the global or the per-sender budget a new entry is `Saturated` — not admitted, nothing
  evicted (saturation costs availability, never integrity) — and admission resumes as entries
  expire; an id longer than `MaxMessageIdLength` is refused before anything is stored. Inside: one
  lock over per-sender buckets plus a min-heap by expiry, purged on every call (amortised O(log n);
  a sender's DID subject is stored once while it has entries). `Count`, `PeerCount` and
  `SaturationCount` (the saturation metric) are there for monitoring. The guard judges time by the
  clock reading its caller passes (at the endpoints, one `DidCommOptions.Clock` reading per
  inbound), exactly as FR-API-05 expiry does.
- **`ReplayProtectionOptions`** (validated; `services.AddOptions<ReplayProtectionOptions>().Configure(...)`):
  `Window` = 5 minutes, the FR-RR-08 default; `ClockSkew` = 60 seconds (#105, below);
  `RequireCreatedTime` = `true` — with `false`, an identical captured timestamp-free request is
  accepted again once `Window` has passed (the documented limitation of that setting); `MaxEntries`
  = 32 768 — about 91 admitted in-scope messages per second sustained over the 6 minutes an entry is
  kept at the defaults on one host, with measured memory at that bound of
  about 6 MB for UUID ids, about 35 MB for 512-character ids held by a few dozen senders, and up to
  about 170 MB when every entry belongs to a different sender with a maximal 2048-character DID
  (the most a fresh-DID flood can reach); `MaxEntriesPerPeer` = 1024 — about 2.8 in-scope messages
  per second per sender over those 6 minutes, far above pickup polling plus acknowledgements and far
  below the total, so one sender cannot starve the rest; `MaxMessageIdLength` = 512 — 14× a UUID
  (FR-MSG-02 bounds the id's alphabet, not its length).
- **`MediatorServiceProtocols`**: the four mediator-service protocol identifiers (Coordinate
  Mediation 2.0 and 3.0, Message Pickup 3.0 and 4.0), `All`, `IsMediatorServiceType(type)` (same
  protocol name and major version, any minor — the handler registry's rule) and
  `IsReplayExempt(type)` (Routing 2.0). Both answer `false` for anything unparseable and never
  throw.
- **Queue transport URI** (FR-RR-06): `QueueTransport.Uri` (`didcomm:transport/queue`) and
  `QueueTransport.IsQueueTransport(string?)` / `(Uri?)`. A DID document whose service `uri` is the
  queue URI parses as before. `DidCommClient.SendAsync` refuses to send to it with a
  `TransportException` (scheme `didcomm`, a text that names no DID) before any transport is called:
  an endpoint override is refused before packing, a resolved endpoint right after packing — the
  endpoint lives in the recipient's service entry, so the recipient's DID is resolved first.
  `TransportRouter.SendAsync` refuses it as well, before consulting any transport, so a custom
  transport claiming the `didcomm` scheme is never asked.
- **`ProtocolContext.Session`** (the session bound to this inbound, or `null`: reply out of band or
  not at all) and **`ProtocolContext.ReturnRoute`** (this message's parsed header — a `thread`
  request is surfaced here but opens no session, DD-12; on a WebSocket a header-less message shows
  `None` here while the session keeps its own mode), both init-only; and
  **`ProtocolDispatcher.DispatchAsync(UnpackResult, DidCommClient?, DidCommOptions, Task acknowledged, IReturnRouteSession? session, CancellationToken)`**.
  The existing overloads keep their signatures and pass no session; the dispatcher reads the
  header on every path, so `ReturnRoute` is truthful for direct callers too. The dispatcher admits
  nothing itself: any other caller must run replay admission before dispatching and pass the
  session it opened, if any — #92's built-in inbound consumer (`UseInboundDispatch`, above) runs
  the same rule and passes none; `MapDidCommMediator` (#95) maps those endpoints, so it does too. Record equality changes — see the upgrader
  note under Changed.
- **DI** (`AddDidComm`): registers a singleton `IReturnRouteSessionRegistry`
  (`InMemoryReturnRouteSessionRegistry`) and `IInboundReplayGuard` (`InMemoryReplayGuard`) plus
  their options, with `TryAdd`, so a host that registers its own first keeps it; both validate
  their options on first resolution. Neither depends on the handler registry, the dispatcher, the
  facade or any handler, so a protocol handler may take either in its constructor. A host that
  maps the endpoints without registering them (a hand-wired container) gets one in-memory registry
  and one guard per application, shared by all its registry-aware endpoints — a request admitted
  over HTTP is a duplicate on the WebSocket endpoint — and one Information line saying so.
- **Logging contract**: the return-route code — admission drops, malformed headers, session
  refusals, evictions and session faults — logs nothing but message ids, counts and enum or
  exception type names, never a DID, key id, thread id or header value, and writes those lines so
  that a throwing logging provider cannot change an admission decision, a session push or the
  response that carries it. The receive endpoints' pre-existing unpack-rejection and handler-fault
  lines, which log the exception object, are unchanged by this release.
- DX gate: the new surface is allowlisted in `coverage-allowlist.json` / `execution-allowlist.json`
  until cookbook sections CC–FF / sample 11 (#97) demonstrate it. Cookbook section R now builds its
  ping with `WithReturnRoute(ReturnRouteMode.All)` (`SendAsync` adds its `created_time`, #92);
  same-socket replies stay off there, so nothing comes back on the socket.

### Changed — return-route receive side (#91)

These affect the registry-aware endpoints, `MapDidCommEndpoint(pattern)` and
`MapDidCommWebSocket(pattern)`. The callback overloads `MapDidCommEndpoint(pattern, Func<UnpackResult,
CancellationToken, Task>)` and `MapDidCommWebSocket(pattern, Func<…>)` are unchanged: no session, no
replay admission.

- **Same-socket WebSocket replies now need `return_route: all`.**
  `DidCommReceiveOptions.AllowSameSocketReplies` is now the WebSocket return-route switch. Before,
  with it on, a handler reply to an encrypted and authenticated inbound, addressed back to exactly
  that peer, was written on the socket. Now a reply rides the socket only once the connection is a
  return-route session, opened by an authcrypted message carrying `return_route: all`; a message
  without the header on a socket that has no session gets no reply (FR-RR-01: an absent header
  means `none`). To keep same-socket replies, have the sender put `return_route: all`
  (`MessageBuilder.WithReturnRoute(ReturnRouteMode.All)` or `SendOptions.ReturnRoute`) and a fresh
  `created_time` (which `DidCommClient.SendAsync` adds itself, #92) on its first message on the
  socket. Its later messages keep getting replies there without the header until it
  sends `none` (or `thread`); any message that carries `return_route: all` again needs its own
  fresh `created_time` (replay admission, below).
- **`anoncrypt(sign(…))` and other signer-only inbound no longer get a same-socket reply.**
  `UnpackResult.Authenticated` is also set by a verified signature. The old reply gate accepted a
  peer bound only by the signer key id; the gate now requires the authcrypt sender key id. A signer
  key id that names a different DID than the sender key id still refuses.
- **Replay admission on the registry-aware endpoints** (FR-RR-08). In scope: a message with an
  authcrypt layer (a non-empty sender key id — a verified signature alone does not count) that
  either carries `return_route: all` or `thread` (a malformed value reads as `none`) or whose
  `type` is under a Coordinate Mediation 2.0 / 3.0 or Message Pickup 3.0 / 4.0 protocol
  identifier; a Routing 2.0 `forward` is exempt even with the header. Such a message is dropped —
  `202` with an empty body on HTTP, nothing on a WebSocket, not dispatched — when it has no
  `created_time`, is older than `Window` (default 5 minutes) or later than
  `now + ClockSkew` (default 60 seconds, #105), repeats a (sender, message id) already admitted, arrives while the
  guard's budget is full, or has an id over `MaxMessageIdLength` (default 512). The WebSocket
  endpoint applies it whether or not `AllowSameSocketReplies` is on, and under STOMP. Everything
  else — plaintext, anoncrypt and signed-only messages, and authcrypted messages that carry no
  `return_route` (or `none`) and are outside those protocols — is dispatched exactly as before. No
  shipped handler serves a mediator-service protocol, so for an existing host the visible change
  is this: an authcrypted message carrying `return_route: all` or `thread` without a fresh
  `created_time`, which was dispatched before, is now dropped. `DidCommClient.SendAsync` stamps
  `created_time` on such messages (FR-RR-09, #92 above); a message packed with
  `PackEncryptedAsync`, or by another sender, needs its own (`MessageBuilder.WithCreatedTime(...)`),
  or relax the rule with `ReplayProtectionOptions.RequireCreatedTime = false` (see its limitation
  above).
- **HTTP can answer `200` with a body.** The registry-aware `MapDidCommEndpoint(pattern)` used to
  answer every accepted message `202` and only log a handler's reply. An authcrypted request
  carrying `return_route: all` whose handler returns (or pushes) a message from the decrypting DID
  to exactly the sender now gets `200` with that message packed as the body. A client that insists
  on exactly `202` must accept `200`; requests without the header are answered as before.
- **A DI cycle through the library's factories now fails fast instead of hanging.** The
  `ProtocolHandlerRegistry`, `ProtocolDispatcher` and `AddProtocol<T>()` handler factories notice
  being re-entered on the resolving thread and throw `InvalidOperationException` naming the
  service (type names only) on the first resolution, where the container used to deadlock
  silently. Shapes: a handler that takes `ProtocolDispatcher` in its constructor (a pre-existing
  hang), or a host-registered `IReturnRouteSessionRegistry` / `IInboundReplayGuard` that takes the
  dispatcher or the handlers while a handler takes it. The fix for such code is unchanged: inject
  `IServiceProvider` and resolve the dispatcher side when first needed. `AddRoutingMediator` still
  refuses, at registration, a sink, recipient registry or routing-key provider that would close
  the cycle; its message now says the first resolution "fails" rather than "hangs".
- **Reply-gate log lines no longer carry the peer DID.** One of the same-socket refusal reasons was
  a string embedding the authenticated peer DID; refusals are now logged as enum names with the
  message id.
- **The registry-aware WebSocket endpoint's `1009` close is bounded.** After an oversized message it
  used to wait indefinitely for the peer's close frame; it now waits at most 5 seconds and then
  aborts the socket. The callback overload and `StompServerSession` keep the unbounded close.
- **Upgrader note — `ProtocolContext` value equality.** `ProtocolContext` is a positional record, so
  `Session` and `ReturnRoute` take part in its synthesized `Equals` / `GetHashCode` / `ToString`:
  two contexts that agree on every other member but differ on `Session` (compared by reference
  unless the session type overrides `Equals`) or on `ReturnRoute` no longer compare equal, where
  before this release they did. Binary and API compatible — no public member or overload was
  removed or changed (package validation against 1.5.0 is clean); code that compared contexts
  should compare the members it means.

### Security — return-route threat model and known limitations (#91)

- **Who opens a session.** Only the AspNetCore endpoints, after a successful unpack and admission:
  an authcrypt layer whose sender key id resolves to a DID subject, `return_route: all`, not a
  `forward`, replay-admitted. `IReturnRouteSessionRegistry.Register` is public, so in-process host
  code can register sessions of its own; that code is trusted (it already holds the facade and the
  secrets), and a remote peer reaches the registry only through the admission path.
- **What a session can do.** It exposes identity facts and `TrySendAsync`, never the socket, the
  bytes or the inbound; every message sent through it is snapshotted, checked to be from `LocalDid`
  to exactly `[PeerDid]` and authcrypted to `PeerDid`, so a handler holding another peer's session
  (through `Find`) can only push messages that peer alone can read.
- **Trust comes from authcrypt, not from the socket.** `PeerDid` is the sender key id's DID subject,
  never the plaintext `from` or the connection. A captured envelope replayed after its sender's own
  delivery is a `Duplicate` and opens nothing; one delivered before the sender's own copy binds a
  session for the real sender on the deliverer's socket (known limitation below), and every push on
  it is encrypted to the real sender, so the socket holder gets ciphertext it cannot open. What a
  hijacked or captured socket can do is drop or delay pushes — availability, the transport's
  existing trust level.
- **What is observable.** Plaintext, anoncrypt and signed-only senders get the same answer with or
  without `return_route` and cannot cause a replay drop. A `200` (and its timing) takes a valid,
  replay-admitted authcrypted `return_route: all` envelope from the sender — its own request, or a
  not-yet-delivered capture of it, in which case the body is ciphertext only the sender can open.
  A replay drop is a `202` like any accepted message without a session. Eviction is visible to the
  evicted peer as a normal close, which FR-RR-03 requires.
- **Isolation.** Messages are snapshotted before validation and packing; registry notifications
  come from one notifier per registry, never the registering or unregistering thread, and `Closed`
  callbacks never run on the closing caller's thread — each subscriber's exception isolated, and a
  subscriber that blocks delays no registration, eviction or connection close, parks at most one
  pool thread and makes the registry hold at most `MaxPendingNotifications` waiting notifications
  (the oldest are dropped and counted); a WebSocket push reserves a bounded slot before any
  snapshot or crypto and never blocks a handler; a throwing logging provider cannot change an
  admission decision, a session push or the response that carries it.
- **Audit.** Every return-route drop and refusal logs the message id and an enum or exception type
  name only; the registry's eviction, total-bound and dropped-notification lines carry counts
  only. Malformed-header and
  replay-drop lines are rate-limited (the first and every 1000th at Warning) because any sender can
  produce the former and any enrolled peer can flood the latter.
- **Tested adversarially** (`ReturnRouteAdversarialTests`, Core and interop suites): Mallory cannot
  receive a push meant for Alice or push to Alice through her own session; a replayed capture opens
  nothing; malformed and megabyte-long `return_route_thread` values are refused, never stored and
  never logged; an HTTP response carries at most one JWE; fresh-DID floods against both bounded
  stores; the logs are scanned for DIDs, key ids and thread ids.
- **Known limitations** (by design; recorded in the #91 plan as W-1..W-4):
  - (W-1, closed by #105.) A server clock that jumped forward past an entry's retention and back
    made a request created inside the restored window admissible again. Since #105 the guard
    refuses it; the cost is that fresh in-scope messages are refused after a clock step back of
    more than `Window + ClockSkew`, until the clock catches up or the process restarts.
  - Freshly minted DIDs can fill the global replay budget and deny in-scope traffic to every sender
    for up to `Window + 2 × ClockSkew` (FR-RR-08 (5): saturation is not admitted); existing entries are never
    displaced. Put admission control or rate limiting in front of a mediator endpoint (#95).
  - The first delivery of a captured authcrypted `all` message owns the session socket, as above:
    confidentiality holds, availability for that sender does not.
  - A handler that throws after pushing answers the uniform `400` and its push is discarded, so the
    output of a failed handler is never published (FR-RR-04 keeps the 400 path unchanged).

### Added — mediator routing core: `forward` as a registered handler (FR-ROUTE-09/10/11, #90, epic #88)

- **`ForwardHandler`** (`DidComm.Protocols.Routing`): the mediator-role `IProtocolHandler` for
  `https://didcomm.org/routing/2.0`, dispatched through `ProtocolHandlerRegistry` like every other
  PIURI. Inline, on the request, it only screens: parse the forward (a malformed or over-cap `next`
  is a drop, not a throw), require the FR-ROUTE-11 posture (`UnpackResult.Encrypted` and a
  non-empty `RecipientKid`), refuse a forward whose own `expires_time` has passed on
  `DidCommOptions.Clock` with the FR-API-05 skew (FR-ROUTE-09), reduce `body.next` to its DID
  subject and ask the `IForwardRecipientRegistry`. Every failure is a drop logged with the message
  id only and a `null` reply — no problem report, and `please_ack` is never read (FR-ROUTE-07). An
  admitted forward reserves its place on a bounded queue inline (1024 outstanding and 64 MiB of
  their plaintext bytes, so a slow sink cannot be used to park 1024 × `MaxReceiveBytes` in memory;
  beyond either bound the newest is dropped with a rate-limited warning) and is published to the
  pump only once `ProtocolContext.Acknowledged` has completed — the transport's signal that the
  inbound was acknowledged on the wire — so neither `ForwardProcessor`'s processing nor the sink
  runs before the sender holds the 202 (since #95 a forward addressed to the mediator itself is
  opened inline, before it, on the sender's request); a cancelled or faulted acknowledgement (client abort before the
  response was committed, a rejection after the handler ran) releases the reservation and drops
  the forward with a warning. The detached work runs on the handler's shutdown token, never the
  request's, so a client abort after the 202 cannot cancel the relay. The clock is read once per
  forward, inline: it judges the relay-time expiry and fixes the `delay_milli` schedule
  (`NotBefore`) at receipt, so time spent waiting or queued never moves it; expiry is checked again
  on the pump for the time spent queued. Logging contract: message id plus exception type name(s)
  only — no exception object ever reaches the logger, because a resolution or sink fault can carry
  the next-hop DID in its message. A singleton, `IAsyncDisposable`: dispose stops admission,
  cancels the pump and discards what is queued or still awaiting acknowledgement (fail closed).
- **`ProtocolContext.Acknowledged`** (init-only, default `Task.CompletedTask`) and
  **`ProtocolDispatcher.DispatchAsync(UnpackResult, DidCommClient?, DidCommOptions, Task acknowledged, CancellationToken)`**:
  a transport that has a wire acknowledgement hands it to the handlers through the new overload;
  the existing four-argument overload delegates with `Task.CompletedTask` ("no ordering"). The
  registry-aware `MapDidCommEndpoint(pattern)` completes it from `Response.OnCompleted` (the 202 is
  committed) and cancels it on `RequestAborted` or when the response becomes the uniform 400 after
  the handler ran; the registry-aware `MapDidCommWebSocket(pattern)` has no per-message wire
  acknowledgement and keeps publishing immediately. Both additions are binary-compatible.
- **`DidCommBuilder.AddRoutingMediator(ForwardProcessorOptions? = null)`**: registers the handler,
  the processor and `TryAdd` defaults for its collaborators, and validates the options at
  registration so a bad combination fails at startup. It also refuses, with
  `InvalidOperationException`, a host-registered sink, registry or routing-key provider whose
  public constructor takes `ProtocolHandlerRegistry`, `ProtocolDispatcher` or a protocol handler:
  the container cannot see that dependency cycle, so it is refused at startup rather than left to
  fail the first resolution with an `InvalidOperationException` from the registry or dispatcher
  factory (#91 Changed; lesson L-017); such a collaborator injects `IServiceProvider` and resolves
  at call time instead. Not
  part of `AddBuiltInProtocols()` — an ordinary agent is not a mediator. Once registered, Discover Features discloses `routing/2.0`
  with `roles: ["mediator"]`.
- **`ForwardProcessor.ProcessAsync(UnpackResult, CancellationToken)`** is now the primary entry
  point: the host hands over its own unpack of the inbound envelope so the envelope metadata
  travels with the message. New public constructor `(DidCommClient, IDidKeyService,
  ForwardProcessorOptions, IRecipientRoutingKeyProvider, DidCommOptions)`; the three-argument one
  stays and uses the system clock.
- **`IRecipientRoutingKeyProvider` / `StaticRecipientRoutingKeyProvider`**: extra routing keys per
  recipient (FR-ROUTE-05 second paragraph), looked up by the DID subject of `next` in pass-through
  mode only; rewrap mode never consults it. The static provider carries
  `ForwardProcessorOptions.ExtraRecipientRoutingKeys` for every recipient.
- **`IForwardRecipientRegistry` / `InMemoryForwardRecipientRegistry`**: the recipients this
  mediator relays for, keyed by DID subject (`Register` reduces a DID URL to its DID; `Unregister`,
  `Count`). Starts empty — nothing is relayed until the host registers someone. The lookup is one
  ordinal hash probe whose work does not depend on whether the DID is present, and an unknown
  `next` is dropped silently (decision D4), so the response is not a registration oracle.
- **`IForwardSink` / `NullForwardSink`**: where the handler hands a processed forward, off the
  request path. The default drops it after logging the id; the mediator's sink (the queue, then a
  live or direct push from its push workers) is registered by `AddMediator()` (#95, above).
- **`ForwardProcessingResult.NotBefore`** — `delay_milli` resolved once at receipt to an absolute
  not-before on the `DidCommOptions` clock, for a queue to store (FR-ROUTE-10): `ForwardHandler`
  stamps it inline at admission, before any queueing, and hands the fixed schedule to the
  processor; the public `ProcessAsync` overloads stamp it when they are called — and
  **`IsExpired(now, skew)`**, the FR-API-05 arithmetic shared with the handler (FR-ROUTE-09).
- **`IProtocolHandlerRoles`** and **`FeatureDisclosure.Roles`**: a handler may name the roles it
  plays; the protocol feature provider copies them onto the Discover Features `disclose` entry
  (`roles` is omitted on the wire when null).
- DX gate: the new surface is allowlisted in `coverage-allowlist.json` / `execution-allowlist.json`
  until the handler-based rewrite of sample 04 (#97) demonstrates it; the gate's stale-entry check
  forces the entries out then.

### Changed — `ForwardProcessor` posture and `delay_milli` cap (#90)

- **`ForwardProcessor.ProcessAsync(string)` refuses forwards that were not encrypted to a held key
  (FR-ROUTE-11).** A plaintext or signed-only forward, or one whose unpack carries no
  `RecipientKid`, now throws `ConsistencyException` right after the forward parse (an over-cap
  `next` still throws `MalformedMessageException` first, FR-API-09). Consumer impact: a host that
  re-packed a decrypted forward as plaintext and fed the string overload — the pattern the old
  sample 04 used after `MapDidCommEndpoint` had opened the envelope — must hand the endpoint's
  `UnpackResult` to `ProcessAsync(UnpackResult)` instead. Hosts feeding real anoncrypt forwards
  (Cookbook section O, `InteropCli unwrap-forward`) see no change.
- The non-forward `ConsistencyException` no longer echoes the inbound message `type`.
- In rewrap mode or with extra routing keys, an attachment that is not a JSON envelope or a `next`
  with no DID part now surfaces as `MalformedMessageException` (previously a raw
  `ArgumentException` escaped the string overload). Plain pass-through still relays the bytes
  opaquely.
- A positive `delay_milli` is capped at `int.MaxValue` ms, as a negative one already was. A hostile
  value (`long.MaxValue`, or one that lands past year 9999) no longer escapes as
  `OverflowException` / `ArgumentOutOfRangeException`.
- `samples/04-MediatorAgent` hands the endpoint's `UnpackResult` straight to `ForwardProcessor`
  instead of re-packing it as plaintext; narrator output is unchanged. The handler-based rewrite
  of the sample is #97.

### Docs

- **PRD: mediator-service requirements (#89, epic #88).** §2.1 gains the `DidComm.Mediator` scope
  row and §2.2 drops coordinate-mediation from out-of-scope (persistent stores, shorten-url 1.0,
  swarm 1.0 and DIDComm v1 pickup forms stay out); §3.1 lists the package. §8 adds
  FR-ROUTE-09..11 (relay-time expiry, `delay_milli`, forward as a registered handler with silent
  drop of unroutable forwards) and amends FR-ROUTE-06; new §8a Return-Route and Queue Transport
  extension (FR-RR-01..09), §8b Coordinate Mediation 3.0 + 2.0 compat (FR-MED-01..10), §8c Message
  Pickup 3.0 + 4.0 follow-up (FR-PICK-01..12); FR-TRN-03/10 now read "one-way unless the
  return-route extension (§8a) is in effect"; §12 Phase 7 with issues #90–#100; §13.5 "Mediation"
  dimension; §14.3 sample 11 and row 04 refresh; §14.4 Mediation row; DD-11..DD-14.
- **Agent instructions.** `AGENTS.md` / `CLAUDE.md` now link the real PRD path
  (`docs/didcomm-dotnet_PRD.md`) and the newly created `docs/codebase-architecture.md` (package map
  + flows, with a Phase 7 mediator-pipeline placeholder).
- **Architecture: return-route receive side (#91).** `docs/codebase-architecture.md` gains the
  section "Return-route receive side (Phase 7, #91)" — the admission pipeline in order, the HTTP
  and WebSocket session lifecycles, the registry bounds, the replay guard, the queue URI, the
  threat model and the known limitations — plus the new types in the package table, the admission
  step in the inbound flow and the DI fail-fast rule beside the sink rule; #91 leaves the "not built
  yet" list. The XML docs of `ProtocolDispatcher`, `DispatchOutcome`, `IProtocolHandler` and
  `ProtocolContext.Acknowledged` no longer say an HTTP receive only logs a handler's reply.
- **PRD: the return-route requirements now match what #91 built (PR #103 review).** "Authenticated"
  in these rows now means an authcrypt sender key id — a verified signature alone does not count —
  so signed-only and `anoncrypt(sign(…))` messages neither open a session nor fall under replay
  protection, and the mediator handlers are the ones that drop them (FR-RR-03, FR-RR-08, FR-MED-05,
  FR-PICK-01, FR-TRN-03). Session bounds apply to WebSocket sessions only: an HTTP session lasts one
  request and is never counted or evicted, so a burst of POSTs cannot push a peer's live socket out,
  and a peer at its own bound replaces its oldest session even when the registry is full
  (FR-RR-03). On an open WebSocket session `thread` or a malformed header stops pushes like `none`,
  push capacity is reserved before any packing, and session notifications are best-effort —
  delivered in state-change order by one notifier per registry, bounded, the oldest dropped under
  subscriber backpressure, with `IReturnRouteSession.Closed` the authoritative signal (FR-RR-05,
  DD-12). Also: a send to the queue endpoint is refused
  before any transport activity (FR-RR-06); over-long message ids are refused and one replay cache
  serves every endpoint of a host (FR-RR-08); the same-socket reply switch is described as the
  WebSocket return-route session it now is (FR-TRN-10, FR-PROTO-05a); an unusable
  `return_route_thread` is treated as `none` (FR-RR-01).
- **Architecture: return-route client side (#92).** `docs/codebase-architecture.md` gains the
  section "Return-route client side (Phase 7, #92)" — the wiring, the `SendAsync` steps in order,
  the HTTP response read, the `Returned` rule, the session sockets, the inbound consumer and its
  admission, `SendPackedAsync`, the threat model and the known limitations — plus the new types in
  the package table and a rewritten outbound flow. The receive-side section now points at the
  shared `InboundReplayGate` and the client's built-in consumer and mentions the session-reply
  timestamps; #92 leaves the "not built yet" list.
- **PRD: FR-RR-07's over-cap response (#92, a replanning item).** The row said an over-cap response
  body "is discarded and logged; the send still counts as accepted", which would make an
  unreadable reply silent loss. It now says what is built: the body is never buffered; the
  transport reports the send accepted with no payload and `ResponseDiscarded`, and logs it;
  `SendAsync`, having asked for the return route, then throws `TransportException` with status
  `200`, the same outcome as an unpack failure; a body that stalls past the transport's request
  timeout or fails mid-read is treated the same. Nothing else in the PRD changed. The other
  client-side refinements — the refusal of `all` / `thread` to recipients naming more than one DID,
  anoncrypt return-route sends, the local-DID half of the `Returned` rule and the per-socket
  inbound consumer — are documented above and in the architecture doc as proposed PRD
  clarifications.

## [1.5.1] - 2026-11-02

> Moves `DataProofsDotnet.Jose` to 2.0.0 (#85). This is a pin bump only: didcomm's public API,
> behavior and wire output do not change. Despite the patch number, the dependency floor crosses a
> major, so read the upgrade notes if your application references a DataProofsDotnet package
> directly.

### Changed

- **`DataProofsDotnet.Jose` 1.5.0 → 2.0.0** (brings `DataProofsDotnet.Core` 2.0.0). didcomm's
  public API and wire output do not change, and package validation is clean against 1.5.0.
  `DataProofsDotnet.Jose` has no source change in this release, and `DataProofsDotnet.Core` only
  adds an option that does nothing unless set. The advisory fixed in 2.0.0
  ([GHSA-6fxh-mc39-94vc](https://github.com/moisesja/dataproofs-dotnet/security/advisories/GHSA-6fxh-mc39-94vc))
  affects RDFC and bbs-2023 proofs, which didcomm does not use, so this is not a security update
  for didcomm.
- **Releases publish through NuGet Trusted Publishing.** `release.yml` no longer reads a stored API
  key, and a separate job holds the publishing permission, so release approval now comes after build
  and test pass. This has no effect on the packages.

Upgrade notes:

- **A direct pin below 2.0.0 fails restore.** If your application references
  `DataProofsDotnet.Core` or `DataProofsDotnet.Jose` directly at an older version, `dotnet restore`
  fails with `NU1605` until you raise it to 2.0.0.
- **DataProofsDotnet: move every package you reference together, then rebuild.** The 2.0.0 major
  is in `DataProofsDotnet.Rdfc` and `DataProofsDotnet.Legacy`, which didcomm does not reference.
  If your application does, this update does not move them for you. Moving them to 2.0.0 removes
  constructors, so an assembly built against 1.x throws `MissingMethodException` until it is
  rebuilt, and it changes signed bytes for some RDFC documents. Read upstream's 2.0.0 notes first.
- **did:webvh.** `NetDid.Method.WebVh` 4.0.0 arrives through
  `DidComm.Extensions.DependencyInjection` and is built against `DataProofsDotnet.Core` 1.4.0. It
  runs unchanged on 2.0.0.

## [1.5.0] - 2026-10-29

> Every dependency moves to its latest stable release (#77). The published packages now require
> **NetDid 4.0.0, NetCrypto 1.7.1, DataProofsDotnet.Jose 1.5.0, Microsoft.Extensions 10.0.14 and
> Polly 8.9.0**. didcomm's public API does not change, and package validation is clean against
> the 1.4.1 baseline. The new floors do reject some malformed input that 1.4.1 accepted. See
> *Changed — input the new floors reject* below. This release also fixes an untyped fault that
> escaped `UnpackAsync` before any authentication (FR-API-07). See *Fixed* below.
> It also caps the length of every wire DID and key id before didcomm parses, resolves or looks
> it up (FR-API-09, #80). That refuses some input 1.4.1 accepted; see *Security* below.
> Encrypted packs now address every keyAgreement key of each recipient, as the spec says, not just
> the first (FR-ENC-19, #81). That changes what goes on the wire; see *Changed (wire-visible)* below.

### Security

- **Microsoft.SourceLink.GitHub 10.0.301 → 10.0.403.** 10.0.301 brought in
  `Microsoft.Build.Tasks.Git` 10.0.301, which has a published advisory:
  [GHSA-23fw-v26w-5fgq](https://github.com/advisories/GHSA-23fw-v26w-5fgq) (moderate,
  information disclosure). SourceLink is build-only (`PrivateAssets="All"`) and never ships to
  consumers, but the task runs whenever `release.yml` packs a release. Once the advisory was
  published, `dotnet restore` of 1.4.1's sources started failing: `TreatWarningsAsErrors` turns
  NuGet audit's `NU1902` into an error. So this bump also unblocks CI.
  `dotnet list package --vulnerable --include-transitive` now reports nothing.

### Fixed — an unpaired-surrogate escape no longer escapes `UnpackAsync` untyped (FR-API-07, #77)

Every layer of an unpack is first classified by `EnvelopeDetector`, which parses the whole JSON
text with duplicate-member detection. To compare names, System.Text.Json decodes every member
name, and it reports one written as an unpaired-surrogate escape (`{"\uD800":1}`) as
`InvalidOperationException`, not `JsonException`. The detector caught only `JsonException`. So
that input left `UnpackAsync` as a raw `InvalidOperationException` at the plaintext, JWS and JWE
layers alike, in any position: top level, `body`, or a per-recipient or per-signature header.
That includes the plaintext inside an anoncrypt envelope, which anyone holding the recipient's
public key can build. No key and no authentication were needed. The JOSE parser's own checks never
saw this input, because the detector runs first. The shipped transports already contained it:
HTTP answers every unpack failure with one opaque rejection, and the WebSocket receive loop
discards the envelope and keeps the socket open. Direct callers of `UnpackAsync` got the raw
exception. The fault predates this release; the #77 adversarial review found it.

- **Member names.** The detector now maps the parse's `InvalidOperationException` to
  `MalformedMessageException`. It does the same for the parse's `ArgumentException`, which a
  caller's string that is not well-formed UTF-16 produces. Decoding wire bytes cannot produce
  one, but the public API accepts any string.
- **String values in a plaintext layer.** The parse does not decode values, so such a message
  unpacked and the fault came later, wherever a value was read. Extension headers are kept as raw
  `JsonElement` (`Message.AdditionalHeaders`) and decoded only when read. With Trace 2.0 enabled,
  `TraceObserver` decodes `~trace.report_uri` on every inbound, so a peer could make dispatch throw.
  `OutOfBand.ReadWebRedirect` decodes `web_redirect` the same way. The detector now checks every
  string of a plaintext layer and refuses the message as `MalformedMessageException` if one does
  not decode. Body values already failed that way, through deserialization. JWS and JWE layers are
  not re-checked: DataProofsDotnet.Jose checks every string in them itself (since 1.4.0, below).
- **`ForwardMessage.Create`** maps a payload with such a member name to its documented
  `ArgumentException` ("Forward payload is not valid JSON"), instead of `InvalidOperationException`.

Pinned by `SurrogateEscapeBoundaryTests` (13 cases) and
`DidCommClientUnitTests.Unpack_RejectsUnpairedSurrogateMemberName_AsMalformed_FrApi07`. Each
hostile case has a control with a well-formed escaped surrogate pair in the same position, which
still unpacks. Before the fix, 9 of the 13 failed with a raw exception, and the 3
extension-header value cases unpacked.

### Changed (wire-visible) — encrypted packs address every keyAgreement key of each recipient (FR-ENC-19, #81)

`PackEncryptedAsync` encrypted to only the **first** keyAgreement key on the chosen curve of each
recipient DID. DIDComm v2.1 says "the default recipients of the envelope SHOULD include all the
`keyAgreement` entries representing Bob. This allows Bob to decrypt his messages on any device he
controls" (encryption §Key IDs). The spec's own Appendix C.3 vectors do that: their `apv` is the
hash over all three of Bob's X25519 kids. didcomm-python 0.3.2 and didcomm-jvm 0.3.2 do it too. A
recipient whose device held only its second or third key could not decrypt what didcomm-dotnet
sent. The PRD has required this since the start (FR-ENC-19); its acceptance test exercised the
envelope writer with the keys handed to it, never the facade's own choice.

- **Every key on the curve.** For each recipient DID, the envelope now has one `recipients` entry
  per keyAgreement key on the negotiated curve, in document order. The body is still encrypted
  once. `SignFrom`, `ProtectSender`, `SendAsync` and the Discover Features client inherit this.
  Curve negotiation is unchanged (X25519 > P-256 > P-384 > P-521, common to every recipient and,
  for authcrypt, to a held sender key). Keys on other curves are skipped, and a Debug log records
  how many (counts only).
- **Pin one key.** A DID URL `did#fragment` in `Recipients` (or `SendOptions.Recipients`) now
  addresses exactly that key, as didcomm-python and didcomm-jvm accept a DID URL `to`. The key
  must be one of the DID's keyAgreement keys; otherwise `DidResolutionException`. Before, any DID
  URL was refused as `DidResolutionException` ("not a syntactically valid DID"). A path, query or
  parameters in the entry gives `ArgumentException`.
- **Mediators.** A mediator DID used as a service `uri` now gets one forward layer addressed to
  all of its keys on one curve (routing Example 1), not just its first. A `routingKeys` entry that
  is a bare DID is accepted and wrapped the same way, as both peers do. Before, it was refused. A
  mediator's `ReanoncryptToNext` rewrap uses the same selection.
- **Only the recipient's own keys.** A DID document can list a keyAgreement key whose kid names
  another DID. Such keys are skipped: only kids whose DID part is the recipient DID are used. Before
  this, Bob's document could list a fake `did:…:alice#a1`. With `Recipients: [bob, alice]` it
  would then shadow Alice's real key under the envelope-wide dedup, and she could not decrypt. Our
  receivers already refuse such entries (FR-CONSIST-02).
- **Forward layers are capped at 8**, counting a mediator-as-DID layer. The cap is checked as soon
  as the recipient's service entry is read, before any mediator or routing-key DID is resolved (the
  recipient's own keys and service entry come first, since the route is in that entry), and each
  routing DID is resolved once. Each layer grows the envelope by about
  4/3, and a peer's DID document chooses how many `routingKeys` it lists. 28 bare-DID routing keys
  with 64 keys each built a 105 MB envelope. A routing-key reference or mediator DID over the
  FR-API-09 limits is refused without being quoted.
- **Hygiene.** Kids are deduped across the envelope, so `Recipients: [bob, bob]` no longer doubles
  every entry. Keys without a kid are skipped; before, they escaped as an untyped DataProofs
  exception. A bare DID contributes at most 64 automatically selected keys, because the
  recipient's DID document decides how many key agreements a pack runs; a key the caller pins by
  DID URL is taken as given and never counts toward that allowance, whether the pin comes before
  or after the bare DID.
- **With #80.** Recipient kids over the FR-API-09 limits are still included; didcomm-dotnet
  receivers skip them per entry. A recipient whose first key has such a kid now decrypts through
  its other keys.

- **Live harness.** Both legs gain three cells in both directions: anoncrypt and authcrypt to a
  3-key DID unpacked by a device holding only the third key, and an authcrypt `to` pinned to the
  second key. Every cell checks the envelope's kids exactly before unpacking. The existing
  "authcrypt 3-rcpt" cell could not catch this, because its unpacker held all three keys. Run
  before the fix, the outbound cells failed on both legs (one entry instead of three, and the DID
  URL `to` refused); the inbound cells passed, because python and JVM already do this. The python
  leg now refuses to start on CPython outside 3.9–3.12, because didcomm-python's pydantic v1
  fails to import on 3.13 and later; set `PYTHON`, for example `PYTHON=python3.12`.
- **Messages.** Forward wrapping with several `Recipients` entries now says "every Recipients entry
  must name the same DID" (pins of one DID are allowed). A mediator's rewrap refuses a next hop with
  no key on a supported curve as `DidResolutionException`; before, it passed a mixed-curve list to
  the writer.

What you'll see: more `recipients` entries per envelope for multi-key recipients, so code that
counts them or assumes `RecipientKid` is a DID's first key sees different values. No public API
changes; package validation is clean against 1.4.1. Published vectors are not regenerated; they
remain valid.

Pinned by `SpecAllRecipientKeysTests`: the spec's Appendix A/B DIDs; every C.3 curve's `apv`
matches, and a pin yields exactly that entry. Also `AllRecipientKeysTests`, `AllRecipientKeysRoutingTests`,
`RecipientKeySelectionTests`, and changes to `MediatorEndpointExpanderTests` and
`SenderForwardWrappingTests`. The spec-vector tests, the last-device test and the #80 interplay
tests failed before the change.

Not covered here: encryption §"A message being sent to multiple DIDs MUST be encrypted for each DID
independently" (FR-ENC-04). `PackEncryptedAsync` still builds one envelope for every DID in
`Recipients`. Meeting it changes the pack result's shape and needs its own API decision.

### Security — wire identifiers are length-capped before DID parsing (FR-API-09, #80)

Key ids reached NetDid's DID-URL parser with no bound but `DidCommOptions.MaxReceiveBytes` (1 MiB
by default). The parser's regular expressions backtrack. Their cost is linear, not catastrophic,
but one maximum-size value is expensive. It is also reachable before any authentication. Measured
on this branch's parent (Apple M3 Max, median of 15): one JWS carrying a ~1 MB kid with a long
method-specific id took **54 ms** in `UnpackAsync`. Eight such envelopes unpacked concurrently
allocated **about 800 MiB**, because each concurrent parse needs its own regex runner. A long
fragment or path with a bad tail cost 14–20 ms and about 500 MiB for eight. The resulting error
also copied the whole kid into the exception message, and from there into logs.

Every identifier a peer controls now has a bound, checked before any DID parse, DID resolution or
secrets lookup sees it. A value that is never parsed, resolved or looked up is not checked; see
*Kids that are never looked up* below. A key id has two: at most 4096 in all, and at most 2048 in its *DID part*,
meaning everything before its first `/`, `;`, `?` or `#`.

| Value | Limit (UTF-16 code units) | Over the limit |
|---|---|---|
| `from`, each `to` entry | 2048 (unchanged) | refused, `MalformedMessageException` |
| JWS signer `kid` the verifier looks up, protected or per-signature header, at any layer | 4096, DID part 2048 | refused, `MalformedMessageException` |
| Authcrypt sender kid, from `skid` or decoded from `apu` | 4096, DID part 2048 | refused, `MalformedMessageException` |
| `from_prior` header `kid` | 4096, DID part 2048 | refused, `ProtocolException` |
| `from_prior` `iss`, `sub` | 2048 | refused, `ProtocolException` |
| Forward `body.next` (`ForwardMessage.TryParse`) | 4096, DID part 2048 | refused, `MalformedMessageException` |
| JWE `recipients[].header.kid`, each entry | 4096, DID part 2048 | **treated as not held**: skipped, not refused |

- **Why 4096 for key ids.** A kid is a DID plus a `#fragment`. FR-CONSIST-01/03 require the
  `skid` and the signer kid to share `from`'s DID subject, and `from` may be up to 2048. A single
  2048 cap would have refused a legal sender whose DID is close to it. The issue proposed 2048;
  the PR explains the change.
- **Why a DID-part rule.** Without it, a 4096-character kid could carry a 4090-character DID, and
  that DID would be resolved before FR-CONSIST-01/03 compare it with `from`. The rule makes the
  extra 2048 a fragment allowance, not a larger DID bound. DID syntax allows none of the four
  delimiters inside a DID, so the DID part is exact. Both checks are a length compare and one
  linear scan, never a regular expression.
- **Why recipient kids are skipped, not refused.** One group member can publish a long kid in their
  own DID document; DID Core sets no limit. Refusing the envelope would let that member make every
  group message undeliverable to everyone else, and `PackEncryptedAsync` builds such envelopes
  without complaint. So an over-limit recipient kid is handled like a key this agent does not hold:
  it never reaches the `ISecretsResolver`, the other entries are processed as before, and an
  envelope with no usable entry left fails exactly as one not addressed to this agent.
  `UnpackResult.AllRecipientKids` still reports every kid verbatim. A skipped entry can carry the
  same key material as a kept one (an alias). The parser opens entries in envelope order and
  FR-CONSIST-08 requires the entry it opens to be the one selected. So when entries are skipped,
  the reader gives the parser a copy with the kept entries first, in their original order. The
  `recipients` array is unprotected and `apv` hashes the sorted kids, so the copy is equally valid.
  The label check itself is unchanged, and a label swap between kept entries is still refused.
- **Why `ProtocolException` for `from_prior`.** It is `FromPriorValidator`'s documented type for
  a malformed JWT, and every other structural `from_prior` rejection already throws it. The issue
  proposed `MalformedMessageException`.
- **No value in any message.** The new errors name the field and the limit, not the value.
  `Message.Validate` now checks `from`/`to` length before its fragment check, which quotes the
  value.
- **Backstops.** `DidSubjectOf` treats input over 4096 as not a DID, without parsing it.
  `NetDidKeyService.RejectUnsupportedMethod`, the other place didcomm calls NetDid's parser,
  refuses it as `DidResolutionException` naming only a 64-character prefix. Together they bound
  the parse cost of values that come from resolved DID documents, such as a verification method
  `controller`. They do not stop such a value being quoted: one consistency error still quotes a
  resolved `controller` in full, as it did before this release.
- **No new possession signal.** The recipient-kid filter reads only envelope content and runs
  before any key lookup. Holding the key of a skipped kid changes nothing, and a receiver left
  with no candidate takes the same decoy path as any stranger. The sender-kid refusal depends only
  on the sender kid, and DataProofs reaches it on the held and decoy paths alike. A holder and a
  stranger get the same exception and message; the tests pin this.
- **Our own pack stays inside the limits.** `PackSignedAsync`, and `PackEncryptedAsync` with
  `SignFrom` or `From`, no longer select an own signing or authcrypt sender key whose kid exceeds
  the limits, because every didcomm-dotnet receiver would refuse the result. If no other held key
  is left, they throw `SecretNotFoundException`, as before. Its message now says how many keys
  were skipped for their length, and so does the "no common keyAgreement curve" error when the
  skipped key was the sender's only one on a shared curve. Only kids the agent asked the
  `ISecretsResolver` about are used, so a resolver that lists everything it holds cannot bring a
  skipped key back. Recipient kids are packed unchanged.

After the fix, the ~1 MB kid envelope is refused in **0.7 ms**, which is the cost of parsing its
JSON, and eight concurrent ones allocate **23 MiB**. The error message is 80 characters.

#### Changed — input now refused

A signer, sender or `from_prior` key id over the limits, a `from_prior` `iss` or `sub` over 2048,
or a forward `next` over the limits was accepted by 1.4.1 (or failed later as a different type) and
is now refused as shown above. A multi-signature JWS is refused if the verifier reaches an
over-limit kid before a signature that verifies: `[over-long, valid]` is refused, even though the
second signature would have verified. A recipient whose only keyAgreement kids exceed the limits
can no longer receive with didcomm-dotnet, because its kids are treated as not held. Two public
members change with it. The `DidCommClient` constructor refuses a `DidCommOptions.OwnIdentifiers`
entry over 4096 (`ArgumentException`). `NetDidKeyService.RejectUnsupportedMethod` refuses a value
over 4096 as `DidResolutionException`. On pack, own signer and sender keys whose kid exceeds the
limits are skipped, as described above.

Real identifiers are far shorter. The longest key id in the interop fixtures is 45 characters. The
live harness's longest is about 565: a did:peer:2 whose service segment routes through two
mediators and carries both mediators' kids. Both legs of the live harness pass on this branch:
python 56/56, and JVM 53/53 with the same three declared n/a cells as before.

#### What the bound does not cover

- **Cost per envelope.** The bound is per value. It does not limit how many identifiers an envelope
  carries. A maximum-size envelope with 360 `to` entries of about 2048 characters each still costs
  about 30 ms of parsing over a same-size control, and about 45–50 MiB of extra allocation per
  envelope under 8-way concurrency. This predates the release and is unchanged by it.
  `MaxReceiveBytes` and a rate limiter in front of the receive endpoint remain the controls.
- **NetDid's own parsing.** NetDid parses the documents it resolves. didcomm cannot bound that.
  A fix in net-did itself (non-backtracking patterns, or a length guard in its parser) would cover
  this and the per-envelope cost above.
- **Kids that are never looked up.** In a multi-signature JWS the verifier tries signatures in
  order and stops at the first that verifies. A later signature's kid is never requested and never
  checked: `[valid, over-long]` unpacks.

Pinned by `IdentifierLengthCapTests` (and its `Permutations` part), `IdentifierLimitsTests`,
`FromPriorLengthCapTests`, `ForwardNextLengthCapTests`, `DidSubjectLengthBoundTests`, and additions
to `RecipientLabelBindingTests` (the alias case, both `FindPresentAsync` orders, and a label swap
next to an over-limit entry), `MessageValidationTests`, `NetDidKeyServiceTests`, `AspNetCoreReceiveRoundTripTests` and
`WebSocketTransportRoundTripTests`. Each hostile case has an at-limit control that unpacks or
validates. Every refused position is also tried with a value as long as a maximum-size envelope
allows (500,000 to 1,000,000 characters). Each asserts that the value is not in the message, and
that the over-long value never reached the DID resolver or the secrets resolver. The recipient
tests build a real group message through `PackEncryptedAsync` with one member's long kid: the other
members unpack it, the long kid never reaches their `ISecretsResolver`, and a stranger's failure is
unchanged. The tests also cover a legacy `IDidKeyService` without binding support, and they show
that the ASP.NET Core endpoint's 400 and both WebSocket receive loops handle a refusal unchanged.
`DidCommClient`'s failure log and span record only the exception type. The transports' logs carry
the refusal message, which never contains the value. Removing any one guard, or cutting a key-id
check down to its length test alone, fails at least one test.

### Changed — new dependency floors (#77)

A runtime dependency's pin becomes the minimum version in the published nuspec, so consumers
resolve at least these:

| Dependency | Declared by | 1.4.1 | Now |
|---|---|---|---|
| `NetDid.Core` | `DidComm.Core`, `DidComm.Adapters.NetDid` | 3.1.0 | **4.0.0** |
| `NetDid.Method.Key`, `NetDid.Method.Peer`, `NetDid.Extensions.DependencyInjection` | `DidComm.Extensions.DependencyInjection` | 3.1.0 | **4.0.0** |
| `NetCrypto` | `DidComm.Core` | 1.5.0 | **1.7.1** |
| `DataProofsDotnet.Jose` (brings `DataProofsDotnet.Core` 1.5.0) | `DidComm.Core` | 1.3.0 | **1.5.0** |
| `Microsoft.Extensions.Logging.Abstractions` | `DidComm.Core`, `DidComm.Transports.Http`, `DidComm.Transports.WebSocket` | 10.0.10 | **10.0.14** |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `DidComm.Extensions.DependencyInjection`, `DidComm.Transports.Http`, `DidComm.Transports.WebSocket` | 10.0.10 | **10.0.14** |
| `Microsoft.Extensions.DependencyInjection` | `DidComm.Extensions.DependencyInjection` | 10.0.10 | **10.0.14** |
| `Microsoft.Extensions.Http` | `DidComm.Transports.Http` | 10.0.10 | **10.0.14** |
| `Polly` | `DidComm.Transports.Http`, `DidComm.Transports.WebSocket` | 8.7.0 | **8.9.0** |

`DidComm.AspNetCore` declares no package dependencies of its own. It uses the
`Microsoft.AspNetCore.App` shared framework and `DidComm.Core`.

Upgrade notes:

- **NetDid 4.0.0** needs no source change. Its major version is for the witnessed did:webvh
  witness-file format, which didcomm never reads or writes. If your application references
  NetDid packages directly, move them to 4.0.0 as well.
- **DataProofsDotnet: move every package you reference together.** DataProofsDotnet.Core 1.4.0
  changed `DataIntegrityProof.Type` and `AdditionalProperties` from `init` to `set`. That is
  source-compatible but not binary-compatible. If your application references another
  DataProofsDotnet package directly (for example `DataProofsDotnet.Legacy`), move it to 1.5.0 too.
  Otherwise it fails at run time with `MissingMethodException`. didcomm itself sets neither member.
- **NetCrypto 1.6.0 and 1.7.x are additive** (`ICapableKeyStore`, `TransferableKeyMaterial`).
  `IKeyStore` gains no member, so your own `IKeyStore` implementations behind
  `IOpaqueKeyResolver` stay source- and binary-compatible.
- **Microsoft.Extensions 10.0.14** is a servicing release. NetDid 4.0.0 already requires 10.0.14
  for `Caching.Memory` and `Logging.Abstractions`. The other three move with them to keep one
  servicing line.

### Changed — input the new floors reject

didcomm's exception contract does not change: `UnpackAsync` still raises only didcomm's typed
exceptions (FR-API-07). What changes is that some malformed input which 1.4.1 accepted is now
refused:

- **Unpaired-surrogate escapes and ill-formed UTF-8 in a string value of a received JWE or JWS**
  (DataProofsDotnet 1.4.0). An escape such as `\uD800` is valid JSON grammar, but I-JSON forbids
  it. DataProofs now checks every JSON text it parses on receipt, including members nothing reads.
  An envelope with `"x":"\uD800"` in an unread member unpacked under 1.4.1. It now throws
  `MalformedMessageException`, before any key work and whether or not the recipient key is held.
  This is pinned by the two `Unpaired_surrogate_escape_*` tests in `JoseInputHardeningTests`, which
  fail on the 1.4.1 dependency set. Member *names* never reach DataProofs; didcomm's own detector
  rejects those first (see *Fixed* above).
- **DIDs and DID URLs that break RFC 3986 / DID Core** (NetDid 4.0.0). This covers raw whitespace
  or control characters, characters such as `<` `"` `{` `|` `\`, and malformed `%` escapes.
  NetDid also now rejects JWKs without `kty`, and did:peer:2 service blocks that break DID Core
  rules. A `from`, `to` or key id written that way no longer parses as a DID URL. Every check that
  compares DID subjects (PRD §4.3) now treats it the way 1.4.1 already treated any value that
  does not parse. A DID document that breaks these rules fails to resolve.

Not a change, now pinned: a **small-order X25519 `epk` or ECDH-1PU sender key**, which yields the
all-zero shared secret, was already refused as the uniform `CryptoException` ("JWE could not be
decrypted."). The type and message are the same whether or not the recipient key is held. Under
DataProofsDotnet 1.4.0 the parser rejects it itself, so only `InnerException` differs: it is now
`JoseCryptoException`, where 1.4.1 had NetCrypto's `CryptographicException`. The three
`Small_order_*` tests in `JoseInputHardeningTests` hold on both dependency sets.

Two timing and misconfiguration notes from the #77 adversarial review, both measured:

- **A small timing residual on keystore-backed keys.** With a key held in a NetCrypto `IKeyStore`
  (through `NetDidKeyStoreSecretsResolver`), a small-order `epk` now costs the holder about 3 µs
  more than a non-holder at the median. That gap was 0.7–1.8 µs on 1.4.1's dependencies. The
  likely cause is one extra rethrow on the holder path, which NetCrypto 1.6.0 added in
  `InMemoryKeyStore` (inferred, not instrumented). Exception type, message and inner type stay
  identical. This is inside the timing caveat FR-SEC-06 already documents for keystore-held keys:
  the backing store's own work is a signal this layer cannot mask. HTTP receives are covered by
  the receive-rejection floor (#35).
- **One misconfiguration oracle is closed.** A held X25519 key with a private `d` of the wrong
  length used to fail as `MalformedMessageException`, where a non-holder got `CryptoException`.
  Both now get the same uniform `CryptoException`.

### Changed — test stack (#77)

1.4.x held the test stack back on purpose. #77 moves it, and nothing in it ships to consumers:

- **FluentAssertions 7.0.0 → AwesomeAssertions 9.6.0**, the Apache-2.0 community fork.
  FluentAssertions 8 and later are under the Xceed license, which needs a paid license for
  commercial use. net-did made the same move in 4.0.0. The swap was mechanical: one renamed API
  (`BeGreaterOrEqualTo` → `BeGreaterThanOrEqualTo`). A green suite does not prove every
  assertion is still as strict, so every assertion API the suite binds was checked against the
  release notes and an FA 7 vs AA 9 probe. Only `OnlyContain` and `AllSatisfy` got looser: they
  now pass on an empty collection. Of their 8 call sites, one could then pass with nothing to
  check, the shared `AssertOneRegistrationPerResult` helper. It now requires a non-empty result
  set, and `AssertOneRegistrationPerResult_rejects_an_empty_result_set` pins that.
- `Microsoft.NET.Test.Sdk` 17.12.0 → 18.11.0, `xunit.runner.visualstudio` 2.8.2 → 4.0.1,
  `NSubstitute` 5.3.0 → 6.2.0, `coverlet.collector` 6.0.3 → 10.1.0, `BenchmarkDotNet` 0.14.0 →
  0.15.8, `Microsoft.AspNetCore.TestHost` 10.0.10 → 10.0.14.
- **xunit stays on 2.9.3**, the last xUnit v2 release (marked *Legacy* on NuGet). Moving to xUnit v3
  is a separate change. xunit.runner.visualstudio 4.x still runs v2 tests.
- `global.json` stays at SDK 10.0.100 with `latestFeature` roll-forward. SourceLink 10.0.403
  builds and packs on it, and CI already rolls forward to the newest SDK on the runner.

### Changed — package validation baseline moved to 1.4.1

`PackageValidationBaselineVersion` 1.4.0 → 1.4.1, now that 1.4.1 is published.

## [1.4.1] - 2026-08-06

> Despite the patch number this release **adds public API** — `DidCommDiagnostics`,
> `StompFrame` / `StompFrameCodec`, `FromPriorClaims.ForTermination` / `IsTermination`, an
> expanded `ProblemReport`, and new `WebSocketTransportOptions` members. Nothing was removed:
> package validation is clean against the 1.4.0 baseline. The one source-level caveat is
> `FromPriorClaims.Sub`, widened from `string` to `string?` to carry FR-ROT-06's
> relationship-termination form — call sites compiled with nullable reference types enabled may
> now warn where they assign it to a non-nullable `string`.
>
> This entry supersedes the 1.4.1 section previously dated 2026-07-31: that version was prepared
> but never tagged or published, so its content ships here.

### Fixed — WebSocket transport retry classification, from the #69 re-review (#72)

- A plain `OperationCanceledException` — how a production `ClientWebSocket` surfaces a
  send/connect timeout on a half-dead connection — is now retried like the
  `TaskCanceledException` subclass always was. Caller cancellation stays fatal: the reconnect
  predicate now splits on *whose* token cancelled, mirroring the existing catch-filter
  (FR-TRN-11).
- The connect-time SSRF refusal (an endpoint resolving only to private/reserved addresses) now
  fails the send on the attempt it arrives instead of burning the full exponential backoff
  budget (~31 s at defaults) while holding the endpoint's send gate. The guard's refusal is
  marked at its throw site and reclassified as permanently fatal — the same non-retryable lane
  the STOMP version mismatch already used. Transient connect failures (DNS, socket errors) keep
  their retry classification.
- A send that passes the entry check but loses the race to `DisposeAsync` now surfaces
  `ObjectDisposedException` instead of a misleading "exhausted the reconnect budget"
  `TransportException`.
- The permanently-fatal failure message now reports the attempt it arrived on instead of
  claiming "no reconnect was attempted" even on attempt 2+.
- Connection pooling now keys on the full path + query: two endpoints differing only in query
  no longer share a socket whose handshake used the other sender's URL (pre-existing on `main`,
  flagged in the #69 re-review).
- The `Lifecycle` event documents its no-blocking-teardown contract: a handler that blocks on
  `DisposeAsync().GetAwaiter().GetResult()` deadlocks permanently.
- New regression coverage for all of the above, plus the previously untested concurrent
  multi-endpoint dispose drain (#69 adversarial fix 5): N wedged endpoints cost ~1× the drain
  budget, not N×.

> The Phase 6 close-out: every PRD requirement that was still open is addressed on one branch.
> Phases 0–5 were already complete; this entry closes the interop, samples/DX, observability,
> and straggler-FR gaps identified in the 2026-08-01 PRD conformance sweep.

### Added — standalone fixture repo, cross-implementation harvest, published vectors (FR-IX-03/06/07/09, §13.3)

- Fixtures moved out of the test tree into the standalone
  [didcomm-dotnet-fixtures](https://github.com/moisesja/didcomm-dotnet-fixtures) repository,
  consumed as a git submodule at `tests/DidComm.InteropTests/fixtures` (the overdue Phase 0
  migration; PRD §13.3). CI and release checkouts init submodules.
- 68 fixture manifests: the spec-v2.1 baseline plus static inbound vectors harvested from
  `sicpa-dlab/didcomm-rust`, `didcomm-python`, and `didcomm-jvm` (commit-pinned `source_ref`s,
  byte-verbatim envelopes) — all pass (FR-IX-03).
- The runner gained the `expected.outcome=error` path, unlocking 13 negative vectors
  (off-curve `epk`, damaged JWE parts, tampered JWS); every one fails with the library's typed
  exceptions — no raw exception leaks (FR-API-07 held).
- `tools/FixtureGen` publishes our own `source: didcomm-dotnet` vector set (16 compositions),
  self-verified by the runner against bit-rot (FR-IX-06). Credo-TS scaffold (non-gating,
  FR-IX-07) and the `interop-a-thon-<date>` convention (FR-IX-09) documented.

### Added — live cross-implementation harness (FR-IX-04/05/08)

- `tools/InteropCli` (mint/pack/unpack over the real facade + net-did `did:peer`) driven by
  `tools/interop-live`: version-frozen Python (`didcomm==0.3.2`) and JVM
  (`org.didcommx:didcomm:0.3.2`, SHA-256-pinned jars) legs round-tripping the §13.5 matrix in
  both directions — 20 compositions each way, plus external verification of the published
  vectors. Executed, unshimmed, and green: python 56/56 with no declared `n/a`; jvm 53/53 with
  three declared `n/a` (all secp256k1, a JDK curve gap — see below).
- `.github/workflows/interop-live.yml`: nightly + on-release + manual, with a per-cell summary
  artifact; offline interop continues to gate every PR (FR-IX-08).
- Conformance findings from the live runs, documented in `tools/interop-live/README.md`. The
  harness earned its keep: it surfaced three didcomm-dotnet defects that no amount of
  self-round-tripping would have — the JWS `kid` was duplicated across the protected and
  unprotected headers (RFC 7515 §7.2; dataproofs-dotnet#17), then briefly placed protected-only
  which is not the DIDComm shape (dataproofs-dotnet#25), and forward attachments carried no `id`
  so didcomm-jvm could not route them. All three are fixed and those cells now execute and pass.
  The one remaining `n/a` (ES256K against didcomm-jvm) is a JDK/nimbus curve gap, tracked at #71
  with a falsification condition and a spec-vector control.

### Added — straggler FRs (FR-ROT-06, FR-I18N-04, FR-SIG-05, FR-TRN-12)

- `from_prior` relationship termination: a JWT that omits `sub`, valid only on a `from`-less
  message, surfaced via `FromPriorClaims.IsTermination` (FR-ROT-06). Stricter side effect:
  a rotation-shaped `from_prior` on a `from`-less message was previously ignored; it is now
  validated and rejected (pinned by test).
- `w.msg.bad-lang` / `e.msg.bad-lang` problem-report factories, including the
  `ThreadState`-aware seam honoring FR-I18N-02 preferences (FR-I18N-04).
- `PackSignedAsync` warns (LoggerMessage source-gen) when a standalone signed message lacks
  `to` (FR-SIG-05); DI now passes `ILogger<DidCommClient>` through.
- Minimal opt-in STOMP 1.2 subset over WebSocket — CONNECT/CONNECTED/SEND/DISCONNECT with
  `content-type: application/didcomm-encrypted+json`, strict escaping/content-length codec,
  off by default on both send and receive sides (FR-TRN-12).

### Added — observability (NFR-04/05/06)

- `DidCommDiagnostics` exposes the `didcomm-dotnet` `ActivitySource`; spans for pack
  (plaintext/signed/encrypted), unpack, send, resolve, and handle carry header-level
  attributes only (type URI, JOSE alg/enc ids, recipient count, DID method name, dispatch
  outcome, transport scheme, exception *type* name). Zero-cost when unobserved; the unused
  `OpenTelemetry.Api` package reference was dropped — consumers wire their own SDK.
- LoggerMessage source-gen breadcrumbs for pack/unpack/send failures (type names only).
- NFR-04 redaction audit test: captures every span and log line across all instrumented flows
  and proves no private scalar or body/attachment sentinel leaks; positive span assertions
  keep it from passing vacuously.

### Added — the complete sample set and the DX gate (FR-DX-01..09, §14.2–§14.4)

- The Cookbook now implements all 28 §14.2 task letters (13 new sections: DI setup, message
  building, every envelope posture, explicit content encryption, multi-recipient, attachments,
  custom secrets resolver + the net-did `IKeyStore` bridge, custom transport) (FR-DX-04).
- Samples 03–10 built (§14.3): EnvelopesAndMessages, MediatorAgent (Routing 2.0 over loopback
  HTTP with DID-published routingKeys), WebSocketChat (ping/discover-features/chat/reconnect),
  OutOfBand (URL round-trip + pthid correlation), ProblemsAndProtocols (taxonomy, escalation,
  cascade guard, custom handler, observer seam, tracing default-off), Extensibility (opaque
  mock KMS, `kidToAlias` bridge, custom transport), NetDidIntegration (did:key/did:peer
  minting, Ed25519→X25519 derivation, did:web rejection from six entry points),
  ProfilesAndI18n (accept negotiation, thread-scoped accept-lang, wire-delivered bad-lang).
  Every sample: README + in-process CI smoke test (FR-DX-02/03).
- **The FR-DX-01 gate**: a Mono.Cecil coverage test enumerates the six shipped assemblies'
  public surface (585 members) against every sample assembly's full metadata —
  **0 undemonstrated members**, with documented structural exemptions and a 12-entry justified
  allowlist. The §14.4 matrix is asserted as data (FR-DX-09); a drift check pins the README
  quickstart to `samples/01-Quickstart` (FR-DX-06). `<example>` tags on 42 major public types
  point at their demonstrating sample (FR-DX-08).

### Added — benchmarks (NFR-07)

- `benchmarks/DidComm.Benchmarks` (BenchmarkDotNet): anoncrypt/authcrypt pack (1 recipient),
  unpack, did:key resolve; results vs the NFR-07 targets recorded in the project README.

### Changed — XML docs enforced (NFR-02)

- `Directory.Build.props` no longer suppresses CS1591: with warnings-as-errors, every public
  member of the six shipped packages must carry XML docs (the surface was already at 100% —
  this pins it). tests/samples/tools/benchmarks re-suppress locally.

### Fixed — QA pass over the close-out branch

- STOMP encoder now rejects NUL in header names/values — STOMP 1.2 has no NUL escape, so such
  a frame truncates for NUL-scanning parsers (FR-TRN-12).
- `from_prior` with a present-but-non-numeric `exp`/`nbf` is now rejected as malformed instead
  of silently ignored — the old behavior erased the issuer's FR-ROT-05 replay bound.
- The WebSocket transport serializes concurrent sends per endpoint: a colliding second send
  previously killed the healthy pooled socket and surfaced an unretried `TransportException`
  (FR-TRN-09/11). Sends to different endpoints remain parallel.
- Bad-lang matching is no longer satisfied by two empty primary subtags (hostile/garbage
  `accept-lang` tags could suppress the FR-I18N-04 report).
- interop-live scripts use GNU-compatible `mktemp` (they were BSD-only, which would have
  broken the nightly job's first run on ubuntu-latest).
- Un-ignored `DxCoverage/coverage-allowlist.json` (a Coverlet ignore pattern swallowed it) —
  without it the FR-DX-01 gate errors on any fresh clone.

### Fixed — the blocking review on #69 (six findings, every one reproduced before it was touched)

Verification confirmed all six review findings against the branch as reviewed, and each fix is held
by a regression test written red-first — the test fails against the code it replaces.

- **The STOMP client accepted a CONNECTED frame at any version.** It offered `accept-version: 1.2`
  and then took whatever the server answered, so a peer speaking a different STOMP version was
  treated as a successful handshake. `WebSocketDidCommTransport` now requires `version: 1.2` on
  CONNECTED — first-wins, per STOMP 1.2's rule for repeated headers — and fails with
  `TransportException` otherwise, mirroring the check `StompServerSession` already performed on the
  server side.
- **The reconnect budget did not cover STOMP handshake failures.** Handshake failures surface as
  `TransportException`, which the Polly retry predicate excluded, so a transport configured with
  `MaxReconnectAttempts > 0` still made exactly one attempt — the setting silently did nothing on
  the one failure class STOMP adds. `TransportException` is now retryable; scheme refusal and the
  SSRF guard throw before the pipeline is entered, so those are still never retried. One handshake
  failure stays deliberately fatal: a protocol version mismatch cannot become true on a retry, so it
  fails immediately, with a message saying no reconnect was attempted. The outer rethrow was removed
  so every retryable failure surfaces in one consistent exhausted-budget shape rather than two shapes
  depending on where the attempt gave up.
- **`DisposeAsync` could hang, throw, or leak** — a cluster of defects an adversarial pass found in
  this branch's own first-cut dispose changes. Dispose waited on the peer's close frame, so a peer
  that never answered hung teardown forever (the shutdown writes are now bounded and use
  `CloseOutputAsync` instead of waiting for the peer to close back). A concurrent second
  `DisposeAsync` threw `ObjectDisposedException` and skipped teardown entirely, leaking the pooled
  HTTP handler (disposal is now idempotent behind an `Interlocked` latch). A send still in its
  connect phase escaped the drain and could republish a socket into an already-cleared pool (the
  drain now covers connect gates as well as pool entries, with a seal the connector re-reads after
  publishing). The drain budget was smaller than the hold it was meant to cover, so teardown could
  kill a healthy in-flight send (it is now `ConnectTimeout + SendTimeout`, and endpoints drain
  concurrently rather than one after another). And a negative or infinite `SendTimeout` could hang
  or abort teardown outright. Separately, lifecycle handlers are no longer invoked while the send
  gate is held — a handler that sent to the same endpoint deadlocked against the gate its own
  notification was holding.
- **`bad-lang` matching treated script variants as interchangeable.** Any shared primary subtag
  counted as a match, so `sr-Cyrl` satisfied a request for `sr-Latn` and `zh-Hant` satisfied
  `zh-Hans` — not interchangeable to a reader — and the report the peer was owed was suppressed.
  `ProblemReport` now matches directionally, RFC 4647-style: a tag matches when it is equal to the
  other or extends it at a subtag boundary, with no culture-sensitive comparison anywhere, since
  `accept-lang` is untrusted remote input. Two related corrections ride along: an `accept-lang` of
  `*` (RFC 4647's "any language") is honored instead of producing a spurious report, and a malformed
  tag with an empty subtag matches nothing at all — not even a byte-identical copy of itself. The
  reference sample's language selector was widened to the same rule, so core and sample agree on
  what "available" means.
- **Forward attachments now carry an `id`.** Attachment `id` is optional in the spec, and the
  routing-2.0 example omits it — but didcomm-jvm's `Attachment.parse` rejects an attachment without
  one, which made our forward onion unroutable through a JVM mediator. Both reference
  implementations emit one, and `ForwardMessage` now does too. Found by the live interop harness,
  and confirmed fixed against didcomm-jvm.
- **`DataProofsDotnet.Jose` 1.1.1 → 1.3.0 — the JWS `kid` header shape.** Up to 1.1.1 the signer's
  `kid` was written into *both* the protected and the unprotected per-signature header, violating
  RFC 7515 §7.2's requirement that the two be disjoint; nimbus — and therefore didcomm-jvm — rejects
  such a JWS outright. Filed and fixed upstream as dataproofs-dotnet#17. The first fix moved `kid`
  to the protected header only, which is disjoint but *not* the DIDComm shape — Appendix C and both
  SICPA implementations carry it in the unprotected per-signature header — so that briefly took
  outbound signed interop from one working counterpart to none, and was corrected as
  dataproofs-dotnet#25 (unprotected-only, shipped in **DataProofsDotnet.Jose 1.3.0**, which also
  emits the General JWS serialization at every signer count). A regression test here asserts both
  properties at once — sets disjoint *and* `kid` unprotected — and is red on both historical
  shapes. The published vector set was regenerated on 1.3.0. **Signed cross-implementation
  round-trips now work**: both legs verify them, and external verification under didcomm-jvm went
  from 12/16 to 15/15 runnable vectors.

### Fixed — sample selector and core matcher agree at every truncation depth (#73)

The #69 re-review found the two sides of the bad-lang contract diverging one truncation level
deeper than the case that review fixed: the core matcher satisfies a preference at **any** subtag
boundary (`zh-Hant-TW` is served by an offered `zh-Hant`), but the reference sample's language
selector truncated only to the *primary* subtag (`zh-Hant-TW` → `zh`), so against a catalog
holding `zh-Hant` the matcher stayed silent — no report owed — while the sample answered in its
default language. Reproduced red-first: the new sample beat showed `Bad-lang report warranted =
False` next to a reply in `en`.

- The sample selector (`samples/10-ProfilesAndI18n`) now truncates one subtag at a time from the
  right, most specific first — and stays no *broader* than the matcher: the broad direction (a
  bare `en` request served by an `en-GB` entry) applies to the request as sent, never to a
  truncation of it, which would have served a sibling (`zh-Hant-TW` handed a `zh-Hant-HK` entry)
  the matcher rightly reports as unsatisfiable. It also skips malformed tags (`en-`, `-en`,
  `en--GB`) under the matcher's own rule, closing the reverse disagreement where a spurious
  report shipped alongside a served reply.
- Bob's catalog gained `zh-Hant` and the sample a depth-2 beat, so the smoke test pins the
  agreement end-to-end; core-side tests pin `zh-Hant-TW` vs `{zh-Hant}` (satisfiable, no report)
  and `zh-Hant-TW` vs `{zh-Hant-HK}` (siblings below a shared prefix — report owed).
- The `CreateBadLangForThread` doc-comment no longer overstates the old selector: it now spells
  out what a mirroring selector must do — truncate at *every* boundary, serve the broad
  direction, and never broaden a truncation.

### Changed — the public-API coverage gate now measures execution, not reference

The gate as first built counts *metadata references* from the sample assemblies, and it propagated a
single referenced interface member onto every shipped implementation of that interface — so
"demonstrated" could mean no more than that a type appeared in a local variable's signature. That
proves presence in sample source, not that a line of the member ever ran.

- A second gate instruments the shipped assemblies with Mono.Cecil, re-runs all ten samples and the
  28 cookbook sections in-process against the instrumented copies, and intersects the real execution
  hits with the public surface: **572 of 585 members are proven executed**, the remaining 13 have no
  executable code at all (enum members, const holders), and the execution allowlist is empty —
  nothing is waved through.
- The reference gate is kept as a cheap first check — it catches "not referenced anywhere" for the
  price of a metadata walk — and is renamed to say what it actually measures
  (`EveryPublicMember_IsReferencedByASampleAssembly`).
- Two members the reference gate passed while nothing ever executed them — the HTTP and WebSocket
  transports' `Scheme` property — are now genuinely exercised by the Cookbook rather than being
  waved through on a reference.

### Changed — a wider live interop matrix

`tools/interop-live` now drives 20 compositions, up from 9, in both directions against the
version-frozen didcomm-python and didcomm-jvm legs, and additionally verifies the published vector
set from outside this codebase. The added cells cover P-256 key agreement, ES256 and ES256K signing,
`did:key` peers, multi-recipient envelopes, and forward routing through one and through two
mediators. This is breadth of coverage, not a conformance statement: what each run establishes, and
which counterpart gaps remain open, is recorded in `tools/interop-live/README.md`.

### Changed — dependency refresh

- **NetCrypto 1.4.0 → 1.5.0** — the secp256k1 verify path rejected high-S ECDSA signatures,
  inheriting libsecp256k1's Bitcoin-oriented policy through NBitcoin.Secp256k1. RFC 8812 §3.2
  defines an ES256K signature as `R‖S` and imposes no low-S rule, so didcomm-python's signatures
  are conformant and were being reported as invalid — indistinguishable from tampering. Fixed
  upstream (crypto-dotnet#23) rather than behind a local decorator, because the same rejection
  affected every NetCrypto consumer. Retiring the interop `n/a` needed more than a green cell:
  inbound ES256K failed only about half the time, since a low-S signature passes either way, so
  it was retired by classifying 20 signatures by S parity before verifying — 8 of 8 high-S now
  verify, against 0 of 8 on 1.4.0.
- **NetDid 3.0.0 → 3.1.0** (all five packages). An additive minor: it still depends on
  NetCrypto (now resolved to 1.5.0 by the pin above) and on Microsoft.Extensions.* 10.0.8 (below
  the 10.0.10 pins here), so the direct and transitive graphs stay in agreement — no downgrade,
  no source change.
- **Microsoft.SourceLink.GitHub 8.0.0 → 10.0.301** — build-only (`PrivateAssets="All"`, never
  shipped); the 10.0.x line is the one that tracks the net10.0 SDK this repo builds on.
- The test stack (Test.Sdk, xunit.runner.visualstudio, NSubstitute, coverlet, FluentAssertions)
  stays pinned on purpose — FluentAssertions in particular is held at 7.0.0 to avoid the 8.x
  Xceed relicensing (paid for commercial use). **DataProofsDotnet.Jose 1.1.1 → 1.3.0** carries the
  JWS `kid` fixes described above (dataproofs-dotnet#17 then #25) and emits General JWS at every
  signer count; **NetCrypto 1.4.0 → 1.5.0** is the high-S ES256K verify fix, both detailed above.

### Fixed — flaky Discover Features correlator test under thread-pool contention (#66)

`DiscoverFeaturesClientTests.Disclosure_parsing_runs_after_the_inline_correlator_returns` timed out
on the ubuntu leg of CI while the Windows leg and the same tree on `main` passed — a scheduling
flake, not an assertion failure. Reaching the wait it timed out on requires two thread-pool
scheduling hops (the `Task.Run` that invokes the correlator, and the asynchronous requester
continuation that calls the parser) while the test itself parks a pool thread. On a two-core runner
the pool injects threads at roughly one per 500 ms once the initial burst is spent, so the 5 s
budget was not generous.

- **The trigger is gone.** The concurrent-unpack test added in #63 queued 64 `Task.Run` unpacks;
  it now uses 16. The invariant it pins — every concurrent unpack registers exactly one snapshot —
  does not get truer with more threads, but xUnit runs test collections in parallel, so that burst
  could saturate a small runner's pool and starve waits in whatever ran alongside it.
- **The fragility is gone.** Test-synchronization waits in `DiscoverFeaturesClientTests` now use a
  named 30-second `SyncTimeout`, following the precedent `ProtocolDispatcherObserverTests` already
  set for observer-pump scheduling: a budget paid only on the failure path, where a tight value
  reports contention as a defect. Client deadline arguments that are themselves under test — every
  `QueryFeaturesAsync` timeout assertion — are deliberately untouched at 5 s.
- **One less scheduling hop.** That test's correlator call now runs on a dedicated thread rather
  than the pool; the call is synchronous and the continuation it releases blocks, so the pool was
  the wrong home for it. The remaining hop is the asynchronous requester continuation, which is the
  behavior under test.

Not reproducible locally — three pre-fix runs under `DOTNET_PROCESSOR_COUNT=2` passed, since that
knob caps the runtime's CPU heuristics while real cores stay available. The change is reasoned from
the stack trace and existing precedent rather than validated against a live repro; if it recurs, the
next step is a genuinely constrained runner rather than a larger timeout.

### Added — `samples/01-Quickstart`, and a README that matches reality (FR-DX-05)

The README advertised `0.1.0-preview.1` as unpublished and told readers to build from source — four
releases after that stopped being true — while contradicting itself on Phase 6 status in three
places and describing `DidComm.Protocols.*` packages that were designed away rather than deferred.
It also lacked the quickstart FR-DX-05 requires. Corrected end to end:

- **New `samples/01-Quickstart`** (§14.3 sample 01): two `did:peer:2` identities, an authcrypt
  round-trip, and the unpack metadata, in 25 lines. The README snippet is this project's `RunAsync`
  body verbatim, and an interop smoke test runs it on every build — so the snippet cannot drift from
  code that compiles, which is how the rest of the README rotted.
- **Facts corrected:** install instructions (no `--prerelease`, no "build from source"), the NuGet
  badge (stable, not prerelease), NetDid 1.3.0 → 3.0.0, and `did:web` resolution scope — `did:key`
  and `did:peer` are wired by default; other net-did methods plug into `UseNetDidResolver(b => …)`.
- **Test counts removed** rather than re-pinned. The same number was stale in three places with
  three different values; the README now points at CI instead.
- **Phase 6 stated honestly:** protocols, OOB, threading, i18n, and the release pipeline are done;
  the live interop harness (FR-IX-03..06/08), samples 03–10 and cookbook tasks A–J/L/Y/Z (FR-DX-04),
  the public-API coverage gate (FR-DX-01/09), and the NFR-04/07 observability and benchmark work are
  not. The cookbook's coverage is given as 15 of 28 §14.2 tasks with the gaps named.
- **API section refreshed:** `IDidKeyBindingService` is shown as the path the unpack pipeline
  actually takes since 1.4.0 (`IsKeyAuthorizedAsync` is no longer called there), alongside
  `IProtocolObserver`, `UnpackResult.RecipientAddressing`, `DidCommOptions.OwnIdentifiers`, and
  `MapDidCommOobEndpoint`.
- **Broken link fixed:** the Code of Conduct section linked a `CODE_OF_CONDUCT.md` that does not
  exist in this repository; it now links the Contributor Covenant itself.

### Changed — package validation baseline moved to 1.4.0

`PackageValidationBaselineVersion` now points at 1.4.0, the previous published release, so each pack
compares against what consumers actually have. Routine post-release bookkeeping (see the note in
[`Directory.Build.props`](Directory.Build.props)).

### Known PRD drift (PRD is the target; flagged, not silently patched)

- §14.2-Y shows `UseSecretsResolver(sp => ...)` — no factory overload exists.
- §14.2-Z names `UseTransport<T>()` — the real surface is per-transport extensions plus
  `b.Services.AddSingleton<IDidCommTransport, T>()`.
- §14.4 names illustrative members (`DidComm` facade, `Secret`, `Pack*Params`) that map to
  the real API (`DidCommClient`, `Jwk`, `Pack*Options`) — mapping documented in the gate test.

## [1.4.0] - 2026-07-31

> Additive security + feature release — new public API, no wire change, no breaking change
> (ApiCompat / package validation clean against the 1.3.0 baseline). Record value-equality
> semantics extend to the new members — see the upgrader notes below. Closes **#56**, **#58**,
> **#59**, **#61**, **#63**.

### Added — FR-CONSIST-04 wired: the "recipient not named in `to`" warning is now surfaced (#59)

The spec's SHOULD-level rule — *the recipient SHOULD verify that their own identifier appears in
the `to` list … MUST NOT fail to accept … but SHOULD give a warning* — existed only as the
never-called pure function `AddressingConsistency.IsRecipientInTo`, so no code path could ever
emit the warning. It is now wired into the unpack pipeline and surfaced **as data** rather than a
log line, so applications can act on it in policy code:

- **New `UnpackResult.RecipientAddressing`** (public-get/internal-init, additive over the v1.3.0
  positional constructor — binary compatible, consumers can read but never forge it): a
  three-state enum. `Addressed` — one of the agent's own identifiers appears in `to` (compared as
  DID subjects); `NotAddressed` — `to` is present, an own identifier was known, and none appear —
  the FR-CONSIST-04 warning, delivered anyway; `NotEvaluated` — no `to` header or no own
  identifier to check against (also the default on synthetically constructed results).
- **New `DidCommOptions.OwnIdentifiers`** (default empty): the DIDs (or DID URLs) the agent
  considers its own identity. The check evaluates the union of these and the decrypting recipient
  kid's DID subject. Declaring identities is what makes the warning reachable on **signed-only
  and plaintext** receives — the paths that previously had no addressing check at all. An entry
  that does not parse as a DID is rejected when `DidCommClient` is constructed so a typo cannot
  silently disable the signal.
- **Interaction with FR-CONSIST-02 (unchanged):** for encrypted envelopes with a present `to`,
  the stronger MUST already rejects a message whose `to` omits the decrypting kid's DID subject —
  that ordering is preserved (the MUST throws before the advisory is computed), which is why the
  warning primarily matters on non-encrypted receives.

The rule lives with its siblings as `AddressingConsistency.CheckRecipientAddressing`, replacing the
never-called `IsRecipientInTo` predicate whose absence of a caller was the defect.

Advisory only: no `RecipientAddressing` value ever affects delivery, and the surfaced value is an
enum — no plaintext body content enters logs or metadata (NFR-04).

Three properties of the implementation are load-bearing, and each is pinned by a test that fails
against the implementation that lacks it (verified by injecting the defect):

- **Per-message operation count has no roster-linear term.** `OwnIdentifiers` is
  parsed to DID subjects, deduplicated, and frozen once at `DidCommClient` construction; unpack then
  walks only the `to` list against that prebuilt set and never enumerates it. Calling the old
  per-identifier predicate once for every configured identity would cost
  `O(|to| × |own|)`; the first wired implementation improved that to `O(|to| + |own|)` but still
  made every unauthenticated message pay a roster-sized term. The frozen-set design removes that
  receiver-chosen CPU multiplier and the roster-proportional latency term; ordinary cache effects
  may still vary with set size. `Recipient_addressing_probes_every_to_entry_without_enumerating_the_declared_identity_set`
  asserts the operation-count invariant structurally rather than by timing.
- **No match-position-dependent operation count.** Which `to` entry matched, or that none did,
  does not change the number of parses or set lookups performed.
  `Recipient_addressing_consumes_the_whole_to_sequence_even_when_the_first_entry_matches` counts
  wire enumeration, while the parameterized probe-set test requires one lookup per parseable
  entry for first/middle/last/miss. This removes the coarse short-circuit channel; it is *not* a
  constant-time membership guarantee. Frozen-set hit/miss paths and ordinal string equality are
  data-dependent, and repeated guessed DIDs can amplify that residual difference.
- **Delivery really is unaffected.** Snapshotting also decouples the check from the caller's live,
  mutable collection, which if enumerated mid-unpack could throw `InvalidOperationException` and
  drop a message the advisory check must never touch. An entry that is not a parseable DID fails at
  construction with `ArgumentException` rather than being skipped silently on every message, which
  would leave the warning permanently dead with no signal to the operator.

### Added — FR-CONSIST-04 outcome mirrored onto the verified snapshot and `InboundObservation` (#61)

The wiring above originally left the outcome on the facade `UnpackResult` only, so observer-only
applications could not read it, and the value went stale if a caller rewrote `Message.To` or
`with`-cloned the result onto another message. Resolved before release: the outcome now rides the
same snapshot backstop #56 built for the key bindings.

- **New `InboundObservation.RecipientAddressing`** (public-get/internal-init, additive over the
  positional constructor — binary compatible): observers receive the outcome on every dispatch
  path — handled, `NoHandler`, ack-loop drop, and handler failure — so an application that does
  its addressing policy in an `IProtocolObserver` actually sees the warning.
- **Three explicit trust-source states:** `InboundObservation.FromUnpackResult` no longer treats
  verified-content divergence like a synthetic result.
  - When the verified snapshot still covers the current message content, all observation metadata
    comes from it: the six positional trust members (`Encrypted` / `Authenticated` /
    `NonRepudiation` / `AnonymousSender` / `SenderKid` / `SignerKid`), all three #56 key bindings,
    and `RecipientAddressing`. Verified null kids and bindings remain null; they never fall back to
    caller claims.
  - When a verified snapshot exists for the message instance but its content has diverged, the
    observation fails closed: all four flags are `false`, both kids and all three bindings are
    null, and addressing is `NotEvaluated`. No caller-supplied trust value survives.
  - Only a message that was never associated with a verified snapshot takes the synthetic
    compatibility path. It preserves the caller's six positional members, but all three bindings
    remain null and addressing remains `NotEvaluated`, whatever the synthetic result claims.
- **Never desynced on the verified-snapshot path:** normal observer delivery for a real unpack
  materializes the message and all metadata from the same verified snapshot, so an in-place edit
  after unpack cannot hand an observer a (content, metadata) pair the unpack never produced.
  The public dispatcher also accepts caller-created synthetic results; that fallback preserves
  only the six positional caller claims and is not cryptographic evidence. **Scope:**
  `InboundObservation` is itself a record. A consumer's `with`-clone copies the stored values onto
  whatever `Message` the clone carries, and direct mutation changes the mutable message without
  recomputing metadata. The pairing guarantee therefore covers observations as the library
  constructs them; after either consumer action, stored values still describe the message the
  observation was *built* from (pinned by a test documenting the clone limitation).

### Fixed — same-document key provenance on unpack (TOCTOU identity gap, #56)

`UnpackAsync` used to verify/decrypt with public-key material from one DID resolution (the
sender/signer JOSE lookups) and then authorize the same kid against a **second** resolution
(`IsKeyAuthorizedAsync` behind the FR-CONSIST-06 predicate). With a mutable, stale/fresh,
evicted-cache, or inconsistent resolver, the two documents can differ — so an envelope verified
with a rotated-out key `KA` could be reported authenticated because a later document still lists
the same kid (now bound to `KB`), and key/controller facts could be spliced across document
versions that were never simultaneously true.

On the provenance-capable path (the built-in `NetDidKeyService`), unpack now:

- **captures the binding atomically**: each sender/signer key lookup resolves the kid's DID once
  and retains the exact JWK together with the normalized kid, DID subject, controller, and
  relationship from that single `DidDocument` (`IDidKeyBindingService.ResolveKeyBindingAsync`,
  new optional capability interface; duplicate/shadowed relationship entries for the same
  normalized kid fail closed with `DidResolutionException`);
- **never re-resolves sender/signer authority after crypto**: the FR-CONSIST-06 controller rule
  is evaluated against the captured binding (`ConsistencyException` semantics unchanged);
  recipient evidence is resolved exactly once, after the decrypting kid is known;
- **scopes evidence to the operation**: a per-unpack lookup context serves the existing JOSE
  callback contracts — no process-global state, no `AsyncLocal`, no TTL/eviction, no
  cross-message races (concurrent unpacks of the same kid hold independent bindings);
- **surfaces immutable evidence**: new `VerifiedKeyBinding` (kid, DID, controller, relationship,
  RFC 7638 SHA-256 key thumbprint; internal constructor — consumers can read, never forge)
  carried as `SenderKeyBinding` / `SignerKeyBinding` / `RecipientKeyBinding` on `UnpackResult`,
  the verified inbound snapshot, and `InboundObservation` (public-get/internal-init properties;
  the v1.3.0 positional constructors are untouched, so the release is binary compatible).
  Synthetic `UnpackResult`s (constructed by callers rather than produced by a real unpack)
  yield no bindings — flags alone cannot manufacture provenance.

Custom `IDidKeyService` implementations remain source- and binary-compatible: without the new
capability they keep the pre-1.4.0 two-resolution behavior and surface null bindings, which the
API documentation now explicitly calls out as *not* same-resolution controller provenance.
Downstream consumers (e.g. opaqai-wallet-platform) can drop global resolver-observation ledgers
and read the message-scoped bindings instead.

### Fixed — `from_prior` rotation accepted across two DID resolutions (same bug class, #56 review)

`FromPriorValidator` authorized the rotation JWT's signer kid in one resolution
(`IsKeyAuthorizedAsync`) and then fetched the verifying key in a second
(`GetVerificationMethodsAsync`). A resolver whose document changed between the two calls could
authorize the victim's genuine key while the JWT was verified with a replacement key under the
same kid — a forged accepted rotation, which hands the attacker whatever relationship the prior
DID held. On the provenance-capable path both now come from a single resolution, with the same
controller rule applied to that binding (duplicate kids fail closed there too, where the old
`FirstOrDefault` silently took the first match). Because that lookup resolves the kid's own DID
rather than `iss`, the signer kid is now required to sit under `iss` before any resolution runs, so
an attacker-chosen kid cannot steer the agent into resolving an arbitrary DID first. Legacy key
services keep the old shape.

### Fixed — decrypted recipient entry must match its own label (#56 review)

The recipient `kid` a JWE reports is the label of whichever recipient entry the derived KEK
opened, and those labels are attacker-authored: `apv` commits only to the *sorted* kid list, so
permuting two entries' labels leaves the envelope cryptographically intact. An envelope whose
real key-wrap was labelled with another DID's kid therefore decrypted with the local key while
reporting someone else's — and the new `RecipientKeyBinding` would have described that other key
as the one that decrypted. Unpack now rejects (`ConsistencyException`) unless the reported
recipient kid is exactly the kid whose key agreement produced the KEK (new FR-CONSIST-08). The
check runs only after a successful decrypt, so it reveals nothing about which recipient keys are
held. `RecipientKid` was already untrusted metadata in 1.3.0; this makes it trustworthy.

### Also hardened (PR review)

- **Recipient provenance is now proven, not assumed.** The recipient key comes from the local
  secrets resolver while its evidence came from a separate document resolution, so if our own
  document rotated that kid to new material while we still held the old private key, the binding
  would have attested a key that did not decrypt the message. `RecipientKeyBinding` is now surfaced
  only when the resolved public key matches the local key's public identity (obtained without
  touching private material — the shipped keystore adapter answers it from the store's public key).
  Unproven means no binding, **not** a rejection: a message encrypted to a still-held, rotated-out
  key stays valid, it just carries no recipient provenance.
- **The FR-CONSIST-08 rejection is now the uniform decrypt failure.** Reporting it as a distinct
  `ConsistencyException` let a peer tell a key holder (who reaches the label check) from a
  non-holder (who fails in the decoy path) by exception category — the very recipient-possession
  oracle the constant-work decrypt path exists to close. It now throws the same `CryptoException`
  with the same message as any undecryptable envelope, which also means every caller that already
  handled an undecryptable envelope handles it unchanged.
- **A resolver must answer for the DID it was asked about.** All three `NetDidKeyService`
  resolution paths now reject a document whose `id` is not the requested DID. Authorization and
  binding evidence record the *requested* DID, so a cross-wired composite resolver, a mis-keyed
  cache, or a hostile implementation could otherwise have another subject's document attributed to
  it — including under FR-CONSIST-06 on the legacy path.
- **Duplicate-id rejection now covers dereferenced methods too.** A single relationship reference
  pointing at two same-id top-level `verificationMethod` entries was previously resolved to the
  first silently; it now fails closed, and dereferencing normalizes relative/absolute ids on both
  sides so `#key-1` and `did:x#key-1` match as DID Core intends.
- **Observer evidence cannot ride on mutated content.** `InboundObservation.FromUnpackResult` keyed
  the bindings on message object identity, which an in-place mutation preserves — so a caller could
  rewrite a verified message and hand an observer evidence that no longer covered it. Bindings are
  now dropped when the current content differs from the verified snapshot.
- The `ConcurrentUnpacks_SameKid_IndependentBindings` test did not actually run concurrently
  (synchronously-completed tasks). It now forces genuine overlap through a rendezvous resolver, and
  a second test races two envelopes signed by different keys under the same kid with a
  deterministic pairing.

### Also hardened (second adversarial pass over the fix)

- **Recipient selection now follows envelope order.** The reader picked the first kid
  `ISecretsResolver.FindPresentAsync` returned, but that contract promises a subset, not an order,
  while the parser scans recipient entries in envelope order. For an envelope carrying the same key
  material under two kids (one key published by two DIDs), a keystore returning the alias first
  would have made the new label check reject a legitimate message. Selection is now by envelope
  order, keeping the reader and the parser in step.
- **`from_prior` `iss` must be a bare DID.** Authorization compares DID subjects, so `did:x`,
  `did:x?v=1`, and `did:x/p` all authorize identically while remaining distinct strings — and `iss`
  is what an application keys its rotation-replay state on (FR-ROT-05 is delegated to that layer).
  The prior key's holder could otherwise mint unlimited equivalent-but-distinct `iss` values, each
  correctly signed. This restores the strictness the pre-1.4.0 path had implicitly.
- **A rejected envelope no longer tears down a WebSocket connection.** Both `MapDidCommWebSocket`
  receive loops (the dispatcher one and the delegate one) discarded only `MalformedMessageException`
  and `CryptoException`; anything else escaped and closed a socket that may carry other peers'
  traffic — a one-envelope disconnect. They now log and drop any unpack failure, including untyped
  faults the unpack contract is not supposed to emit but can (a non-string JOSE `kid` currently
  surfaces a raw `InvalidOperationException` from the delegated parser — see #58; a DID-resolution
  timeout arrives as `TaskCanceledException` with our token not cancelled). A genuine shutdown still
  ends the loop, and a throwing receive callback is logged instead of killing the connection.
  Pre-existing (a mismatched plaintext `to` already reached it), but the new label check widened
  reachability.
- `VerifiedKeyBinding.AuthorizedForDid` stores the DID subject that was compared rather than `from`
  verbatim, and `==` / `!=` now match `Equals` instead of falling back to reference equality.

### Notes for upgraders — same-document key provenance (#56)

- `VerifiedKeyBinding.AuthorizedForDid` records the asserted identity the controller rule was
  evaluated against, and is **null** when the plaintext carried no `from` — in that case the
  binding is key evidence only and its `Controller` was never compared to a claimed identity.
- `VerifiedKeyBinding` implements value equality, so `UnpackResult` / `InboundObservation` record
  equality keeps comparing structurally as it did in 1.3.0 (their synthesized `Equals` now also
  covers the three new properties).
- Key services that implement `IDidKeyBindingService` no longer have `IsKeyAuthorizedAsync`
  called during unpack — any extra policy it enforced must move into `ResolveKeyBindingAsync`
  (return null to reject). A decorator that wraps a capable service without forwarding the
  interface silently reverts to the legacy path; forward it.
- Authorization now compares DID *subjects* (per PRD §4.3), so a `from` that is a DID URL with a
  query string is accepted where the legacy path's raw-string resolution rejected it.

New requirements FR-CONSIST-07 / FR-CONSIST-08 (and the FR-API-04 update) document the invariants
in the PRD. 62 new tests cover the acceptance criteria: rotated-kid races in both orderings
(signed and authcrypt), controller splices, duplicate/shadowed kids (both as relationship entries
and as top-level methods behind one reference), relative/embedded/referenced ids and mixed
relative/absolute dereference, missing and cross-DID controllers, wrong relationships, documents
returned for the wrong subject, nested compositions (`anoncrypt(sign)`,
`anoncrypt(authcrypt(sign))`), from-less signed envelopes, genuinely-overlapping concurrent unpacks
(including two envelopes signed by different keys under one kid), observer/snapshot propagation,
mutated-content and synthetic-result denial, legacy-path compatibility, `from_prior`
single-resolution rotation, decorated-`iss` rejection, the swapped-recipient-label envelope,
recipient-key rotation, held-vs-unheld failure equivalence, resolver-order independence for aliased
recipient keys, and a WebSocket connection surviving a rejected envelope.

### Fixed — untyped faults could escape `UnpackAsync` on the signed path (FR-API-07, #58)

`DidCommClient.UnpackAsync` could throw a raw `System.InvalidOperationException`, breaking the
FR-API-07 promise that every unpack failure is a `DidCommException` subtype — the contract
`catch (DidCommException)` in consumer code and in the samples relies on. A remote,
**unauthenticated** peer triggered it with a flattened JWS whose unprotected `header.kid` was any
non-string JSON value: the read happens while the parser enumerates raw signatures, *before* any
signature is checked, so no key material and no prior relationship were required.

`EnvelopeReader`'s signed branch already mapped `MalformedJoseException`, `JoseCryptoException`, and
`ArgumentException` onto the contract, and its sibling JWE branch already had a catch-all. That
`ArgumentException` guard is now widened to the whole untyped-fault class: any exception from the
delegated JWS parse that is neither a cancellation nor already a `DidCommException` surfaces as
`MalformedMessageException` with the original preserved as `InnerException`.

Two deliberate details:

- The filter excludes the **entire `DidCommException` hierarchy**, not a hand-listed set. On the
  built-in `NetDidKeyService` the signer lookup *is* the DID-resolution path, so a
  `DidResolutionException` stays a resolution failure with its `Did` / `Reason` intact rather than
  being relabelled "malformed JWS".
- It maps to `MalformedMessageException`, **not** the uniform `CryptoException` the JWE branch's
  catch-all uses. That branch deliberately flattens failure shape to deny a recipient-possession
  oracle; the signed path has no such oracle to close.

The guard is independent of the dependency bump below. Verified both ways: with
`DataProofsDotnet.Jose` pinned back to 1.1.0 (the version carrying the upstream bug) the reported
repro still fails as a typed `DidCommException`, and with the guard removed the new
untyped-fault test fails.

Three tests cover it — the reported repro (contract), a signer lookup raising a raw
`InvalidOperationException` (the guard itself, which no dependency fix can satisfy), and a
`DidResolutionException` passing through untouched (so the filter cannot be narrowed later without
a failing test). The transports were already resilient to this fault class: the HTTP receive
endpoint collapses every unpack failure into one opaque 400, and both WebSocket receive loops
drop-and-continue.

### Changed — dependency refresh

First-party pins moved to current, and the graph now converges on a single `NetCrypto` and a single
`DataProofsDotnet.Core` version (no `NU1605`). No source changes were required; the full suite and
the Release build (including package validation against the 1.3.0 baseline) are clean.

- **`NetDid.*` 2.3.0 → 3.0.0** (`Core`, `Method.Key`, `Method.Peer`, `Method.WebVh`,
  `Extensions.DependencyInjection`). The major signals the scale of net-did's release — a new
  `did:ethr` method, shipped as a separate `NetDid.Method.Ethr` package DidComm does not reference,
  and the `DataProofsDotnet` major crossing beneath `did:webvh` — not a break: every public-API
  change 2.x consumers compile against is additive.
- **`NetCrypto` 1.2.0 → 1.4.0.** Additive and unused here (EC point decompression; recoverable
  secp256k1 signing for EVM flows, whose new `IKeyStore` member ships as a throwing default
  interface implementation so external key stores stay source- and binary-compatible). This is also
  net-did 3.0.0's own pin, so the direct reference and the transitive graph agree.
- **`DataProofsDotnet.Jose` 1.1.0 → 1.1.1.** Fixes dataproofs-dotnet#15 — the upstream root cause
  of #58 above — and additionally wraps `JwsParser`'s top-level `JsonDocument.Parse` so malformed
  JSON surfaces as `MalformedJoseException`.
- **`Microsoft.Extensions.*` 10.0.8 → 10.0.10**, **`OpenTelemetry.Api` 1.15.3 → 1.17.0**,
  **`Polly` 8.5.0 → 8.7.0** — servicing/minor updates.
- **`Microsoft.AspNetCore.TestHost` 10.0.0-preview.1.25120.3 → 10.0.10** — a .NET 10 *preview* pin
  left over from pre-GA scaffolding, now on the GA servicing line. Test/sample-only.

The test stack is held deliberately, not by neglect: `FluentAssertions` stays on 7.0.0 because 8.x
relicensed under Xceed (free for open source, paid for commercial use) and breaks API across the
suite — keeping 7.0.0 leaves this library's test dependencies unencumbered for downstream
contributors. `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `NSubstitute`,
`coverlet.collector`, and `Microsoft.SourceLink.GitHub` likewise stay put; moving the test stack is
its own change with its own risk. `dotnet list package --vulnerable --include-transitive` is clean.

### Fixed — the dispatcher's snapshot guard could raise a second fault while handling the first (#63)

`ProtocolDispatcher.DispatchAsync` wraps snapshot creation in a `catch` so a malformed caller-created
`UnpackResult` cannot break dispatch. That handler logged `received.Message.Id` — the very expression
whose null-ness is one of the faults it catches (both snapshot paths null-check the message). A
caller-constructed `UnpackResult` with a null `Message` therefore surfaced as `NullReferenceException`
*thrown from inside the catch* when an observer or correlator was registered, but as a clean
`ArgumentNullException` when neither was — the same input, two different faults depending on host
configuration. The message id is now read null-conditionally, so both configurations produce the same
`ArgumentNullException`. Not reachable from the wire: the library never produces a null `Message`, and
no unpack path accepts one — `UnpackResult` is a positional record, so only a caller can build this.

### Documentation

- **`InboundObservation.FromUnpackResult` thread affinity (#63).** The compatibility overload
  serializes the caller's live `Message`, so a caller mutating that message on another thread —
  most easily a collection-valued header such as `to` or `ack` — makes the serializer throw. The
  exception is now documented (`InvalidOperationException`, alongside `ArgumentNullException` and
  `JsonException`) together with the contract it implies: callers own the message's thread affinity
  for the duration of the call. No behavior change, and the library's own receive path is unaffected
  — observer delivery materializes every observation from the immutable snapshot, never from a
  mutable message.
- **Registration invariant pinned by test (#63).** `RegisterVerified` stores into a
  `ConditionalWeakTable` with `Add`, which throws on a duplicate key; what keeps that safe is that
  there is exactly one registration per unpack. New tests assert the count directly across all
  envelope shapes, envelope-shaped body/attachment/extension-header payloads, and repeated
  sequential and concurrent unpacks of identical bytes — so a future second registration site fails
  loudly in CI whether or not it reuses the same message instance. The fail-fast `Add` is deliberate
  and is now documented as such: `AddOrUpdate` would let a later, weaker registration silently
  replace verified trust metadata.

### Notes for upgraders — recipient addressing and observations (#59, #61)

- `RecipientAddressing` participates in record value equality: the synthesized `Equals` /
  `GetHashCode` / `ToString` of `UnpackResult` (#59) and `InboundObservation` (#61) now cover the
  new member. Two observations that agree on all seven positional members but differ on
  `RecipientAddressing` — e.g. a library-produced observation carrying `NotAddressed` versus a
  consumer-constructed one holding the default `NotEvaluated` — no longer compare equal, where
  before this release they did. Binary and source compatibility are unaffected; code that
  compared observations across that boundary should compare the members it means.
- `InboundObservation.FromUnpackResult` now has three trust-source states. A covering verified
  snapshot supplies all six positional members, all three key bindings, and addressing. A
  verified snapshot whose message content has diverged supplies none of them: the four flags are
  `false`, both kids and all three bindings are null, and addressing is `NotEvaluated`. Only a
  never-snapshotted synthetic message preserves the caller's six positional members for
  compatibility; caller-supplied bindings and addressing are never preserved.

## [1.3.0] - 2026-06-22

### Added — fail-closed skid guard on authenticated decrypt (defense in depth, #52)

Closes **#52**. The authcrypt unpack branch trusted the JOSE layer's definitional invariant
`IsAuthenticated ⟺ non-empty skid` without asserting it; had that invariant ever regressed
upstream, a null skid would have silently skipped the FR-CONSIST-01 `from`↔`skid` binding on an
envelope still reported authenticated (and an empty skid would have escaped as a raw
`ArgumentException`, off the FR-API-07 contract). `EnvelopeReader` now asserts a surfaced skid
whenever a decrypt is authenticated, rejecting with `ConsistencyException` — parity with the JWS
branch's empty-signer-kid guard. The check lives in `AddressingConsistency` as
`CheckAuthcryptSkidSurfaced` so it is directly unit-tested; it is unreachable through today's
parser, so no current input changes behavior. The JWS branch's `CryptoException` guard is
deliberately untouched.

### Changed — lazy UTF-8 size on the inbound snapshot (perf, #53)

Closes **#53**. Every successful `UnpackAsync` registers an `InboundMessageSnapshot`, but only the
observer byte-budget admission ever reads its UTF-8 size. The `Encoding.UTF8.GetByteCount` scan
(O(plaintext), bounded by `MaxReceiveBytes`) moved from the snapshot constructor to first access of
`Utf8ByteCount`, so unpack-only consumers and dispatcher graphs without observers no longer pay an
O(plaintext) pass per inbound message. Snapshot registration stays at the unpack boundary — the
verified-at-unpack immutability binding from #51 is unchanged, and all #51 correlator/observer trust
tests pass unmodified. New tests pin the lazy contract (no scan on registration, exact multibyte
count on first read, memoized thereafter).

## [1.3.0] - 2026-07-19

> Additive feature release — new public API, no wire change, no breaking change. Closes **#49**
> and **#50**.

### Added — Discover Features 2.0 initiator round-trip (FR-PROTO-05a)

Closes **#49**. Discover Features 2.0 previously shipped only the **responder** side; an application
could build a `queries` and parse a `disclose` but could not **correlate** the response, so the
requester role (programmatic capability discovery) was effectively unimplemented.

- **New `DiscoverFeaturesClient.QueryFeaturesAsync(from, to, queries, timeout, …)`.** Sends a
  `queries` and awaits the peer's `disclose`, correlating by `thid` == the query `id`. Registered by
  `AddBuiltInProtocols()`; resolve it from DI. The `timeout` bounds the **whole operation** (send +
  await) under one deadline; a transport/send failure propagates unchanged rather than being
  mislabeled "no disclose".
- **Queue-independent, spoofing-resistant correlation.** Successful unpack preserves one immutable
  copy of the exact verified plaintext and trust metadata before mutable handlers run. A bounded
  synchronous **`IInboundCorrelator`** checks that snapshot and atomically claims a pending query;
  typed disclosure parsing is forced onto the requester's continuation, even for reentrant loopback
  sends. This is NOT the best-effort observer queue, so legitimate responses are not dropped behind
  that firehose. Completion requires an authenticated sender whose sender/signer key DID subject
  matches plaintext `from` and the queried responder, a signed `to` naming the requester, and—for
  encrypted replies—a `RecipientKid` naming the requester that decrypted it. Rejected forgeries leave
  the waiter pending; response/cancellation races settle once, and only a response winner parses.
- **Out-of-band reply delivery is the responder application's job (FR-TRN-10).** The initiator runs
  a receive endpoint; the responder sends its `disclose` there with `DidCommClient.SendAsync`, bound
  to the authenticated inbound sender and local decrypting identity. The library performs **no
  automatic out-of-band endpoint egress**—avoiding the reflector / cross-tenant / endpoint-SSRF / DoS
  class. The pre-existing opt-in same-WebSocket convenience remains, but now requires encrypted+
  authenticated input and derives both participants from verified key ids; signed-only, plaintext,
  anoncrypt, keyless, cross-tenant, and fan-out cases emit no frame. A two-agent HTTP test proves the
  application-wired round-trip with separate key stores; real WebSocket crypto tests prove the
  decrypting tenant is selected even when another hosted tenant is first in plaintext `to`.

### Added — Inbound protocol observer seam (`IProtocolObserver`, FR-PROTO-12)

Closes **#50**. Lets an application observe inbound traffic whose PIURI is owned by a built-in handler
(e.g. react to an inbound `report-problem/2.0`) without **replacing** that handler.

- **New `IProtocolObserver` + `DidCommBuilder.AddProtocolObserver<T>()`.** Opt-in only —
  `AddBuiltInProtocols()` registers **no** default observer (Discover Features uses the bounded inline
  claim hook above), so a default deployment has no observer/firehose surface for a remote flood to
  target.
- **Admission before work, fully decoupled from dispatch.** Each per-observer queue atomically reserves
  both one outstanding item and the immutable snapshot's **exact UTF-8 byte count** before any
  observer-private deserialization. Bounds include queued + in-flight work, so count-full, cumulative-
  byte-full, oversized, and shutdown-racing drops perform zero clone work and cannot overshoot under
  concurrent producers. The dispatcher returns immediately; slow/hung/faulting observers cannot gate
  replies or alter outcomes, and independent pumps prevent starvation. Overflow is best-effort /
  at-most-once and rate-limited logs report the actual message id, configured capacity/budget, and
  cumulative drops. Each accepted observer materializes its own clone from the pre-handler snapshot.
- **Read-only, not a sandbox.** Each observer receives a defensive deep clone via `InboundObservation`
  (never the live message, the facade, or the thread store); the clone prevents mutation _through the
  payload_, but observers are trusted in-process host code registered at DI-composition time and are
  not otherwise sandboxed. No observer code runs on the dispatch path — the `ProtocolUriFilter` getter
  is read once, guarded, at construction (a throwing getter disables that observer). The registered
  observer set is logged at construction (operator-auditable).
- **`ProtocolDispatcher` is now `IDisposable` / `IAsyncDisposable`** (additive). Shutdown stops
  admission, cancels cooperative callbacks, drains queued plaintext/barriers, releases reservations,
  and observes pump faults before disposing their token source. .NET cannot kill trusted callback code
  that ignores cancellation; only its single in-flight snapshot may remain beyond the async grace,
  while the rest of that observer's queue is released immediately.

### Hardened — surfaced by adversarial + code review

- **Binary compatibility preserved + gated.** `ProtocolDispatcher`'s exact 1.2.0 four-argument
  constructor is retained (a new overload adds observers; the internal correlator ctor is separate),
  so code compiled against 1.2.0 keeps binding. **SDK Package Validation** (ApiCompat vs the published
  1.2.0 packages) runs at pack time AND in PR CI, failing on any binary-incompatible change.
- **Malformed `type` no longer throws on the dispatch path.** A crafted doubled-slash `type` (e.g.
  `https://didcomm.org//x/1.0/m`) parses as an MTURI but its derived PIURI does not; `TryResolve` and
  the observer filter now use `ProtocolIdentifier.TryParse` and fail closed (no handler / no match).
- **Real-crypto tests.** The initiator/anti-spoof paths are exercised against genuine authcrypt and
  JWS-signed envelopes unpacked end to end: legitimate ones complete; an authcrypt-tampered and a
  post-sign **signature-verification** tamper are rejected at unpack (`CryptoException`); a
  self-consistent third-party (authcrypt and signed) forgery cannot answer the query.
- **Same-socket identity binding.** Automatic WebSocket replies use verified `SenderKid`/`SignerKid`
  for the peer and `RecipientKid` for the local tenant, require the reply to name exactly that pair,
  and deep-snapshot the mutable handler reply before validation/packing. Discover Features and Trust
  Ping handlers prefer the actual decrypting DID instead of plaintext recipient ordering.

## [1.2.0] - 2026-07-13

> Foundation-pin bump only — no public API or behavior change in DidComm itself. Closes **#47**.

### Changed — Dependencies

Bumps the DID + crypto foundation pins so a wallet app that pulls `DidComm.*` transitively resolves
the `did:webvh` method driver at **≥ 2.2.0**, letting the downstream net-wallet-sdk 0.2.0 graph
converge on the `DidUpdateResult` evidence its identity document-update / key-rotation integrity
contract (FR-ID-10) requires. DidComm is the foundation library that references
`NetDid.Method.WebVh`, so this is the key step for that transitive graph to converge.

- **`NetDid.*` 2.0.1 → 2.3.0** — `NetDid.Core`, `NetDid.Extensions.DependencyInjection`,
  `NetDid.Method.Key`, `NetDid.Method.Peer`, `NetDid.Method.WebVh`. Pinned to the current latest
  (2.3.0) rather than the issue's 2.2.0 floor. DidComm consumes net-did for **DID resolution only**,
  and 2.1→2.3 is additive there: the changes are `did:webvh` Update/Deactivate/resolution security +
  conformance hardening (stricter SCID identity and controller-proof validation, post-sign
  nested-member tamper fixes — net-did#82, #91, #101). Resolution-only usage is unaffected.
- **`NetCrypto` 1.1.0 → 1.2.0** — required by `NetDid.Core 2.3.0`; the direct pin now matches the
  transitive graph (no `NU1605` downgrade).
- `DataProofsDotnet.Jose` stays at **1.1.0** (unchanged; already latest).

## [1.1.0] - 2026-06-22

> Follows the published **1.0.0** line — a normal minor bump (new opt-in feature, no breaking public API).
> The `DidCommVersion` build property had drifted to `0.1.0` in source after the 1.0.0 release; it is
> corrected to `1.1.0` here. (The 1.0.0 notes predate this changelog's per-version sectioning.)

### Added — Opaque (non-extractable) signing & key agreement (`feat/opaque-keystore-crypto-45`)

Closes **#45** (depends on, and ships after, `moisesja/dataproofs-dotnet#13`). DIDComm can now sign and
decrypt with private keys that **never leave a secure boundary** — HSM, cloud KMS, OS keychain, MPC, or
a `NetCrypto.IKeyStore` — instead of requiring an extractable private `Jwk`. This unblocks
non-extractable custody for downstream consumers (e.g. net-wallet-sdk's Messaging phase) whose invariant
is "private keys never leave the keystore" (FR-SEC-06).

Only **two** operations ever need the private key — a raw JWS signature and an ECDH shared-secret
derivation — so the change is a thin, additive seam; everything downstream of the signature/`Z`
(Concat-KDF, A256KW key wrap, AEAD, header assembly, signature normalization) is public-data math and is
untouched.

- **New optional capability `DidComm.Secrets.IOpaqueKeyResolver`.** An `ISecretsResolver` MAY also
  implement it: `ResolveSignerAsync(kid)` → `NetCrypto.ISigner` and `ResolveKeyAgreementAsync(kid)` →
  `DataProofsDotnet.Jose.Encryption.IEcdhKey`. When the registered resolver provides it, the facade routes
  signing (signed envelopes, the inner JWS of sign-then-encrypt, and the `from_prior` JWT) and ECDH
  (authcrypt send, authcrypt/anoncrypt receive) through these handles. Returns `null` per kid to fall back
  to the extractable path, so a wallet may **mix** opaque and extractable keys.
- **`NetDidKeyStoreSecretsResolver` is now a sufficient sole resolver for an HSM-backed agent.** It
  implements `IOpaqueKeyResolver` over `IKeyStore` (signing via `CreateSignerAsync`, ECDH via the new
  `KeyStoreEcdhKey` over `DeriveSharedSecretAsync`), surfaces **public-only** JWKs (`d` absent), and gains
  an optional `kid → alias` mapping hook (identity by default). Its XML docs no longer flag the opaque
  path as "future work."
- **`FromPriorBuilder` gains `JwsSigner` overloads** so the rotation JWT can be minted with an opaque
  signer; the existing `Jwk` overloads delegate to them (back-compatible). EdDSA output is byte-identical
  to the extractable path (covered by a determinism test).
- **DI:** `AddDidComm` honors a separately-registered `IOpaqueKeyResolver`, and `UseSecretsResolver`
  surfaces a resolver's opaque capability automatically. No new required wiring — the facade also
  auto-detects when the `ISecretsResolver` itself implements `IOpaqueKeyResolver`.
- **Dependency:** `DataProofsDotnet.Jose` `1.0.1 → 1.1.0` (the additive async `IEcdhKey` ECDH seam +
  `JweParser.PeekRecipients`/`ParseAsync`, and the constant-work JWE decrypt — dataproofs#12/#13).
- **Acceptance:** interop round-trips (`DidCommClientOpaqueKeystoreRoundTripTests`) pack authcrypt /
  anoncrypt / signed / sign-then-encrypt / anoncrypt(authcrypt) and unpack them end-to-end through a
  non-extractable `InMemoryKeyStore`, plus a cross-custody interop case (opaque ⇄ extractable) — with no
  private key bytes leaving the store.

### Changed

- **Unpack is now async end-to-end internally.** `DidCommClient.UnpackAsync`'s public signature is
  unchanged, but the recipient (secret-key) decrypt path now runs natively async through the opaque-or-
  extractable `IEcdhKey` handles — retiring the `SyncSecretsAdapter` sync-over-async bridge and its
  documented deadlock caveat for the secrets path. The FR-CONSIST-06 authorization check also runs natively
  async. Public-key DID-resolution lookups (sender/signer) remain sync-over-async by DataProofs' contract.
- **Durable constant-work recipient selection (closes #42 / lands dataproofs#12 here).** The unpack path
  always drives `JweParser.ParseAsync` — with a throwaway decoy `IEcdhKey` when no recipient key is held —
  so from `ParseAsync` inward the ECDH/unwrap cost and the uniform decryption failure no longer depend on
  which (or whether) recipient key the agent holds. This is the durable fix the #35 HTTP rejection floor was
  the compensating edge control for; the floor remains as defense-in-depth for the resolver prologue.
- **Behavior change — malformed `iv`/`tag`/`encrypted_key` lengths on decrypt now surface as
  `CryptoException` ("JWE could not be decrypted."), not `MalformedMessageException`.** The constant-work
  decrypt folds non-canonical AEAD field lengths into the same uniform failure as a wrong-key / tampered-
  ciphertext failure, so a malformed envelope cannot be distinguished from an undecryptable one by exception
  type or message. Any opaque-handle fault (e.g. a `KeyNotFoundException` from a key rotated out mid-unpack,
  or an enclave `CryptographicException`) is likewise folded into that uniform `CryptoException`, so the
  opaque path leaks no recipient-possession oracle by exception type and no raw exception escapes the
  FR-API-07 unpack contract. (Adversarial review, Finding 1.)

### Security — HTTP receive timing side-channel (`feat/security-receive-timing-floor`)

Closes GitHub issue **#35** (High) — the timing residual the #20 red-team explicitly filed (see
`tasks/lessons.md` L-025). The #20 fix made every HTTP receive failure return a uniform bodyless
`400`, closing the response-content/status oracle, but response **time** still partitioned those 400s:
an envelope addressed to a recipient key the agent holds runs the full ECDH/AEAD path (~360 µs) while
one addressed to a key it does not hold fast-fails before any crypto (~180 µs), so a single timed
request enumerated which recipient keys the agent holds (FR-API-07). A PoC-backed adversarial pass
caught — and the fix folds in — two further residuals.

- **Constant-time rejection floor.** New `DidCommReceiveOptions.ReceiveRejectionFloor` (default
  **5 ms**, set `TimeSpan.Zero` to disable). The HTTP receive handler times the unpack window and,
  before returning the uniform `400`, pads with a non-cancelable timer-backed delay up to the floor, so
  the rejection time is independent of how far envelope processing got. The fast-fail "unheld kid" path
  and the full-crypto "held kid + AEAD fail" path now both finish under the floor and pad to the same
  duration. **Behavior change:** rejected requests now take ≥ the floor; legitimate received messages
  answer `202` and are never padded (the hot path is untouched), and `415`/`413` pre-decrypt rejections
  are unchanged. The cost is paid only on rejected — typically hostile or malformed — traffic.
- **Red-team Finding 1 (folded in): floor measured after the body read.** The pad clock starts _after_
  `ReadCappedAsync`, not at handler entry — otherwise a peer could pad the envelope toward
  `MaxReceiveBytes` so the (kid-independent) body read alone exhausts the floor, collapse the pad to
  zero, and re-expose the gap. Covered by `Large_body_rejection_is_still_padded_to_the_floor`.
- **Red-team Finding 2 (documented residual, not closed by a fixed floor).** The held-only path that
  decrypts and then does **network** DID resolution (authcrypt sender / FR-CONSIST-06 / `from_prior`)
  can run longer than the floor — e.g. an attacker authcrypts to a guessed kid signed by an
  attacker-controlled, deliberately-slow `did:webvh` sender, making a held response visibly exceed the
  floor while an unheld one stays at it. That residual is network-bound and unbounded, so a fixed floor
  cannot close it without an absurd value; the API docs direct operators to authentication / a
  rate-limiter in front of the receive endpoint (the deployment tradeoff #35 calls out). Tracked as #44.
- **WebSocket receive is document-only.** The WS loops log-and-discard with nothing written back, so
  there is no per-message response to time; the floor is intentionally not applied there (noted in code).
- **Root cause tracked upstream.** The local-crypto gap originates in `JweParser.Parse` /
  `FindPresent` (DataProofsDotnet.Jose), which fast-fails before ECDH when no recipient kid is held.
  The floor is a compensating edge control; the durable constant-time-recipient-selection fix is filed
  upstream (`moisesja/dataproofs-dotnet#12`) and tracked here in #42.

### Security — protocol & transport hardening (`feat/security-protocol-hardening`)

Resolves four scattered Low/Info findings (GitHub issues #27, #30, #31, #34); #32 closed as
spec-compliant (no change — see below). Each fix passed a PoC-backed adversarial red-team pass that
caught two real residuals (folded in). Full suite (650 tests) green.

- **`web_redirect.redirectUrl` validated (#30).** `OutOfBand.ReadWebRedirect` previously returned the
  peer-supplied URL verbatim; it now returns `null` unless the value is an absolute `http`/`https` URL
  with no userinfo and a non-private host, and returns the canonical (normalized) form. Closes a
  `javascript:`/`data:`/`file:` and `user@host`-phishing passthrough on the documented "may navigate to"
  target. (A public-host open redirect remains by design — there is intentionally no allowlist here, so
  consumers MUST still confirm before navigating.)
- **OOB invitation `from` enforced (#34).** `OutOfBand.FromPlaintext` (the single inbound choke point)
  now rejects an invitation with an absent **or whitespace-only** `from` (`FormatException`, FR-OOB-01),
  matching the build side (which likewise tightened to `ThrowIfNullOrWhiteSpace`).
- **FR-THR-04 rule 3 implemented (#31).** The dispatcher now records each ACK request it emits
  (`ThreadState.AckRequested`) and consumes the answering pure-ACK instead of re-dispatching it, rather
  than only doing a rule-2-shaped check. `AckLoopGuard`'s remarks are corrected to describe exactly what
  is enforced (ACK requests sent via the facade directly remain the application's responsibility).
- **Single source of truth for the outbound SSRF policy (#27).** `HttpTransportOptions` /
  `WebSocketTransportOptions` `OutboundEndpointPolicy` are now nullable (`null` = inherit
  `DidCommOptions.OutboundEndpointPolicy`), so configuring the policy in one place governs the facade
  pre-send check and both transports' connect-time pin alike. The default stays strict
  (`BlockPrivateNetworks = true`). **Behavior change:** because the transports now inherit the core
  policy, setting `DidCommOptions.OutboundEndpointPolicy.BlockPrivateNetworks = false` (or another
  permissive core setting) now relaxes the transport connect-time guard as well — previously each
  transport kept an independent strict default. This is the intended single-source consistency; set an
  explicit per-transport policy only to diverge deliberately (note: the facade pre-send always uses the
  core policy).
- **#32 (JWS `typ` not validated on verify) — no change, spec-compliant.** The cited local parser was
  delegated to `DataProofsDotnet.Jose` (which does not surface `typ`); RFC 7515 §4.1.9 makes `typ`
  advisory, FR-SIG-04 has no MUST to validate it on receive, and envelope kind is determined
  structurally (no confusion risk). Closed with rationale.

Review remediation (PR #41 review):

- **#27 — HTTP inherit path now covered by tests.** Added `HttpTransportPolicyInheritanceTests`, the
  symmetric coverage to the existing WebSocket inherit test: it exercises the real
  `SocketsHttpHandler.ConnectCallback` (a positive test proving the permissive core policy is inherited
  when the transport policy is unset, plus a complement proving the default blocking policy governs when
  no policy is configured anywhere — so the positive test cannot pass vacuously).
- **#30 — open-redirect contract documented on the public API.** `OutOfBand.ReadWebRedirect` now carries
  a `<remarks>` warning (surfaced in IntelliSense) that a non-null result is a _candidate_ navigation
  target, not a vetted one, and the consumer MUST confirm it before navigating.
- **#31 — `AckRequested` limitation documented.** `ThreadState.AckRequested` now documents that, if the
  answering pure-ACK never arrives, the flag persists until the entry is evicted from the bounded store
  (#21), during which a later _unsolicited_ pure-ACK on the same thread is dropped (a benign over-drop —
  pure ACKs are inert; rule-2 remains the actual loop barrier).
- **`IsSafeRedirectUrl` numeric-IP handling clarified.** Added a comment pinning that `System.Uri`
  canonicalizes numeric-encoded IPv4 literals (decimal/hex/octal/short) to dotted form with
  `HostNameType == IPv4` _before_ the guard runs, so the private/reserved check catches them (verified
  empirically) — and noting the guard would need its own normalization if a future parser stopped
  canonicalizing.

### Security — problem-report cascade-guard redesign (`feat/security-cascade-guard`)

Resolves two FR-PROTO-10 cascade-guard findings (GitHub issues #29, #36), addressed together since both
stem from the per-`pthid` error budget living on the dispatcher's general thread store. Passed a
PoC-backed adversarial red-team pass that caught — and drove the fix for — a concurrency bug in the
first cut. Full suite (631 tests) green.

- **Cascade budget moved to a dedicated, bounded store (#36).** The FR-PROTO-10 budget now lives in a
  new `CascadeBudgetStore`, separate from the dispatcher's general thread store (which is flooded with
  one entry per inbound `thid`). Previously a flood of cheap fresh `thid`s could evict — and reset to
  zero — a victim `pthid`'s error budget, defeating the cascade-stop. The dedicated store **owns its
  concurrency**: the increment + threshold check + trip decision is atomic under a single `pthid`-keyed
  lock (the earlier "lock on the per-thread state object" seam broke under LRU eviction and could
  double-emit the cascade-stop — fixed). Its LRU **prefers evicting not-yet-tripped entries**, so a
  tripped thread's silenced decision survives a fresh-`pthid` flood without exempting entries from
  eviction (still bounded). Registered as its own DI singleton so the budget persists regardless of the
  handler's lifetime. Residual: a determined attacker can still pressure the bound with distinct-`pthid`
  _error reports_ (far costlier than the original cheap-`thid` reset); a distributed/persistent budget
  is the path to a hard guarantee for horizontally-scaled hosts.
- **Unrepliable-stream counter/log flood capped (#29).** A sustained stream of unrepliable
  (anoncrypt/from-less) over-threshold reports previously incremented the counter and emitted an Info
  log line forever (the trip permanently deferred). The handler now clamps the count at `threshold + 1`
  and stops per-message logging after the breach, while preserving the deferral so a later repliable
  report still fires the cascade-stop.
- `ThreadState.ErrorCount`/`MaxErrorsNoticeSent` are retained as general-purpose per-thread fields but
  are no longer used by the built-in cascade guard (which uses `CascadeBudgetStore`).

### Fixed — rotation JWT compliance

Resolves two Low-severity audit findings on the `from_prior` rotation path (GitHub issues #25, #26).
Both passed a PoC-backed adversarial red-team pass; full suite (625 tests) green.

- **`FromPriorBuilder` now emits `exp`/`nbf` (#25, FR-ROT-05).** The builder previously serialized only
  `{ iat, iss, sub }`, silently discarding any `FromPriorClaims.Exp`/`Nbf` — so every self-issued
  rotation JWT was non-expiring and the validator-side freshness control (already enforced by
  `DidCommClient.ValidateFromPriorFreshness`) could never fire on tokens this library minted. Claims are
  now built via an insertion-ordered `JsonObject` (`exp, iat, iss, nbf, sub`), emitting `exp`/`nbf` only
  when set (the no-expiry payload is byte-identical to before). A new
  `BuildAsync(claims, signerJwk, TimeSpan lifetime)` overload sets `exp = iat + lifetime` (rejecting a
  sub-second lifetime, which would floor to an already-expired token) so issuing a freshness-bounded
  rotation JWT is one call. The PRD's rotation cookbook/API-matrix is realigned to the actual API
  (`FromPriorBuilder.BuildAsync` + `MessageBuilder.WithFromPrior`); the previously-documented
  `WithDidRotation` helper was never built and is noted as possible future DX.
- **`from_prior` validator rejects a `crit` protected header (#26, RFC 7515 §4.1.11).** The validator
  hand-parses the JWS header and previously read only `alg`/`kid`, silently ignoring `crit` — unlike
  `JwsParser`/`JweParser`, which fail closed. It now rejects any `crit` member with `ProtocolException`,
  before signature verification, bringing the rotation path to parity.

### Fixed — receive-path correctness

Resolves three Low-severity audit findings on the unpack/JOSE receive surface (GitHub issues
#22, #23, #24). Each ships with tests and an adversarial red-team pass; full suite (615 tests) green.

- **Unpack honors its exception contract for non-canonical field lengths (#22).** `EnvelopeReader`
  gains a defensive `ArgumentException → MalformedMessageException` boundary catch at both the JWS and
  JWE delegation points (neutral message; `InnerException` preserved). Note: the originally-reported
  escape is **already resolved upstream** — the delegated `DataProofsDotnet.Jose` parser now wraps the
  AEAD's wrong-length-iv/tag `ArgumentException` (and base64 `FormatException`) as `MalformedJoseException`.
  Covered by a regression test on the unpack contract plus a unit test that drives the boundary catch
  directly (a throwing consumer lookup → `MalformedMessageException`).
- **Strict JOSE base64url decoding (#24).** `Base64Url.Decode` now rejects `=` padding, embedded ASCII
  whitespace, and standard-base64 `+`/`/` (anything outside `[A-Za-z0-9-_]`) with `FormatException`,
  per RFC 7515 §2 / RFC 4648 §3.2-3.3, closing a parser-differential / non-canonical-encoding gap on
  the strict-no-pad paths: `from_prior` JWTs, OOB URLs (FR-OOB-02), and JWK material. **Attachment
  `data.base64` is deliberately kept lenient** (new `Base64Url.DecodeRelaxed`): FR-ATT-02 imposes no
  no-pad requirement and the mediator only relays bytes the recipient re-parses strictly, so a peer's
  padded forward attachment is still relayed rather than refused (PR #38 review). Either way a
  genuinely-invalid forward attachment maps to `MalformedMessageException` rather than escaping the
  mediator as a raw `FormatException` (red-team follow-up).
- **`UnpackResult.AnonymousSender` derived from the outermost encrypt layer (#23).** The flag is now
  computed from the outermost encrypt layer's alg (matching its documented semantics) instead of
  OR-accumulating across layers. The contradiction the finding described (`authcrypt(anoncrypt)` →
  both flags true) was already unreachable after the #17 composition gate; this makes the code match
  its contract by construction.

### Security — Medium-severity audit remediation

Remediates the five Medium-severity findings from the multi-agent security & compliance audit
(GitHub issues #17–#21), plus two lower-severity defects on the same code path (#28, #33). Each fix
ships with negative and regression tests, and each was then put through an adversarial red-team pass
which surfaced two further hardening items (folded in below) and two deeper residuals (filed as
follow-ups). The full suite (600 tests) is green.

- **Illegal envelope compositions rejected (#17, FR-ENV-04a).** `EnvelopeReader.Unpack` now tracks
  each unwrapped layer with its authenticated/anonymous flag and enforces the legal DIDComm v2.1
  receive grammar `anoncrypt? authcrypt? sign? plaintext` before any content/consistency processing.
  Illegal nestings — sign-outside-encrypt (FR-ENV-05), `anoncrypt(anoncrypt)`, `authcrypt(authcrypt)`,
  `authcrypt(anoncrypt)`, double-sign — are rejected with `MalformedMessageException`. The grammar
  admits every spec Appendix C unpack vector, including `authcrypt(sign)` (FR-ENV-03) and
  `anoncrypt(authcrypt(sign))` (Appendix C.3). The auth/anon flag is what distinguishes the legal
  `anoncrypt(authcrypt)` from the illegal `anoncrypt(anoncrypt)` (indistinguishable at envelope-kind
  level). PRD gains **FR-ENV-04a** documenting the receive-acceptance set.
- **Controller-aware resolver authorization (#18, FR-CONSIST-06 / DD-01).** `NetDidKeyService`
  `IsKeyAuthorizedAsync` now walks the raw resolved verification methods (not the curve-projected
  JWKs, which dropped `controller`) and authorizes a `kid` only when its id-subject **and** its
  `controller` resolve to the asserted DID — rejecting a key listed under the DID's relationship but
  controlled by a different DID, and cross-DID embedded ids. did:key / did:peer keep authorizing
  (controller == subject by construction).
- **`from_prior` JWT parse errors normalized (#19).** `FromPriorValidator` wraps base64url decode and
  all JSON reads in one boundary that surfaces malformed input (`FormatException`,
  `InvalidOperationException`, `ArgumentException` on an empty segment, `JsonException`,
  `KeyNotFoundException`) as the typed `ProtocolException` with a generic message — closing an
  unhandled-exception path out of `UnpackAsync` and removing the header-vs-claims info leak.
- **Receive endpoint response oracle closed (#20, #28, #33).** Both ASP.NET Core HTTP receive overloads
  collapse every input- or handler-triggered failure to a single opaque `400` with an empty body,
  logging the reason server-side only. This closes the verbatim-`CryptoException` decryption /
  recipient-`kid` enumeration oracle (#20), the `Consistency`/`Resolution`/`UnsupportedDidMethod`
  exceptions that previously escaped to `500` + dev stack-trace (#28), and the echoed handler-bug
  detail (#33). The 202 success, 415, and 413 paths are unchanged; cancellation still propagates.
  Red-team hardening: the inline-callback overload now also wraps `onReceive` so a consumer callback
  that throws after a successful unpack returns 202 (logged) instead of escaping as a distinguishable
  `500` — removing an unpack-success oracle the uniform-400 path would otherwise leave. (A residual
  _timing_ side-channel in the underlying decrypt path — held-vs-unheld recipient kid — is **not**
  closed by this change and is tracked as a follow-up.) A downstream DID-resolution timeout
  (`TaskCanceledException` with the request token not cancelled — e.g. a hung `did:webvh` host) also
  collapses to the uniform 400 rather than escaping as a `500`; only a genuine client abort propagates.
  **Operator note:** a receiver-side handler bug (FR-THR-04 `InvalidOperationException`) now returns
  `400` instead of `500` for response uniformity — it is logged at `Error` level, so alert on the
  `DidComm.AspNetCore` error log rather than on a 5xx rate for handler faults.
- **Thread-state store bounded (#21).** `InMemoryThreadStateStore` is now capped
  (`DefaultMaxEntries = 10_000`, overridable via ctor) with approximate-LRU eviction, converting an
  unbounded-growth memory-exhaustion DoS (attacker-chosen, unauthenticated `thid`/`pthid`) into a
  few-MB bound. Secure-by-default — no DI wiring change. Red-team hardening: eviction is **single-flight**
  (an `Interlocked` guard) so N concurrent inserters over the cap can't each run a full O(n log n)
  snapshot-sort — closing a CPU-amplification DoS. (A residual cascade-guard-reset-via-eviction vector,
  costing the attacker ~`cap` messages per suppressed report, is tracked as a follow-up alongside #29.)

### Changed — delegate the envelope layer to DataProofsDotnet.Jose

didcomm-dotnet no longer carries its own cryptography or JWE/JWS assembly. The entire `Crypto/`
folder and almost all of `Jose/` were deleted; envelope build/parse is delegated to
**`DataProofsDotnet.Jose` (1.0.1)** which runs on **`NetCrypto` (1.1.0)**. This realizes the
foundation-stack split (architectural-path §5.6) and removes ~435+ lines of hand-rolled,
security-sensitive crypto from this repo. Suite: **475 unit + 99 interop** green under `warnaserror`;
all DIDComm v2.1 Appendix C byte-equivalence vectors (anoncrypt/authcrypt/signed, every curve) pass.

- **Crypto substrate → NetCrypto.** AES-256-CBC-HMAC-SHA512 / A256GCM / XC20P, RFC 3394 A256KW, the
  ECDH-ES & ECDH-1PU (`Ze‖Zs`) Concat-KDF, raw ECDH, sign/verify, JWK conversion, base64url, and the
  RFC 7518 §6.2.2 on-curve `epk` validation are all NetCrypto's now (byte-identical to the deleted
  in-repo versions, proven by the interop vectors).
- **JOSE composition → DataProofsDotnet.Jose.** Multi-recipient `JweBuilder`/`JweParser`
  (ECDH-ES+A256KW anoncrypt, ECDH-1PU+A256KW authcrypt, single shared `epk`, apu/apv binding,
  single-curve enforcement, authcrypt-pins-A256CBC) and `JwsBuilder`/`JwsParser` (Flattened/General,
  compact) are delegated. DidComm's internal secret/sender lookups now directly satisfy DataProofs'
  `IJweRecipientKeyResolver`/`IJweSenderKeyResolver` (no adapter), and its `Jwk` is the shared
  `DataProofsDotnet.Jose.Jwk`.
- **`NetDid.Core` → 2.0.x, resolution-only.** All crypto primitives moved out of net-did in 2.0.0;
  the `NetDidKeyService` adapter now materializes Multikey verification methods via
  `JwkConversion.FromMultikey`, and the optional `IKeyStore`→`ISecretsResolver` bridge re-targets
  `NetCrypto.IKeyStore`. The direct `NSec.Cryptography` dependency is dropped.
- **DIDComm-specific rules stay in didcomm.** Legal compositions + media-type pinning (FR-ENV),
  recipient defaulting (FR-ENC-19), curve negotiation (FR-ENC-04), and the addressing-consistency
  checks (FR-CONSIST-01..06) remain in `EnvelopeWriter`/`EnvelopeReader`/`DidCommClient`. Because the
  delegated JWS parser returns verified-payload bytes (not a DIDComm message), FR-CONSIST-03
  (signed `from` ↔ signer kid) and FR-SIG-06 (inner signed JWM must carry `to`) are now enforced in
  `EnvelopeReader` against the deserialized inner message.
- **Pack APIs are now async** end-to-end (`EnvelopeWriter.PackSignedAsync`/`PackEncryptedAsync`,
  `ForwardWrapper.WrapAsync`, `FromPriorBuilder.BuildAsync`) since DataProofs signs through an async
  `ISigner`. `from_prior` is built via DataProofs' compact JWS.
- **Upstream fixes landed** from this integration: NetCrypto 1.1.0 (on-curve guarantee in
  `ExtractPublicKey`, `IKeyStore.DeriveSharedSecretAsync`, `Base64Url`, AEAD size constants —
  crypto-dotnet #10/#11/#12); DataProofsDotnet.Jose 1.0.1 (surface the verified JWS signer kid from
  the unprotected header for DIDComm v2.1 conformance — dataproofs-dotnet #10).
- **Receive-side defense-in-depth:** a verified JWS that surfaces **no** signer kid is now rejected
  (`CryptoException`) rather than reported authenticated — the `from`↔signer binding (FR-CONSIST-03)
  never silently no-ops, independent of the delegated parser's behavior.

### Breaking

- `FromPriorBuilder.Build(FromPriorClaims, Jwk)` → **`BuildAsync(FromPriorClaims, Jwk, CancellationToken)`**
  (method name **and** return type change). DataProofs signs through an async `ISigner`, so direct
  callers must `await` the new method. The primary `DidCommClient` pack/unpack facade is unchanged
  (it was already async); `ForwardWrapper.WrapAsync` is `internal`.
- **ES512 / P-521 _signing_ dropped.** The old `KeyTypeMapper` mapped `P-521 → ES512` for JWS; the
  delegated signer scopes JWS to EdDSA / ES256 / ES384 / ES256K, so a P-521 signer JWK now raises
  `NotSupportedException`. **Not a DIDComm v2.1 regression** — signed messages require only
  EdDSA / ES256 / ES256K (none were tested with P-521), and P-521 _key agreement_
  (anoncrypt/authcrypt) is unaffected and still tested.

### Notes

- The deleted in-repo crypto unit tests (AEAD tag, off-curve `epk`, key-wrap, malformed JWK
  negatives) are carried by the upstream suites — NetCrypto (`EcPointValidatorTests`, the
  AES-GCM / AES-CBC-HMAC / XChaCha20-Poly1305 / AES-KW cipher tests, `JwkConverterTests`,
  input-validation fuzz) and DataProofsDotnet.Jose (`Parity/*`, `Conformance/NegativePathTests`,
  `Hardening/*`). This repo's 99 interop **vectors** backstop byte-equivalence; the primitive
  negatives live where the primitives now live.
- **Follow-up (not in this PR):** enabling a package lockfile (`packages.lock.json`,
  `RestorePackagesWithLockFile`) to pin the resolved transitive graph is worthwhile now that the
  security-critical core lives in transitive packages, but it currently conflicts with this repo's
  `Directory.Build.props`-level `TargetFramework` (NuGet's lock-file restore evaluates an empty TF →
  NETSDK1013). It needs `TargetFramework` moved into each csproj first; tracked separately.

## [0.1.0-preview.1]

### Security — full-codebase audit remediation (`feat/security-hardening`)

A defensive security + correctness review of the whole library, fixed in priority order. Suite:
**606 unit + 99 interop** under `warnaserror` (was 578 + 96). No public API removed; several
receive-side paths now reject inputs they previously tolerated (all spec-conformant tightenings).

- **WebSocket SSRF (HIGH):** the WS transport now pins each connection to a guard-vetted IP via a
  `SocketsHttpHandler.ConnectCallback` (mirroring the HTTP transport), closing a DNS-rebinding TOCTOU
  and the `ResolveDnsNames=false` bypass. TLS still validates against the original host.
- **SSRF classifier:** `OutboundEndpointGuard` now blocks IPv4 embedded in IPv6 alternate forms
  (IPv4-compatible `::/96`, 6to4 `2002::/16`, NAT64 `64:ff9b::/96`) and Teredo `2001::/32`, and
  normalizes trailing-dot hosts.
- **JWE receive validation:** reject any `enc` outside `{A256CBC-HS512, A256GCM, XC20P}` and pin
  authcrypt (ECDH-1PU) to `A256CBC-HS512` (FR-ENC-09); enforce `crit` (RFC 7516) and bind `apu` to
  `skid` with the FR-ENC-17 `apu` fallback (FR-ENC-14); require well-typed members so malformed JWEs
  yield `MalformedMessageException`.
- **JSON:** reject duplicate member names (`AllowDuplicateProperties = false`) across the message
  model and every raw JWE/JWS/`from_prior`/forward parse — closes a parser-differential vector.
- **Keys:** reject OKP (Ed25519/X25519) JWKs whose `x` is not exactly 32 bytes before import.
- **DID rotation:** `from_prior` is accepted only on a sender-authenticated envelope (authcrypt or
  signed), not plain anoncrypt, and is checked for `exp`/`nbf` freshness (FR-ROT-03/05 reconciled in
  the PRD). `UnpackResult` documents that `Message.From` is trustworthy only when authenticated.
- **JWS:** enforce `crit`/`b64` (RFC 7515/7797); normalize malformed-input errors.
- **Correctness/defense-in-depth:** control-char + length validation on `from`/`to`; forward
  `body.next` is a bare DID; `SyncSecretsAdapter` avoids a sync-over-async deadlock; canonical
  protocol-version parsing; UTF-8 `apv` hashing; lowercase UUID ids; Discover Features disclosure
  de-duplication; ephemeral-key zeroization on early-throw.

### Added — Phase 6.3 (Out-of-Band 2.0 + NuGet release pipeline)

Closes PRD §10.3 **FR-OOB-01..05** (the last spec built-in protocol) and **NFR-08**
(release pipeline). Suite: **578 unit + 96 interop** under `warnaserror` (was 559 + 93).

- **Out-of-Band 2.0** (`Protocols/OutOfBand/`):
    - `OutOfBand` static API — `CreateInvitation(from, goal?, goalCode?, accept?, attachments?, id?)`
      (goal_code / goal / accept written into `body`, FR-OOB-01); `ToUrl(invitation, baseUrl)` /
      `FromUrl(url)` for the `?_oob=<base64url(plaintext-jwm)>` form (FR-OOB-02), reusing the
      existing compact plaintext serializer + `Base64Url`; `FromPlaintext(json)`; short-form
      `ToShortUrl(baseUrl, oobId)` / `TryGetShortFormId(...)` (FR-OOB-04); `AddWebRedirect(...)` /
      `ReadWebRedirect(...)` for the `web_redirect` block (FR-OOB-05).
    - `OutOfBandInvitation` — read-only projection (`Id`, `From`, `Goal`, `GoalCode`, `Accept`,
      `Attachments`); the invitation `id` is the recipient's response `pthid` (FR-OOB-03).
    - `WebRedirect` record; `IOobInvitationStore` + `InMemoryOobInvitationStore` for the short-form.
    - **No dispatcher handler** — an invitation arrives out of band (URL/QR) and bootstraps a
      follow-up protocol, so there is nothing to route through `ProtocolDispatcher`.
    - **Encoding (FR-OOB-02):** `ToUrl` emits a canonical, reproducible payload — keys sorted at
      every level (the deterministic JSON writer) and the `typ` header dropped — so the same
      invitation always yields the same URL. That key order still differs from the spec's
      illustrative example, so it is not a byte-for-byte reproduction of that example; interop is
      proven instead by decoding the spec's own example fixture + round-trip equality.
- **ASP.NET Core**: `MapDidCommOobEndpoint(pattern, store)` — HTTP GET serving the short-form
  invitation by `_oobid` (200 `application/didcomm-plain+json` / 404 / 400) (FR-OOB-04).
- **Cookbook Section V** (`Section_V_OutOfBandInvitation`) — build → URL → decode round trip,
  short-form retrieval, and `pthid` correlation.
- **NuGet release pipeline (NFR-08)**: `.github/workflows/release.yml` packs + pushes all six
  packages (with symbols + SourceLink) to NuGet.org on a `v*` tag, with the version derived from
  the tag; `Directory.Build.targets` packs the repo `README.md` into each package (fixes NU5039);
  README gains an Install section. _(Publishing requires a `NUGET_API_KEY` repo secret + a pushed
  tag — neither is done here.)_
- **Review hardening**: OOB URL parsing tolerates a trailing `#fragment` (decodes instead of
  failing base64url); the release job is gated behind a `nuget-release` GitHub Environment — add
  required reviewers to enforce manual approval before the irreversible publish.

### Fixed — PR #11 review casualties (carried over from the abandoned local fix branch)

The independent `113608d` that landed on `main` dropped three deliverables the abandoned local
PR #11 branch carried; restored here (behavior was already correct on `main`):

- `DidCommBuilder.EnableTracing` now registers `IOptions<TraceOptions>` (via `TryAddSingleton`)
  symmetrically with `ProblemReportOptions`, so consumers using the Options pattern can resolve it.
- Restored the `Read_helpers_return_null_or_empty_on_non_string_body_fields` regression test
  (adapted to main's index-preserving `ReadArgs` — non-string entries become `""`).
- Restored the `<remarks>` XML doc on `TraceOptions.AllowedReportingUris` documenting canonical
  `AbsoluteUri` allowlist matching.

### Added — Phase 6.2c (Report Problem 2.0 + Trace 2.0 off-by-default)

Closes PRD §12 Phase 6 partially: **FR-PROTO-07, FR-PROTO-08, FR-PROTO-09,
FR-PROTO-10, FR-PROTO-11, FR-PROTO-11a**. Last of the three 6.2 sub-PRs —
**every spec built-in protocol** is now shipped (TrustPing / DiscoverFeatures /
Empty / ProblemReport / Trace).

- **Report Problem 2.0** (`Protocols/ProblemReport/`):
    - `ProblemCode` — typed taxonomy parser for `sorter.scope.descriptor[.sub…]`;
      `IsError` / `IsWarning` / `IsProtocolScoped` / `IsMessageScoped` flags;
      `StartsWith(prefix)` implements FR-PROTO-08 structural-prefix matching.
    - `CommentInterpolator` — FR-PROTO-07 `{n}` interpolation, 1-based; missing
      args render as `?`; unreferenced extras appended in a `[extra: …]` block;
      `{{` / `}}` escape to literal braces.
    - `ProblemReport` static API — `Create(...)` / `Escalate(...)` /
      `ReadCode(...)` / `ReadComment(...)` / `ReadArgs(...)` / `RenderComment(...)`.
      The single message type (`…/report-problem/2.0/problem-report`) plus the
      cascade-stop code (`e.p.req.max-errors-exceeded`).
    - `ProblemReportOptions.CascadeThreshold = 5` (matches `sicpa-dlab/didcomm-python`
      per locked decision).
    - `ProblemReportHandler` — increments `ThreadState.ErrorCount` on the
      **failing thread (pthid)** for inbound errors, emits exactly one
      `e.p.req.max-errors-exceeded` reply on threshold breach, then silently
      ignores subsequent reports on the same `pthid` (FR-PROTO-10).
- **Trace 2.0 — off-by-default** (`Protocols/Trace/`):
    - `Trace` — protocol/header constants (`trace` header, `report_uri` / `trace_id` members).
    - `TraceOptions.Validate()` — `Enabled = true` REQUIRES at least one entry in
      `AllowedReportingUris` per FR-PROTO-11a's "explicitly configured safeguards";
      throws `InvalidOperationException` at startup otherwise.
    - `TraceObserver.ShouldReport(...)` — pure decision surface: returns `false`
      unconditionally when the operator hasn't opted in; otherwise returns the
      validated absolute report URI (allowlist gate + absolute-URI check).
      HTTP POSTing is left to a future integration; the off-by-default guarantee
      is the spec-mandated piece.
    - `TraceObserver.BuildReportBody(...)` — minimal observed-metadata body for
      consumers wiring up the POST themselves.
- **`ProtocolContext.Threads`** — new field exposes `IThreadStateStore` to
  handlers (so ProblemReport can resolve the failing-thread state by `pthid`,
  not the report's own thread). Found via an integration test that exposed
  a real bug in the original design.
- **DI** (`DidCommBuilder`):
    - `AddBuiltInProtocols()` now also registers `ProblemReportHandler` + binds
      `ProblemReportOptions` via the standard Options pattern.
    - `EnableTracing(Action<TraceOptions>)` — opt-in only; validates immediately
      and registers `TraceOptions` as a singleton.
- **Cookbook Section U** (Report Problem) — parses a structured code,
  interpolates a `{n}` comment, escalates a warning to an error with preserved
  scope, and trips the cascade guard in 3 packed messages. 14 sections total.
- **Tests** (+57 unit, +5 interop, total **543 unit + 93 interop**):
    - 18 `ProblemCode` cases (taxonomy parse, malformed-input rejection,
      structural-prefix `StartsWith`, sorter/scope flags).
    - 8 `CommentInterpolator` cases (spec example, missing args → `?`, extras
      appended, brace escapes, unclosed-brace tolerance, non-positional
      placeholder passthrough).
    - 7 `ProblemReport` API tests.
    - 7 `ProblemReportHandler` cases (incl. cascade trip + exactly-once + silent-after).
    - 4 `TraceOptions` validation cases.
    - 7 `TraceObserver` cases (default-off, allowlist match/miss, malformed/missing
      header, non-absolute URI, body-builder metadata).
    - 5 DI integration tests (incl. end-to-end cascade-guard trip over real
      did:peer pack/unpack/dispatch).

### Fixed (Phase 6.2c review)

Addresses 15 review findings on the Phase 6.2c surface (see PR #11 inline comments).

- **`ProblemReportHandler.HandleAsync` rewritten for FR-PROTO-10 correctness.**
    - Atomic increment + threshold check + trip decision under a per-`ThreadState` lock — concurrent
      inbound error reports on the same `pthid` (singleton handler fed by parallel transports) now
      yield exactly one cascade-stop instead of racing on `ErrorCount++`.
    - The cascade-trip is deferred when the threshold-tripping report is unrepliable
      (anoncrypt — no `from`/`to`); without the deferral, the counter landed past the threshold and
      the silent-ignore branch swallowed every subsequent report, so the cascade-stop was never
      emitted (FR-PROTO-10 silently broken).
    - Past-trip reports are now truly silent: short-circuit happens BEFORE the increment + Information
      log, eliminating the unbounded counter growth and per-message log-flood that "silently
      ignores" used to allow.
    - Threshold semantics simplified from `> CascadeThreshold + 1` / `== CascadeThreshold + 1` to a
      single `> CascadeThreshold` decision driven by `ThreadState.MaxErrorsNoticeSent` (new flag).
- **`ProblemReportOptions.Validate()`** — added; the handler ctor calls it so misconfigured
  thresholds (negative, `int.MaxValue`) fail loudly at DI resolution rather than silently
  disabling the cascade guard.
- **`ProblemReport.ReadCode` / `ReadComment` / `ReadArgs`** — pattern-match `JsonValue` instead of
  unconditionally calling `JsonNode.AsValue()`. A malformed peer sending
  `{"code": {"x": 1}}` (etc.) no longer crashes the handler with
  `InvalidOperationException`. `ReadArgs` additionally preserves null/non-string entries as empty
  strings so 1-based positional indexes stay aligned with the on-wire array.
- **`CommentInterpolator.Interpolate`** —
    - Brace-collapse (`{{` → `{`, `}}` → `}`) now runs uniformly, even when `args` is null/empty:
      identical templates render identically regardless of args presence.
    - `{0}` (or any non-positive numeric placeholder) renders as `?` per the 1-based spec, instead
      of leaking the placeholder text + dumping the unused arg into `[extra: …]`.
- **`TraceObserver.ShouldReport`** — three correctness/security fixes:
    - **Scheme guard**: reject any URI whose scheme is not `http`/`https` (previously `file://`,
      `javascript:`, `gopher:`, `data:`, `ftp:` all admitted if allowlisted).
    - **SSRF defense-in-depth**: reject IP-literal hosts that classify as loopback / private /
      link-local / metadata via `OutboundEndpointGuard.IsPrivateOrReserved` — wires up the
      defense-in-depth the docstring already promised.
    - **Allowlist normalisation**: compare both sides via `Uri.AbsoluteUri`. Trailing-slash,
      default-port, percent-encoding, and host-case differences no longer silently drop legitimate
      matches.
- **`ProtocolDispatcher`** — accepts an optional `TraceOptions` (DI-injected when
  `EnableTracing` was called) and invokes `TraceObserver.ShouldReport` on every inbound message.
  An authorised trace intent is logged at `Information`; HTTP POST integration remains deferred,
  but the decision logic is no longer dead in production.
- **`DidCommBuilder.EnableTracing`** uses `TryAddSingleton` so repeated calls are idempotent
  (first-call-wins) instead of silently shadowing prior `TraceOptions`.
- **`ProtocolHandlerRegistry` factory** in `DidCommBuilder` now logs a warning when two
  `IProtocolHandler` registrations target the same PIURI (most commonly: `AddBuiltInProtocols()`
  silently overriding a host's custom handler registered earlier). Last-write-wins semantics are
  preserved — but the override is now observable.

### Breaking (Phase 6.2c review)

- **`ProtocolContext` positional ctor signature.** `Threads` was previously inserted at index 2
  (between `Thread` and `Client`); it is now appended at the end of the positional list so
  callers using the prior positional shape `new ProtocolContext(received, thread, client,
options)` keep compiling. Downstream consumers that built `ProtocolContext` positionally
  against the dd204b0 commit of this PR will need to swap to the new order:
  `new ProtocolContext(received, thread, client, options, threads)`. Callers using named
  parameters are unaffected.

### Added — Phase 6.2b (Discover Features 2.0 + custom-handler cookbook)

Closes PRD §12 Phase 6 partially: **FR-PROTO-05** (Discover Features 2.0 with the
`max_receive_bytes` constraint), and demonstrates **FR-PROTO-03**'s extension
point with a one-file custom `IProtocolHandler` (`lets_do_lunch`).

- **Discover Features 2.0** (`Protocols/DiscoverFeatures/`):
    - `DiscoverFeatures` static API — `CreateQuery(from, to, queries…)`,
      `CreateDisclose(from, to, thid, disclosures…)`, `ReadQueries(msg)`,
      `ReadDisclosures(msg)`; spec constants for the four `feature-type` values
        - the `max_receive_bytes` constraint id.
    - `FeatureQuery` / `FeatureDisclosure` records (spec wire shape:
      `feature-type` is hyphenated, `value` is omitted when null).
    - `DiscoverFeaturesHandler` — routes each query to the matching
      `IFeatureProvider`, concatenates disclosures, emits a single threaded
      `disclose` reply. Unrecognized `feature-type`s are silently skipped;
      empty disclosures are meaningful (not "unsupported").
    - `FeatureMatch` — spec-conformant trailing-`*` prefix matching;
      bare `*` matches everything; otherwise exact.
    - `IFeatureProvider` extension point + two built-in providers:
      `ProtocolFeatureProvider` (reflects the registry) and
      `MaxReceiveBytesConstraintProvider` (advertises `DidCommOptions.MaxReceiveBytes`).
- **DI** (`DidCommBuilder`):
    - `AddBuiltInProtocols()` now also registers `DiscoverFeaturesHandler` + both
      default `IFeatureProvider`s (via `TryAddEnumerable` so re-registration is a no-op).
    - New `AddFeatureProvider<T>()` for consumers that want to expose goal-codes,
      custom headers, or app-specific constraints.
- **Cookbook**:
    - **Section T** (Discover Features) — Alice queries `https://didcomm.org/*` +
      `constraint:max_receive_bytes`; Bob's disclose reply lists all 3 built-in
      PIURIs + the byte-cap value; narrator confirms `thid` matches.
    - **Section X** (Custom IProtocolHandler) — a one-file `lets_do_lunch/1.0`
      protocol shows the FR-PROTO-03 extension point: define a handler, register
      via `ProtocolHandlerRegistry.Register(...)` (or `b.AddProtocol<T>()`), run
      through the dispatcher.
- **Tests** (+27 unit, +3 interop, total **486 unit + 88 interop** green):
    - 8 `FeatureMatch` cases covering wildcard / prefix / exact / empty.
    - 7 `DiscoverFeatures` static-API tests (CreateQuery/Disclose round-trip,
      `feature-type` JSON name, optional `value` omission, malformed-entry tolerance).
    - 9 `DiscoverFeaturesHandler` cases (wildcard match, prefix filter,
      `max_receive_bytes` reflection of `DidCommOptions`, case-insensitive
      `feature-type` matching, unrecognized type silently skipped, disclose-typed
      inbound is terminal, anonymous query rejected).
    - 3 DI-graph integration tests (`AddBuiltInProtocols` registers handler +
      providers; full pack→unpack→dispatch round-trip lists all built-ins +
      `max_receive_bytes`; `AddFeatureProvider<T>()` appends consumer providers).

### Added — Phase 6.2a (Protocol handler registry + TrustPing + Empty)

Closes PRD §12 Phase 6 partially: **FR-PROTO-03..04, FR-PROTO-06**, runtime dispatch
for FR-PROTO-01/02 (parser shipped earlier), and enforces **FR-THR-04 rule 2** at
the dispatcher boundary plus defensive **rule 3** drop of peer rule-2 violations.

- **`IProtocolHandler` + dispatch framework** (`DidComm.Core/Protocols/`):
    - `IProtocolHandler` — `ProtocolUri` + `HandleAsync(Message, ProtocolContext, ct)` returning an optional reply.
    - `ProtocolIdentifier` — PIURI parser (`<doc-uri>/<protocol-name>/<major.minor>`) mirroring `MessageTypeUri`.
    - `ProtocolHandlerRegistry` — case- and punctuation-insensitive PIURI lookup (FR-PROTO-01) with the older-minor-wins interop floor (FR-PROTO-02). Thread-safe; intended as a singleton.
    - `ProtocolContext` — wraps `UnpackResult`, `ThreadState`, optional `DidCommClient`, and `DidCommOptions`.
    - `ProtocolDispatcher` — orchestrates resolution → pre-filter (FR-THR-04 rule 3) → handler invocation → reply safety check (`AckLoopGuard.IsSafeToSend`, FR-THR-04 rule 2) → `DispatchOutcome` (NoHandler / NoReply / ReplyProduced / DroppedAsAckLoop).
- **Trust Ping 2.0** (`Protocols/TrustPing/`):
    - `TrustPing.CreatePing(from, to, responseRequested=true)`, `TrustPing.CreateResponse(ping)`, `TrustPing.IsResponseRequested(ping)` (default `true` when the body member is missing or non-boolean — matches `sicpa-dlab/didcomm-python`).
    - `TrustPingHandler` auto-replies `ping-response` with `thid = ping.id`; suppresses reply when `response_requested = false`; never replies to a `ping-response` (terminal leaf).
- **Empty 1.0** (`Protocols/Empty/`):
    - `EmptyProtocol` constants + `EmptyHandler` (returns `null` reply — Empty is header-only).
    - `Message.Empty()` static factory: pre-seeded `MessageBuilder` for the Empty 1.0 type.
- **`ThreadState.ErrorCount`** added for the FR-PROTO-10 cascade guard (Phase 6.2c wires it).
- **DI** (`DidCommBuilder`):
    - `AddProtocol<T>()` registers an `IProtocolHandler` singleton.
    - `AddBuiltInProtocols()` registers TrustPing + Empty.
    - Registry built via a DI factory that walks every `IProtocolHandler` so order of `AddProtocol` calls doesn't matter.
- **ASP.NET Core endpoint overloads** (`DidCommEndpointRouteBuilderExtensions`):
    - `MapDidCommEndpoint(pattern)` — parameterless overload that dispatches each inbound through the registry; HTTP replies are LOGGED, not returned (FR-TRN-10 one-way).
    - `MapDidCommWebSocket(pattern)` — same, with optional same-socket reply when `DidCommReceiveOptions.AllowSameSocketReplies = true` (default `false` per FR-TRN-10).
- **Cookbook**:
    - **Section S** (Trust Ping liveness) — Alice pings Bob; the dispatcher resolves `TrustPingHandler` and produces a reply with `thid = ping.id`; the response round-trips back.
    - **Section W** (Empty 1.0 ACK) — Bob ACKs a prior message with `Message.Empty().WithAck(...)`; narration shows `AckLoopGuard.IsPureAck = true` and `IsSafeToSend = true`.
- **Tests** (+26 unit, +2 interop, total **461 unit + 77 interop** green):
    - 10 `ProtocolHandlerRegistry` tests (exact match, case/punctuation, older-minor-wins, malformed input, re-registration).
    - 9 `ProtocolDispatcher` tests (no-handler, null/non-null reply, FR-THR-04 rules 2 + 3, thread-state passthrough, full TrustPing + Empty round-trips).
    - 6 `TrustPing` static-API tests (`response_requested` parsing, response construction, validation).
    - 4 `TrustPingHandler` tests (auto-reply, suppression, terminal-leaf, PIURI).
    - 3 `EmptyHandler` tests (null reply, `Message.Empty()` factory, PIURI).
    - 1 `ThreadState.ErrorCount` per-thread isolation test.
    - 2 DI-graph integration tests (`AddBuiltInProtocols` populates the registry; end-to-end pack→unpack→dispatch round-trip for Trust Ping over real `did:peer` identities).

### Security

- **SSRF hardening for outbound sends** (`DidComm.Core/Transports/OutboundEndpointGuard.cs`,
  `DidComm.Core/Transports/OutboundEndpointPolicy.cs`). A recipient's DID-document
  `serviceEndpoint` host is attacker-influenced, so `DidCommClient.SendAsync` now rejects an
  endpoint **resolved from a DID** that targets a private, loopback, link-local, unique-local,
  CGNAT, or cloud-metadata (`169.254.169.254`) address before dispatching the packed envelope.
  IPv4-mapped IPv6 addresses are unwrapped first so they cannot dodge the IPv4 rules.
    - The HTTP transport enforces the same policy at TCP connect time via
      `SocketsHttpHandler.ConnectCallback`, pinning every connection — including each manually
      followed 307 redirect — to a vetted IP, which also defeats redirect-to-internal and DNS
      rebinding.
    - The WebSocket transport vets the host on its default connect path.
    - Tunable via `DidCommOptions.OutboundEndpointPolicy` and each transport's
      `OutboundEndpointPolicy` (`BlockPrivateNetworks` defaults to `true`, plus `AllowedHosts` and
      `ResolveDnsNames`). A caller-supplied `SendOptions.ServiceEndpointOverride` is trusted and
      intentionally bypasses the gate.

### Added — Phase 6.1 (Threading, ACKs, Profiles, i18n)

Closes PRD §12 Phase 6 partially: FR-THR-01..04 (the message-layer surface),
FR-PROF-01/02, FR-I18N-01..03. Lays the message-model and DI foundation the
Phase 6.2 protocol handlers need to honor `please_ack` / `ack` and to localize
human-readable strings per thread.

- **`Message` plaintext model** (`DidComm.Core/Messages/Message.cs`):
    - `PleaseAck` (`please_ack`) — array of message ids to be acknowledged;
      empty string `""` is the spec sentinel for "this current message".
    - `Ack` (`ack`) — array of acknowledged message ids, oldest→newest.
    - `Lang` (`lang`) — IANA language tag for the message's protocol-defined
      human-readable fields (FR-I18N-03).
    - `AcceptLang` (`accept-lang`) — ranked IANA codes the sender prefers on this
      thread (FR-I18N-01/02). Spec name is hyphenated, not snake_case.
    - `Validate()` enforces FR-THR-03 / FR-I18N-01/03 character constraints (with
      the empty-string sentinel allowed in `please_ack`).
- **`MessageBuilder`** gained `WithPleaseAck(...)`, `WithAck(...)`,
  `WithLang(...)`, `WithAcceptLang(...)`.
- **Threading types** (`DidComm.Core/Threading/`):
    - `ThreadState` (`Thid`, `AcceptLang`) — per-thread mutable state record.
    - `IThreadStateStore` + `InMemoryThreadStateStore` — thread-safe
      `ConcurrentDictionary`-backed store. Registered as a singleton in
      `DidCommBuilder` so FR-I18N-02 thread-scoped `accept-lang` persists across
      pack/unpack while concurrent threads stay isolated.
    - `AckLoopGuard` — pure predicates (`IsPureAck`, `RequestsAck`, `IsSafeToSend`)
      that future protocol handlers consume to enforce FR-THR-04 (rule 2 today,
      rules 1/3 wired in Phase 6.2 when handlers dispatch replies).
- **Profile negotiation** (`DidComm.Core/Profiles/`):
    - `Profiles` constants (`DidCommV2`, `DidCommAip1`, `DidCommAip2Env10`).
    - `ProfileNegotiator.Choose(...)` / `IsSupported(...)` — case-insensitive,
      whitespace-tolerant peer-`accept[]` selection (FR-PROF-01/02).
- **Cookbook**:
    - **Section M** (Threading & ACKs) — Alice asks for an ACK with
      `WithPleaseAck`; Bob replies with a thread-correlated pure ACK; the
      `IsSafeToSend` loop-trap is demonstrated.
    - **Section BB** (Profiles & i18n) — `ProfileNegotiator` picks `didcomm/v2`
      from a peer's `accept[]`; the chess example flows with `lang=fr` +
      `accept-lang=[fr,en]`; thread-state isolation is asserted in narration.
- **Tests** (+32, total 396 unit + 69 interop green):
    - 9 round-trip / validation tests for the four new `Message` fields.
    - 6 `InMemoryThreadStateStore` tests including the FR-I18N-02 cross-thread
      isolation assertion.
    - 8 `AckLoopGuard` tests covering pure-ACK detection and FR-THR-04 rule 2.
    - 9 `ProfileNegotiator` tests (overlap, case, whitespace, null/empty edge cases).

### Added — Phase 5 (Transports)

Closes PRD §12 Phase 5: FR-TRN-01..12 + the FR-API-06 transport (413) path.
Ships the HTTP and WebSocket transport bindings plus the ASP.NET Core receive
endpoint, so a packed envelope finally turns into bytes on the wire and the
matching server side accepts them.

- **Core transport abstractions** (`DidComm.Core/Transports/`):
    - `IDidCommTransport` — scheme, `CanHandle(Uri)`, `SendAsync(...)` (FR-TRN-01).
    - `ITransportRouter` + `TransportRouter` (default impl) — dispatches by URI
      scheme; throws `TransportException` when no transport handles a scheme.
    - `TransportRequest`, `TransportResult` records (FR-TRN-02/03/05).
    - `SendOptions` (mirrors `PackEncryptedOptions` + `ServiceEndpointOverride`)
      and `SendResult` (bundles `PackEncryptedResult` + transport outcome +
      endpoint used).
- **Facade** (`DidCommClient`):
    - `Task<SendResult> SendAsync(Message, SendOptions, CancellationToken)` —
      packs with `Forward = true` unless `ServiceEndpointOverride` is set, then
      dispatches via the registered `ITransportRouter`. New 5-arg public ctor
      accepting `(secrets, keyService, serviceResolver, transportRouter, options)`.
    - DI registration in `DidCommServiceCollectionExtensions` now passes
      `ITransportRouter` through to the facade singleton and idempotently registers
      the default router.
- **Exception taxonomy** — `DidComm.Exceptions.TransportException` (derived
  from `DidCommException`) fills the FR-API-07 gap for transport-level
  failures; carries optional `HttpStatusCode` + `Scheme`.
- **HTTPS sender** — new project `DidComm.Transports.Http`:
    - `HttpDidCommTransport` — `IHttpClientFactory`-backed POST; 2xx ⇒ accepted
      (FR-TRN-05); 307 followed manually with a `MaxRedirectHops` cap
      (FR-TRN-06); 301/308 + non-2xx surfaced as `TransportException`; rebuilds
      the request on every Polly retry so `HttpClient` doesn't reject the resend.
    - `HttpTransportOptions` — `RequestTimeout`, `MaxRetryAttempts`,
      `RetryBaseDelay`, `CircuitBreakerFailureThreshold`,
      `CircuitBreakerOpenDuration`, `AllowedSchemes` (default `{"https"}`),
      `MaxRedirectHops`.
    - `HttpResiliencePipelineFactory` — Polly v8 pipeline (retry + circuit
      breaker + per-attempt timeout), driven entirely by the options shape;
      skips the retry strategy when `MaxRetryAttempts == 0`.
    - `HttpDidCommBuilderExtensions.UseHttpTransport(...)` — DI extension that
      registers the transport, the named `"didcomm"` HTTP client, the router,
      and disables auto-redirect on the handler so the transport can enforce
      FR-TRN-06.
- **WebSocket sender** — new project `DidComm.Transports.WebSocket`:
    - `WebSocketDidCommTransport` — one binary message per packed envelope
      (FR-TRN-09); end-of-message flag set on the last fragment; per-endpoint
      connection pool keyed by `Authority + Path`; Polly-driven exponential
      reconnect (1s / 30s / 0.5-jitter — DD-05 / FR-TRN-11); per-send timeout;
      dropped socket recycled on `SendFailed`; `IAsyncDisposable` cleans up on
      container shutdown.
    - `WebSocketTransportOptions` — connect/send timeouts, max reconnect
      attempts + base/max delay, allowed schemes (default `{"wss"}`),
      `WebSocketFactory` + `Connect` seams (used by the InteropTests +
      cookbook to point at a `Microsoft.AspNetCore.TestHost.TestServer` WS
      client without opening a real port).
    - `WebSocketLifecycleEventArgs` + `Lifecycle` event for FR-TRN-11
      observability (Connected / Disconnected / SendFailed / Reconnected).
    - `WebSocketDidCommBuilderExtensions.UseWebSocketTransport(...)`.
- **ASP.NET Core integration** — new project `DidComm.AspNetCore`
  (`<FrameworkReference Include="Microsoft.AspNetCore.App" />`, zero NuGet
  weight):
    - `MapDidCommEndpoint(IEndpointRouteBuilder, string, Func<UnpackResult, CancellationToken, Task>)`
      — minimal-API `POST` mapping (FR-TRN-07). Validates `Content-Type`
      against the configured accept list ⇒ 415 on mismatch; streams the body
      with a hard cap at `DidCommOptions.MaxReceiveBytes` ⇒ 413 (FR-API-06);
      unpacks via `DidCommClient.UnpackAsync`; dispatches to the inline
      receiver; returns 202. `MalformedMessageException` / `CryptoException`
      ⇒ 400; `TransportException` ⇒ 502.
    - `MapDidCommWebSocket(IEndpointRouteBuilder, string, Func<UnpackResult, CancellationToken, Task>)`
      — accepts WebSocket; loops `ReceiveAsync` until `EndOfMessage=true`
      (frame reassembly); honours `MaxReceiveBytes` and closes with 1009
      "Message Too Big" on overflow; one-way per FR-TRN-10.
    - `DidCommReceiveOptions` — per-endpoint accept-list (defaults cover the
      three DIDComm v2.1 media types).
- **DI plumbing** — `DidCommServiceCollectionExtensions` now auto-registers
  `TransportRouter` so DI hosts get the FR-TRN-01 dispatch surface for free
  the moment they call `.UseHttpTransport()` / `.UseWebSocketTransport()`.
  Hand-constructed clients still receive a clean `InvalidOperationException`
  on `SendAsync` when no router was supplied.

### Tests — Phase 5

- `DidComm.Core.Tests/Transports/TransportRouterTests` — scheme dispatch,
  case-insensitive match, no-handler → `TransportException` with the offending
  scheme, null-arg guards.
- `DidComm.Core.Tests/Transports/DidCommClientSendAsyncTests` — no-router
  refusal with an actionable message; empty-recipients refusal.
- `DidComm.InteropTests/Transports/HttpTransportSendTests` — 2xx accepted
  (Theory ×3), 307 followed to a final 2xx, 301/308 refused (Theory ×2), 500
  retried then surfaced as `TransportException`, scheme allow-list refusal,
  Content-Type propagation, case-insensitive `CanHandle`.
- `DidComm.InteropTests/Transports/AspNetCoreReceiveRoundTripTests` — full
  Alice→Bob HTTP round-trip via `TestServer.CreateHandler()`; 415, 413, 400
  negative cases.
- `DidComm.InteropTests/Transports/WebSocketTransportRoundTripTests` — full
  WS round-trip; explicit fragmented-send case (three frames coalesce into
  one envelope) to nail the FR-TRN-09 reassembly invariant; oversize message
  triggers a 1009 close per FR-API-06; `CanHandle` honours allow-list.

### Changed

- `Directory.Packages.props` adds `Polly` 8.5.0 (per the user-confirmed Phase 5
  resilience choice) and bumps `Microsoft.AspNetCore.TestHost` to the version
  that ships in the local SDK cache. `Microsoft.Extensions.Http` is no longer
  pinned in `DidComm.InteropTests` (it now arrives via the AspNet shared
  framework — `NU1510`).
- `DidComm.sln` — three new project entries (`DidComm.Transports.Http`,
  `DidComm.Transports.WebSocket`, `DidComm.AspNetCore`).

### Cookbook (samples/02-Cookbook)

- New sections `P` (send over a transport), `Q` (receive over HTTP — incl. the
  415 / 413 negative branches), `R` (receive / chat over WebSocket — incl.
  lifecycle-event subscription). All three host an in-process `TestServer` so
  the cookbook stays offline-safe; `dotnet run --project samples/02-Cookbook`
  exits 0 with the section banners printed.
- `samples/02-Cookbook/02-Cookbook.csproj` references the three new transport
  projects + `Microsoft.AspNetCore.TestHost` and brings in the AspNet shared
  framework.
- `samples/02-Cookbook/README.md` — table extended; expected-output sample
  refreshed with P/Q/R frames; section file list updated.

### Fixed (Phase 5 review)

- **WebSocket failures now surface as `TransportException` (FR-API-07).**
  `WebSocketDidCommTransport.SendAsync` wraps an exhausted reconnect budget (and
  any other transport-library failure) in `TransportException` — carrying the
  scheme + inner exception via a new ctor overload — instead of leaking the raw
  `WebSocketException` / `TimeoutException`. Caller-initiated cancellation still
  propagates as `OperationCanceledException`.
- **No socket leak on a failed WebSocket connect.** `GetOrConnectAsync` disposes
  the nascent socket when the connect handshake throws or times out (previously
  leaked once per failed reconnect attempt).
- **Malformed `Content-Type` → 415, not 500.** `MapDidCommEndpoint` now treats a
  `Content-Type` header that the media-type parser rejects as an unsupported
  type rather than letting a `FormatException` escape as a 500.
- **WebSocket lifecycle events `Disconnected` / `Reconnected` now fire.**
  `Disconnected` on a dropped socket and on `DisposeAsync`; `Reconnected` when a
  send succeeds after a prior failed attempt. Connect failures now also raise
  `SendFailed`.
- **Polly circuit-breaker no longer throws when `CircuitBreakerFailureThreshold`
  is below 2** — clamped up to Polly's `MinimumThroughput` floor.
- **Per-endpoint WebSocket connect locks** replace the single global semaphore so
  connecting to one endpoint no longer blocks connects to another.
- Removed the unused `WebSocketTransportOptions.Clock` knob and a dead
  `TransportException → 502` branch on the HTTP receive path; the WebSocket
  oversize close now uses `WebSocketCloseStatus.MessageTooBig` instead of a magic
  `1009` cast; `DidCommReceiveOptions` reuses the `DidCommMediaTypes` constants.
- WebSocket round-trip tests and Cookbook section R replaced fixed `Task.Delay`
  drains with bounded polling to remove timing flakiness.

### Added — Phase 4 (Routing & Mediation)

Closes PRD §12 Phase 4: FR-ROUTE-01..08. Sender-side `forward` wrapping,
mediator-side processing, and the conformant `serviceEndpoint` object /
array-of-objects parser with FR-ROUTE-04 mediator-as-DID-endpoint expansion.

- **Forward message** (`Protocols/Routing/`):
    - `ForwardConstants` — `ProtocolIdentifier`
      (`https://didcomm.org/routing/2.0`), `ForwardTypeUri`
      (`…/routing/2.0/forward`), `PayloadMediaType` (alias for
      `application/didcomm-encrypted+json`).
    - `ForwardMessage.Create(mediator, next, packedPayloads, idGenerator?, expiresTimeEpochSeconds?)`
      builds a `Message` with `Type = forward`, `Body.next`, and one
      `AttachmentData.Json`-bearing attachment per packed payload (FR-ROUTE-01).
    - `ForwardMessage.TryParse(message, out next, out payloads)` returns `false`
      for non-forward types, throws `MalformedMessageException` for forwards
      missing `body.next` or `attachments`.
- **Service-endpoint resolution** (`Resolution/`):
    - `DidCommServiceInfo(Uri, RoutingKeys, Accept)` — public record.
    - `IServiceEndpointResolver` — public contract: `ResolveAsync(did, ct)` →
      ordered `IReadOnlyList<DidCommServiceInfo>` (FR-ROUTE-03; preference
      order = FR-ROUTE-08 failover input).
    - `ServiceEndpointParser` (internal) — projects `NetDid.Core.Model.Service`
      entries through the v2.1 canonical shapes (single object / array of
      objects). Bare-string `serviceEndpoint` is gated behind the new
      `DidCommOptions.AllowBareStringServiceEndpoint` toggle (DD-10) and OFF by
      default.
    - `NetDidServiceEndpointResolver` — public default implementation backed by
      `NetDid.Core.IDidResolver`; rejects `did:web` at the perimeter for symmetry
      with `NetDidKeyService`.
    - `ResolvedRoute(TransportUri, RoutingKeyJwks, FallbackUris)` — public
      record.
    - `MediatorEndpointExpander` (internal) — implements FR-ROUTE-04. When the
      primary candidate's `uri` is itself a DID, resolves the mediator's
      `DIDCommMessaging` service, **prepends** its first `keyAgreement` key
      to the recipient's `routingKeys`, refuses a second DID-as-uri hop
      (`ConsistencyException`).
- **Sender-side forward wrapping** (`Composition/ForwardWrapper.cs`,
  internal) — loops `JweBuilder.PackAnoncrypt` over the routing-key JWKs in
  reverse (outermost first), producing one `forward` per layer.
  Content-encryption is fixed to A256CBC-HS512 per layer.
- **Facade** (`Facade/`):
    - `PackEncryptedResult(Message, ServiceEndpoint?, FallbackServiceEndpoints)`
      — **breaking change**: `DidCommClient.PackEncryptedAsync` now returns
      `Task<PackEncryptedResult>` instead of `Task<string>`. Consumers of the
      `.Message` field can append `.Message` to existing call sites.
    - `PackEncryptedOptions.Forward` — when `true`, the facade resolves the
      single recipient's `DIDCommMessaging` service, expands a mediator-as-DID
      endpoint, and wraps forward layers (FR-ROUTE-02). Multi-recipient
      `Forward = true` throws `InvalidOperationException`.
    - New `DidCommClient(secrets, keyService, serviceResolver, options)`
      constructor adds the `IServiceEndpointResolver` slot. The existing
      3-argument constructor still works (routing unavailable without the
      resolver).
    - `DidCommServiceCollectionExtensions.AddDidComm` now passes the optional
      `IServiceEndpointResolver` into the facade, so hosts that call
      `UseNetDidResolver()` get routing automatically.
- **Mediator-side processing** (`Protocols/Routing/`):
    - `ForwardProcessor` — public; drives the supplied `DidCommClient`'s
      `UnpackAsync`, validates the unpacked plaintext is a forward, silently
      drops `please_ack` (FR-ROUTE-07), and emits `ForwardProcessingResult`.
    - `ForwardProcessingResult(NextHop, OnwardPacked, ExpiresTime?, Delay?)`.
    - `ForwardProcessorOptions(Mode, ExtraRecipientRoutingKeys?)` +
      `RewrapMode` enum (`PassThrough`, `ReanoncryptToNext`). Pass-through is
      the default; rewrap (FR-ROUTE-06) re-anoncrypts the payload to `next` to
      keep onion size constant. `expires_time` propagates from the inbound
      forward; `delay_milli` resolves to a `TimeSpan` (negative input →
      randomised between 0 and |n|).

### Vendored spec fixtures (Phase 4 routing)

- `tests/DidComm.InteropTests/fixtures/spec/{endpoint-example-1,endpoint-example-2}.json`
  pinned verbatim from the DIDComm v2.1 spec §Service Endpoint /
  "Using a DID as an endpoint" (Apache-2.0). KAT anchors per L-005.
- `tests/DidComm.InteropTests/fixtures/diddocs/spec/{bob-with-routing,mediator1,mediator2,charlie}.json`
  transcribed from didcomm-python's
  `tests/test_vectors/did_doc/did_doc_{bob,mediator1,mediator2,charlie}.py`
  into the v2.1 canonical service shape (object form with nested
  `routingKeys` / `accept`). Provenance + transcription notes in
  `diddocs/spec/README.md`.
- `tests/DidComm.InteropTests/fixtures/secrets/{mediator1,mediator2}.json`
  reuse Bob's matching private bytes — didcomm-python's own fixtures do the
  same; documented in `fixtures/secrets/README.md`.

### Tests — Phase 4

- 13 forward-message + spec-endpoint tests (Checkpoint A — 11 unit + 2 interop).
- 16 service-endpoint resolver tests (Checkpoint B — 11 parser unit + 1 DI unit + 4 NetDid adapter interop).
- 10 mediator-endpoint expander tests (Checkpoint C — internal contract via `InternalsVisibleTo`).
- 6 sender-side forward wrapping tests (Checkpoint D — 2 facade unit + 4 interop covering single-hop Bob, two-hop Charlie via mediator-as-DID, no-service refusal, Forward=false bypass).
- 13 ForwardProcessor tests (Checkpoint E — option matrix, non-forward refusal, pass-through, FR-ROUTE-07 please_ack silence, expires_time, delay_milli ±, malformed attachment).
- 2 Alice → mediator1 → Bob end-to-end round-trip tests (Checkpoint F — happy path + missing-service-block refusal).
- Cookbook smoke test continues to pass after adding section O.
- Test totals: **300 → 348 unit (+48)** and **31 → 43 interop (+12)**.

### Changed (Phase 4)

- `DidCommClient.PackEncryptedAsync` return type is now `Task<PackEncryptedResult>`. Existing call sites that fed the result straight to `UnpackAsync` need a `.Message` extraction; six in-repo sites updated (4 round-trip tests, 1 rotation interop test, 2 cookbook sections).
- `MediatorEndpointExpander` only weaves the mediator's _keyAgreement_ (FR-ROUTE-04 implicit-prepend), **not** the mediator's own `routingKeys` — per a re-read of the spec text. The mediator's own routingKeys apply only when the mediator is itself the message recipient.

### Fixed (Phase 4 review)

- `ForwardProcessor.ExtractAttachmentBytes` now decodes `attachment.data.base64` with
  `Base64Url` (DIDComm attachments are base64url); previously `Convert.FromBase64String` threw
  `FormatException` on conformant base64url payloads from interop peers.
- `ForwardProcessor` rewrap mode (`RewrapMode.ReanoncryptToNext`) now strips the DID fragment
  from `next` before building the self-addressed forward and resolving the next hop's
  keyAgreement keys, so multi-hop rewrap no longer feeds a fragment'd DID-URL to
  `GetVerificationMethodsAsync`. Added an Alice→mediator→Bob rewrap round-trip test (the
  self-addressed `to == next` envelope is peeled by a forward-aware recipient).
- `ForwardProcessor.ProcessAsync` throws `ConsistencyException` for a forward carrying more than
  one attachment instead of silently relaying only the first and dropping the rest.
- `ForwardProcessor.ExtractDelay` no longer crashes on extreme negative `delay_milli`
  (`long.MinValue` / `-long.MaxValue`); the random hold is computed without overflowing
  `Math.Abs`/`int` and is capped at `int.MaxValue` ms.
- `ForwardMessage.TryParse` raises `MalformedMessageException` (not `InvalidOperationException`)
  when `body.next` is present but not a JSON string.
- `MediatorEndpointExpander` rejects relative/fragment-only routing-key references with a clear
  `DidResolutionException` (previously an empty subject DID produced a confusing
  `ArgumentException`), and drops DID-valued fallback URIs so `FallbackServiceEndpoints` only
  carries usable transport URIs.
- `AddDidComm` registers `DidCommClient` via `TryAddSingleton` (parity with its sibling
  registrations), so a repeated `AddDidComm` no longer double-registers the client.

### Added — Cookbook (PRD §14.2 Phase 4 increment: section O)

- `samples/02-Cookbook/Sections/Section_O_RoutingViaMediator.cs` — narrates
  `Forward = true` end-to-end against a section-local inline
  `IServiceEndpointResolver` so the runnable cookbook needs no fixture
  dependency.
- `samples/02-Cookbook/CookbookContext.cs` exposes `ServiceProvider` so a
  section can mint extra identities from the shared net-did graph.
- `Program.cs` registers Section O in narration order; `README.md` updated
  with the new section description and expected-output frame.

### Added — Phase 3 (Facade, net-did Integration, Secrets, Rotation)

Closes PRD §12 Phase 3: FR-DID-01..07, FR-SEC-01..05, FR-API-01..08,
FR-CONSIST-06 (resolver-backed authorization now active), FR-ROT-01..06.

- **Public surface promotions** — every type the facade returns or accepts is now
  `public sealed`: `Messages/{Message, Attachment, AttachmentData, MessageBuilder,
IMessageIdGenerator, UuidV4MessageIdGenerator, MediaTypes}`,
  `Protocols/{MessageTypeUri, ProtocolVersion}`, `Jose/{Jwk, EnvelopeKind}`. Helper
  types (`UnreservedUriChars`, `DidSubject`, all `Composition/*`, all
  `Jose/Signing|Encryption/*`, all `Crypto/*`, the internal lookups) stay internal.
- **Facade** (`Facade/`):
    - `DidCommClient` — sealed, thread-safe (NFR-03). Public methods
      `PackPlaintextAsync`, `PackSignedAsync`, `PackEncryptedAsync`, `UnpackAsync`
      (FR-API-01..03). Auto-detects envelope shape on unpack, enforces
      `expires_time` (FR-API-05) and `MaxReceiveBytes` (FR-API-06), rejects
      `did:web` at every entry point on every DID-bearing field (FR-DID-06).
    - `PackEncryptedOptions`, `ContentEncryptionAlgorithm`, `DidCommOptions`,
      public `UnpackResult` (FR-API-04 metadata + `FromPrior` slot).
    - `MapContentEncryption` enforces FR-ENC-09 (refuses A256GCM / XC20P for
      authcrypt at pack time).
- **Resolution** (`Resolution/`):
    - `IDidKeyService` — public contract: `GetVerificationMethodsAsync`,
      `IsKeyAuthorizedAsync`, `RejectUnsupportedMethod`. `VerificationRelationship`
      enum (`KeyAgreement`, `Authentication`).
    - `NetDidKeyService` — public adapter wrapping `NetDid.Core.IDidResolver`.
      Method extraction via `NetDid.Core.Parsing.DidParser.ExtractMethod`; rejects
      `did:web` with `UnsupportedDidMethodException` (DD-08). Dereferences fragment
      references against the doc's `verificationMethod` array; materialises JWKs
      from `publicKeyJwk` (off-curve EC points already rejected inside
      `JwkConversion.ExtractPublicKey` by net-did's `EcPointValidator`); silently
      skips multibase-only methods and curves outside the `KeyTypeMapper` set so
      mixed-curve documents still surface usable keys. No internal cache —
      relies on `CachingDidResolver` from `NetDid.Extensions.DependencyInjection`
      (FR-DID-04 "no double-caching").
    - `DidKeyServiceLookups` — internal sync-over-async bridges that satisfy the
      envelope layer's `IInternalSenderKeyLookup` and signer-`Func<string, Jwk?>`
      slots by walking back to the public async `IDidKeyService`.
- **Secrets** (`Secrets/`):
    - `ISecretsResolver` — public contract: `FindAsync(kid)`,
      `FindPresentAsync(kids)`. Consumer-supplied; the library ships no production
      key store per DD-02.
    - `SyncSecretsAdapter` — internal `IInternalSecretsLookup` wrapper that
      blocks sync-over-async on the public resolver (safe under .NET 10's
      no-synchronization-context runtime).
- **Exceptions** — `UnsupportedDidMethodException(method, did, reason)`,
  `DidResolutionException(did, reason, inner?)`, `SecretNotFoundException(kid)`
  (FR-API-07).
- **FR-CONSIST-06 wiring** — `EnvelopeReader.Unpack` gained a
  `Func<string,string,string,bool>? resolverCheck` parameter that fires
  `AddressingConsistency.CheckResolverAuthorization` at three points (sender
  keyAgreement, recipient keyAgreement, signer authentication) once the inner
  plaintext reveals `from`. The facade binds the predicate to
  `IDidKeyService.IsKeyAuthorizedAsync`.
- **DID rotation** (`Protocols/Rotation/`):
    - `Message.FromPrior` typed slot + `MessageBuilder.WithFromPrior` (FR-ROT-01).
    - `FromPriorClaims` record (`Sub`, `Iss`, `Iat`).
    - `FromPriorBuilder` (internal) — emits a compact JWT
      `<b64u(header)>.<b64u(claims)>.<b64u(sig)>` signed by a key authorized in
      the prior DID's `authentication`.
    - `FromPriorValidator` (internal) — three-part split, signature verification
      via `IDidKeyService`, `sub == currentSenderDid` enforcement (FR-ROT-02),
      alg-curve cross-check to defeat downgrade swaps.
    - `DidCommClient` enforces FR-ROT-03: refuses to emit `from_prior` on a
      plaintext or signed-only envelope; raises `ConsistencyException` on unpack
      when a `from_prior`-carrying message arrives unencrypted.
- **DI extension** (`src/DidComm.Extensions.DependencyInjection/`):
    - New csproj. `services.AddDidComm(b => …)` registers `DidCommClient` as
      singleton with `DidCommOptions`, `ISecretsResolver`, and `IDidKeyService`.
    - `DidCommBuilder` methods: `UseNetDidResolver(...)` (defaults to `did:key` +
      `did:peer` via net-did's DI builder; extra methods via the inner action),
      `UseSecretsResolver<T>()` / `UseSecretsResolver(instance)`,
      `Configure(Action<DidCommOptions>)`.
    - Build-time FR-SEC-02 fail-fast: throws `InvalidOperationException` with
      actionable docs-pointer text when `ISecretsResolver` or `IDidKeyService` is
      unregistered.
- **NetDid IKeyStore adapter** (`src/DidComm.Adapters.NetDid/`):
    - New csproj. `NetDidKeyStoreSecretsResolver` bridges
      `NetDid.Core.IKeyStore` → `ISecretsResolver` (FR-SEC-04, SHOULD). XML doc
      surfaces the scope limit: `IKeyStore` exposes signing + public-key surfaces
      only, never raw private bytes, so this adapter is sufficient for resolving
      _which_ kids are held but cannot yield decryption-path private keys until
      net-did adds an opaque-ECDH provider.
- **TestSupport** (`tests/DidComm.TestSupport/`):
    - New library (non-test). `InMemorySecretsResolver` is the dictionary-backed
      test fake (FR-SEC-05) — deliberately outside `DidComm.Core` so DD-02 stays
      honest.
- **`JweParser.PeekRecipients`** — lightweight structural peek (recipient kids
    - skid, no crypto) for facade pre-warm scenarios. Wired into the design but
      not yet consumed by the current facade implementation; kept available for
      future caching/optimization work.

### Vendored spec fixtures (FR-IX-01 extension)

- `tests/DidComm.InteropTests/fixtures/diddocs/spec/{alice,bob}.json` — DIDComm
  v2.1 Appendix B DID Documents transcribed from didcomm-python's
  `DID_DOC_*_SPEC_TEST_VECTORS` (Apache-2.0). Provenance + scope note in
  `fixtures/diddocs/spec/README.md`. Charlie / mediator1 / mediator2 are
  intentionally deferred to Phase 4 alongside the FR-ROUTE-\* work that actually
  exercises them.

### Tests — Phase 3

Adds **54 new** `DidComm.Core.Tests` cases (299 total) plus **18 new**
`DidComm.InteropTests` cases (30 total).

- `Exceptions/Phase3ExceptionsTests` — the three new typed exceptions carry the
  declared properties and inherit `DidCommException`.
- `Messages/MessageFromPriorTests` — `Message.FromPrior` round-trips, omitted
  when null, `MessageBuilder.WithFromPrior` populates the slot.
- `Secrets/{ISecretsResolverContractTests, InMemorySecretsResolverTests,
NetDidKeyStoreSecretsResolverTests}` — contract semantics + the two adapters.
- `Resolution/{IDidKeyServiceContractTests, NetDidKeyServiceTests}` — contract
    - adapter (did:web rejection, malformed input, missing-doc, embedded JWK,
      fragment deref, missing reference, unsupported-curve filter,
      multibase-only-skip, `IsKeyAuthorizedAsync` relationship boundary).
- `Consistency/ResolverAuthorizationTests` — predicate-fires-correct-triple,
  null-short-circuit, authorized passes, unauthorized throws.
- `Facade/{DidCommClientUnitTests, DependencyInjectionTests}` — FR-ROT-03
  refusal on plaintext / signed; did:web rejection across every entry point and
  every DID-bearing field; MaxReceiveBytes; fail-fast on missing
  `ISecretsResolver` / `IDidKeyService`; `Configure(...)` applies; instance
  registration overload.
- `Rotation/FromPriorClaimsTests` — record equality + iat inequality.
- InteropTests:
    - `Resolution/AppendixBResolutionTests` — Alice authentication (3 keys),
      Alice keyAgreement (X25519+P256+P521), Bob keyAgreement (9 keys across
      four curves), Bob no-authentication, `IsKeyAuthorizedAsync` relationship
      boundary.
    - `Facade/DidCommClientRoundTripTests` — plaintext, signed, anoncrypt,
      authcrypt, sign-then-encrypt, anoncrypt(authcrypt), authcrypt FR-ENC-09
      refusal — every legal FR-ENV-02 composition through the public facade.
    - `Rotation/FromPriorRotationTests` — builder/validator round-trip, tampered
      signature rejected, mismatched `sub` rejected (FR-ROT-02), signer-not-in-
      authentication rejected, malformed JWT rejected, `DidCommClient.UnpackAsync`
      populates `UnpackResult.FromPrior` end-to-end.

### Changed

- `DidComm.Core.csproj` gained `<InternalsVisibleTo>` entries for the three new
  sibling assemblies (`DidComm.Extensions.DependencyInjection`,
  `DidComm.Adapters.NetDid`, `DidComm.TestSupport`).
- `EnvelopeReader.Unpack` signature gained a trailing optional `resolverCheck`
  parameter (default `null` preserves the Phase 2 behaviour where the
  FR-CONSIST-06 hook is a no-op).
- `SpecActorRegistry.AsSecretsResolver()` — new test helper exposing the
  Appendix A secrets through the public `ISecretsResolver` shape.

### Fixed (Phase 3 review)

- `FromPriorBuilder` now serializes the JWT protected header via
  `JsonSerializer` instead of string interpolation, so an unusual signer `kid`
  (embedded quote / control char) is escaped rather than injected into the
  header JSON. Output remains byte-identical for well-formed kids (key order
  `alg, kid, typ` preserved), so round-trip / tamper coverage is unchanged.
- `DidCommClient.UnpackAsync` documents the sync-over-async **support
  boundary** (supported on hosts without a captured `SynchronizationContext`;
  use `Task.Run(...)` from legacy UI contexts), and the `ISecretsResolver` /
  `IDidKeyService` contracts now require `ConfigureAwait(false)` in
  implementations. Corrected a stale `ISecretsResolver` remark that claimed the
  facade pre-resolves all secrets, and removed dead doc references
  (`UnpackAsync` "Checkpoint D" forward-reference, a misplaced `PickCommonCurve`
  summary).

### Added — Cookbook (PRD §14.2 Phase 3 increment)

Per the PRD §14 note, the Cookbook gains the API tasks each phase ships.
Phase 3's increment lands here: **K (unpack metadata), N (from_prior rotation),
AA (net-did + did:web rejection)**.

- **`samples/_shared/`** (`DidComm.Samples.Shared`):
    - `Narrator` — labeled console output (section banners, key=value frames,
      notes). Writes through an injectable `TextWriter` so the smoke test
      captures the transcript without process spawning.
    - `PeerIdentityFactory.CreateAsync(manager, keyGenerator, cryptoProvider)`
      mints a `did:peer:2` identity with one X25519 keyAgreement key + one
      Ed25519 authentication key (via net-did's `KeyPairSigner` +
      `DidPeerCreateOptions(Numalgo.Two, ...)`); surfaces the matching private
      JWKs with absolute-DID-URL `Kid` values so they can be loaded into
      `InMemorySecretsResolver`.
- **`samples/02-Cookbook/`** (`DidComm.Samples.Cookbook`):
    - `CookbookContext.BuildAsync()` runs `services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(instance))`,
      mints `alice`, `bob`, and `alice2` peer identities, and resolves the
      `DidCommClient`. Shared by every section.
    - `Program.RunAsync(TextWriter? output)` — testable entry point; `Main`
      wraps it for CLI use.
    - `Sections/Section_K_UnpackMetadata` — packs authcrypt(sign(plaintext))
      alice→bob, unpacks as bob, prints every `UnpackResult` field
      (Encrypted/Authenticated/NonRepudiation/AnonymousSender/ContentEncryption/
      KeyWrap/SignatureAlgorithm/SignerKid/SenderKid/RecipientKid/
      AllRecipientKids/Stack/FromPrior + Message.From + Message.Body).
    - `Sections/Section_N_FromPriorRotation` — builds the `from_prior` JWT via
      the now-public `FromPriorBuilder.Build(claims, signerPrivateJwk)`, packs
      as authcrypt(alice2→bob), unpacks as bob, asserts
      `UnpackResult.FromPrior.Sub == message.From`. Then demonstrates FR-ROT-03
      by attempting `PackPlaintextAsync` with `FromPrior` set and reporting the
      `InvalidOperationException` message.
    - `Sections/Section_AA_NetDidAndDidWebRejection` — every prior section is
      already going through `NetDidKeyService` over a `CompositeDidResolver`
      (did:key + did:peer). This section adds the explicit DD-08 / FR-DID-06
      rejection paths: `PackEncryptedAsync` (recipient, From, SignFrom) and
      `PackSignedAsync` (signFrom) all throw `UnsupportedDidMethodException`
      when given `did:web:example.com`.
    - `README.md` — what each section demonstrates + the expected output shape.
- **`tests/DidComm.InteropTests/Samples/CookbookSmokeTests`** — FR-DX-02
  build+run gate: invokes `Program.RunAsync(StringWriter)` and asserts every
  Phase 3 section banner appears in the transcript, no exceptions, no process
  spawn.

### Public-surface bumps to unblock the Cookbook

- `Protocols/Rotation/FromPriorBuilder` and `FromPriorValidator` promoted
  `internal → public` (Section N consumes them directly). Each gains a no-
  crypto-provider overload as the public entry point; the explicit-provider
  variant stays `internal` for tests/facade reuse.
- `NetDidKeyService` now decodes `publicKeyMultibase` (Multikey) verification
  methods via NetCid's `Multibase` + `Multicodec` + net-did's
  `KeyTypeExtensions.ToKeyType` — needed because `did:peer:2` resolved DID
  Documents emit Multikey form (not JsonWebKey2020). It also absolutizes
  relative VM ids (`#key-1` → `<did>#key-1`) so kids match the envelope
  layer's expectations. The previous "multibase-only methods are skipped"
  test became a "Multikey methods decode to JWK" test; a new
  malformed-multibase test asserts the skip-on-error path.

### Added — Phase 2 (Envelopes + Interop Gate)

Closes PRD §12 Phase 2: FR-ENV-01..07, FR-ENC-04, FR-ENC-09..19, FR-SIG-01..06,
FR-IX-01 (vendored spec Appendix C fixtures), FR-IX-03 (inbound static gate).

- **JWS layer** (`Jose/Signing/`):
    - `JwsBuilder` emits Flattened JSON Serialization for one signer and General JSON
      for multiple (FR-SIG-02). Signs the deterministic canonical bytes of the inner
      plaintext JWM (NFR-10).
    - `JwsParser` accepts both serializations; verifies the signature; runs FR-CONSIST-03
      (signer kid ↔ plaintext `from` DID-subject equality); tolerates kid in either the
      protected or unprotected header per JOSE.
    - `JwsProtectedHeader` DTO with `JsonExtensionData` for unknown-member preservation
      (FR-MSG-15 carries through to the protected header too).
- **JWE layer** (`Jose/Encryption/`):
    - `JweBuilder` packs General-JSON multi-recipient envelopes. `PackAnoncrypt` uses
      ECDH-ES+A256KW with any of A256CBC-HS512 / A256GCM / XC20P; `PackAuthcrypt` uses
      ECDH-1PU+A256KW and **enforces FR-ENC-09**: refuses A256GCM / XC20P for authcrypt.
      Same-curve invariant enforced inside one envelope (FR-ENC-04 / FR-ENC-11); cross-
      curve splitting is the Phase 3 facade's job.
    - `JweParser` decrypts the first recipient whose kid matches a held private key,
      re-derives `apv` from the parsed recipient kids and **rejects on mismatch
      (FR-ENC-13)**, re-validates `epk` through `JwkConversion.ExtractPublicKey` (so
      off-curve EC points throw `CryptoException` per FR-ENC-03 via net-did's
      `EcPointValidator`).
    - `ApvComputer` (FR-ENC-13: base64url(SHA-256(sort(kids).join('.')))),
      `ApuComputer` (FR-ENC-14: base64url(UTF-8(skid))), `JweProtectedHeader` DTO,
      `RecipientWrap` record.
- **Composition** (`Composition/`):
    - `EnvelopeWriter.PackPlaintext` / `PackSigned` / `PackEncrypted` accept explicit
      key material (no resolver lookups in Phase 2). `PackEncrypted` orchestrates the
      legal FR-ENV-02 / FR-ENV-04 compositions: anoncrypt, authcrypt, sign-then-encrypt
      (FR-ENV-05 ordering), and anoncrypt-of-authcrypt (`ProtectSender = true`).
    - `EnvelopeReader.Unpack` auto-detects envelope shape (FR-API-03), recursively
      unwraps up to 4 layers, enforces the addressing-consistency rules as each layer
      is revealed — FR-CONSIST-01 (authcrypt `skid` ↔ plaintext `from`), FR-CONSIST-02
      (recipient kid ↔ `to`), FR-CONSIST-03 (signer kid ↔ `from`), and FR-CONSIST-05
      (authcrypt(sign) inner signer ↔ outer `skid`) — and surfaces FR-API-04 metadata
      (`encrypted`, `authenticated`, `non_repudiation`, `anonymous_sender`, enc/kw/sig
      algorithms, signer/sender/recipient kids, envelope stack). FR-CONSIST-06's
      resolver-backed authorization is wired in Phase 3.
    - `UnpackResult` carries the metadata shape that the Phase 3 public facade will
      surface unchanged.
- **Crypto additions** (`Crypto/`):
    - `Kdf/EcdhEsKdf` — anoncrypt KDF wrapper (`Z = Ze`, tag-free `SuppPubInfo`) plus
      receive-side variant; mirrors the `Ecdh1PuKdf` pattern.
    - `KeyAgreement/EphemeralKeyPair.Generate(crv)` — wraps net-did's
      `DefaultKeyGenerator` to produce one-shot ephemeral keypairs for each pack call;
      `Clear()` zeroes the private half (NFR-09).
    - `KeyAgreement/KeyTypeMapper` — single source of truth for JOSE `crv` ↔
      `KeyType` ↔ JWS `alg` ↔ AEAD key/IV sizes; eliminates ad-hoc dispatch tables
      scattered across the envelope code.
- **Secrets** (`Secrets/`):
    - `IInternalSecretsLookup` and `IInternalSenderKeyLookup` — minimal internal
      contracts so the envelope layer is testable in isolation. The Phase 3 public
      `ISecretsResolver` (FR-SEC-01) will adapt.
- **Exceptions**: `CryptoException` joins the typed hierarchy (FR-API-07). Decrypt /
  verify / unwrap / off-curve failures throw it instead of raw
  `CryptographicException`.
- **Jose plumbing**: `Base64Url` (thin wrapper over `System.Buffers.Text.Base64Url`,
  used by every JOSE encoder/decoder), `EnvelopeKind` enum, `EnvelopeDetector`
  (FR-API-03 structural sniff).

### Fixed — Phase 0 carry-over

- **`Crypto/Kdf/Ecdh1PuKdf.cs`**: the `SuppPubInfo` layout was
  `BE32(keyDataLen*8) ‖ tag`. Per draft-madden-jose-ecdh-1pu-04 §2.3 the tag MUST be
  prefixed with a 4-octet big-endian length: `BE32(keyDataLen*8) ‖ BE32(tagLen) ‖ tag`.
  The original Phase 0 wrapper omitted the prefix; self-round-trip tests masked it
  because both sides used the same (incorrect) layout. Discovered when the SICPA
  Appendix C.3 authcrypt vectors all failed AES-KW unwrap with "integrity check
  failed". The matching Phase 0 KAT was updated to the corrected layout.
- **`Json/DidCommJson.cs` + `Json/DeterministicJsonWriter.cs`**: both serializers now
  use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so the `+` in
  `application/didcomm-plain+json` is emitted literally rather than as `\u002B`. The
  spec vectors carry the literal `+`; deterministic JSON bytes that feed JWS signing
  input and `apv` hashing must match byte-for-byte.

### Tests — Phase 2

Adds **53 new** `DidComm.Core.Tests` cases (245 total) plus **10 new** spec-vector
runners under `DidComm.InteropTests` (12 total: 1 fact, 11 theory cases).

- `Envelopes/Signing/JwsRoundTripTests` — Sign+verify across EdDSA, ES256, ES256K;
  payload tampering rejection; unknown-kid rejection; Flattened vs General serialization
  selection; FR-SIG-06 inner-`to` enforcement; FR-CONSIST-03 wiring.
- `Envelopes/Encryption/AnoncryptRoundTripTests` — Every supported (curve, enc) cell
  per PRD §13.5 anoncrypt row; multi-recipient JWE on same curve (FR-ENC-19);
  cross-curve rejection (FR-ENC-04); apv-tampering detection (FR-ENC-13).
- `Envelopes/Encryption/AuthcryptRoundTripTests` — All four curves
  (X25519/P-256/P-384/P-521); FR-ENC-09 A256GCM rejection; cross-curve sender/recipient
  rejection; missing-sender-lookup rejection; tag-tampering propagates through both
  KEK derivation (FR-ENC-15) and AEAD verification.
- `Envelopes/Encryption/ApvComputerTests` + `ApuComputerTests` + `EnvelopeDetectorTests`
    - `EphemeralKeyPairTests` — per-curve length contracts, freshness, FR-MSG-06
      prefix-normalization for media types.
- `Envelopes/Composition/EnvelopeReaderTests` — End-to-end round-trips for plaintext,
  signed, anoncrypt, authcrypt, anoncrypt(sign), anoncrypt(authcrypt) compositions;
  FR-CONSIST-02 wiring; metadata shape (FR-API-04).
- `Crypto/Kdf/EcdhEsKdfTests` — Sender / receiver KDF agreement; apv sensitivity.
- `Crypto/KeyAgreement/KeyTypeMapperTests` — Routing-table coverage.

### Vendored spec fixtures (FR-IX-01)

DIDComm v2.1 Appendix A/B/C test material harvested from
`sicpa-dlab/didcomm-python` (the SICPA reference impl; same cryptographic baseline as
the spec):

- `secrets/alice.json` + `secrets/bob.json` — 6 + 9 JWKs covering Ed25519 / X25519 /
  P-256 / P-384 / P-521 / secp256k1 (Appendix A).
- `packed/spec/` — 3 signed (C.2 EdDSA / ES256 / ES256K) and 5 encrypted (C.3
  anoncrypt-X25519/XC20P×2, anoncrypt-A256CBC-HS512, anoncrypt-A256GCM,
  authcrypt-X25519, authcrypt-of-signed-P-256, anoncrypt-of-authcrypt-of-signed-P-521)
  packed envelopes.
- `manifest/spec/c2-*.json` + `c3-*.json` — 8 fixture manifests, each running through
  the new `Runner/FixtureDispatcher` and asserting both successful unpack and FR-API-04
  metadata against the SICPA-published expectations.

`InteropTests/Resolution/SpecActorRegistry` loads the Appendix-A secrets once per test
host, exposing both `IInternalSecretsLookup` (for recipient private keys) and
`IInternalSenderKeyLookup` (for authcrypt sender public keys); the resolver-backed
Phase 3 path will subsume this with `IDidKeyService`.

### Added — Phase 1 (Message Model & Consistency)

Closes PRD §12 Phase 1 line items: FR-MSG-01..15, FR-ATT-01..05, FR-CONSIST-01..05
(FR-CONSIST-06 hook present, resolver wiring stubbed for Phase 3), FR-PROTO-01/02,
NFR-10.

- **`Messages/`** — plaintext message model:
    - `Message` — POCO mirroring the §Plaintext Message Structure header set
      (`id`, `type`, `typ`, `to`, `from`, `thid`, `pthid`, `created_time`,
      `expires_time`, `body`, `attachments`) plus a `JsonExtensionData`
      `AdditionalHeaders` bag that survives unpack→repack (FR-MSG-12, FR-MSG-15).
      `Validate()` enforces the §4 structural rules: REQUIRED `id` of unreserved
      URI characters (FR-MSG-02), REQUIRED MTURI `type` (FR-MSG-05), no-fragment
      constraint on `to` / `from` (FR-MSG-07/08), same constraints on
      `thid`/`pthid` (FR-MSG-11).
    - `Attachment` + `AttachmentData` — §Attachments shape with FR-ATT-02 (data
      must carry one of `jws` / `hash` / `links` / `base64` / `json`), FR-ATT-03
      (`links` requires `hash`), FR-ATT-04 (attachment `id` unreserved-char
      requirement) all validated in code.
    - `MessageBuilder` — fluent builder per FR-MSG-13; auto-populates `id` via
      `IMessageIdGenerator` (default `UuidV4MessageIdGenerator`, FR-MSG-03) and
      `typ` (`application/didcomm-plain+json`).
    - `IMessageIdGenerator` carries the FR-MSG-14 uniqueness obligation in its
      XML docs; custom implementations are responsible for it.
    - `MediaTypes` — IANA constants for plaintext / signed / encrypted with
      FR-MSG-06 normalization (`didcomm-plain+json` accepted as equivalent to
      `application/didcomm-plain+json`).
- **`Protocols/`** — MTURI parsing:
    - `MessageTypeUri` — parses
      `<doc-uri>/<protocol-name>/<major.minor>/<message-type>` into four named
      components (FR-PROTO-01); `Matches` comparison is case- and
      punctuation-insensitive on protocol/message and uses
      `ProtocolVersion.IsCompatibleWith` for the version.
    - `ProtocolVersion` — `major.minor` value type with
      `IsCompatibleWith`/`NegotiateWith` implementing FR-PROTO-02 spec semver.
- **`Consistency/`** — addressing-consistency check functions (PRD §4.3):
    - `DidSubject.DidSubjectOf(string)` — delegates to net-did's
      `DidParser.ParseDidUrl` and returns the bare DID subject, the primitive
      every FR-CONSIST-\* rule pivots on.
    - `AddressingConsistency` — pure static functions for FR-CONSIST-01..05
      (`CheckAuthcryptFromMatchesSkid`, `CheckRecipientKidInTo`,
      `CheckSignedFromMatchesSignerKid`, `IsRecipientInTo`,
      `CheckAuthcryptInnerSignerMatchesSkid`) plus the FR-CONSIST-06
      `CheckResolverAuthorization` hook (real resolver wiring lands in Phase 3).
- **`Json/`** — deterministic JSON for NFR-10:
    - `DeterministicJsonWriter.WriteUtf8(JsonNode?)` walks the tree and emits a
      UTF-8 byte sequence with object members sorted ASCII-lexicographically at
      every nesting level and no whitespace. Future signing inputs and `apv`
      hashing in Phase 2 route through this writer.
    - `EpochSecondsConverter` enforces integer JSON output for `created_time` /
      `expires_time` (FR-MSG-09) while tolerating string input on read.
    - `DidCommJson.Default` `JsonSerializerOptions` instance with
      `WhenWritingNull` ignore policy so unset optional headers don't appear on
      the wire.
- **`Exceptions/`** — typed failure hierarchy scaffolding (FR-API-07):
  `DidCommException` base + `MalformedMessageException`, `ConsistencyException`,
  `ProtocolException`. Crypto / resolver / transport exceptions land in their
  respective phases.
- **InteropTests fixture payload** — Appendix C.1 "Let's Do Lunch" plaintext
  saved at `tests/DidComm.InteropTests/fixtures/payloads/c1-lets-do-lunch.json`;
  the data-driven runner will wire it into `manifest/spec/` when Phase 2 adds
  the corresponding pack/unpack fixtures.

### Tests — Phase 1

Adds 83 tests to `DidComm.Core.Tests` (86 → 169 total, all green); InteropTests
remains 2/2.

- `Messages/MessageJsonTests` — Appendix C.1 round-trips structurally; body
  absent unpacks to `null` body without error (FR-MSG-10); unknown headers
  survive round-trip (FR-MSG-12, FR-MSG-15); `created_time`/`expires_time`
  serialize as integers (FR-MSG-09) and tolerate string input; null optional
  headers omitted from output.
- `Messages/MessageValidationTests` — FR-MSG-02 / -05 / -07 / -08 / -11
  rejections each have a dedicated case; minimal valid message passes;
  media-type normalization accepts both forms (FR-MSG-06).
- `Messages/MessageBuilderTests` — auto-population of `id`+`typ` (FR-MSG-13),
  custom `IMessageIdGenerator` honored (FR-MSG-03), `Build()` runs validation.
- `Messages/IdGeneratorTests` — default generator emits a lowercase RFC 4122
  UUID v4; **10,000-id no-collision run** satisfies FR-MSG-14.
- `Messages/AttachmentTests` — FR-ATT-01..05: round-trip, data-required
  rejection, links-requires-hash rejection, reserved-char-`id` rejection,
  absent-`id` acceptance, JWS attachment round-trip.
- `Protocols/MessageTypeUriTests` — captures the four components for every
  spec example (`forward`, `ping-response`, `empty`, `problem-report`, plus
  the Appendix C.1 `lets_do_lunch/1.0/proposal`); rejects malformed inputs;
  punctuation- and case-insensitive `Matches`.
- `Protocols/ProtocolVersionTests` — FR-PROTO-02 semver compatibility and
  minor negotiation.
- `Consistency/AddressingConsistencyTests` — FR-CONSIST-01..05 positive and
  negative cases including DID URLs with query/path/fragment (per the §4.3
  normative paragraph); FR-CONSIST-06 short-circuit and reject paths.
- `Json/DeterministicJsonTests` — member ordering, recursive nested sorting,
  whitespace insensitivity, primitives/arrays/null pass-through.

### Added — Phase 0 (Repository & JOSE-Composition Substrate)

Closes PRD §12 Phase 0 line items.

- **Solution scaffolding** — `DidComm.sln`, `src/DidComm.Core`,
  `tests/DidComm.Core.Tests`, `tests/DidComm.InteropTests`. Targets `net10.0`
  per NFR-01 (file-scoped namespaces, nullable enabled, warnings-as-errors).
- **`DidComm.Crypto.ICryptoProvider`** + `DefaultCryptoProvider` — JOSE-shaped
  surface that dispatches by algorithm string (`"EdDSA"`, `"ES256"`,
  `"ES256K"`, `"ES384"`, `"ES512"`, `"ECDH-ES+A256KW"`, `"ECDH-1PU+A256KW"`,
  `"A256CBC-HS512"`, `"A256GCM"`, `"XC20P"`, `"A256KW"`). Sign/verify and raw
  ECDH delegate to `NetDid.Core.ICryptoProvider` 1.3.0+; AEAD, key wrap, and
  the 1PU KDF wrapper are owned locally.
- **AEAD layer** (`Crypto/Aead/`):
    - `AesCbcHmacSha512` — RFC 7518 §5.2.5 (the JOSE-defined encrypt-then-MAC
      composition; mandatory `enc` for authcrypt per FR-ENC-05). Constant-time
      tag check via `CryptographicOperations.FixedTimeEquals` (NFR-09).
    - `AesGcmAead` — thin BCL wrapper for A256GCM.
    - `XChaCha20Poly1305Aead` — thin NSec wrapper for XC20P.
- **`Crypto/KeyWrap/AesKeyWrap`** — RFC 3394 / RFC 7518 §4.4 A256KW. Manual
  implementation because the BCL has no public AES-KW API. Constant-time IV
  integrity check on unwrap.
- **`Crypto/Kdf/Ecdh1PuKdf`** — JOSE 1PU KDF wiring: composes `Z = Ze ‖ Zs`
  from two `NetDid.DeriveSharedSecret` calls, threads the AEAD authentication
  tag into `SuppPubInfo`, and runs net-did's `ConcatKdf`. Implements the 1PU
  encrypt-then-derive-KEK-with-tag ordering required by FR-ENC-15.
- **JOSE primitives** (`Jose/`): `JoseAlgorithms` (algorithm-name constants),
  `Jwk` (DIDComm-shaped JSON Web Key record with `AdditionalData` bag for
  unknown-header preservation per FR-MSG-15 forward compatibility),
  `JwkConversion` (shim over `NetDid.Core.Jwk.JwkConverter` so off-curve EC
  points are rejected at the JWK boundary via net-did's `EcPointValidator`).
- **InteropTests scaffolding** (FR-IX-02) — `fixtures/schema/didcomm-fixture.v1.schema.json`
  (full v1 manifest schema per PRD §13.4), one smoke manifest under
  `fixtures/manifest/spec/_smoke.json`, and the data-driven
  `FixtureDiscoveryTests` xUnit runner that enumerates
  `fixtures/manifest/**/*.json` and emits one theory case per file. Fixtures
  stage inline for Phase 0; the directory layout matches the destination so
  the Phase 2 migration to a standalone `didcomm-dotnet-fixtures` git
  submodule is a `git rm -r` + `git submodule add` (no data restructuring).
- **CI workflow** — `.github/workflows/ci.yml` on `ubuntu-latest` +
  `windows-latest`, `dotnet build /warnaserror` + `dotnet test --configuration
Release` with TRX + cobertura coverage upload (NFR-08 scaffold).

### Changed

- **PRD §3.1 dependency table** — recorded that the SSI crypto substrate
  (sign/verify with format choice, raw ECDH for X25519/P-256/P-384/P-521,
  off-curve point validation, public Concat KDF) is owned by `NetDid 1.3.0+`.
  DidComm.Core now owns only the JOSE composition layer.
- **PRD §12 Phase 0** — Build line, Exit criteria, and Kickoff prompt revised
  to reflect the smaller scope; FR-ENC-01/02/03 and FR-SIG-01 are now satisfied
  by net-did and exercised here only as integration concerns (deferred to later
  phases).
- **`Portable.BouncyCastle` → `NBitcoin.Secp256k1`** in `Directory.Packages.props`
  for consistency with net-did's secp256k1 implementation choice; secp256k1
  reaches us transitively through net-did, so no direct package reference is
  needed in `DidComm.Core.csproj`.
- **`OpenTelemetry.Api`** bumped `1.10.0 → 1.15.3` to clear NU1902 audit errors
  (GHSA-8785-wc3w-h8q6, GHSA-g94r-2vxg-569j) under warnings-as-errors.

### Tests

- **`DidComm.Core.Tests`** — 86 tests covering the JOSE composition layer:
    - `AesCbcHmacSha512Tests` — **RFC 7518 §B.3 KAT** byte-for-byte (encrypt → expected
      ciphertext + tag; decrypt → recovered plaintext), round-trip on random inputs,
      tamper rejection of ciphertext / tag / AAD, length-validation on key & IV.
    - `AesGcmAeadTests` — round-trip + tamper rejection on ciphertext / tag / AAD,
      length validation, IV-freshness invariant (FR-ENC-08).
    - `XChaCha20Poly1305AeadTests` — round-trip + tamper rejection, 24-byte nonce
      length contract.
    - `AesKeyWrapTests` — **RFC 3394 §4.6 KAT** byte-for-byte (256-bit KEK, 256-bit
      data → 320-bit wrapped output), round-trip across every block-aligned CEK
      length (16, 24, 32, 48, 64), integrity-check rejection on tampered wrapped
      bytes and wrong KEK, malformed-input rejection.
    - `Ecdh1PuKdfTests` — differential composition test against
      `NetDid.Core.Crypto.Kdf.ConcatKdf` (proves `Z = Ze ‖ Zs` ordering and
      tag-in-`SuppPubInfo` wiring), determinism, tag/apu sensitivity, counter-loop
      coverage at `keyDataLen=64`, dispatch over P-256 in addition to X25519.
    - `NetDidDelegationTests` — round-trip sign/verify for every supported JOSE
      algorithm (`EdDSA`, `ES256`, `ES384`, `ES512`, `ES256K`) with P1363 length
      assertion for the ECDSA variants; round-trip `DeriveSharedSecret` on every
      curve (`X25519`, `P-256`, `P-384`, `P-521`) with the ECDH-commutativity
      invariant; **off-curve EC JWK and identity-point JWK both throw
      `CryptographicException` through `JwkConversion.ExtractPublicKey`** (FR-ENC-03
      / RFC 7518 §6.2.2 invalid-curve defense, inherited from net-did's
      `EcPointValidator`); AEAD + key-wrap dispatch round-trips.

### Deferred to Phase 1+

- DIDComm plaintext message model + attachments + MTURI parsing (Phase 1).
- JWE / JWS envelope construction, `apv`/`apu` derivation, sign-then-encrypt
  enforcement (Phase 2 — the interop gate).
- `DidComm` facade, DID resolver adapter over net-did, `did:web` rejection
  (Phase 3).

### Upstream coordination

- Filed and closed five net-did issues that defined the SSI crypto substrate
  net-did 1.3.0 ships: moisesja/net-did#60 (raw ECDH), #61 (P-521), #62 (ECDSA
  IEEE P1363 format), #63 (off-curve EC point rejection — invalid-curve
  defense), #64 (Concat KDF).

[Unreleased]: https://github.com/moisesja/didcomm-dotnet/compare/v1.5.1...HEAD
[1.5.1]: https://github.com/moisesja/didcomm-dotnet/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/moisesja/didcomm-dotnet/compare/v1.4.1...v1.5.0
[1.4.1]: https://github.com/moisesja/didcomm-dotnet/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/moisesja/didcomm-dotnet/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/moisesja/didcomm-dotnet/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/moisesja/didcomm-dotnet/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/moisesja/didcomm-dotnet/compare/v1.0.0...v1.1.0
[0.1.0-preview.1]: https://github.com/moisesja/didcomm-dotnet/releases/tag/v0.1.0-preview.1
