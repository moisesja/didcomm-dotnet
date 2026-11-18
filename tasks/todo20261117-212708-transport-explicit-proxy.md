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
4. **HTTP socket-independent checks (as built after ADV-P1): in the transport's own 307 loop.**
   `HttpDidCommTransport` holds `_destinationCheck` (an `OutboundEndpointGuard` on the resolved
   policy) when a proxy is set and runs `Validate` on every hop before the resilience pipeline, so
   a refusal is neither retried nor counted by the breaker. The DI registration is a factory that
   passes the container's `TrustedProxySnapshot` and the inherited policy; the public constructor
   uses the transport's own policy or the default. The primary handler is always the guarded
   `SocketsHttpHandler`. First design (a `DelegatingHandler` wrapping the primary when proxied) was
   dropped: a host's `UseSocketsHttpHandler` tweak replaced the wrapper with an unguarded handler
   that used the process proxy (ADV-P1).
5. **WebSocket socket-independent checks (as built): the pre-send `Validate`, on every send when a
   proxy is set** (`_proxied`), also under a custom `Connect`. Covers pooled sockets, session
   sockets and reconnects (same endpoint). With a proxy the handshake handler follows no redirects
   (`AllowAutoRedirect = false`); direct, it keeps today's behaviour (each hop pinned). A separate
   per-handshake check was built and removed: once the pre-send check runs on every proxied send it
   adds nothing but a second lookup.
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
9. **Custom paths:** a host-supplied HTTP primary handler or WebSocket `Connect` delegate decides
   how and where it connects. Without `TrustedProxy` nothing changed. With it, the destination
   check still runs first (both transports, same semantics). `TrustedProxy` is still validated.
10. **Recorded limit, not a stop:** one named proxy per transport. PAC/WPAD and system proxy
    discovery are excluded by the owner's "explicit only"; per-destination proxy choice and proxy
    failover are not served (a `BypassList` can send some destinations direct, guarded). Raised in
    the hand-back for the owner's information.
11. **HTTP reads `TrustedProxy` once per container** (`TrustedProxySnapshot`), like the WebSocket
    transport, instead of at every handler rotation (QA finding).
12. **`OutboundEndpointGuard.Validate` classifies the wire host** (`Uri.IdnHost`); `AllowedHosts`
    matches either spelling (ADV-P2). Reachability in published versions checked against the 1.5.1
    package: no exposure beyond the advisory (direct paths are caught by the connect-time pin;
    elsewhere names already pass when `ResolveDnsNames` is off). The advisory's wording was made
    precise.

## Sweep: outbound HTTP clients, handlers and WebSockets in `src/`

