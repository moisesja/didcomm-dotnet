# 04 — Mediator Agent

Mediated routing over real HTTP, in one process (PRD §14.3 sample 04): an ASP.NET Core
**mediator** running the library's routing role, and a console flow routing
**Alice → Mediator → Bob's own inbox**. Bob's `did:peer:2` names the mediator as its route, so a
single `SendAsync` on Alice's side discovers the route, wraps the forward and posts it — she
writes no routing code at all. On the mediator, the library receives and checks each forward,
answers the sender `202`, and then hands it to a small relay the host writes itself.

This sample is the **routing relay only**. For a complete mediator — recipients enroll with
Coordinate Mediation, messages wait in a queue and are picked up over HTTP or pushed over a
WebSocket — see [`samples/11-MediatedAgent`](../11-MediatedAgent/) (`AddMediator()`).

## What it demonstrates

- **The routing role (FR-ROUTE-09/10/11)** — `AddRoutingMediator(new ForwardProcessorOptions(RewrapMode.PassThrough) { MaxDelay = … })`
  behind the registry-aware `MapDidCommEndpoint("/didcomm")`, with a rate limiter in front (one
  window per client address: anyone who can reach a mediator can post forwards to it). A forward is checked inline
  (encrypted to a key the mediator holds, not expired, `next` registered), the sender gets its
  `202`, and only then is the forward processed and handed to the `IForwardSink`.
  `ForwardProcessorOptions.Validate()` refuses a bad setting (a negative `MaxDelay`) up front.
- **Who the mediator relays for** — the `InMemoryForwardRecipientRegistry` that
  `AddRoutingMediator()` registers: the host calls `Register` for Bob and Carol, reads `Count`, and
  asks `IForwardRecipientRegistry.IsRegisteredAsync` the way the forward handler does. In a full
  mediator, Coordinate Mediation fills this registry instead.
- **The host's own relay (`InboxRelaySink.cs`)** — an `IForwardSink` registered *before*
  `AddRoutingMediator()` (every registration that method makes is "add if missing", so it
  replaces the default `NullForwardSink`). For each forward it reads `ProtocolContext.Acknowledged`
  (already complete: the 202 went out first), re-checks expiry with
  `ForwardProcessingResult.IsExpired`, reads `NotBefore`, looks the recipient up in a bounded host
  directory (DID → inbox URL, at most 16 entries, filled only by the operator — never from a
  peer's message or a DID document) and relays the still-encrypted onward bytes with
  `DidCommClient.SendPackedAsync` — which applies the mediator's SSRF policy to the inbox URL and
  never reads a response body. A failed relay, of any kind, is recorded as `Failed` and handed
  back to the forward handler, which logs its message id and exception type.
  **The library's built-in push to a recipient's endpoint is deferred to #110**; until then a
  relay like this one is the host's own code.
- **A time limit on every relay** — a sink runs on the forward handler's background pump, so it
  must never wait on an endpoint a peer controls without a bound: each relay gets at most
  `InboxRelaySink.RelayTimeout` (5 seconds) and is recorded as `Failed` when it runs out. Known
  limit: this sample relays one forward at a time, so a slow inbox still delays every forward
  queued behind it by up to that bound each. A production relay hands each forward to a bounded
  queue per recipient, served by its own workers, so a slow recipient only delays its own
  messages. That protects other recipients from a slow one, not from a failing one: the HTTP
  transport has one circuit breaker for every destination, so repeated failed attempts at one
  inbox (retries included) can open it, and while it is open every recipient's relay fails.
- **A registered recipient with no inbox** — Carol is registered but the host knows no inbox for
  her, so the relay hands her forward to the library's `NullForwardSink`, which drops it and logs
  the message id.
- **A forward for an unregistered DID** — after `Unregister(carol)`, the forward handler drops her
  next forward itself, before any sink runs, and still answers `202` (the response must not reveal
  who is registered). The sample sees the drop on the handler's `didcomm.forward.outcomes` counter
  (`Dropped-Unknown`) through a `MeterListener` — what an operator would export with OpenTelemetry.
- **Discover Features, answered on the same request (FR-RR-07)** — Alice asks the mediator Bob
  named which routing protocol it serves, sending with `SendOptions { ReturnRoute = ReturnRouteMode.All }`.
  The mediator's own DID names its inbox, so `SendAsync` finds that address the normal way and the
  outbound guard checks it. An address read from someone else's DID document is never passed as
  `SendOptions.ServiceEndpointOverride`: an override is trusted as the caller's own choice and
  skips the guard.
  The `disclose` comes back as the HTTP `200` body in `SendResult.Returned`, and
  `FeatureDisclosure.Roles` reads `mediator` for `routing/2.0`. A reply rides back only to an
  authenticated (authcrypt) sender that asked for it, and only between directly connected agents.
- **Bob's inbox** uses the `MapDidCommEndpoint` overload whose callback receives each message with
  its return-route session and may return a reply. Relayed messages never carry a session, so
  Bob answers nothing.
- **Route discovery from the DID itself (FR-ROUTE-03)** and **automatic forward wrapping
  (FR-ROUTE-02)** — Bob's DID embeds `{"uri": <mediator inbox>, "routingKeys": [<mediator kid>],
  "accept": ["didcomm/v2"]}`; `SendAsync` wraps a Routing 2.0 `forward` anoncrypted to the
  mediator's routing key. The mediator never sees the content or Alice's identity.
- **The SSRF guard and its explicit opt-in** — the mediator inbox URL comes from *Bob's* DID
  document, i.e. from a counterparty. `DidCommOptions.OutboundEndpointPolicy` blocks
  private/loopback/metadata destinations by default: the sample shows `SendAsync` refusing this
  loopback demo, and the HTTP transport's own connect-time check refusing it too when bytes are
  handed straight to `ITransportRouter`. Every party then opens one host (on every port),
  `OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")` — never by disabling the guard. The
  refusal messages printed here name the endpoint and its address: a production log records the
  exception type only.
