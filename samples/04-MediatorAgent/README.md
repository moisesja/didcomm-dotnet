# 04 — Mediator Agent

Mediated routing over real HTTP, in one process (PRD §14.3 sample 04, tasks O/P/Q): an
ASP.NET Core **mediator** (receive endpoint + Routing 2.0 forward relay) and a console flow
routing **Alice → Mediator → Bob**. Bob's `did:peer:2` carries the mediator in its
`DIDCommMessaging` service `routingKeys`, so a single `SendAsync` on Alice's side discovers
the route, forward-wraps, and posts — she writes no routing code at all.

## What it demonstrates

- **Route discovery from the DID itself (FR-ROUTE-03)** — Bob's DID embeds
  `{"uri": <mediator inbox>, "routingKeys": [<mediator kid>], "accept": ["didcomm/v2"]}` in
  the conformant object-form `serviceEndpoint` (minted via the shared `PeerIdentityFactory`
  with a `DidCommServiceSpec`). Everything a sender needs travels inside the DID string.
- **Automatic forward wrapping (FR-ROUTE-02, task O)** — `SendAsync` without an endpoint
  override packs with `Forward = true`: the inner authcrypt envelope for Bob rides as the
  attachment of a Routing 2.0 `forward`, anoncrypted to the mediator's routing key.
- **Sending over HTTP (FR-TRN-01/04, task P)** — the transport router picks the HTTP
  transport by URI scheme; `SendResult` surfaces the endpoint used and the `202` status.
- **The mediator role (FR-ROUTE-05/07, task Q)** — `MapDidCommEndpoint` unpacks the envelope
  addressed to the mediator; `ForwardProcessor` then validates it is a forward, drops any
  `please_ack`, and yields the next hop plus the still-encrypted onward payload, which the
  mediator relays to Bob's inbox via `ITransportRouter`. The mediator never sees the content.
- **The SSRF guard and its explicit opt-in** — the mediator inbox URL comes from *Bob's* DID
  document, i.e. from a counterparty. `DidCommOptions.OutboundEndpointPolicy` therefore blocks
  private/loopback/metadata destinations by default — the sample shows the default policy
  refusing this loopback demo, then opts in narrowly with
  `OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")` (never by disabling the guard).
- **Recipient addressing (FR-CONSIST-04)** — Bob's agent sets `OwnIdentifiers`, so his unpack
  reports `RecipientAddressing = Addressed`.

Both web apps bind `http://127.0.0.1:0` (a dynamic port picked by the OS), and every await has
a hard timeout — the run is loopback-only and deterministic (FR-DX-02). The mediator's
DID → inbox delivery registry is populated directly; in production the coordinate-mediation
protocol does that enrollment (out of the messaging spec's scope).

## Run it

```bash
dotnet run --project samples/04-MediatorAgent
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~MediatorAgentSmokeTests
```

## Expected output (shape)

Ports and DIDs change every run; the structure is stable:

```
== Section 1 — Start the mediator (ASP.NET Core, dynamic port) ==
    Mediator inbox = http://127.0.0.1:<port>/didcomm

== Section 2 — Mint identities — Bob's did:peer advertises the mediator via routingKeys ==
  • Minted mediator = did:peer:2.Ez6LS…
  • Minted bob = did:peer:2.Ez6LS…
    Bob's routingKeys[0] = did:peer:2.Ez6LS…

== Section 3 — Start Bob's agent (his own receive endpoint) ==
    Bob inbox (known only to the mediator) = http://127.0.0.1:<port>/didcomm

== Section 4 — The outbound-endpoint guard — why the default refuses this demo ==
  • Default policy refused, as designed:
    note: Refusing to send to 'http://127.0.0.1:<port>/didcomm': host '127.0.0.1' resolves to a private or reserved address …

== Section 5 — Alice sends — route discovery, forward wrapping, HTTP, relay, delivery ==
  • [mediator] received an envelope (type = https://didcomm.org/routing/2.0/forward).
  • [mediator] forward unwrapped — next hop did:peer:2.…, onward payload <n> bytes.
  • [mediator] relayed to http://127.0.0.1:<port>/didcomm (HTTP 202).
    Endpoint used (the mediator, from Bob's DID) = http://127.0.0.1:<port>/didcomm
    Transport HTTP status = 202
  • [bob] unpacked the relayed envelope as if no mediator had been involved:
    [bob] Content = Routed through the mediator.
    [bob] Authenticated = True
    [bob] From == alice = True
    [bob] RecipientAddressing = Addressed
```

## Where to go next

- [`samples/05-WebSocketChat`](../05-WebSocketChat/) — the WebSocket transport: trust ping,
  discover-features, chat, and reconnect-after-drop.
- [`samples/02-Cookbook`](../02-Cookbook/) sections O/P/Q — the same routing pieces
  individually, in-process.
