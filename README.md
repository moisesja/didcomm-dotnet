# didcomm-dotnet

[![NuGet](https://img.shields.io/nuget/vpre/DidComm.Core.svg)](https://www.nuget.org/packages/DidComm.Core)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/status-phase%206%20complete-orange.svg)](#project-status)
[![Spec](https://img.shields.io/badge/spec-DIDComm%20v2.1-informational.svg)](https://identity.foundation/didcomm-messaging/spec/v2.1)

A spec-complete .NET 10 implementation of **DIDComm Messaging v2.1** — the [DIF](https://identity.foundation/) protocol for confidential, integrity-protected, optionally non-repudiable messaging between parties identified by Decentralized Identifiers (DIDs).

> DIDComm gives two parties a way to exchange messages whose trust derives from control of DIDs rather than from CAs, IdPs, or transport-level TLS. It is message-based, asynchronous, simplex, and transport-agnostic.

DID resolution is delegated to the sibling library [**NetDid**](https://github.com/moisesja/net-did) — didcomm-dotnet implements only the messaging layer (message model, JOSE envelopes, routing, threading, OOB, and the protocols defined directly in the spec).

## Project status

**Phases 0 – 6 complete.** The library has a public Pack / Unpack / Send
surface (`DidCommClient`), DID resolution via [NetDid 1.3.0](https://github.com/moisesja/net-did),
a consumer-supplied `ISecretsResolver` contract, the three protective envelope
shapes (signed / anoncrypt / authcrypt) and their legal compositions, addressing
consistency including the FR-CONSIST-06 resolver-backed authorization check,
DID rotation via `from_prior`, Routing Protocol 2.0 (sender forward wrapping
+ mediator relay + rewrapping), and the HTTPS / WebSocket transports plus the
ASP.NET Core receive endpoint. Phase 6 adds threading / ACKs / i18n / profiles
and **all the spec's built-in protocols** — Trust Ping, Discover Features, Empty,
Report Problem, Trace (off by default), and Out-of-Band 2.0. The DIDComm v2.1
Appendix C inbound interop gate passes for every vendored vector.

Shipped highlights:

- **Public facade** — `services.AddDidComm(b => …)` →
  `Pack{Plaintext,Signed,Encrypted}Async` + `UnpackAsync` + `SendAsync`.
  Auto-detects envelope shape on unpack, enforces FR-API-05 (`expires_time`)
  and FR-API-06 (`MaxReceiveBytes`), surfaces FR-API-04 metadata on every
  unpack.
- **DID resolution** via the `NetDidKeyService` adapter over net-did
  (`did:key`, `did:peer`); JWK + Multikey verification methods both supported;
  `did:web` deliberately refused at every entry point with
  `UnsupportedDidMethodException` (DD-08).
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
  `MapDidCommEndpoint` / `MapDidCommWebSocket` minimal-API extensions —
  `Content-Type` validation ⇒ 415, `MaxReceiveBytes` ⇒ 413 / 1009
  (FR-TRN-07 + FR-API-06).
- **Built-in protocols** — every protocol the spec defines directly:
  Trust Ping 2.0, Discover Features 2.0, Empty 1.0, Report Problem 2.0
  (taxonomy + interpolation + escalation + cascade guard), Trace 2.0
  (off by default), and Out-of-Band 2.0 (`OutOfBand.CreateInvitation` /
  `ToUrl` / `FromUrl`, short-form `?_oobid=` retrieval, `web_redirect`).
- **Cookbook** — runnable, narrated samples for the PRD §14.2 API tasks:
  **K** (unpack metadata), **M** (threading & ACKs), **N** (rotation),
  **O** (routing via a mediator), **P** (send over a transport), **Q**
  (receive over HTTP), **R** (receive over WebSocket), **S** (Trust Ping),
  **T** (Discover Features), **U** (Report Problem), **V** (Out-of-Band
  invitation), **W** (Empty message), **X** (custom handler), **AA**
  (net-did + did:web rejection), **BB** (profiles & i18n). Build the project
  and `dotnet run --project samples/02-Cookbook` to see end-to-end output.

576 unit + 96 interop tests pass under `warnaserror`. See
[CHANGELOG.md](CHANGELOG.md) for the per-phase log, the
[PRD](docs/didcomm-dotnet_PRD.md) for normative requirements
(the six-phase plan is §12), and the [roadmap](#roadmap) below for status at a
glance.

## Install

didcomm-dotnet ships as focused NuGet packages (hybrid packaging, DD-03) — the core plus one
package per transport and integration:

| Package | What it gives you |
|---|---|
| `DidComm.Core` | Message model, JWE/JWS envelopes, pack/unpack, routing, rotation, threading, and the built-in protocols (Trust Ping, Discover Features, Empty, Report Problem, Trace, Out-of-Band) |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` wiring with net-did resolution |
| `DidComm.AspNetCore` | `MapDidCommEndpoint` / `MapDidCommWebSocket` / `MapDidCommOobEndpoint` receive endpoints |
| `DidComm.Transports.Http`, `DidComm.Transports.WebSocket` | Sender-side transport bindings |
| `DidComm.Adapters.NetDid` | Optional bridge from a NetDid key store to `ISecretsResolver` |

```bash
# The first release is a prerelease (0.1.0-preview.1), so pass --prerelease until a stable tag:
dotnet add package DidComm.Core --prerelease
dotnet add package DidComm.Extensions.DependencyInjection --prerelease
```

> The release pipeline ([`.github/workflows/release.yml`](.github/workflows/release.yml)) packs
> all packages (with symbols + SourceLink) and pushes them to NuGet.org on a `vMAJOR.MINOR.PATCH`
> tag. The first version (`0.1.0-preview.1`) has not been tagged/published yet — until then, build
> from source. Maintainers: see [RELEASING.md](RELEASING.md) for the publish runbook.

## What "spec-complete" means

didcomm-dotnet v1.0 will implement, in full, the messaging layer of [DIDComm Messaging v2.1](https://identity.foundation/didcomm-messaging/spec/v2.1):

| Area | Scope |
|---|---|
| **Envelopes** | Plaintext, Signed (JWS), Anoncrypt (JWE/ECDH-ES+A256KW), Authcrypt (JWE/ECDH-1PU+A256KW), and all legal compositions |
| **Signing algorithms** | EdDSA (Ed25519), ES256 (P-256), ES256K (secp256k1) |
| **Key-agreement curves** | X25519, P-256, P-384 (required); P-521 (optional) |
| **Content encryption** | A256CBC-HS512 (required), A256GCM (recommended), XC20P (optional) |
| **DID resolution** | Delegated to NetDid — `did:key`, `did:peer`, `did:webvh`, `did:dht`, `did:ethr` |
| **Routing & mediation** | Forward protocol, mediator relay, rewrapping mode |
| **Transports** | HTTPS (send + ASP.NET Core receive), WebSocket |
| **Protocols** | Trust Ping 2.0, Discover Features 2.0, Report Problem 2.0, Out-of-Band 2.0, Empty 1.0, Trace 2.0 |
| **Cross-message** | Threading, ACK loop-guards, DID rotation (`from_prior`), `i18n`/`accept-lang`, profile negotiation |

> **`did:web` is explicitly NOT supported.** This is a deliberate security policy (DD-08), not a messaging-conformance gap. See PRD §1.1 and §15.

The conformance gate is the spec's own Appendix C test vectors plus a live cross-implementation harness round-tripping against the SICPA reference implementations in Python, JVM, and Rust.

## Package map

### Built today

| Package | Responsibility |
|---|---|
| `DidComm.Core` | Message model; JWE/JWS envelopes; pack/unpack/send facade; `IDidKeyService` + `NetDidKeyService` resolver adapter; `ISecretsResolver` contract; `from_prior` rotation; Routing Protocol 2.0 (forward wrapping + mediator processing + service-endpoint resolution); transport abstractions (`IDidCommTransport`, `ITransportRouter`); typed exception hierarchy |
| `DidComm.Extensions.DependencyInjection` | `IServiceCollection.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver<T>().UseHttpTransport().UseWebSocketTransport().Configure(...))`; FR-SEC-02 fail-fast on missing registrations |
| `DidComm.Adapters.NetDid` | Optional bridge from `NetDid.Core.IKeyStore` → `ISecretsResolver` (FR-SEC-04, SHOULD); documented scope (sign-side surface only — see class XML doc) |
| `DidComm.Transports.Http` | HTTPS sender (FR-TRN-04..08): `IHttpClientFactory`-backed POST, manual 307 follow + 301/308 refusal, Polly retry / circuit-breaker / timeout |
| `DidComm.Transports.WebSocket` | WebSocket sender (FR-TRN-09..11): one binary message per packed envelope, per-endpoint pool, Polly exponential reconnect, lifecycle events |
| `DidComm.AspNetCore` | Minimal-API extensions: `MapDidCommEndpoint` (HTTP receive, FR-TRN-07) and `MapDidCommWebSocket` (WS receive with frame reassembly, FR-TRN-09/10); `MaxReceiveBytes` ⇒ 413 / 1009 (FR-API-06) |
| `DidComm.TestSupport` *(non-shipped helper)* | `InMemorySecretsResolver` for tests and samples — deliberately kept out of `DidComm.Core` per DD-02 |

### Planned (later phases)

| Package | Phase | Responsibility |
|---|---|---|
| `DidComm.Protocols.TrustPing` | 6 | Trust Ping 2.0 |
| `DidComm.Protocols.DiscoverFeatures` | 6 | Discover Features 2.0 |
| `DidComm.Protocols.ReportProblem` | 6 | Report Problem 2.0 helpers + problem-code taxonomy |
| `DidComm.Protocols.OutOfBand` | 6 | Out-of-Band 2.0 invitation build/parse, URL/QR encoding |

### Naming convention

The repository is `didcomm-dotnet` (kebab-case, matching `net-did` and `zcap-dotnet`). .NET assemblies, NuGet packages, and namespaces use the PascalCase root `DidComm` (e.g. `DidComm.Core`, `DidComm.Transports.Http`). The acronym "DIDComm" from the spec is rendered `DidComm` in code per .NET capitalization guidelines for 3+ letter acronyms (matching `NetDid`). Prose references to the protocol keep the spec spelling "DIDComm".

## Public API at a glance

The signatures below are what ships today in `DidComm.Core` +
`DidComm.Extensions.DependencyInjection`.

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

// DID resolution adapter — DidComm.Resolution
public interface IDidKeyService
{
    Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship rel, CancellationToken ct = default);
    Task<bool>               IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship rel, CancellationToken ct = default);
    void                     RejectUnsupportedMethod(string did);  // throws UnsupportedDidMethodException for did:web
}

// Consumer-supplied secrets (KMS / HSM / Vault) — DidComm.Secrets
public interface ISecretsResolver
{
    Task<Jwk?>                  FindAsync(string kid,                       CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids,  CancellationToken ct = default);
}

// Transport binding (Phase 5) — DidComm.Transports
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
    b.Configure(o => o.MaxReceiveBytes = 1 * 1024 * 1024);
});
var client = sp.GetRequiredService<DidCommClient>();

// Server side — DidComm.AspNetCore
app.MapDidCommEndpoint("/didcomm",      async (unpacked, ct) => { /* host dispatch */ });
app.MapDidCommWebSocket("/ws/didcomm",  async (unpacked, ct) => { /* host dispatch */ });
```

The runnable [`samples/02-Cookbook`](samples/02-Cookbook/) project demonstrates
each shipped API task — the README at that path documents the §14.2 letter
mapping (K, M, N, O, P, Q, R, S, T, U, V, W, X, AA, BB through the Phase 6 increment).

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
| **3** | Pack/Unpack facade, NetDid integration, secrets, DID rotation (+ Cookbook §14.2 K/N/AA) | ✅ Complete |
| **4** | Routing & mediation (Forward protocol, mediator-as-DID-endpoint, rewrapping) (+ Cookbook §14.2 O) | ✅ Complete |
| **5** | Transports (HTTPS + ASP.NET Core receive, WebSocket) (+ Cookbook §14.2 P/Q/R) | ✅ Complete |
| **6** | Protocols, OOB, threading, i18n, live interop harness, samples, release | Not started |

The conformance bar is binary: `MUST` requirements implemented, full Appendix C vector suite passes, cross-implementation interop matrix passes (both inbound static vectors and live round-trip against SICPA Python/JVM/Rust), every public API member demonstrated by a runnable sample, and the README quickstart works unmodified.

## Repository layout

```
didcomm-dotnet/
├── src/
│   ├── DidComm.Core/                              # message model, envelopes, facade, resolution, secrets, rotation, routing, transport abstractions
│   ├── DidComm.Extensions.DependencyInjection/    # services.AddDidComm(b => …)
│   ├── DidComm.Adapters.NetDid/                   # optional NetDid.IKeyStore → ISecretsResolver bridge
│   ├── DidComm.Transports.Http/                   # Polly-backed HTTPS sender (FR-TRN-04..08)
│   ├── DidComm.Transports.WebSocket/              # WebSocket sender with pool + reconnect (FR-TRN-09..11)
│   ├── DidComm.AspNetCore/                        # MapDidCommEndpoint / MapDidCommWebSocket (FR-TRN-07/09/10)
│   └── DidComm.Protocols.*/                       # (Phase 6)
├── tests/
│   ├── DidComm.Core.Tests/                        # 576 unit tests
│   ├── DidComm.InteropTests/                      # 63 cases: Appendix C vectors + Appendix B resolution + facade round-trip + rotation + routing + transports + Cookbook smoke
│   └── DidComm.TestSupport/                       # InMemorySecretsResolver helper (non-test library)
├── samples/
│   ├── _shared/                                   # Narrator + PeerIdentityFactory (did:peer:2 via NetDid)
│   └── 02-Cookbook/                               # PRD §14.2 sections K, N, O, P, Q, R, AA today; grows with each phase
├── docs/
│   └── didcomm-dotnet_PRD.md                      # normative product requirements
├── tasks/                                         # phased todo files + lessons.md
├── Directory.Build.props
├── Directory.Packages.props
└── DidComm.sln
```

> The Phase 2 fixtures submodule migration (planned at PRD §13.3) is still
> pending — Appendix A / B / C vectors currently live inline under
> `tests/DidComm.InteropTests/fixtures/`. They'll move to a dedicated
> `didcomm-dotnet-fixtures` repo before Phase 6 closes.

## Contributing

didcomm-dotnet welcomes contributions. The PRD is the source of truth for what to build; contributors should read it before opening non-trivial PRs. See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, code conventions, and the phased delivery model.

If you're filing an issue or PR for a specific requirement, please reference its ID (e.g. `FR-ENC-13`) — the PRD is structured so that traceability stays tight.

## Security

didcomm-dotnet handles cryptographic key material and implements security-critical primitives (JWE, JWS, ECDH-1PU, AES-CBC-HMAC). If you discover a vulnerability, **do not open a public issue**. See [SECURITY.md](SECURITY.md) for the responsible-disclosure process.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to uphold its terms.

## Related projects

- [**NetDid**](https://github.com/moisesja/net-did) — W3C DID Core 1.0 implementation; provides DID resolution to didcomm-dotnet
- [**zcap-dotnet**](https://github.com/moisesja/zcap-dotnet) — Authorization Capabilities (ZCAP-LD) for .NET

## License

Licensed under the [Apache License 2.0](LICENSE). See also [NOTICE](NOTICE).
