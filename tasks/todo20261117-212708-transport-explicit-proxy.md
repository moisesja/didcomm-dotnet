# Transports: direct connections by default, explicit trusted proxy (epic #88)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Security hardening, tracked privately (see the private advisory). Slotted by the owner after #97 and
before #99 (2026-11-17). Branch `fix/transport-explicit-proxy` off `origin/main` `7985d2d`.

## What changes (behaviour)

The HTTP and WebSocket transports guard each outbound connection: when the socket opens, the
outbound-endpoint guard checks the address it connects to. Until now both handlers kept .NET's
default proxy setting, so a process proxy (`HttpClient.DefaultProxy`: `HTTP_PROXY`, `HTTPS_PROXY`,
`ALL_PROXY`, the system proxy) was used whenever one was configured. The guard then checked the
connection to the proxy, not to the destination. After this change:

- **Default:** guarded connections are direct. No proxy from the environment is ever read or used.
  A deployment that today depends on such a proxy sees its sends fail until it names the proxy.
- **Opt-in:** `TrustedProxy` (a `System.Net.WebProxy`) on `HttpTransportOptions` and on
  `WebSocketTransportOptions`, with the same semantics on both. The connection to the named proxy is
  not checked against the policy (it is the host's explicit choice, so a proxy on `10.x` or loopback
  works). The checks that need no socket still run before every request: the scheme, IP-literal
  destinations, and (with `ResolveDnsNames`) a best-effort local lookup. Past the proxy the library
  cannot see where a connection ends up: **the host's proxy must refuse internal destinations**.

## Owner decisions (2026-11-17, binding)

- Fix shape = option 2: direct by default, environment/system proxies ignored, one opt-in public
  trusted-proxy setting whose docs state that egress filtering is then the proxy's job. The
  CHANGELOG leads with the behaviour change.
- Tracking = a private GitHub security advisory (draft). Public trail stays terse.
- Timing: now, before #99, so it lands before 1.6.0.
- Constraints: explicit only; same name and semantics on both options types; keep the
  socket-independent checks with a proxy set; the named proxy's address is never refused; DI and the
  mediator client pass it through; custom `Connect` / handler paths keep their behaviour.

## Design (architect pass reconciled; my calls with reasons)

1. **Setting = `public WebProxy? TrustedProxy { get; set; }` on both options types.**
   Reason: a `Uri` cannot serve an authenticating proxy (.NET ignores user info in a proxy address
   and prints it in exception text), and a second credentials property would be more public API.
   An `IWebProxy` can pick a proxy per destination, so the connect callback could not recognise
   "this socket is the named proxy" without failing open. `WebProxy` carries `Credentials` /
   `UseDefaultCredentials`, binds from configuration, and has one static `Address`.
2. **The value is validated and copied when read** (HTTP: transport constructor and each handler
   build; WebSocket: constructor). Rules: `Address` non-null, absolute, scheme in
   `http|https|socks4|socks4a|socks5`, non-empty host, explicit or default port > 0, no user info,
   path `/` with no query or fragment; a bad `BypassList` pattern is reported against `BypassList`.
   Throws `InvalidOperationException` naming the option, never echoing the address. The copy
   (`new WebProxy(address, bypassOnLocal, bypassList, credentials)`) means later changes to the
   host's instance cannot move a running transport. Reason: fail fast, no secrets in messages,
   repo convention for option errors.
3. **Connect callback, both transports.** No proxy: `UseProxy = false`. Proxy: `UseProxy = true`,
   `Proxy = copy`; an endpoint that matches the proxy's host and port (brackets, case and one
   trailing dot normalised; IPs compared as addresses) is dialled without the private-address
   filter; any other endpoint (a bypassed destination) gets the full guard as today, so a spelling
   mismatch fails closed. Internal `TrustedProxyRoute`, duplicated in each package. Reason: a
   linked source file has no precedent and would clash under the shared test project's IVT; the
   repo already duplicates small helpers between the transports (`AsWaitBudget`).
