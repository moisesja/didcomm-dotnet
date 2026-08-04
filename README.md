# didcomm-dotnet

[![NuGet](https://img.shields.io/nuget/v/DidComm.Core.svg)](https://www.nuget.org/packages/DidComm.Core)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/phase%206-in%20progress-yellow.svg)](#roadmap)
[![Spec](https://img.shields.io/badge/spec-DIDComm%20v2.1-informational.svg)](https://identity.foundation/didcomm-messaging/spec/v2.1)

A .NET 10 implementation of **DIDComm Messaging v2.1** — the [DIF](https://identity.foundation/) protocol for confidential, integrity-protected, optionally non-repudiable messaging between parties identified by Decentralized Identifiers (DIDs).

> DIDComm gives two parties a way to exchange messages whose trust derives from control of DIDs rather than from CAs, IdPs, or transport-level TLS. It is message-based, asynchronous, simplex, and transport-agnostic.

DID resolution is delegated to the sibling library [**NetDid**](https://github.com/moisesja/net-did) — didcomm-dotnet implements only the messaging layer (message model, JOSE envelopes, routing, threading, OOB, and the protocols defined directly in the spec).

## Quickstart

Two identities, an authcrypt round-trip, and the metadata the envelope actually proved — no network, no key management setup.

```bash
dotnet add package DidComm.Core
dotnet add package DidComm.Extensions.DependencyInjection
```

```csharp
// An in-memory secrets resolver stands in for your KMS/HSM. Nothing here touches a network:
// did:peer:2 encodes its own DID document, so resolution is a local parse.
var secrets = new InMemorySecretsResolver();
var services = new ServiceCollection();
services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
await using var sp = services.BuildServiceProvider();

var alice = await PeerIdentityFactory.CreateAsync(
    sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
var bob = await PeerIdentityFactory.CreateAsync(
    sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
foreach (var key in alice.Privates.Concat(bob.Privates))
    secrets.Add(key);

var client = sp.GetRequiredService<DidCommClient>();
var message = new MessageBuilder()
    .WithType("https://example.com/protocols/hello/1.0/greeting")
    .WithFrom(alice.Did)
    .WithTo(bob.Did)
    .WithBody(new JsonObject { ["text"] = "Hello, Bob." })
    .Build();

var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions([bob.Did], From: alice.Did));
var received = await client.UnpackAsync(packed.Message);

Console.WriteLine($"body          : {received.Message.Body}");
Console.WriteLine($"authenticated : {received.Authenticated}");   // the sender proved control of Alice's key
Console.WriteLine($"encrypted     : {received.Encrypted}");
Console.WriteLine($"sender kid    : {received.SenderKid}");
Console.WriteLine($"addressed to  : {received.RecipientAddressing}");
```

```
body          : {
  "text": "Hello, Bob."
}
authenticated : True
encrypted     : True
sender kid    : did:peer:2.Ez6LSi8yoKGbLoTZBB9FMrmpvtaaGcsNHEv4Cj6xAXwAdYbe7.Vz6MkemzLKfrkRqb1NRU5EA4xwGqz1xaCG2KHNAqArWtYeQUV#key-1
addressed to  : Addressed
```

`authenticated` is the line that matters: it is evidence from ECDH-1PU that the sender controls Alice's key, not a claim the message makes about itself. `addressed to` is the advisory FR-CONSIST-04 check — act on `NotAddressed`, but never treat `Addressed` as authorization, since `to` is written by the sender.

The 24 lines above are the body of [`samples/01-Quickstart`](samples/01-Quickstart/)'s `RunAsync`, minus its closing `return` — a compiled project whose smoke test runs on every build, so the snippet cannot drift from code that works. Copy [`Program.cs`](samples/01-Quickstart/Program.cs) instead of the block above if you want the `using` directives with it. The two DIDs are generated per run, so only the `sender kid` line differs from what you see here. `InMemorySecretsResolver` and `PeerIdentityFactory` are sample/test helpers, deliberately kept out of `DidComm.Core` (DD-02); in a real agent those are your KMS and your own identity provisioning.

## Project status

**Phases 0–5 complete; Phase 6 in progress.** The library has a public Pack / Unpack / Send
surface (`DidCommClient`), DID resolution via [NetDid 3.0.0](https://github.com/moisesja/net-did),
a consumer-supplied `ISecretsResolver` contract, the three protective envelope
shapes (signed / anoncrypt / authcrypt) and their legal compositions, addressing
consistency including same-document key provenance, DID rotation via `from_prior`,
Routing Protocol 2.0 (sender forward wrapping + mediator relay + rewrapping), and the
HTTPS / WebSocket transports plus the ASP.NET Core receive endpoints. Phase 6 has landed
threading / ACKs / i18n / profiles and **all the spec's built-in protocols** — Trust Ping,
Discover Features, Empty, Report Problem, Trace (off by default), and Out-of-Band 2.0 — plus
the NuGet release pipeline. The DIDComm v2.1 Appendix C inbound interop gate passes for every
vendored vector.

**What Phase 6 still owes** (see [Roadmap](#roadmap)): the live cross-implementation interop
harness, the remaining sample applications and the public-API coverage gate, and the
observability/benchmark NFRs.

Shipped highlights:

- **Public facade** — `services.AddDidComm(b => …)` →
  `Pack{Plaintext,Signed,Encrypted}Async` + `UnpackAsync` + `SendAsync`.
  Auto-detects envelope shape on unpack, enforces FR-API-05 (`expires_time`)
  and FR-API-06 (`MaxReceiveBytes`), surfaces FR-API-04 metadata on every
  unpack.
- **DID resolution** via the `NetDidKeyService` adapter over net-did.
  `UseNetDidResolver()` wires `did:key` and `did:peer`; its `configure` callback takes any other
  net-did method you add. JWK + Multikey verification methods are both supported, and
  `did:web` is deliberately refused at every entry point with
  `UnsupportedDidMethodException` (DD-08).
- **Same-document key provenance** — a sender/signer is never authorized against a different
  DID-document version than the one that supplied the key the JOSE layer verified with, and the
  exact evidence (kid / DID / controller / relationship / thumbprint) is surfaced on
  `UnpackResult` as `VerifiedKeyBinding`.
- **DID rotation** — `Message.FromPrior` carries a JWT validated against the
  prior DID's `authentication` relationship; FR-ROT-03 enforced (rotation
  messages MUST be encrypted).
- **Routing & mediation** — `PackEncryptedAsync(... Forward: true)` resolves
  the recipient's `DIDCommMessaging` service (object / array-of-objects /
  opt-in DD-10 bare-string), implicitly prepends mediator `keyAgreement`
  keys (FR-ROUTE-04), reverse-order anoncrypt-wraps a `forward` per routing
  key, and surfaces the transport URI on `PackEncryptedResult.ServiceEndpoint`.
  `ForwardProcessor` handles the mediator side with optional rewrapping
  (FR-ROUTE-05/06).
- **Transports** — `DidCommClient.SendAsync(...)` packs and dispatches via an
  `ITransportRouter`. `DidComm.Transports.Http` ships a Polly-backed HTTPS
  sender (FR-TRN-04..08); `DidComm.Transports.WebSocket` ships a one-message-
  per-envelope WS sender with connection pool + exponential reconnect
  (FR-TRN-09..11). `DidComm.AspNetCore` provides
  `MapDidCommEndpoint` / `MapDidCommWebSocket` / `MapDidCommOobEndpoint` minimal-API
  extensions — `Content-Type` validation ⇒ 415, `MaxReceiveBytes` ⇒ 413 / 1009
  (FR-TRN-07 + FR-API-06).
- **Built-in protocols** — every protocol the spec defines directly:
  Trust Ping 2.0, Discover Features 2.0, Empty 1.0, Report Problem 2.0
  (taxonomy + interpolation + escalation + cascade guard), Trace 2.0
  (off by default), and Out-of-Band 2.0 (`OutOfBand.CreateInvitation` /
  `ToUrl` / `FromUrl`, short-form `?_oobid=` retrieval, `web_redirect`).
- **Inbound observation** — `IProtocolObserver` lets an application watch inbound traffic whose
  PIURI a built-in handler owns, without replacing that handler. Delivered off the dispatch path
  through a bounded queue, from an immutable verified snapshot, so an observer can neither gate
  replies nor be handed content the unpack never verified.

The unit and interop suites build and run under `/warnaserror` on Linux and Windows on every PR
([`ci.yml`](.github/workflows/ci.yml)). See [CHANGELOG.md](CHANGELOG.md) for the per-release log,
the [PRD](docs/didcomm-dotnet_PRD.md) for normative requirements (the six-phase plan is §12), and
the [roadmap](#roadmap) below for status at a glance.

## Install

didcomm-dotnet ships as focused NuGet packages (hybrid packaging, DD-03) — the core plus one
package per transport and integration. The badge at the top of this file shows the current version.

| Package | What it gives you |
|---|---|
| `DidComm.Core` | Message model, JWE/JWS envelopes, pack/unpack, routing, rotation, threading, and the built-in protocols (Trust Ping, Discover Features, Empty, Report Problem, Trace, Out-of-Band) |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` wiring with net-did resolution |
| `DidComm.AspNetCore` | `MapDidCommEndpoint` / `MapDidCommWebSocket` / `MapDidCommOobEndpoint` receive endpoints |
| `DidComm.Transports.Http`, `DidComm.Transports.WebSocket` | Sender-side transport bindings |
| `DidComm.Adapters.NetDid` | Optional bridge from a NetDid key store to `ISecretsResolver` |

```bash
dotnet add package DidComm.Core
dotnet add package DidComm.Extensions.DependencyInjection
```

> Releases are tag-driven: pushing a `vMAJOR.MINOR.PATCH` tag runs
> [`.github/workflows/release.yml`](.github/workflows/release.yml), which packs every package
> (with symbols + SourceLink) and pushes to NuGet.org behind a reviewer-approved environment gate.
> Maintainers: see [RELEASING.md](RELEASING.md) for the runbook.

## What "spec-complete" means

didcomm-dotnet implements the messaging layer of [DIDComm Messaging v2.1](https://identity.foundation/didcomm-messaging/spec/v2.1):

| Area | Scope |
|---|---|
| **Envelopes** | Plaintext, Signed (JWS), Anoncrypt (JWE/ECDH-ES+A256KW), Authcrypt (JWE/ECDH-1PU+A256KW), and all legal compositions |
| **Signing algorithms** | EdDSA (Ed25519), ES256 (P-256), ES256K (secp256k1) |
| **Key-agreement curves** | X25519, P-256, P-384 (required); P-521 (optional) |
| **Content encryption** | A256CBC-HS512 (required), A256GCM (recommended), XC20P (optional) |
| **DID resolution** | Delegated to NetDid. Wired by default: `did:key`, `did:peer`. Any other net-did method (e.g. `did:webvh`) plugs into `UseNetDidResolver(b => …)` |
| **Routing & mediation** | Forward protocol, mediator relay, rewrapping mode |
| **Transports** | HTTPS (send + ASP.NET Core receive), WebSocket |
| **Protocols** | Trust Ping 2.0, Discover Features 2.0, Report Problem 2.0, Out-of-Band 2.0, Empty 1.0, Trace 2.0 |
| **Cross-message** | Threading, ACK loop-guards, DID rotation (`from_prior`), `i18n`/`accept-lang`, profile negotiation |

> **`did:web` is explicitly NOT supported.** This is a deliberate security policy (DD-08), not a messaging-conformance gap. See PRD §1.1 and §15.

The conformance gate is the spec's own Appendix C test vectors — which pass today — plus a live
cross-implementation harness round-tripping against the SICPA reference implementations in Python,
JVM, and Rust. **That live harness is not built yet** (FR-IX-03/04/05/06/08); every fixture in the
suite is currently `source: spec-v2.1`.

## Package map

| Package | Responsibility |
|---|---|
| `DidComm.Core` | Message model; JWE/JWS envelopes; pack/unpack/send facade; `IDidKeyService` / `IDidKeyBindingService` + `NetDidKeyService` resolver adapter; `ISecretsResolver` contract; `from_prior` rotation; Routing Protocol 2.0 (forward wrapping + mediator processing + service-endpoint resolution); the built-in protocols and the dispatcher/observer seam; transport abstractions (`IDidCommTransport`, `ITransportRouter`); typed exception hierarchy |
| `DidComm.Extensions.DependencyInjection` | `IServiceCollection.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver<T>().UseHttpTransport().UseWebSocketTransport().AddBuiltInProtocols().Configure(...))`; FR-SEC-02 fail-fast on missing registrations |
| `DidComm.Adapters.NetDid` | Optional bridge from `NetDid.Core.IKeyStore` → `ISecretsResolver` (FR-SEC-04, SHOULD); documented scope (sign-side surface only — see class XML doc) |
| `DidComm.Transports.Http` | HTTPS sender (FR-TRN-04..08): `IHttpClientFactory`-backed POST, manual 307 follow + 301/308 refusal, Polly retry / circuit-breaker / timeout |
| `DidComm.Transports.WebSocket` | WebSocket sender (FR-TRN-09..11): one binary message per packed envelope, per-endpoint pool, Polly exponential reconnect, lifecycle events |
| `DidComm.AspNetCore` | Minimal-API extensions: `MapDidCommEndpoint` (HTTP receive, FR-TRN-07), `MapDidCommWebSocket` (WS receive with frame reassembly, FR-TRN-09/10), `MapDidCommOobEndpoint` (short-form OOB retrieval); `MaxReceiveBytes` ⇒ 413 / 1009 (FR-API-06) |
| `DidComm.TestSupport` *(not shipped)* | `InMemorySecretsResolver` for tests and samples — deliberately kept out of `DidComm.Core` per DD-02 |

The spec's built-in protocols live **inside `DidComm.Core`**, not in separate `DidComm.Protocols.*`
packages: they are part of what makes an agent conformant, so splitting them out would only let a
consumer assemble a non-conformant install.

### Naming convention

The repository is `didcomm-dotnet` (kebab-case, matching `net-did` and `zcap-dotnet`). .NET assemblies, NuGet packages, and namespaces use the PascalCase root `DidComm` (e.g. `DidComm.Core`, `DidComm.Transports.Http`). The acronym "DIDComm" from the spec is rendered `DidComm` in code per .NET capitalization guidelines for 3+ letter acronyms (matching `NetDid`). Prose references to the protocol keep the spec spelling "DIDComm".

## Public API at a glance

```csharp
// The facade — DidComm.Facade.DidCommClient
public sealed class DidCommClient
{
    public Task<string>              PackPlaintextAsync(Message m,                              CancellationToken ct = default);
    public Task<string>              PackSignedAsync(Message m, string signFrom,                CancellationToken ct = default);
    public Task<PackEncryptedResult> PackEncryptedAsync(Message m, PackEncryptedOptions opts,   CancellationToken ct = default);
    public Task<SendResult>          SendAsync(Message m, SendOptions opts,                     CancellationToken ct = default);
    public Task<UnpackResult>        UnpackAsync(string packed,                                 CancellationToken ct = default);
}

// What an unpack proved (FR-API-04) — DidComm.Facade.UnpackResult (selected members)
public sealed record UnpackResult
{
    public bool                Encrypted { get; }           // confidentiality
    public bool                Authenticated { get; }       // authcrypt proved the sender's key
    public bool                NonRepudiation { get; }      // a signature a third party can check
    public VerifiedKeyBinding? SenderKeyBinding { get; }    // the exact key + document that verified
    public RecipientAddressing RecipientAddressing { get; } // FR-CONSIST-04 advisory: are we in 'to'?
}

// DID resolution adapter — DidComm.Resolution
public interface IDidKeyService
{
    Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship rel, CancellationToken ct = default);
    Task<bool>               IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship rel, CancellationToken ct = default);
    void                     RejectUnsupportedMethod(string did);  // throws UnsupportedDidMethodException for did:web
}

// Since 1.4.0 the unpack path takes its evidence from this capability instead. A key service that
// also implements it is used for sender/signer/recipient provenance, and IsKeyAuthorizedAsync is
// then no longer called during unpack — exactly one resolution per key, and every field of the
// returned binding comes from that one document. Decorators MUST forward the interface or they
// silently fall back to the legacy path.
public interface IDidKeyBindingService
{
    Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(string kid, VerificationRelationship relationship, CancellationToken ct = default);
}

// Consumer-supplied secrets (KMS / HSM / Vault) — DidComm.Secrets
public interface ISecretsResolver
{
    Task<Jwk?>                  FindAsync(string kid,                       CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids,  CancellationToken ct = default);
}

// Read-only inbound observation — DidComm.Protocols
public interface IProtocolObserver
{
    string? ProtocolUriFilter { get; }                       // null observes everything
    Task    OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct);
}

// Transport binding — DidComm.Transports
public interface IDidCommTransport
{
    string                Scheme { get; }
    bool                  CanHandle(Uri endpoint);
    Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct);
}

// DI wiring — DidComm.Extensions.DependencyInjection
services.AddDidComm(b =>
{
    b.UseNetDidResolver();                     // did:key + did:peer via net-did
    b.UseSecretsResolver<MyVaultResolver>();   // FR-SEC-02 fail-fast if absent
    b.UseHttpTransport();                      // FR-TRN-04..08 (Polly resilience)
    b.UseWebSocketTransport();                 // FR-TRN-09..11
    b.AddBuiltInProtocols();                   // Trust Ping, Discover Features, Empty, Report Problem
    b.Configure(o =>
    {
        o.MaxReceiveBytes = 1 * 1024 * 1024;
        o.OwnIdentifiers = ["did:peer:2…"];    // makes the FR-CONSIST-04 warning reachable
    });
});
var client = sp.GetRequiredService<DidCommClient>();

// Server side — DidComm.AspNetCore
app.MapDidCommEndpoint("/didcomm",      async (unpacked, ct) => { /* host dispatch */ });
app.MapDidCommWebSocket("/ws/didcomm",  async (unpacked, ct) => { /* host dispatch */ });
```

## Samples

| Sample | What it shows |
|---|---|
| [`samples/01-Quickstart`](samples/01-Quickstart/) | The quickstart above: two identities, authcrypt round-trip, unpack metadata |
| [`samples/02-Cookbook`](samples/02-Cookbook/) | One narrated section per API task (all 28 PRD §14.2 letters) — run `dotnet run --project samples/02-Cookbook` |
| [`samples/03-EnvelopesAndMessages`](samples/03-EnvelopesAndMessages/) | Every envelope composition and content-encryption alg, multi-recipient, attachments, threading + ACKs, DID rotation |
| [`samples/04-MediatorAgent`](samples/04-MediatorAgent/) | ASP.NET Core mediator + Routing 2.0 relay; Alice→Mediator→Bob over HTTP with DID-published routingKeys |
| [`samples/05-WebSocketChat`](samples/05-WebSocketChat/) | Two agents over WebSocket: trust-ping, discover-features, chat, reconnect after drop |
| [`samples/06-OutOfBand`](samples/06-OutOfBand/) | OOB invitation → URL/QR → decode → `pthid`-correlated response |
| [`samples/07-ProblemsAndProtocols`](samples/07-ProblemsAndProtocols/) | Problem-report taxonomy, escalation, cascade guard, empty-ACK, custom `lets_do_lunch` handler |
| [`samples/08-Extensibility`](samples/08-Extensibility/) | Custom (mock-KMS) secrets resolver, the net-did `IKeyStore` bridge, custom transport |
| [`samples/09-NetDidIntegration`](samples/09-NetDidIntegration/) | did:key / did:peer minting, Ed25519→X25519 derivation, the deliberate `did:web` rejection |
| [`samples/10-ProfilesAndI18n`](samples/10-ProfilesAndI18n/) | `accept` profile negotiation and `lang`/`accept-lang` (the spec's chess example) |

Every sample builds and runs in CI via an in-process smoke test, and the FR-DX-01 coverage gate
(`tests/DidComm.InteropTests/DxCoverage/`) fails the build if any public member of the shipped
packages is not demonstrated by at least one sample — currently **0 undemonstrated members**.

## Specifications

| Specification | Version | Reference |
|---|---|---|
| **DIDComm Messaging** | v2.1 (Editor's Draft, WG Approved) | [identity.foundation/didcomm-messaging/spec/v2.1](https://identity.foundation/didcomm-messaging/spec/v2.1) |
| **JSON Web Encryption (JWE)** | RFC 7516 | [rfc7516](https://www.rfc-editor.org/rfc/rfc7516) |
| **JSON Web Signature (JWS)** | RFC 7515 | [rfc7515](https://www.rfc-editor.org/rfc/rfc7515) |
| **JSON Web Algorithms (JWA)** | RFC 7518 | [rfc7518](https://www.rfc-editor.org/rfc/rfc7518) |
| **ECDH-1PU** | draft-madden-jose-ecdh-1pu-04 | [draft-madden-jose-ecdh-1pu](https://datatracker.ietf.org/doc/draft-madden-jose-ecdh-1pu) |
| **W3C DIDs** | v1.0 | [w3.org/TR/did-core](https://www.w3.org/TR/did-core/) |

## Roadmap

didcomm-dotnet is delivered in six phases (see [PRD §12](docs/didcomm-dotnet_PRD.md) for the full plan, exit criteria, and per-phase agent kickoff prompts):

| Phase | Scope | Status |
|---|---|---|
| **0** | Repository & JOSE-composition substrate (`ICryptoProvider`, AEAD, AES-KW, 1PU KDF wrapper, JWK shim, fixtures harness) | ✅ Complete |
| **1** | Message model, attachments, MTURI parsing, consistency-check functions | ✅ Complete |
| **2** | Envelopes: Signed, Anoncrypt, Authcrypt — Appendix C interop gate | ✅ Complete |
| **3** | Pack/Unpack facade, NetDid integration, secrets, DID rotation | ✅ Complete |
| **4** | Routing & mediation (Forward protocol, mediator-as-DID-endpoint, rewrapping) | ✅ Complete |
| **5** | Transports (HTTPS + ASP.NET Core receive, WebSocket) | ✅ Complete |
| **6** | Protocols, cross-message concerns, live interop, samples, release | ✅ Complete |

Phase 6, in detail:

| Item | Status |
|---|---|
| Built-in protocols, OOB 2.0, threading/ACKs, profiles & i18n | ✅ Done |
| NuGet release pipeline (tag-driven, gated) | ✅ Done |
| Fixture submodule + harvested vectors + published `didcomm-dotnet` vectors (FR-IX-03/06) | ✅ Done — [didcomm-dotnet-fixtures](https://github.com/moisesja/didcomm-dotnet-fixtures) |
| Live interop harness — both directions vs didcomm-python and didcomm-jvm over `did:peer`, nightly job (FR-IX-04/05/08) | ✅ Done — `tools/interop-live` |
| Sample applications 03–10 and cookbook tasks A–J/L/Y/Z (FR-DX-04, §14.3) | ✅ Done |
| Public-API coverage gate + §14.4 matrix (FR-DX-01, FR-DX-09) | ✅ Done — 0 undemonstrated members |
| ActivitySource spans with redaction audit (NFR-04/05) and the BenchmarkDotNet suite (NFR-07) | ✅ Done |

The conformance bar is binary: `MUST` requirements implemented, full Appendix C vector suite passes, cross-implementation interop matrix passes (both inbound static vectors and live round-trip against SICPA Python/JVM/Rust), every public API member demonstrated by a runnable sample, and the README quickstart works unmodified.

## Repository layout

```
didcomm-dotnet/
├── src/
│   ├── DidComm.Core/                              # message model, envelopes, facade, resolution, secrets, rotation, routing, protocols, transport abstractions
│   ├── DidComm.Extensions.DependencyInjection/    # services.AddDidComm(b => …)
│   ├── DidComm.Adapters.NetDid/                   # optional NetDid.IKeyStore → ISecretsResolver bridge
│   ├── DidComm.Transports.Http/                   # Polly-backed HTTPS sender (FR-TRN-04..08)
│   ├── DidComm.Transports.WebSocket/              # WebSocket sender with pool + reconnect (FR-TRN-09..11)
│   └── DidComm.AspNetCore/                        # MapDidCommEndpoint / MapDidCommWebSocket / MapDidCommOobEndpoint
├── tests/
│   ├── DidComm.Core.Tests/                        # unit tests
│   ├── DidComm.InteropTests/                      # Appendix C vectors + Appendix B resolution + facade round-trip + rotation + routing + transports + sample smoke tests
│   └── DidComm.TestSupport/                       # InMemorySecretsResolver helper (non-test library)
├── samples/
│   ├── _shared/                                   # Narrator + PeerIdentityFactory (did:peer:2 via NetDid)
│   ├── 01-Quickstart/                             # the README quickstart, compiled and CI-verified
│   ├── 02-Cookbook/                               # one narrated section per PRD §14.2 API task (A–BB)
│   └── 03…10                                      # the §14.3 sample applications (see the Samples table)
├── tools/
│   ├── FixtureGen/                                # emits the published `source: didcomm-dotnet` vectors (FR-IX-06)
│   ├── InteropCli/                                # mint/pack/unpack CLI for the live harness
│   └── interop-live/                              # cross-impl harness vs didcomm-python / didcomm-jvm (FR-IX-04/05)
├── benchmarks/
│   └── DidComm.Benchmarks/                        # NFR-07 BenchmarkDotNet suite (results in its README)
├── docs/
│   └── didcomm-dotnet_PRD.md                      # normative product requirements
├── tasks/                                         # phased todo files + lessons.md
├── Directory.Build.props
├── Directory.Packages.props
└── DidComm.sln
```

> Interop fixtures (Appendix A/B/C, harvested didcomm-rust/-python/-jvm vectors, our own
> published set) live in the standalone
> [`didcomm-dotnet-fixtures`](https://github.com/moisesja/didcomm-dotnet-fixtures) repository,
> wired in as a git submodule at `tests/DidComm.InteropTests/fixtures` (PRD §13.3). Clone with
> `--recurse-submodules`, or run `git submodule update --init` after a plain clone.

## Contributing

didcomm-dotnet welcomes contributions. The PRD is the source of truth for what to build; contributors should read it before opening non-trivial PRs. See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, code conventions, and the phased delivery model.

If you're filing an issue or PR for a specific requirement, please reference its ID (e.g. `FR-ENC-13`) — the PRD is structured so that traceability stays tight.

## Security

didcomm-dotnet handles cryptographic key material and implements security-critical primitives (JWE, JWS, ECDH-1PU, AES-CBC-HMAC). If you discover a vulnerability, **do not open a public issue**. See [SECURITY.md](SECURITY.md) for the responsible-disclosure process.

## Code of Conduct

This project follows the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/). By participating, you agree to uphold its terms.

## Related projects

- [**NetDid**](https://github.com/moisesja/net-did) — W3C DID Core 1.0 implementation; provides DID resolution to didcomm-dotnet
- [**zcap-dotnet**](https://github.com/moisesja/zcap-dotnet) — Authorization Capabilities (ZCAP-LD) for .NET

## License

Licensed under the [Apache License 2.0](LICENSE). See also [NOTICE](NOTICE).
