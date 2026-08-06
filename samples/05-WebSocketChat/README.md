# 05 — WebSocket Chat

Two independent agents talking DIDComm v2.1 over WebSocket (PRD §14.3 sample 05, tasks R/S,
plus the discover-features handshake from task T). Each agent is what a real deployment looks
like in miniature: its own keys, its own `did:peer:2`, its own ASP.NET Core host receiving on
`MapDidCommWebSocket`, and its own `WebSocketDidCommTransport` for outbound sends.

## What it demonstrates

- **The WebSocket receive endpoint (FR-TRN-09/10, task R)** — `app.MapDidCommWebSocket(...)`:
  one packed envelope per WebSocket *message*, fragmented frames reassembled before
  unpacking, and a one-way connection — handler replies are delivered out of band over the
  agent's own transport to the peer's endpoint, never written back on the inbound socket.
- **The WebSocket client transport** — `UseWebSocketTransport(...)` with connection pooling,
  and the SSRF outbound-endpoint guard's narrow loopback opt-in
  (`OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")`) that a local demo requires.
- **Trust-ping liveness (FR-PROTO-04, task S)** — `TrustPing.CreatePing` from Alice; Bob's
  registered `TrustPingHandler` auto-replies `ping-response` with `thid == ping.Id`.
- **Discover-features handshake (FR-PROTO-05)** — `DiscoverFeaturesClient.QueryFeaturesAsync`
  sends `queries` to Bob's endpoint and awaits his correlated `disclose`, which arrives at
  *Alice's* receive endpoint — a genuine two-endpoint round trip. Bob's disclosure includes
  the custom chat protocol, because the responder reflects its handler registry.
- **A scripted bidirectional chat** — Basic Message 2.0 as a custom `IProtocolHandler`
  (FR-PROTO-03), replies threaded via `thid`.
- **Reconnect after drop (FR-TRN-11)** — Bob's host is stopped; Alice's send exhausts the
  (shortened for the demo) exponential reconnect backoff and surfaces `TransportException`,
  with `Lifecycle` events (`Connected` / `Disconnected` / `SendFailed`) narrated as they fire.
  Bob restarts on the same port with the same DID and keys, and the next send redials and
  resumes the conversation. Library defaults are 1 s base / 30 s cap / 0.5 jitter, 5 attempts
  (DD-05).

Everything is loopback with dynamic ports, and every wait is a concrete signal (channel reads,
`TaskCompletionSource`) with a hard bound — no sleeps (FR-DX-02).

## Run it

```bash
dotnet run --project samples/05-WebSocketChat
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~WebSocketChatSmokeTests
```

## Expected output (shape)

Ports and DIDs change every run; the structure is stable:

```
== Section 1 — Start two agents over WebSocket (dynamic ports) ==
    alice = did:peer:2.Ez6LS… @ ws://127.0.0.1:<port>/didcomm
    bob = did:peer:2.Ez6LS… @ ws://127.0.0.1:<port>/didcomm

== Section 2 — Trust ping — is anybody out there? (FR-PROTO-04) ==
  • [alice transport] Connected → ws://127.0.0.1:<port>/didcomm
  • [alice] sent ping (id = …) as one binary WebSocket message (FR-TRN-09).
  • [bob] handler produced ping-response — sending it out of band.
    ping-response thid == ping.id = True
    ping-response authenticated = True

== Section 3 — Discover features — what can Bob speak? (FR-PROTO-05) ==
  • [bob] handler produced disclose — sending it out of band.
    Disclosed protocols = 5
    - protocol = https://didcomm.org/empty/1.0
    …
    - protocol = https://didcomm.org/basicmessage/2.0

== Section 4 — Chat — Basic Message 2.0 through a custom handler ==
  • [bob] received: "Hello Bob — one envelope per WebSocket message."
  • [alice] received: "Loud and clear, Alice."
  • [bob] received: "Envelope tour is green. Ready to ship?"
  • [alice] received: "Ship it."

== Section 5 — Reconnect after drop — lifecycle events + backoff (FR-TRN-11) ==
  • [bob] goes offline (host stopped, connections aborted).
  • [alice transport] Disconnected → ws://…
  • [alice transport] SendFailed → ws://…      (repeated per backoff attempt)
  • [alice] offline send refused after exhausting the reconnect budget:
    note: WebSocket send to 'ws://…' failed after exhausting the reconnect budget (2 attempt(s)).
  • [bob] comes back on the SAME port — same DID, same keys, fresh process.
  • [alice transport] Connected → ws://…
  • [bob] received: "Welcome back?"
  • [alice] received: "Back online — nothing lost but time."
```

## Where to go next

- [`samples/06-OutOfBand`](../06-OutOfBand/) — how two agents that have never met exchange
  the endpoint/DID information this sample wires up by hand.
- [`samples/02-Cookbook`](../02-Cookbook/) section R — the WebSocket receive path in
  isolation, including STOMP framing notes (FR-TRN-12).