4. **HTTP socket-independent checks: a `DelegatingHandler` that wraps the primary handler only when
   a proxy is set** (`ProxiedDestinationCheck`, calls `OutboundEndpointGuard.Validate` on each
   request). It runs on every attempt and every 307 hop, is built in the same factory call as the
   proxy and the policy (consistent across handler rotation), and goes away with a host's own
   primary handler (custom path unchanged). A `TransportException` is neither retried nor counted by
   the circuit breaker. Reason: a check inside `HttpDidCommTransport` would need the inherited core
   policy, which only a new constructor could pass, and it would also run on custom handler paths.
5. **WebSocket socket-independent checks: the existing pre-send `Validate`** (once per send, before
   the reconnect pipeline; covers pooled sockets, session sockets and reconnects). With a proxy set
   the handshake handler does not follow redirects (`AllowAutoRedirect = false`), so no unchecked
   hop goes through the proxy. The direct path keeps today's redirect behaviour (each hop is pinned
   by the callback).
6. **Name lookups:** the proxied pre-check honours `ResolveDnsNames` as the facade's check already
   does. Documented: best effort (the proxy does its own lookup); where only the proxy can resolve
   names, set `ResolveDnsNames = false` and only IP literals are checked locally.
7. **A clear error after the upgrade:** on the direct path, a `SocketException` from the connect
   (refused, unreachable, name not found) is re-thrown as a `SocketException` with the same code and
   one added sentence: the transport connects directly, does not use `HTTP_PROXY` / `HTTPS_PROXY` /
   `ALL_PROXY` / the system proxy, and `…TransportOptions.TrustedProxy` names one. Exception types
   and `HttpRequestError` are unchanged. Nothing is read from the environment to word it. A timeout
   cannot carry it; the CHANGELOG names timeouts as a symptom.
8. **Latent branch closed (sweep):** the WebSocket `DefaultConnect` fallback
   `cws.ConnectAsync(endpoint, ct)` (no guard, default proxy) was reachable when `Connect` was set at
   construction and cleared later. The guarded invoker is now always built and the fallback removed.
   The custom `Connect` path never calls `DefaultConnect`, so it is unchanged.
9. **Custom paths:** `TrustedProxy` is ignored (still validated) under a custom WebSocket `Connect`
   and under a host-supplied HTTP primary handler. Documented.
10. **Recorded limit, not a stop:** one named proxy per transport. PAC/WPAD and system proxy
    discovery are excluded by the owner's "explicit only"; per-destination proxy choice and proxy
    failover are not served (a `BypassList` can send some destinations direct, guarded). Raised in
    the hand-back for the owner's information.

## Sweep: outbound HTTP clients, handlers and WebSockets in `src/`

| Site | Verdict |
|---|---|
| `Transports.Http/HttpDidCommBuilderExtensions.cs` — the `"didcomm"` primary `SocketsHttpHandler` | **Fix** (1–4, 7) |
| `Transports.Http/HttpDidCommTransport.cs` — `CreateClient("didcomm")` | Covered by the row above; validates `TrustedProxy` in its constructor. A host-built factory is the custom path. |
| `Transports.WebSocket/WebSocketDidCommTransport.cs` — `_pinnedInvoker` `SocketsHttpHandler` | **Fix** (1–3, 5, 7, 8) |
| `Transports.WebSocket/WebSocketDidCommTransport.cs` — `new ClientWebSocket()` (pooled and session sockets) | Fine: connects through the invoker, whose proxy settings apply. A `WebSocketFactory` socket with its own `Options.Proxy` is refused by .NET when an invoker is used (unchanged). |
| `Transports.WebSocket/WebSocketDidCommTransport.cs` — `DefaultConnect` fallback without the invoker | **Fix** (8) |
| `Core/Transports/OutboundEndpointGuard.cs` — raw `Socket` connect | Fine: no proxy concept |
| `Core/Facade/DidCommClient*.cs` — `Validate` | N/A: no connection |
| `Core/Protocols/Trace/TraceObserver.cs`, `Core/Protocols/OutOfBand/OutOfBand.cs` | N/A: decisions only, no request |
| `Mediator.Client` | N/A: builds no transport; sends through the host's `UseHttpTransport` / `UseWebSocketTransport` registrations, so the setting rides on those |
| `Mediator`, `AspNetCore`, `Mediator.AspNetCore` | N/A: receive side / stored delivery, no outbound client |
| `Extensions.DependencyInjection`, `Adapters.NetDid` | N/A: no outbound HTTP |