| Site | Verdict |
|---|---|
| `Transports.Http/HttpDidCommBuilderExtensions.cs` — the `"didcomm"` primary `SocketsHttpHandler` | **Fix** (1–4, 7) |
| `Transports.Http/HttpDidCommTransport.cs` — `CreateClient("didcomm")` | **Fix**: the per-hop destination check through a proxy (4); validates `TrustedProxy` in its constructor. |
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
- [x] Implement: `TrustedProxy` on both options types with XML docs
- [x] Implement: `TrustedProxyRoute` (HTTP and WebSocket copies)
- [x] Implement: HTTP handler factory (`UseProxy`, proxy route, callback, hint) and the per-hop check in the transport
- [x] Implement: HTTP transport constructor validates `TrustedProxy`
- [x] Implement: WebSocket invoker (always built, `UseProxy`, route, `AllowAutoRedirect`, hint), `DefaultConnect` fallback removed, pre-send check on every proxied send
- [x] Core XML docs: `OutboundEndpointPolicy.ResolveDnsNames`, `OutboundEndpointGuard` summary
- [x] QA: permutations below; each new test fails on `origin/main` or with the fix mutated out
- [x] Adversarial pass (~30 min), findings recorded below with severity and disposition
- [x] Cookbook section GG (both setters executed; offline-safe stand-in proxy); DX gate negative control (GG without the WebSocket setter) fails
- [x] Docs: CHANGELOG (behaviour change first), PRD (new FR-TRN-13, §14.2 GG, §14.3, §14.4), architecture doc, root README (the packages' readme)
- [x] Package validation: additive change passes; negative control (`MaxRedirectHops` made internal) fails with CP0002
- [x] Gate: `dotnet build DidComm.sln -c Release /warnaserror` (0 warnings), `dotnet test … --no-build` (Core 2548, Mediator 4272 + 8 skipped, Interop 1701 + 3 skipped; 0 failed), `dotnet pack … --no-build` (9 packages)
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

## QA (two agents, merged)

- HTTP (`qa/proxy-http`, 53 cases) and WebSocket plus the drift guard (`qa/proxy-ws`): every new
  test fails on `origin/main` (the default-mode and closed-fallback tests, run against main's
  `src/`) or under at least one of ~30 scripted mutations; a slow-but-correct run (250 ms delays at
  the racy points) passes (L-065). No product defects.
- Findings acted on: HTTP re-read `TrustedProxy` at each handler rotation (now once per container,
  with a rotation test that fails otherwise); the bracket strip in `Normalize` was dead code
  (removed).
- Accepted: the destination check's name lookup is synchronous, like the facade's (an async form
  would change Core's public API); a proxy that hangs up without answering a CONNECT gets it again
  from the runtime (up to 16 times per attempt), outside the transport's retry budget.

## Adversarial pass (one pass, ~30 min; probes merged under `Transports/Proxy/Adversarial/`)

| Id | Severity | Finding | Disposition |
|---|---|---|---|
| ADV-P1 | Medium | With a proxy set, a host's `UseSocketsHttpHandler` tweak replaced the wrapped primary with an unguarded handler that used the process proxy | **Fixed**: the check moved into the transport's 307 loop; the primary is always the guarded handler (design 4) |
| ADV-P2 | Medium | Hosts that IDNA maps to an IP (`127。0。0。1`, full-width digits) passed the literal check and reached the proxy as literals | **Fixed** in Core: `Validate` classifies `IdnHost` (design 12) |
| ADV-P3 | Low | An HTTP connect-time refusal (direct, or a bypassed destination) is retried and counted by the shared breaker | **Declined**: unchanged from main on the direct path; #88 follow-up candidates (a)/(b). Probe skipped with that reason; docs say it |
| ADV-P4 | Low | Ambiguous numeric names (`127.0.0.08`) are names to .NET and reach the proxy as written | **Declined**: inside the documented limit (names are the proxy's to filter) |
| ADV-P5 | Info | .NET's own exception text names the proxy's host and port (never credentials) | Accepted |
| ADV-P6 | Info | `BypassList` entries are unanchored regexes | Accepted: bypassed destinations get the full guard; .NET semantics |
| ADV-P7 | Info → P2 (review) | A bypassed destination naming the proxy itself is dialled unfiltered | First accepted ("reaches only the trusted proxy"), which contradicted the documented promise that bypassed destinations get the full connect-time check. **Fixed in review round 1** (below) |
| ADV-P8 | Info | A proxy's 403 on CONNECT spends the WebSocket reconnect budget | Accepted: same as a peer's failure |
| ADV-P9 | Info | Synchronous lookups in the destination check ignore cancellation | Accepted: same as the facade's check; now once per hop / per send |

## Review

### Round 1 (PR #124, head `fae64a8`, changes requested: one P2)

Reviewer notes: `tasks/todo20261118-pr124-review.md` (main checkout). Comment:
https://github.com/moisesja/didcomm-dotnet/pull/124#issuecomment-6983407685.

- **P2: a bypassed destination must not inherit the proxy's private-network exemption.** The
  connect callbacks exempted any connection whose host:port matched the named proxy. With a
  bypass (`BypassList` or `BypassProxyOnLocal`) the handler connects to such a destination
  directly, so the exemption applied to a direct connection. My acceptance of ADV-P7 was wrong:
  it contradicted the options' own promise.
  - **Fix (both transports):** `TrustedProxyRoute.IsProxyConnection(context)` replaces the
    endpoint-only test in both callbacks. A connection is exempt only when its endpoint is the
    proxy's and the opening request is the handler's own `CONNECT` tunnel (not an extended
    CONNECT) or a request the proxy copy does not bypass (`WebProxy.IsBypassed`, the handler's own
    routing question, on the same instance). Every other connection gets the full guard. No public
    API or semantics change: this makes the documented behaviour true.
  - **Tests:** the probe that pinned the old outcome is now
    `Http_a_bypassed_destination_naming_the_proxy_is_refused_at_connect_time`, with
    `Ws_a_bypassed_destination_naming_the_proxy_is_refused_at_connect_time`, each for
    `BypassList` and `BypassProxyOnLocal`, `ResolveDnsNames = false`, no `AllowedHosts`: a
    connect-time refusal and zero connections. All four fail on `fae64a8` (HTTP: the send was
    accepted; WebSocket: no refusal). Controls with the same bypass config prove legitimate proxy
    traffic still works (an absolute-form POST, an https and a ws `CONNECT`, with the proxy named
    `localhost` and itself bypassed); they fail when the `CONNECT` special case is removed.
  - Docs: both options' remarks, the architecture section, the CHANGELOG bypass sentence.
