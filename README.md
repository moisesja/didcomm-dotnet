# didcomm-dotnet

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/status-phase%203%20complete-orange.svg)](#project-status)
[![Spec](https://img.shields.io/badge/spec-DIDComm%20v2.1-informational.svg)](https://identity.foundation/didcomm-messaging/spec/v2.1)

A spec-complete .NET 10 implementation of **DIDComm Messaging v2.1** — the [DIF](https://identity.foundation/) protocol for confidential, integrity-protected, optionally non-repudiable messaging between parties identified by Decentralized Identifiers (DIDs).

> DIDComm gives two parties a way to exchange messages whose trust derives from control of DIDs rather than from CAs, IdPs, or transport-level TLS. It is message-based, asynchronous, simplex, and transport-agnostic.

DID resolution is delegated to the sibling library [**NetDid**](https://github.com/moisesja/net-did) — didcomm-dotnet implements only the messaging layer (message model, JOSE envelopes, routing, threading, OOB, and the protocols defined directly in the spec).

## Project status

**Phases 0 – 3 complete.** The library has a public Pack/Unpack surface
(`DidCommClient`), DID resolution via [NetDid 1.3.0](https://github.com/moisesja/net-did),
a consumer-supplied `ISecretsResolver` contract, the three protective envelope
shapes (signed / anoncrypt / authcrypt) and their legal compositions, addressing
consistency including the FR-CONSIST-06 resolver-backed authorization check,
and DID rotation via `from_prior`. The DIDComm v2.1 Appendix C inbound interop
gate passes for every vendored vector.

Shipped highlights:

- **Public facade** — `services.AddDidComm(b => …)` →
  `Pack{Plaintext,Signed,Encrypted}Async` + `UnpackAsync`. Auto-detects
  envelope shape on unpack, enforces FR-API-05 (`expires_time`) and FR-API-06
  (`MaxReceiveBytes`), surfaces FR-API-04 metadata on every unpack.
- **DID resolution** via the `NetDidKeyService` adapter over net-did
  (`did:key`, `did:peer`); JWK + Multikey verification methods both supported;
  `did:web` deliberately refused at every entry point with
  `UnsupportedDidMethodException` (DD-08).
- **DID rotation** — `Message.FromPrior` carries a JWT validated against the
  prior DID's `authentication` relationship; FR-ROT-03 enforced (rotation
  messages MUST be encrypted).
- **Cookbook** — runnable, narrated samples for the PRD §14.2 API tasks the
  shipped surface covers: **K** (unpack metadata), **N** (rotation), **AA**
  (net-did integration + did:web rejection). Build the project and
  `dotnet run --project samples/02-Cookbook` to see end-to-end output.

300 unit + 31 interop tests pass under `warnaserror`. See
[CHANGELOG.md](CHANGELOG.md) for the per-phase log, the
[PRD](docs/didcomm-dotnet_PRD.md) for normative requirements
(the six-phase plan is §12), and the [roadmap](#roadmap) below for status at a
glance.

No NuGet packages have been published yet.

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
| `DidComm.Core` | Message model; JWE/JWS envelopes; pack/unpack facade; `IDidKeyService` + `NetDidKeyService` resolver adapter; `ISecretsResolver` contract; `from_prior` rotation; typed exception hierarchy |
| `DidComm.Extensions.DependencyInjection` | `IServiceCollection.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver<T>().Configure(...))`; FR-SEC-02 fail-fast on missing registrations |
| `DidComm.Adapters.NetDid` | Optional bridge from `NetDid.Core.IKeyStore` → `ISecretsResolver` (FR-SEC-04, SHOULD); documented scope (sign-side surface only — see class XML doc) |
| `DidComm.TestSupport` *(non-shipped helper)* | `InMemorySecretsResolver` for tests and samples — deliberately kept out of `DidComm.Core` per DD-02 |

### Planned (later phases)

| Package | Phase | Responsibility |
|---|---|---|
| `DidComm.Transports.Http` | 5 | HTTPS send + ASP.NET Core receive endpoint |
| `DidComm.Transports.WebSocket` | 5 | WebSocket send/receive, connection lifecycle |
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
    public Task<string>       PackPlaintextAsync(Message m,                              CancellationToken ct = default);
    public Task<string>       PackSignedAsync(Message m, string signFrom,                CancellationToken ct = default);
    public Task<string>       PackEncryptedAsync(Message m, PackEncryptedOptions opts,   CancellationToken ct = default);
    public Task<UnpackResult> UnpackAsync(string packed,                                 CancellationToken ct = default);
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

// DI wiring — DidComm.Extensions.DependencyInjection
services.AddDidComm(b =>
{
    b.UseNetDidResolver();                     // did:key + did:peer via net-did
    b.UseSecretsResolver<MyVaultResolver>();   // FR-SEC-02 fail-fast if absent
    b.Configure(o => o.MaxReceiveBytes = 1 * 1024 * 1024);
});
var client = sp.GetRequiredService<DidCommClient>();
```

The runnable [`samples/02-Cookbook`](samples/02-Cookbook/) project demonstrates
each shipped API task — the README at that path documents the §14.2 letter
mapping (currently K / N / AA for the Phase 3 increment).

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
| **4** | Routing & mediation (Forward protocol) | Not started |
| **5** | Transports (HTTPS, WebSocket) | Not started |
| **6** | Protocols, OOB, threading, i18n, live interop harness, samples, release | Not started |

The conformance bar is binary: `MUST` requirements implemented, full Appendix C vector suite passes, cross-implementation interop matrix passes (both inbound static vectors and live round-trip against SICPA Python/JVM/Rust), every public API member demonstrated by a runnable sample, and the README quickstart works unmodified.

## Repository layout

```
didcomm-dotnet/
├── src/
│   ├── DidComm.Core/                              # message model, envelopes, facade, resolution, secrets, rotation
│   ├── DidComm.Extensions.DependencyInjection/    # services.AddDidComm(b => …)
│   ├── DidComm.Adapters.NetDid/                   # optional NetDid.IKeyStore → ISecretsResolver bridge
│   ├── DidComm.Transports.Http/                   # (Phase 5)
│   ├── DidComm.Transports.WebSocket/              # (Phase 5)
│   └── DidComm.Protocols.*/                       # (Phase 6)
├── tests/
│   ├── DidComm.Core.Tests/                        # 300 unit tests
│   ├── DidComm.InteropTests/                      # 31 cases: Appendix C vectors + Appendix B resolution + facade round-trip + rotation + Cookbook smoke
│   ├── DidComm.TestSupport/                       # InMemorySecretsResolver helper (non-test library)
│   └── DidComm.Transports.*.Tests/                # (Phase 5)
├── samples/
│   ├── _shared/                                   # Narrator + PeerIdentityFactory (did:peer:2 via NetDid)
│   └── 02-Cookbook/                               # PRD §14.2 sections K / N / AA today; grows with each phase
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