No library package outside the two transports makes outbound connections, so the sweep raises no stop.

## Checklist

- [x] Worktree `orch-proxy`, branch `fix/transport-explicit-proxy`, submodules initialised
- [x] Private advisory drafted (draft only; never published, no CVE requested)
- [x] Architect pass; design reconciled above
- [x] Lesson L-066 (first-principles owner decisions)
- [ ] Implement: `TrustedProxy` on both options types with XML docs
- [ ] Implement: `TrustedProxyRoute` (HTTP and WebSocket copies)
- [ ] Implement: HTTP handler factory (`UseProxy`, proxy route, callback, `ProxiedDestinationCheck`, hint)
- [ ] Implement: HTTP transport constructor validates `TrustedProxy`
- [ ] Implement: WebSocket invoker (always built, `UseProxy`, route, `AllowAutoRedirect`, hint), `DefaultConnect` fallback removed
- [ ] Core XML docs: `OutboundEndpointPolicy.ResolveDnsNames`, `OutboundEndpointGuard` summary
- [ ] QA: permutations below; each new test fails on `origin/main` or with the fix mutated out
- [ ] Adversarial pass (~30 min), findings recorded below with severity and disposition
- [ ] Cookbook section GG (both setters executed; offline-safe stand-in proxy)
- [ ] Docs: CHANGELOG (behaviour change first), PRD (new FR-TRN-13, §14.2 GG, §14.3, §14.4), architecture doc, root README (the packages' readme)
- [ ] Package validation: additive change passes; negative control fails
- [ ] Gate: `dotnet build DidComm.sln -c Release /warnaserror`, `dotnet test … --no-build`, `dotnet pack … --no-build`
- [ ] PR opened, CI green

## QA permutations

Process-wide cases run in the existing non-parallel `DefaultProxyCollection`, with a loopback stand-in
proxy that records each request line (tests wait on the recorded line, real time only as a hang
guard, L-065).

- Default, per transport and scheme (http, https, ws, wss) × {`HttpClient.DefaultProxy`,
  `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`}: the stand-in receives no connection and the guard
  refuses a forbidden target. On `origin/main` the stand-in is reached (the proxy is allowlisted in
  the test so the old code is not stopped by the guard first). Environment cases rebuild the
  process proxy and assert it is in effect before the send, so they cannot pass vacuously.
- With `TrustedProxy`: http goes through the stand-in (it answers 202), https/ws/wss send `CONNECT`;
  literal private, metadata and IPv6-embedded destinations are refused before the proxy, also after
  a 307; a refusal takes one attempt and does not trip the breaker; the stand-in on loopback is
  allowed under the default policy; proxy recognition across `LOCALHOST.`, `127.0.0.1`, `[::1]`;
  bypassed destinations get the guard; WebSocket handshake redirects are not followed; WebSocket
  reconnects and session sockets go through the proxy; the copy ignores later mutation.
- Validation rules; the hint on direct failures (same exception types and codes) and its absence
  when proxied.
- Custom paths unchanged: WebSocket `Connect` delegate, HTTP host primary handler.

## Adversarial pass

(filled in after QA)

## Review

(filled in at each review round)
