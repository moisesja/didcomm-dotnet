# didcomm-dotnet

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/status-pre--alpha-orange.svg)](#project-status)
[![Spec](https://img.shields.io/badge/spec-DIDComm%20v2.1-informational.svg)](https://identity.foundation/didcomm-messaging/spec/v2.1)

A spec-complete .NET 10 implementation of **DIDComm Messaging v2.1** — the [DIF](https://identity.foundation/) protocol for confidential, integrity-protected, optionally non-repudiable messaging between parties identified by Decentralized Identifiers (DIDs).

> DIDComm gives two parties a way to exchange messages whose trust derives from control of DIDs rather than from CAs, IdPs, or transport-level TLS. It is message-based, asynchronous, simplex, and transport-agnostic.

DID resolution is delegated to the sibling library [**NetDid**](https://github.com/moisesja/net-did) — didcomm-dotnet implements only the messaging layer (message model, JOSE envelopes, routing, threading, OOB, and the protocols defined directly in the spec).

## Project status

**Pre-alpha — implementation has not begun.** This repository currently contains the [Product Requirements Document](didcomm-dotnet_PRD.md) and project scaffolding. The PRD is normative; implementation follows the six-phase plan in §12.

No NuGet packages have been published yet. See the [roadmap](#roadmap) below for tracking progress.

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

## Planned package map

When published, the library will ship as the following NuGet packages:

| Package | Responsibility |
|---|---|
| `DidComm.Core` | Message model; JWE/JWS envelopes; all crypto; pack/unpack; resolver adapter over NetDid; secrets interface; routing; rotation; threading; problem reports; OOB |
| `DidComm.Transports.Http` | HTTPS send + ASP.NET Core receive endpoint |
| `DidComm.Transports.WebSocket` | WebSocket send/receive, connection lifecycle |
| `DidComm.Protocols.TrustPing` | Trust Ping 2.0 |
| `DidComm.Protocols.DiscoverFeatures` | Discover Features 2.0 |
| `DidComm.Protocols.ReportProblem` | Report Problem 2.0 helpers + problem-code taxonomy |
| `DidComm.Protocols.OutOfBand` | Out-of-Band 2.0 invitation build/parse, URL/QR encoding |

### Naming convention

The repository is `didcomm-dotnet` (kebab-case, matching `net-did` and `zcap-dotnet`). .NET assemblies, NuGet packages, and namespaces use the PascalCase root `DidComm` (e.g. `DidComm.Core`, `DidComm.Transports.Http`). The acronym "DIDComm" from the spec is rendered `DidComm` in code per .NET capitalization guidelines for 3+ letter acronyms (matching `NetDid`). Prose references to the protocol keep the spec spelling "DIDComm".

## Anticipated API shape

> The signatures below are informative, drawn from PRD §3.3. The final shape will be settled during implementation.

```csharp
// The facade
public sealed class DidComm
{
    Task<PackEncryptedResult> PackEncryptedAsync(PackEncryptedParams p, CancellationToken ct = default);
    Task<PackSignedResult>    PackSignedAsync(PackSignedParams p,    CancellationToken ct = default);
    Task<PackPlaintextResult> PackPlaintextAsync(Message m,          CancellationToken ct = default);
    Task<UnpackResult>        UnpackAsync(string message, UnpackParams? p = null, CancellationToken ct = default);
}

// DID resolution adapter (over NetDid)
public interface IDidKeyService
{
    Task<ResolvedKeys>     GetKeyAgreementKeysAsync(string did, CancellationToken ct = default);
    Task<ResolvedKeys>     GetAuthenticationKeysAsync(string did, CancellationToken ct = default);
    Task<DidCommService?>  GetDidCommServiceAsync(string did,   CancellationToken ct = default);
}

// Consumer-supplied secrets (KMS / HSM / vault)
public interface ISecretsResolver
{
    Task<Secret?>                    FindSecretAsync(string kid, CancellationToken ct = default);
    Task<IReadOnlyList<string>>      FindSecretsAsync(IEnumerable<string> kids, CancellationToken ct = default);
}
```

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

didcomm-dotnet is delivered in six phases (see [PRD §12](didcomm-dotnet_PRD.md) for the full plan, exit criteria, and per-phase agent kickoff prompts):

| Phase | Scope | Status |
|---|---|---|
| **0** | Repository & crypto substrate (`ICryptoProvider`, KDF, JWK conversions, fixtures harness) | Not started |
| **1** | Message model, attachments, MTURI parsing, consistency-check functions | Not started |
| **2** | Envelopes: Signed, Anoncrypt, Authcrypt — Appendix C interop gate | Not started |
| **3** | Pack/Unpack facade, NetDid integration, secrets, DID rotation | Not started |
| **4** | Routing & mediation (Forward protocol) | Not started |
| **5** | Transports (HTTPS, WebSocket) | Not started |
| **6** | Protocols, OOB, threading, i18n, live interop harness, samples, release | Not started |

The conformance bar is binary: `MUST` requirements implemented, full Appendix C vector suite passes, cross-implementation interop matrix passes (both inbound static vectors and live round-trip against SICPA Python/JVM/Rust), every public API member demonstrated by a runnable sample, and the README quickstart works unmodified.

## Repository layout (planned)

```
didcomm-dotnet/
├── src/
│   ├── DidComm.Core/
│   ├── DidComm.Transports.Http/
│   ├── DidComm.Transports.WebSocket/
│   ├── DidComm.Protocols.TrustPing/
│   ├── DidComm.Protocols.DiscoverFeatures/
│   ├── DidComm.Protocols.ReportProblem/
│   └── DidComm.Protocols.OutOfBand/
├── tests/
│   ├── DidComm.Core.Tests/
│   ├── DidComm.InteropTests/                # Appendix C + cross-implementation vectors
│   ├── DidComm.Transports.Http.Tests/
│   ├── DidComm.Transports.WebSocket.Tests/
│   └── DidComm.Protocols.*.Tests/
├── samples/
│   └── …                                    # 10 sample apps + 02-Cookbook (PRD §14)
├── fixtures/                                # git submodule: didcomm-dotnet-fixtures
├── didcomm-dotnet_PRD.md                    # normative product requirements
├── Directory.Build.props
├── Directory.Packages.props
└── didcomm-dotnet.sln
```

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
