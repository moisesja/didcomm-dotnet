# 11 — Mediated Agent

A complete mediator and a wallet that uses it, in one process (PRD §14.3 sample 11). The
**mediator** is an ASP.NET Core app built with `AddMediator()` and `MapDidCommMediator()`.
**Bob** has a wallet with no endpoint of its own, like a phone app: he reads the mediator's
invitation, asks it to hold his messages, publishes a DID that routes through it, and picks his
messages up — over HTTP, then live over a WebSocket. **Alice** reaches Bob with one `SendAsync`
and no routing code. **Carol**, who is not on the operator's allow-list, is refused. The
**operator** inspects and changes what the mediator holds, then shuts it down.

## What it demonstrates

1. **Starting a mediator.** The settings are checked before deploying
   (`MediatorOptions.Validate()`), and two bad ones are refused by name. The mediator's
   `did:peer:2` is minted with `DidCommServiceBuilder.ForEndpoint(<its own URL>)`; the operator's
   collaborators are registered before `AddDidComm(b => b.AddMediator(...))` (every mediator
   registration is a `TryAdd`, so yours win): the identity (`MediatorIdentity`), the mediation
   store, an allow-list `IMediationPolicy`, the replay guard and a wrapped session registry. The
   endpoints go behind a rate limiter with one window per client address:
   `app.MapDidCommMediator("/didcomm").RequireRateLimiting("mediator")`, after
   `app.UseWebSockets()`. The invitation URL (`…/didcomm/oob?_oobid=mediator`) is printed.
2. **Enrolling (Coordinate Mediation).** Bob's wallet fetches the invitation, takes the
   mediator's DID from it, asks for mediation (`RequestMediationAsync` → a grant), mints a DID
   whose service entry is `DidCommServiceBuilder.ForMediation(grant)` (the mediator's DID as the
   `uri`), registers it (`UpdateRecipientsAsync`), and lists what is registered
   (`QueryRecipientsAsync` for one page, `QueryAllRecipientsAsync` for all of them).
3. **Sending through the mediator (FR-ROUTE-04).** Alice resolves Bob's DID: its service names
   the mediator's DID, not an address. `SendAsync` expands it into the mediator's own address,
   wraps one forward for the mediator and posts it: `202`. A `202` means *received*; the
   mediator stores the message just after, and the sample waits for that from the mediator's
   own metrics (`didcomm.forward.outcomes`, outcome `Queued`).
4. **Picking up over HTTP (Message Pickup).** `GetStatusAsync` says one message is waiting;
   `FetchAsync` (until `Drained`) returns it opened and acknowledges it before returning; the
   status is then zero.
5. **Live delivery over a WebSocket.** A second container with the WebSocket's three set-up
   calls — `UseWebSocketTransport(o => o.ReceiveInbound = true)`, `UseInboundDispatch()`,
   `AddMediatorClient()` — shows the live switch (`SetLiveDeliveryAsync`), then
   `ReceiveLiveAsync`: Alice sends again and Bob receives it (pushed, or collected by the stream
   if it was stored first — either way it arrives). The stream acknowledges an item when the next
   one is asked for, so the last one is acknowledged with `AckAsync(item.Id)`. The operator finds
   Bob's session (`Find`), and when Bob goes offline, the session's `Closed` token fires. The
   registry's open/close notifications are best effort; the token is what to rely on.
6. **A refusal.** Carol never signed up, so the allow-list policy denies her: `mediate-deny`,
   which the client reports as `null` — a deny is an answer, not an exception.
7. **The operator's console.** Reading the mediation store (`IMediationStore`: the grant, who
   owns a DID, the registered DIDs, the counts); unregistering a DID and dropping the messages
   waiting for it (`PurgeRecipientAsync`); putting a message in a mailbox and taking it back out
   (`DeliverAsync`, `RemoveAsync`); removing expired messages now (`PurgeExpiredAsync`);
   pre-provisioning a grant (`GrantAsync`) and revoking it; ending Bob's mediation with
   everything held for him (`RevokeAndPurgeAsync`); and the replay guard's and session
   registry's counters.

## Run it

```bash
dotnet run --project samples/11-MediatedAgent
```

Or through the smoke test, which is what CI runs:

```bash
dotnet test tests/DidComm.InteropTests --filter FullyQualifiedName~MediatedAgentSmokeTests
```

Everything runs on `127.0.0.1` with no network, and every wait is for a specific event with a
30-second guard (FR-DX-02). It takes about a second.

## Which packages

| Side | Packages |
| --- | --- |
| Wallet (`Wallet/`) | `DidComm.Mediator.Client` and a transport: `DidComm.Transports.Http`, plus `DidComm.Transports.WebSocket` for live delivery; `DidComm.Extensions.DependencyInjection` for `AddMediatorClient()` |
| Mediator | `DidComm.Mediator` (`AddMediator()` and the roles; it brings `DidComm.Mediator.Client` along) and `DidComm.Mediator.AspNetCore` (`MapDidCommMediator()`) |

