# Transports: WebSocket receive deadlines (epic #88)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Security hardening, tracked privately (see the private advisory). Slotted after #98 and before #138
(owner decision 2026-11-19). Branch `fix/websocket-receive-deadlines` off `origin/main` `ed25993`.

## What changes (behaviour)

The WebSocket receive loops read frames until a message is complete. Until now nothing bounded how
long that took, so a connection that never finished a message, or never sent one, stayed open with
what it had sent so far. After this change:

- **Server (`MapDidCommWebSocket`, both overloads, and `MapDidCommMediator`'s WebSocket endpoint):**
  each message must be finished within `DidCommReceiveOptions.MessageReceiveTimeout` of its first
  part being read; a new connection must finish its first message within the same time of being
  accepted. On expiry the endpoint sends a close frame with status 1008 (policy violation), drops
  what it had read, and aborts the connection if the peer does not answer the close within the
  existing close grace (5 s).
- **Client (`WebSocketDidCommTransport` return-route session sockets):** a pushed message must be
  finished within `WebSocketTransportOptions.MessageReceiveTimeout` of its first part being read;
  on expiry the session socket is closed with 1008 and the rest of its messages are discarded, as
  after an oversized message.
- **Idle is never timed after the first message:** return-route sessions and live-delivery sockets
  stay open while idle, on both sides. The client never times its wait for the next push.
- Default 60 s on both; `Timeout.InfiniteTimeSpan` turns it off.

## Owner decisions (binding; quoted)

- **"1"** (the owner's answer on the privately tracked item): private advisory, a separate small fix right after #98, terse public wording,
  shipped in 1.6.0. The fix shape shown: "a time limit per message, and a limit on how long a new
  connection may stay silent. It needs one new setting, which is a public API addition."
- **"small items as suggested"**: fold the small released items into this fix, and add a 1.6.0
  release note for the released log-value item main already fixed.
- **"orchestrate the private fix"** (2026-11-19).
- Standing: "I do not want to deviate from the specification EVER."

## Spec check

`docs/spec-files-v2.1-snapshot/transports.md` at `c78bcc4`, L72-79 (WebSockets): each message is
transmitted individually; trust comes from encryption or signing, not the socket; one-way; wss with
TLS 1.2+; the STOMP content type. Nothing about timeouts, idle connections or keep-alive. The
`return_route` extension (L21-29) says only that the connection may be held to return messages.
So the deadlines are local policy, like `max_receive_bytes`: they close a connection on which no
message has been completed in time, and refuse no message a peer finished. A legitimate sender is
affected only if it needs longer than the limit for one message (60 s is about 17 KB/s for a full
1 MiB message), or opens a connection and waits longer than the limit before its first message.

## Design (architect pass reconciled; implementation choices are ours, with reasons)

1. **One setting, `TimeSpan MessageReceiveTimeout`, same name and doc on `DidCommReceiveOptions`
   and `WebSocketTransportOptions`.** The owner was shown one setting for both limits. On a
   connection the endpoint accepted, the first message's time starts at accept (the silent limit);
   every other message is timed from its first part to its last frame. The client accepts no
   connections, so only a started message is timed there: a silent-connection limit on the client
   would close idle live-delivery sockets (coordinator constraint 2).
2. **Default 60 s** (ours): above our own client's `SendTimeout` (30 s), so a send our client
   reports as done is never cut by our server; the same as the pickup lease. `Timeout.InfiniteTimeSpan`
   turns it off. **Validation in the setter** with the `HttpClient.Timeout` rule (greater than zero
   and at most `int.MaxValue` ms, or infinite; else `ArgumentOutOfRangeException`), so every
   configuration path (code, `Configure`, binding) fails at once; a clamp would silently turn 0 into
   "close everything".
3. **Clock** (ours): the server uses the container's `TimeProvider` (as the HTTP response deadlines
   do); the client gets an internal constructor taking a `TimeProvider`, which `UseWebSocketTransport`
   passes from the container (the mediator client's pattern). No public API.
4. **Mechanism** (ours): an internal `ReceiveDeadline` in Core (one timer per message, not per read)
   completes a task; the loop awaits `Task.WhenAny(receive, expired)`. It never cancels the receive
   token: in .NET's managed WebSocket a cancelled receive aborts the socket, so no close frame could
   follow. A failing host `TimeProvider` ends the connection (fails closed, L-054).
5. **Expiry, server**: close code 1008 (RFC 6455 §7.4.1: a policy violation, the generic code when
   no other fits; 1009 is size, 1002 protocol, 1001 going away). The registry-aware loop stops the
   session writer first (no close overlaps a send), then sends the close and drains until the peer's
   close or the grace, then aborts. `CloseAsync` is never called with a receive outstanding (it starts
   a second receive). The rented buffer goes back to the pool only once every receive on it has
   completed.
6. **Close handshakes the sweep found unbounded** (ours, same loops, L-043): the callback overload's
   1009 close and the STOMP session's closes used `CloseAsync(..., CancellationToken.None)`, which
   waits for the peer's answer with no end. They get the same 5 s grace, then abort, as the
   registry-aware loop's 1009 close already has.
7. **Expiry, client**: `CloseOversizedAsync` generalised to a close-and-discard step (status and
   reason as parameters): the socket stops taking sends, the partial message is dropped, 1008 is sent
   under the send gate, the backstop is armed, and the loop keeps reading in discard mode until the
   peer's close or the backstop. No deadline while discarding. The consumer runs after `EndOfMessage`,
   outside the deadline.
8. **Mediator**: `ForMediatorEndpoint()` copies the setting; the mediator client builds no transport
   (it uses the host's `UseWebSocketTransport`), so the setting rides on it. Docs only there.

## Folded items (released, Low/Info)

| Item | Verdict |
|---|---|
| A stranger's message type on the `didcomm.handle` span tag reaches log lines where a host copies activity tags into scopes (control characters included) | **Fix**: `LogValues.MessageType` writes a type only when it is at most 256 URI characters, else a placeholder; used at every span site that tags a type (dispatcher, unpack, pack). Architecture doc sentence corrected. |
| Core unpack's in-memory form can be many times the envelope's size for some shapes | **Docs only** (ours): a bound would be a new option (stop) or a refusal of spec-valid JSON; a lazy body is a public API and perf change. The cost is per request and transient. `MaxReceiveBytes` docs and the architecture doc say it. |
| A front-loaded HTTP body is held under Kestrel's cumulative minimum body rate | **Docs**: raise `MinRequestBodyDataRate` for public endpoints; request-timeouts middleware only on the HTTP endpoint (it would also cut WebSocket sessions, including `MapDidCommMediator`'s). No new option. |
| No doc names Kestrel connection limits, data rates or the new deadlines | **Docs**: "Operating a mediator", `SECURITY.md`, the README package rows, the endpoint XML docs. |
| An `Expect: 100-continue` request aborted mid-body can surface as an Error "unhandled exception" | **Fix**: the three HTTP overloads catch a body-read failure and answer without processing, logged at Debug by type name: `BadHttpRequestException` → its own status code; a cancellation that is not the request's abort → 400; `IOException` → the connection is aborted (ours: a 400 let Kestrel log an Error while draining the broken body, 3 of 300 resets in a probe; with the abort 0 of 300; cost: one Kestrel Information line per such request). A genuine abort still propagates. |
| `NetDidServiceEndpointResolver` attributes another subject's document to the requested DID | **Fix**: one internal helper shared with `NetDidKeyService` (same refusal, same text), so the two adapters cannot drift. |
| 1.5.1 logged a stranger's type and `Message.Validate`'s quoted wire values at default config | **Release note only** (fixed on main by #95). |

## Sweep: WebSocket receive loops and request-body reads in `src/`

| Site | Verdict |
|---|---|
| `AspNetCore/DidCommEndpointRouteBuilderExtensions.cs` `ReceiveLoopWithDispatchAsync` (`MapDidCommWebSocket(pattern)`, `MapDidCommMediator`) | **Fix** (1-6) |
| same file, `ReceiveLoopAsync` (`MapDidCommWebSocket(pattern, onReceive)`) | **Fix** (1-6), incl. its unbounded 1009 close |
| `AspNetCore/StompServerSession.cs` `CloseAsync` (both loops under STOMP) | **Fix** (6) |
| `AspNetCore/WebSocketReturnRouteSession.cs` writer | Fine: sends only; stopped before any loop close; eviction close bounded by its backstop |
| `Transports.WebSocket/WebSocketDidCommTransport.cs` `ReceiveLoopAsync` (session sockets) | **Fix** (1, 3, 4, 7) |
| same file, `ReceiveOneMessageAsync` (STOMP `CONNECTED` read) | Fine: under the connect token (`ConnectTimeout`), 16 KiB cap |
| same file, pooled sockets | N/A: never read |
| `AspNetCore` `ReadCappedAsync` (three HTTP overloads) | Docs (Kestrel body rate) + the body-read failure fix |
| `Transports.Http/HttpDidCommTransport.cs` `ReadCappedAsync` (return-route response) | Fine: under `RequestTimeout`, capped |
| `Mediator.Client` `LiveDeliveryStream.ReceiveAsync` | N/A: reads the transport's consumer, not a socket |
| `Mediator`, `Mediator.AspNetCore`, `Extensions.DependencyInjection`, `Adapters.NetDid`, Core | N/A: no socket or body read of their own (`MapDidCommMediator` uses the endpoints above) |

No other place has the same shape, so the sweep raises no stop.

## Documented limits (not stops)

- A connection that has finished one message (even an invalid one, or a plaintext) is then untimed,
  by design: that is what keeps idle sessions open. What it holds is then bounded by the server's
  buffers: a peer that stops reading (ADV-1 below) can leave up to Kestrel's `MaxRequestBufferSize` (1 MiB by default) of its input buffered per
  connection. Lower `MaxRequestBufferSize` and bound the count with Kestrel's
  `MaxConcurrentUpgradedConnections` / `MaxConcurrentConnections` and a concurrency limiter
  partitioned by client. A code bound (server pings with a pong timeout) is an owner decision (ADV-1).
- Before a message's first part is returned (up to one 8 KiB read of a large frame), a later message
  is not yet "started"; what the peer holds then is that one read, in the connection's own buffer.
- Nothing remembers a slow peer across connections.

## Checklist

- [x] Worktree `orch-ws`, branch `fix/websocket-receive-deadlines`, submodules initialised
- [x] Private advisory drafted (draft only; never published, no CVE requested)
- [x] Architect pass; design reconciled above
- [x] Core: `ReceiveDeadline`, `LogValues.MessageType`, span sites, resolver binding helper
- [x] Server: the setting, both loops, close helper, STOMP closes, `ForMediatorEndpoint` copy, XML docs
- [x] Server: HTTP body-read failures
- [x] Client: the setting, internal clock ctor, DI factory, loop, close-and-discard, XML docs
- [x] QA permutations below; each new test fails on `origin/main` or with the fix mutated out (rebuild after revert, L-071)
- [x] Adversarial pass (~30 min), findings with severity and disposition
- [x] Cookbook section HH (both setters executed); DX gate negative control
- [ ] Docs: CHANGELOG (behaviour change first; 1.5.1 logging note), PRD, architecture doc, SECURITY.md, README package rows
- [ ] Package validation: additive; negative control
- [ ] Gate: build `/warnaserror`, test, pack; Live interop (normal + negative controls)
- [ ] PR opened, CI green

## QA permutations

Kestrel with raw `ClientWebSocket` / raw-frame peers for expiry semantics; the container's clock is
a manual one, moved only after the specific timer is armed (L-065 rule 3).

- Server, as a theory over the registry-aware endpoint, the callback endpoint and the mediator
  endpoint: a silent connection is closed with 1008 at the limit; a first message finished just inside
  is dispatched and the connection then stays open past many limits and dispatches a later message; a
  later message stalled mid-frames is closed at its deadline; a trickle with pings mixed in is still
  closed at the deadline; STOMP (no `CONNECT` closed; idle after `CONNECT` stays open); an idle
  return-route session and an idle live-delivery socket keep getting pushes past the limit; a
  stalled message on a session socket ends the session (no push after the close).
- Close handshakes: the callback endpoint's 1009, a STOMP protocol-error close, and the 1008 close,
  against a peer that never answers, end within the grace.
- Client: an idle session socket survives many limits with no timer; a stalled push gets a 1008 at
  the peer and later pushes are discarded; a push finished just inside reaches the consumer; a slow
  consumer is not cut off.
- Settings: defaults (60 s both); `Configure<DidCommReceiveOptions>` and `UseWebSocketTransport(o => …)`;
  `ForMediatorEndpoint` copies; invalid values throw; infinite disables (no timer).
- Folded: the type tag (control characters, over-long, a clean type), the resolver binding (a
  tampered and a self-consistent forged document), the body-read failures (each exception kind, and
  a genuine abort still propagating).

## Built (engineers in child worktrees, merged)

- Server (`fix/ws-server`): `WebSocketMessageDeadline` (per-connection timing both loops share),
  `MessageReceiveTimeoutLog` (per endpoint, first and every 1000th at Information), and
  `WebSocketClosing.EndAsync` (the one close step: `CloseOutputAsync`, await the outstanding receive,
  drain until the peer's close or the 5 s grace, abort; it returns the buffer to the pool only when
  no receive can still write into it). 17 mutations killed; slow-but-correct run passes.
- Client (`fix/ws-client`): `CloseAndDiscardAsync` (1009 unchanged, 1008 added), internal clock
  constructor, DI passes the container's clock. 19 mutations killed (M1–M19), two slow-but-correct
  runs pass, post-revert green.
- Folded (`fix/ws-folded`): `LogValues.MessageType` at all six type-tag sites; `ResolvedDidDocument`
  shared by both resolver adapters; `ReadBodyAsync` for the three HTTP overloads. Each mutation killed.
- Docs (`fix/ws-docs`): cookbook HH, PRD FR-TRN-14 / FR-API-06 / §14.2 HH / §14.3 / §14.4 / DD-15,
  architecture, SECURITY.md "Public endpoints", README rows, `MaxReceiveBytes` XML. DX gates pass
  with no allowlist change; negative control (HH without the client setter) fails.

## QA (independent, `fix/ws-qa`)

No product defect. 25 cases added: configuration binding (`"00:00:37"`, infinite, refused values) on
all three server endpoints and the client; a close mid-message; a later message finished just
inside; an idle `PickupClient` live stream outlasting many limits on the client's clock; a correct
`Expect: 100-continue` client. Four clock moves gained a `TimersFired` assertion (L-065). Flakiness:
5 runs and 3 loaded runs, 0 failures. New tests fail on `origin/main` (14) or under a named
mutation (M1–M7), post-revert green.

## Adversarial pass (one pass, ~30 min, `fix/ws-adv`)

| Id | Severity | Finding | Disposition |
|---|---|---|---|
| ADV-1 | Medium (pre-existing on main) | After its first message, a connection whose peer stops reading what the server sends can keep up to Kestrel's request buffer (1 MiB by default) for as long as it lives; no later message has started, so nothing is timed. | **Docs** (the "fixed buffers" claim corrected; lower `MaxRequestBufferSize`, bound connections). **Code: owner decision, tracked privately** (a server-side liveness check would also close peers that never read, our own pooled client sockets included). Test `After_one_message_a_peer_that_floods_pings_and_never_reads_keeps_its_connection_with_no_timer_running` shows the hold. |
| ADV-2 | Low (pre-existing on main) | An invalid frame (reserved opcode), or a drop without a close, made `ReceiveAsync` throw `WebSocketException` out of both server loops: an Error-level "unhandled exception" line any peer can cause. | **Fixed** (ours): both loops catch `WebSocketException`, end the connection, one Debug line by type and error code. `WebSocketPeerFailureLogTests` (the reserved-opcode rows fail on main every run; the drop-without-close rows are a smoke test of a race; repeated 12×, green). |
| ADV-3 | Info | A host timer whose `Dispose` throws would skip the server loop's cleanup. | **Fixed**: `ReceiveDeadline.Dispose` never throws (`ReceiveDeadlineTests`). |
| ADV-4 | Info | The type placeholder `(none)` could equal a real type. | **Fixed**: `(no type)` (a space is not a URI character). URI-legal text that looks like another field is accepted: it carries no control character and is bounded. |
| ADV-5 | Info | The `IOException` abort makes Kestrel write one Information line per such request. | Accepted (see the folded table). |

Held: pings/trickles/zero-length continuations inside a message; a peer answering 1008 with data
forever (drain stops at the grace); a ping flood during the first message (aborted at the grace);
close mid-message; STOMP split frames and stalled `SEND`; N connections renewing near-cap partials
(each ≤ 1 MiB for ≤ 60 s, buffers and timers released); expiry in CloseSent after an eviction; a
writer stuck mid-send; host stop racing an expiry; buffer return on every path; throwing or inline
host clocks; client gate contention, retire and dispose races; the HTTP front-loaded body
(unchanged, docs only); all six type-tag sites; the resolver binding against case, trailing
characters, DID URLs and look-alikes, on every resolution path.