- **The same relay without a DI container** — `StaticRecipientRoutingKeyProvider`, the public
  `ForwardProcessor` and `ForwardHandler` constructors, a hand-built `ProtocolHandlerRegistry` and
  `ProtocolDispatcher`. A forward packed by hand (`ForwardMessage.Create`, `ForwardMessage.TryParse`,
  `ForwardProcessor.ProcessAsync` to preview what the sink will get) is dispatched twice: once
  with no acknowledgement to wait for (`DispatchAsync(…, Task.CompletedTask, ct)`), and once with
  an acknowledgement the host completes later (`DispatchAsync(…, acknowledged, session: null, ct)`)
  — nothing is relayed until it completes, the same rule that holds the relay behind the 202.
  Then `ForwardHandler.Dispose()` and `DisposeAsync()`.

Both web apps bind `http://127.0.0.1:0` (a port picked by the OS), every wait is on the event
the next line reads with a 30-second hang guard, and nothing touches the network — the run is
loopback-only and deterministic (FR-DX-02).

## Run it

```bash
dotnet run --project samples/04-MediatorAgent
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test tests/DidComm.InteropTests --filter FullyQualifiedName~MediatorAgentSmokeTests
```

## Expected output (shape)

Ports and DIDs change every run; the structure is stable:

```
== Section 1 — Start the mediator — the library's routing role (AddRoutingMediator) ==
    Rewrap mode = PassThrough
    Extra routing wraps = 0
    Longest hold a sender may ask for = 00:05:00
  • A negative MaxDelay is refused by Validate(), as designed.
    Role declared for https://didcomm.org/routing/2.0 = mediator
    Mediator inbox = http://127.0.0.1:<port>/didcomm

== Section 2 — Mint identities — Bob's and Carol's did:peer name the mediator as their route ==
  • Minted mediator = did:peer:2.Ez6LS…
  • Minted bob = did:peer:2.Ez6LS…
  • Minted carol = did:peer:2.Ez6LS…
    Their routingKeys[0] = did:peer:2.Ez6LS…
    [mediator] registered recipients = 2
    [mediator] relays for Bob = True
    [mediator] relays for Carol = True

== Section 3 — Start Bob's agent (his own inbox) and give the mediator's relay his address ==
    Bob inbox (known only to the mediator) = http://127.0.0.1:<port>/didcomm
    [mediator] Bob's inbox added to the relay's directory = True
    [mediator] inboxes in the directory = 1 of 16

== Section 4 — The outbound-endpoint guard — why the default refuses this demo ==
  • Default policy refused, as designed:
    note: Refusing to send to 'http://127.0.0.1:<port>/didcomm': host '127.0.0.1' resolves to a private or reserved address … to prevent SSRF …
  • The transport's own connection check refused it too:
    note: Refusing to connect to '127.0.0.1:<port>': every resolved address is private or reserved (SSRF defense). …

== Section 5 — Alice asks the mediator what it does — Discover Features, answered on the same request ==
    Bob's service endpoint (from his DID) = http://127.0.0.1:<port>/didcomm
    Bob's routingKeys to wrap for = 1
    The mediator's own endpoint (from its DID) = http://127.0.0.1:<port>/didcomm
    return_route = All
    return_route_thread = <none: only the thread mode names one>
    Endpoint used (the mediator's, from its DID) = http://127.0.0.1:<port>/didcomm
    Answer arrived as HTTP = 200
    Answer type = https://didcomm.org/discover-features/2.0/disclose
    Answer authenticated = True
    Answer threads to the question = True
    Disclosed https://didcomm.org/routing/2.0 roles = mediator

== Section 6 — Alice sends — route discovery, forward wrapping, 202, then the relay to Bob's inbox ==
    Endpoint used (the mediator, from Bob's DID) = http://127.0.0.1:<port>/didcomm
    Transport HTTP status = 202
    [mediator] relay outcome = Relayed
    [mediator] the 202 was on the wire before the sink ran = True
    [mediator] not before = <none: no hold asked for>
  • [mediator] relayed to Bob's inbox with SendPackedAsync (HTTP 202).
    [mediator] relay response discarded = False
    [mediator] relay response body = none (0 bytes): a relay never reads one
  • [bob] unpacked the relayed envelope as if no mediator had been involved:
    [bob] Content = Routed through the mediator.
    [bob] Authenticated = True
    [bob] From == alice = True
    [bob] RecipientAddressing = Addressed
    [bob] return-route session = none (nothing to answer on)
    note: The mediator saw only an anoncrypt forward addressed to its routing key — never the content, never Alice's authenticated identity.

== Section 7 — Carol — registered without an inbox here, then unregistered ==
  • [mediator] Carol is registered but has no inbox here: outcome = NoInbox (handed to the library's NullForwardSink, which drops it and logs the message id).
    [mediator] Carol unregistered = True
    [mediator] registered recipients = 1
    [mediator] relays for Carol = False
    Transport HTTP status for the unregistered DID = 202
  • [mediator] the forward handler dropped it before any sink: didcomm.forward.outcome = Dropped-Unknown.

== Section 8 — The same relay without a DI container ==
    Extra routing wraps for Bob = 0
  • Composed: ForwardProcessor + InMemoryForwardRecipientRegistry (1 recipient) + InboxRelaySink → ForwardHandler → ProtocolDispatcher.
  • [mediator] verified forward — next=did:peer:2.Ez6LS…, payloads=1.
    [mediator] next hop = did:peer:2.Ez6LS…
    [mediator] onward payload bytes > 0 = True
    [mediator] expires_time set = True
    [mediator] requested delay = <none>
    [mediator] dispatch result = NoReply
    [mediator] relay outcome = Relayed
    [mediator] acknowledged (nothing to wait for) before the sink ran = True
    …
    [bob] Content = Relayed by the hand-built mediator.
    [mediator] dispatch result before the acknowledgement = NoReply
    [mediator] relay outcome = Relayed
    [mediator] our transport acknowledged before the sink ran = True
    …
    [bob] Content = Held until the acknowledgement.
  • Hand-built handler stopped (Dispose, then DisposeAsync waited for its relay to finish).

== Section 9 — Teardown ==
    [mediator] Bob unregistered = True
    [mediator] registered recipients = 0
```

## Where to go next

- [`samples/11-MediatedAgent`](../11-MediatedAgent/) — the complete mediator: enrollment,
  queued pickup over HTTP and live delivery over a WebSocket.
- [`samples/05-WebSocketChat`](../05-WebSocketChat/) — the WebSocket transport: trust ping,
  discover-features, chat, and reconnect-after-drop.
- [`samples/02-Cookbook`](../02-Cookbook/) sections O/P/Q — the same routing pieces
  individually, in-process.