The wallet code lives in its own class library, `Wallet/Wallet.csproj`, which references only
the wallet's packages. The smoke test checks that its assembly never references
`DidComm.Mediator`, `DidComm.Mediator.AspNetCore` or ASP.NET Core.

## Why the mediator uses a known port

Most samples let the server pick a port (`http://127.0.0.1:0`). The mediator can't: its DID
names its own address, and `AddMediator()` checks that DID (it resolves, names a plain
`http`/`https`/`ws`/`wss` address, and its key-agreement key is in the secrets) before the
server starts listening. So the sample asks the operating system for a free loopback port first
(`samples/_shared/LoopbackPort.cs`) and tries a new one if another program takes it in between.
In production none of this is needed: a mediator's public address is fixed and known before it
starts.

## Things to copy with care

- **Loopback and plain `http`/`ws`** are for the demo only. Every party opens `127.0.0.1` (on
  every port) in the outbound guard (`OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")`) and
  never turns it off; production uses `https`/`wss`. The wallet fetches the invitation URL through
  the same guard (`OutboundEndpointGuard.ConnectAsync`), follows no redirect, caps the body at
  64 KiB, and takes plain `http` only for a loopback address.
- **Enrolment is open by default** and grants never expire: a public mediator needs a policy
  like the allow-list here, or one that rate-limits enrolment, plus the rate limiter in front
  (one window per client address; on the WebSocket endpoint a permit is a connection, not a
  message).
- **The allow-list** compares DID subjects (a DID URL's path, query or fragment is ignored),
  ordinally, in one pass over the whole list, and holds at most 1024 peers. It gates only the
  first grant: taking a peer off the list must also end its grant
  (`MessagePickupQueue.RevokeAndPurgeAsync(peer)`).
- **The first peer to register a DID gets it**: the protocol asks for no proof that a peer
  controls the DID it registers, so a wallet registers its DID before handing it out.
- **A recipient offline longer than the mailbox age limit** (`MediatorOptions.Pickup.MaxMessageAge`)
  loses those messages, and nobody is told.
- **Clocks** of the mediator and its peers must agree within a minute (the default
  `ReplayProtectionOptions.ClockSkew`), or requests and answers are refused.
- **A wrapped session registry** hides the in-memory type from the mediator's own report of
  dropped notifications; the operator reads `DroppedNotifications` itself.

## Expected output (abridged)

Ports, DIDs and ids change every run; the structure is stable:

```
== Section 1 — Start the mediator (AddMediator + MapDidCommMediator) ==
  • Settings checked before deploying: OK
  • Invalid settings refused: Pickup.MaxMessageAge must be longer than MaxForwardDelay, … (Parameter 'Pickup.MaxMessageAge')
  • Invalid settings refused: ReturnRouteSessionOptions.MaxSessionsPerPeer must not exceed MaxSessions.
  • [mediator] listening on http://127.0.0.1:<port>/didcomm
  • [mediator] routing DID = did:peer:2.Ez6LS…
  • [mediator] invitation URL = http://127.0.0.1:<port>/didcomm/oob?_oobid=mediator

== Section 2 — Bob enrolls: the invitation, a grant, a DID that routes through the mediator ==
  • [bob] invitation from did:peer:2.Ez6LS…
  • [bob] granted: routing DIDs = 1
  • [bob] registered his public DID: Success
  • [bob] registered a second DID, for a shop: Success
  • [bob] all registered DIDs: 2

== Section 3 — Alice sends to Bob's public DID (the route goes through the mediator) ==
    Bob's service uri (the mediator's DID) = did:peer:2.Ez6LS…
    Transport HTTP status = 202
  • [mediator] stored Alice's message "Hello Bob" for pickup

== Section 4 — Bob picks up over HTTP: status, fetch, status again ==
  • [bob] status: 1 message(s)
  • [bob] fetched: Hello Bob
  • [bob] status after fetch: 0 message(s) (the fetch acknowledged what it opened)

== Section 5 — Bob goes live over a WebSocket: Alice sends again, Bob receives it ==
  • [bob] live delivery = True
  • [bob] live: Hello again, Bob
  • [bob] acknowledged the last message; status after ack: 0 message(s)
  • [mediator] sessions for Bob: 1
  • [mediator] Bob's session closed (its Closed token fired)

== Section 6 — Carol is refused: mediate-deny from the allow-list policy ==
  • [carol] mediate-deny: RequestMediationAsync returned null (a deny is an answer, not an exception)
  • [carol] mediate-deny: not on the allow-list

== Section 7 — The operator's console, then shutdown ==
  • [operator] Remove Bob's shop DID: Removed
  • [operator] Purged recipient: 1 message(s) that were waiting for that DID
  • [operator] Removed by operator: True
  • [operator] pre-provisioned a grant for Carol: True
  • [operator] Bob's mediation ended: Revoked = True, Purged = 1
```
