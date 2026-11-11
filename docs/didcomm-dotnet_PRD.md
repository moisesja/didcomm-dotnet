# didcomm-dotnet — Product Requirements Document

**A spec-complete .NET 10 implementation of DIDComm Messaging v2.1**

| Field | Value |
|---|---|
| Document version | 2.1 (re-baselined against live spec; net-did integrated) |
| Date | May 2026 |
| Normative source | DIDComm Messaging v2.1 Editor's Draft (Working Group Approved), DIF — https://identity.foundation/didcomm-messaging/spec/v2.1 |
| Target runtime | .NET 10 / C# 13 |
| Repo / product name | `didcomm-dotnet` |
| NuGet / namespace root | `DidComm` (e.g. `DidComm.Core`) — kebab repo → PascalCase namespace, mirroring `net-did`→`NetDid` |
| Supporting library | **net-did** (NetDid) provides DID resolution; see §3 and §6 |
| License | Apache 2.0 |
| Status | Draft for implementation |
| Audience | Autonomous code agents (e.g. Claude Code) + human reviewers |

> **Naming convention.** The repository/product is `didcomm-dotnet` (kebab-case, consistent with `net-did` and `zcap-dotnet`). .NET assemblies, NuGet packages, and namespaces use the PascalCase root `DidComm` (e.g. `DidComm.Core`, `DidComm.Transports.Http`), and the facade type is `DidComm`. The acronym "DIDComm" from the spec is rendered `DidComm` in code per .NET capitalization guidelines for 3+ letter acronyms (matching `NetDid`). Prose references to the protocol/spec keep the spec spelling "DIDComm".

---

## How to use this document

This PRD is written to be consumed by code agents. It is organized so that an agent can be handed (a) the whole document for context and (b) a single phase section as a work order.

- **Requirements are atomic, numbered, and testable.** Each carries an ID (`FR-*` functional, `NFR-*` non-functional), an RFC 2119 keyword (MUST / SHOULD / MAY), the spec section it derives from, and an acceptance check.
- **Do not invent behavior.** Where the spec is silent, this document records an explicit Design Decision (`DD-*`). If a requirement is unclear, raise it rather than guessing.
- **The spec ships its own test vectors** (Appendix A/B/C). These are the source of truth for crypto interop and are referenced throughout. They MUST be vendored into the test suite verbatim.
- **Traceability:** every phase in §12 lists the requirement IDs it closes and the exit criteria that prove it.

A glossary of terms (authcrypt, anoncrypt, skid, apu, apv, KEK, CEK, PIURI, MTURI) appears in §16.

---

## Table of Contents

1. [Product Overview](#1-product-overview)
2. [Scope](#2-scope)
3. [Architecture & Packages](#3-architecture--packages)
4. [Functional Requirements — Message Model](#4-functional-requirements--message-model)
5. [Functional Requirements — Cryptographic Envelopes](#5-functional-requirements--cryptographic-envelopes)
6. [Functional Requirements — DID Resolution & Secrets](#6-functional-requirements--did-resolution--secrets)
7. [Functional Requirements — Pack / Unpack API](#7-functional-requirements--pack--unpack-api)
8. [Functional Requirements — Routing & Mediation](#8-functional-requirements--routing--mediation)
9. [Functional Requirements — Transports](#9-functional-requirements--transports)
10. [Functional Requirements — Protocols & Cross-Message Concerns](#10-functional-requirements--protocols--cross-message-concerns)
11. [Non-Functional Requirements](#11-non-functional-requirements)
12. [Phased Implementation Plan](#12-phased-implementation-plan)
13. [Test Strategy & Interoperability Fixtures](#13-test-strategy--interoperability-fixtures)
14. [Developer Experience & Sample Applications](#14-developer-experience--sample-applications)
15. [Design Decisions & Spec Ambiguities](#15-design-decisions--spec-ambiguities)
16. [Glossary](#16-glossary)

---

## 1. Product Overview

**didcomm-dotnet** is an open-source .NET 10 library that implements the DIDComm Messaging v2.1 messaging specification in full: the plaintext message model, the three protective envelope formats (signed / anoncrypt / authcrypt) and their legal compositions, DID-based key discovery, mediated routing, transport bindings, and the protocols defined directly in the spec.

DIDComm gives two parties a way to exchange confidential, integrity-protected, optionally non-repudiable messages whose trust derives from control of DIDs rather than from CAs, IdPs, or transport-level TLS. It is message-based, asynchronous, simplex, and transport-agnostic. (§Purpose and Scope.)

There is no production-grade DIDComm v2.1 library for .NET. Reference implementations exist in Rust, Kotlin, Swift, and Python (the SICPA / Roots family). didcomm-dotnet targets wire interoperability with those implementations by passing the spec's own Appendix C test vectors.

### 1.1 Conformance scope (what "in full" means)

DIDComm v2.1 is **DID-method-agnostic**: resolution is an external concern, and the spec mandates no particular DID method. "Implements DIDComm v2.1 in full" in this document therefore refers strictly to the **messaging layer** — message structure, envelopes/JOSE, message-layer addressing consistency, routing, threading, problem reports, OOB, and the in-spec protocols — all of which are conformance obligations.

Two kinds of choices in this PRD are **product/policy layers, not messaging-spec conformance**, and are labeled as such so the conformance claim stays precise:

- **DID-method selection**, including the deliberate `did:web` exclusion (DD-08). The spec does not require `did:web`; excluding it is a security policy that does not affect messaging-layer conformance.
- **Receive-side tolerances** such as accepting a bare-string `serviceEndpoint` (DD-10), which is a compatibility extension, not the spec's canonical shape.

Where this PRD adopts a product/policy position that diverges from the most permissive reading of the spec, it is recorded as a `DD-*` decision rather than presented as conformance.

### 1.2 Primary use cases

1. SSI wallets and agents written in .NET that must speak DIDComm v2 to existing ecosystems.
2. Enterprise services that issue/verify verifiable credentials over DIDComm.
3. Mediator/relay infrastructure for edge agents behind NAT.
4. Agentic-AI trust layers where autonomous agents authenticate and delegate using DID-anchored keys.

### 1.3 Definition of done

The library is "done" for v1.0 when every `MUST` requirement in this document is implemented, the full Appendix C vector suite passes, **the interoperability fixture matrix passes (§13.2–§13.6) — both the inbound vectors from the SICPA reference implementations and the live cross-implementation round-trip**, **every public API member is demonstrated by a runnable sample (§14, FR-DX-01) and all sample apps run on .NET 10 GA**, and the quickstart (§14, FR-DX-05) works unmodified. See §13 and §14.

---

## 2. Scope

### 2.1 In scope (v1.0)

| Area | Decision | Source |
|---|---|---|
| Envelopes | Plaintext, Signed, Anoncrypt, Authcrypt, and all legal compositions | §Message Formats |
| Signing algorithms | EdDSA (Ed25519), ES256 (P-256), ES256K (secp256k1) | §Message Signing |
| Key-agreement curves | X25519, P-384, P-256 (required); P-521 (optional) | §Curves |
| Content encryption | A256CBC-HS512 (required), A256GCM (recommended), XC20P (optional) | §Curves and Content Encryption Algorithms |
| Key wrapping | ECDH-ES+A256KW (anoncrypt), ECDH-1PU+A256KW (authcrypt) | §Key Wrapping Algorithms |
| DID resolution | **Delegated to net-did (NetDid).** didcomm-dotnet consumes `NetDid.IDidResolver` via a thin adapter; methods `did:key`, `did:peer`, `did:webvh`, `did:dht`, `did:ethr` come from net-did. **`did:web` is explicitly NOT supported** (DD-08). | DD-01, DD-08 |
| Secrets | `ISecretsResolver` abstraction only (consumer supplies KMS) | DD-02 |
| Routing | Routing Protocol 2.0 (forward, rewrapping, mediator-as-DID endpoint) | §Routing Protocol 2.0 |
| Transports | HTTPS and WebSocket bindings, send + receive | §Transports |
| Spec protocols | Out-of-Band 2.0, Discover Features 2.0, Trust Ping 2.0, Empty 1.0, Report Problem 2.0, Trace 2.0 | §Protocols |
| Cross-message | Threading (thid/pthid), ACKs (please_ack/ack), DID Rotation (from_prior), Problem Codes, i18n (lang/accept-lang), Profiles/`accept` negotiation | §Threading, §When Problems Happen, §i18n |
| Packaging | Hybrid: `DidComm.Core` (+crypto) as one package; transports and protocols as separate packages | DD-03 |
| Interoperability | Versioned fixture suite + cross-implementation harness against the SICPA reference family (rust/python/jvm) and the spec vectors; both directions (we-unpack-theirs, they-unpack-ours) | §13.2–§13.6 |
| Developer experience | 100%-public-API sample demonstration, a runnable cookbook, a ≤5-min quickstart, and 10 sample apps (v1.0; sample 11 arrives with Phase 7) | §14 |
| License | Apache 2.0 | DD-04 |
| Mediator service | **Separate package `DidComm.Mediator`** (post-v1.0, §12 Phase 7): Coordinate Mediation 3.0 (mediator + recipient roles; 2.0 compatibility handler), Message Pickup 3.0 (mediator + recipient roles; 4.0 follow-up), and the Return-Route and Queue Transport extension (return-route and the Routing 2.0 fixes land in the existing Core / AspNetCore / Transports packages because they are in the core spec, D1). Store contracts ship with in-memory implementations only. | §8a–§8c; didcomm.org coordinate-mediation 3.0/2.0, messagepickup 3.0, message-pickup 4.0; return_route extension |

### 2.2 Explicitly out of scope (v1.0)

| Item | Rationale |
|---|---|
| Persistent mediator stores | `DidComm.Mediator` ships the `IMediationStore` / `IMessageStore` contracts with in-memory implementations only; durable stores are a host concern (D1 in #88). |
| shorten-url 1.0 | didcomm.org protocol adjacent to mediation; not required by Coordinate Mediation 3.0 or Message Pickup 3.0. |
| swarm 1.0 | didcomm.org protocol adjacent to mediation; not required by Coordinate Mediation 3.0 or Message Pickup 3.0. |
| DIDComm v1 pickup forms | Message Pickup 4.0 also defines a DIDComm v1 (`@type` / `~thread` / `did:key` `recipient_did`) form; v1 envelopes are out of scope (see the Aries RFC 0019 row), so only the v2 form of 4.0 is implemented (FR-PICK-12). |
| Verifiable Credential issuance/verification | Higher-level protocol; complementary, separate library. |
| Advanced Sequencing extension | Spec extension, not core. (§Gaps, Resends.) |
| l10n extension | Spec extension; core `lang`/`accept-lang` is in scope. |
| DIDComm v1 / Aries RFC 0019 envelope | Profile `didcomm/aip2;env=rfc19` not implemented; only `didcomm/v2`. |
| Binary encodings (CBOR/msgpack/protobuf) | Spec marks these future work. |
| Embedded key store / HSM driver | Provided via `ISecretsResolver` abstraction (DD-02). |
| Post-quantum algorithms | Spec marks as future work. |
| **`did:web` resolution** | **Explicitly excluded on security grounds (DD-08).** `did:web` derives trust from DNS + web PKI + domain control with no verifiable history or key pre-rotation, permitting silent key substitution and domain-takeover attacks. The library actively rejects `did:web` DIDs (FR-DID-06). Use `did:webvh` (verifiable history) instead — already supported via net-did. |

---

## 3. Architecture & Packages

### 3.1 Package map (DD-03)

| Package | Responsibility | Key dependencies |
|---|---|---|
| `DidComm.Core` | Message model; the DIDComm **envelope-composition** layer (legal compositions FR-ENV-02, media-type pinning, addressing-consistency FR-CONSIST-01..06, recipient defaulting, curve negotiation); pack/unpack; **resolver adapter over net-did**; secrets interface; routing/forward; rotation; threading; problem reports; OOB encode/decode. DidComm carries **no crypto or JWE/JWS assembly of its own** — those are delegated. | **`DataProofsDotnet.Jose 2.0.0`** — owns the JOSE envelope layer: multi-recipient JWE General-JSON (`ECDH-ES+A256KW` / `ECDH-1PU+A256KW`; `A256CBC-HS512` / `A256GCM` / `XC20P`), JWS (Flattened/General, compact), `JwkConversion`, base64url — all on `NetCrypto`. **`NetCrypto 1.7.1`** — the crypto substrate (sign/verify with IEEE P1363, raw ECDH, AEAD, A256KW, Concat KDF, on-curve validation in `JwkConverter.ExtractPublicKey`, `Base64Url`); reached directly only by `JwsSignerFactory` (adapts a private signer JWK into a NetCrypto `ISigner`), otherwise transitively via DataProofs. **`NetDid.Core 4.0.x`** — **DID resolution only** (`IDidResolver`, DID Document model); all crypto moved out of net-did in 2.0.0, and the 3.0.0 major is scale (a new `did:ethr` method DidComm does not reference), not a break for resolution consumers. No `NSec` / `System.Security.Cryptography` envelope crypto in DidComm. |
| `DidComm.Transports.Http` | HTTPS send + ASP.NET Core receive endpoint | `DidComm.Core`, `Microsoft.Extensions.Http` |
| `DidComm.Transports.WebSocket` | WebSocket send/receive, connection lifecycle | `DidComm.Core` |
| `DidComm.Protocols.TrustPing` | Trust Ping 2.0 | `DidComm.Core` |
| `DidComm.Protocols.DiscoverFeatures` | Discover Features 2.0 | `DidComm.Core` |
| `DidComm.Protocols.ReportProblem` | Report Problem 2.0 helpers + problem-code taxonomy | `DidComm.Core` |
| `DidComm.Protocols.OutOfBand` | Out-of-Band 2.0 invitation build/parse, URL/QR encoding | `DidComm.Core` |
| `DidComm.Mediator` *(Phase 7, post-v1.0)* | Coordinate Mediation 3.0 (+2.0 compat) and Message Pickup 3.0 (+4.0) handlers for both roles; the `forward` → live session / pickup queue / optional direct-push delivery pipeline; `IMediationStore` / `IMessageStore` / `IMediationPolicy` contracts with in-memory implementations; `AddMediator()` and `MapDidCommMediator()` composition; `MediationClient` / `PickupClient` / `DidCommServiceBuilder` for the recipient role. | `DidComm.Core` (+ `DidComm.AspNetCore` only if `MapDidCommMediator` is hosted here — #95 decides); the recipient-role client surface MUST NOT require ASP.NET Core |

> Note: Empty 1.0 and Trace 2.0 are small enough to live in `DidComm.Core` (Empty is needed internally for header-only ACKs; Trace is a header + report type).

### 3.2 Layering

```
        ┌─────────────────────────────────────────────┐
        │  Protocols (TrustPing, DiscoverFeatures, …)  │
        ├─────────────────────────────────────────────┤
        │  Transports (HTTP, WebSocket)                │
        ├─────────────────────────────────────────────┤
        │  DidComm facade  (Pack / Unpack / Send)      │
        ├──────────────┬───────────────┬──────────────┤
        │  Routing      │  Rotation     │  Threading    │
        ├──────────────┴───────────────┴──────────────┤
        │  Envelope COMPOSITION (DidComm-specific):     │
        │  legal compositions · media types ·           │
        │  FR-CONSIST addressing checks · recipient      │
        │  defaulting · curve negotiation                │
        ├───────────────────────┬─────────────────────┤
        │  Resolver adapter      │ ISecretsResolver     │
        │  (over NetDid)         │ (consumer-supplied)  │
        └───────┬───────────────┴──────────┬───────────┘
                │                           │
   ┌────────────▼────────────┐  ┌───────────▼──────────────────────┐
   │ net-did (NetDid 4.0.x): │  │ DataProofsDotnet.Jose:            │
   │ resolution only —       │  │ JWE (anon/auth) + JWS build/parse │
   │ key · peer · webvh ·    │  │            │                      │
   │ ethr (→ net-cid)        │  │            ▼                      │
   └─────────────────────────┘  │ NetCrypto: curves · AEAD · KW ·   │
                                 │ KDF · sign/verify · JWK · b64url  │
                                 └───────────────────────────────────┘
```

net-did owns DID method logic and W3C-conformant resolution; **DataProofsDotnet.Jose (on NetCrypto) owns the JOSE envelope build/parse and all crypto**; didcomm-dotnet owns the DIDComm message-and-envelope **composition** layer above them — which envelopes may legally nest, the media-type and addressing-consistency rules (FR-ENV / FR-CONSIST), recipient defaulting, and curve negotiation. The couplings are the resolver adapter (§6.1), the DataProofs JWE/JWS surface (its key-resolver hooks are satisfied directly by DidComm's internal secret/sender lookups), and an optional `IKeyStore`→`ISecretsResolver` bridge (DD-09).

### 3.3 Core abstractions (informative signatures; final shape is the agent's, constrained by the FRs)

```csharp
// DID resolution is provided by net-did. didcomm-dotnet consumes NetDid.IDidResolver
// directly and wraps it in an adapter that extracts the keyAgreement / authentication
// verification methods DIDComm needs. didcomm-dotnet does NOT define its own DID-method resolvers.
//
//   NetDid.IDidResolver:  Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions?, CancellationToken)
//                         bool CanResolve(string did)
//
public interface IDidKeyService {                        // didcomm-dotnet's adapter surface
    Task<ResolvedKeys> GetKeyAgreementKeysAsync(string did, CancellationToken ct = default);
    Task<ResolvedKeys> GetAuthenticationKeysAsync(string did, CancellationToken ct = default);
    Task<DidCommService?> GetDidCommServiceAsync(string did, CancellationToken ct = default);
}
// Default implementation: NetDidKeyService(NetDid.IDidResolver resolver) — see FR-DID-02..04.

public interface ISecretsResolver {                      // unchanged; consumer-supplied (DD-02)
    Task<Secret?> FindSecretAsync(string kid, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindSecretsAsync(IEnumerable<string> kids, CancellationToken ct = default);
}

public interface ICryptoProvider { /* KeyAgree, Sign, Verify, Aead encrypt/decrypt, KeyWrap/Unwrap, ConcatKDF */ }

public sealed class DidComm {
    Task<PackEncryptedResult> PackEncryptedAsync(PackEncryptedParams p, CancellationToken ct = default);
    Task<PackSignedResult>    PackSignedAsync(PackSignedParams p, CancellationToken ct = default);
    Task<PackPlaintextResult> PackPlaintextAsync(Message m, CancellationToken ct = default);
    Task<UnpackResult>        UnpackAsync(string message, UnpackParams? p = null, CancellationToken ct = default);
}
```

---

## 4. Functional Requirements — Message Model

### 4.1 Plaintext message structure (§Plaintext Message Structure, §Message Headers)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-MSG-01 | MUST | Model a plaintext message as a JSON object with predefined headers as siblings of `body`. Serialize/deserialize with `System.Text.Json`. | Round-trips the Appendix C.1 example byte-for-byte (modulo key ordering). |
| FR-MSG-02 | MUST | `id` is REQUIRED, a string ≤ 32 bytes of unreserved URI chars. A message lacking `id` is invalid and rejected. | Unpack of an `id`-less plaintext throws `MalformedMessageException`. |
| FR-MSG-03 | SHOULD | Generate `id` as a lowercase UUID (RFC 4122) by default; expose `IMessageIdGenerator` to override. | Default IDs parse as UUIDs; custom generator is honored. |
| FR-MSG-04 | MUST | Compare `id`/`thid`/`pthid` **case-insensitively** (UUID semantics) while emitting lowercase. | Two ids differing only in case are treated equal in threading. |
| FR-MSG-05 | MUST | `type` is REQUIRED and MUST be a valid MTURI (see FR-PROTO-01). | Invalid `type` rejected. |
| FR-MSG-06 | MUST | `typ` (media type) is supported; for plaintext it is `application/didcomm-plain+json`. Accept media types lacking the `application/` prefix as if present. | `didcomm-plain+json` and `application/didcomm-plain+json` both accepted. |
| FR-MSG-07 | MUST | `to` is OPTIONAL; when present MUST be an array of DIDs/DID-URLs **without fragment**. | A `to` entry with a fragment is rejected. |
| FR-MSG-08 | MUST | `from` is OPTIONAL for anoncrypt, REQUIRED for authcrypt; MUST be a DID/DID-URL without fragment. | Authcrypt pack without `from` throws; anoncrypt without `from` succeeds. |
| FR-MSG-09 | SHOULD | Support `created_time` and `expires_time` as integer UTC epoch seconds. | Values survive round-trip as integers, not strings. |
| FR-MSG-10 | MUST | `body` is **OPTIONAL** (2.1 change). When present it MUST be a JSON object. Absence is equivalent to empty `{}`. Emit `body` when sending for v2.0 compatibility but accept its absence when receiving. | A message with no `body` unpacks; `body` defaults to empty object. |
| FR-MSG-11 | MUST | `thid` and `pthid` are OPTIONAL and obey the same value constraints as `id`. | Validated identically to `id`. |
| FR-MSG-12 | MUST | A message MUST NOT be rejected solely because it carries unrecognized/extension headers (JOSE-style extensibility); unknown headers are ignored for processing, not fatal. The `X-*` convention is not special-cased. | A message with an unknown header unpacks successfully. |
| FR-MSG-13 | MUST | Provide a fluent `Message` builder that auto-populates `id` and `typ`. | Builder produces a valid minimal message. |
| FR-MSG-14 | MUST | **`id` uniqueness is a protocol-correctness requirement, not just a format rule.** Each `id` MUST be unique to the sender across all messages they send, and at minimum unique across all interactions visible to the parties involved. The default generator MUST produce collision-resistant identifiers (UUID v4 satisfies this in normal operation). A custom `IMessageIdGenerator` carries the uniqueness obligation; this MUST be stated in its XML docs and the developer docs. | Default generator yields no collisions across a large generation run; docs state the uniqueness contract for custom generators. |
| FR-MSG-15 | SHOULD | Preserve unknown/extension headers across an unpack→repack round-trip (distinct from the MUST-not-fail rule in FR-MSG-12). | An unrecognized header survives unpack→repack. |

### 4.2 Attachments (§Attachments)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ATT-01 | MUST | Model `attachments` as a list; each item supports `id`, `description`, `filename`, `media_type`, `format`, `lastmod_time`, `byte_count`, and `data`. | Round-trips the §Attachment Example. |
| FR-ATT-02 | MUST | `data` MUST contain at least one of `jws`, `hash`, `links`, `base64`, `json`. | An attachment with empty `data` is rejected. |
| FR-ATT-03 | MUST | When `links` is used, `hash` MUST be present (integrity). | `links` without `hash` rejected. |
| FR-ATT-04 | MUST | Attachment `id` is OPTIONAL, but **when present it MUST consist entirely of unreserved URI characters** (it is used to compose URIs). An attachment whose `id` contains reserved characters is rejected. | An attachment with a reserved-char `id` is rejected; an absent `id` is accepted. |
| FR-ATT-05 | MAY | Support `jws` attachments in JWS detached-content mode. | A signed attachment verifies. |

### 4.3 Message-layer addressing consistency (§Message Layer Addressing Consistency) — **security-critical**

> **Comparison primitive (normative for this section).** `from` and `to` entries are DIDs **or DID URLs without a fragment** (per FR-MSG-07/08 — a DID URL MAY carry `path`/`query`); `skid` and recipient/signer `kid`s are DID URLs that include a fragment (per FR-ENC-16, FR-SIG-03). The implementable rule is therefore **not** a raw string compare and **not** a naïve "substring before `#`". Define `DidSubjectOf(value)` = **parse `value` as a DID/DID URL using net-did's DID-URL parser and return the bare DID subject** (`did:<method>:<method-specific-id>`), discarding any `path`, `query`, and `fragment`. Apply `DidSubjectOf(...)` to **both** sides before comparing. Input longer than 4096 characters is not a DID (FR-API-09): `DidSubjectOf` returns no subject for it without calling the parser, and callers treat that as "not a DID": such a value can never compare equal to a DID subject. All "match" checks below mean: `DidSubjectOf(kid)` equals `DidSubjectOf(from)` / is a member of `{ DidSubjectOf(t) : t ∈ to }`, compared as DID subjects per DID syntax — **plus** the resolver-backed authorization check (FR-CONSIST-06). Implementations MUST NOT compare a full DID URL against a possibly-DID-URL `from`/`to` by raw string (it can reject valid inputs and invites brittle parsing). A custom `IMessageIdGenerator` is unrelated; these are addressing rules.

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-CONSIST-01 | MUST | **Authcrypt `from` ↔ `skid`.** On unpack, if plaintext `from` is present, `DidSubjectOf(skid)` MUST equal `DidSubjectOf(from)`. Mismatch → `ConsistencyException`. | **Positive:** `from = did:example:alice`, `skid = did:example:alice#key-1` ⇒ passes; a `from` that is a DID URL with a query (`did:example:alice?foo=bar`) still matches the same `skid`. **Negative:** `skid = did:example:carol#key-1` with `from = did:example:alice` ⇒ throws. |
| FR-CONSIST-02 | MUST | **Recipient `to` ↔ `kid`.** On unpack, when plaintext `to` is present, `DidSubjectOf(kid)` of the recipient entry used to decrypt MUST be a member of `{ DidSubjectOf(t) : t ∈ to }`. Mismatch → `ConsistencyException`. | **Positive:** decrypt with `kid = did:example:bob#key-x25519-1` while `to` contains `did:example:bob`. **Negative:** that DID absent from `to` ⇒ throws. |
| FR-CONSIST-03 | MUST | **Signed `from` ↔ signer `kid`.** For signed messages, if plaintext `from` is present, `DidSubjectOf(signerKid)` MUST equal `DidSubjectOf(from)`. Mismatch → `ConsistencyException`. | **Positive:** `from = did:example:alice`, signer `kid = did:example:alice#key-2`. **Negative:** signer `kid = did:example:mallory#key-2` ⇒ throws. |
| FR-CONSIST-04 | SHOULD | **Recipient self-presence in `to` (#59).** When a `to` header is present, verify one of the recipient's **own identifiers** appears (compared as DID subjects). "Own" is the union of the decrypting recipient kid's DID subject (encrypted envelopes) and the DIDs the application declares in `DidCommOptions.OwnIdentifiers` — the spec's "their own identifier" is the agent's identity, which only the application can assert for non-encrypted receives. If none appears, do NOT fail: surface the warning as data — `UnpackResult.RecipientAddressing = NotAddressed` (`Addressed` on a match; `NotEvaluated` when `to` is absent or no own identifier is known). The outcome MUST also be surfaced on the verified inbound snapshot and `InboundObservation` (#61). `InboundObservation.FromUnpackResult` MUST distinguish three trust-source states. **(1) Covering verified snapshot:** when the snapshot still covers the current `Message` content, it MUST exclusively supply all observation metadata — the six positional trust members (`Encrypted`, `Authenticated`, `NonRepudiation`, `AnonymousSender`, `SenderKid`, `SignerKid`), all three key bindings, and `RecipientAddressing` — including verified null kids or bindings. **(2) Verified but diverged:** when a verified snapshot exists for the `Message` instance but its current content differs, the observation MUST fail closed with all four flags `false`, both kids and all three bindings null, and `RecipientAddressing = NotEvaluated`; none of those members may fall back to caller claims. **(3) Never snapshotted:** only when no verified snapshot exists for the supplied `Message` may the synthetic compatibility path preserve the caller's six positional members; all three bindings MUST remain null and addressing MUST remain `NotEvaluated`, whatever the caller claims. Normal observer delivery for a real unpack result MUST materialize the verified plaintext and all metadata from the same snapshot. The public `ProtocolDispatcher.DispatchAsync` MAY also accept a caller-created synthetic result; its observer fallback follows state (3), so the six positional values are caller claims rather than cryptographic evidence. **Scope:** `InboundObservation` is a record with a mutable `Message`. A consumer's `with`-clone copies stored values onto whatever `Message` the clone carries, and direct mutation changes message content without recomputing metadata; the pairing guarantee is therefore construction-time only, and after either action the stored values still describe the message the observation was built from (documented limitation, pinned by test). **Thread affinity (#63):** the public `FromUnpackResult` overload reads the caller's live `Message`, which this library neither owns nor locks, so concurrent mutation of a collection-valued header (`to`, `ack`) MAY fault the call; that exception MUST be documented as part of the API contract rather than swallowed, since swallowing would turn a caller bug into silently degraded trust metadata. Library-internal observer delivery has no such exposure — it materializes from the immutable snapshot, never from a mutable message. An unparseable configured identifier MUST be rejected at `DidCommClient` construction so a typo cannot silently disable the signal. An unparseable wire `to` entry cannot match; if a present `to` contains no parseable own DID subject, the outcome is `NotAddressed`. `OwnIdentifiers` is snapshotted at construction so a concurrent caller mutation cannot fault an unpack. Note the encrypted mismatch case is already **rejected** by the stronger FR-CONSIST-02 MUST, so the warning is reachable primarily on signed-only and plaintext receives — exactly the paths with no addressing check otherwise. **Per-message parse/lookup count MUST have no term linear in the host's declared roster:** `OwnIdentifiers` MUST be normalized to DID subjects, deduplicated, and frozen once at construction, and the check MUST then walk only the `to` sequence against that prebuilt set — never enumerating it — and MUST perform one set lookup for every parseable `to` entry without short-circuiting after a match. Calling the old per-identifier predicate once for each configured identity costs `O(|to| × |own|)`; the first wired `O(|to| + |own|)` shape still imposed a roster-sized per-message term. The frozen-set shape removes that receiver-chosen multiplier, and the fixed parse/lookup count removes the coarse match-position channel. Neither property is a constant-time membership guarantee: set hit/miss paths and string equality are data-dependent, repeated guessed DIDs can amplify the residual difference, and cache effects may still vary with set size. `Addressed` is not a trust signal: `to` is attacker-authored, so it is meaningful only on an authenticated or signed envelope. | **Positive:** signed-only message with `to = [did:example:carol]` and `OwnIdentifiers = [did:example:bob]` ⇒ delivered with `NotAddressed`; same message with bob in `to` ⇒ `Addressed`. **Negative-space:** unconfigured agents see `Addressed`/`NotEvaluated` only; delivery is never affected. **Mirror (#61):** a covering snapshot supplies every observation metadata member; after post-unpack content mutation, `FromUnpackResult` yields the full neutral tuple (four false flags, null kids/bindings, `NotEvaluated`) even if caller fields are poisoned; a never-snapshotted synthetic result preserves only its six positional members and cannot supply bindings or addressing; observers on the no-handler / ack-loop-drop / handler-failure paths for real unpack results read the verified snapshot outcome; dispatching a caller-created synthetic result preserves only its six positional caller claims and does not create cryptographic evidence; a consumer `with`-clone or direct `Message` mutation keeps the built-from values (scope limitation, pinned by dedicated tests). |
| FR-CONSIST-05 | MUST | Reject `authcrypt(sign(plaintext))` where the inner signer differs from the authcrypt sender (`DidSubjectOf(signerKid) != DidSubjectOf(skid)`); accepting that composition at all is optional, but the mismatch check is mandatory. | Mismatched signer/sender in that combo throws. |
| FR-CONSIST-06 | MUST | **Resolver-backed authorization (controller rule).** Beyond the `DidSubjectOf(...)` equality above, the referenced verification method MUST actually be authorized by the asserted DID: resolving the `from` (or the matched `to`) DID subject via **net-did** MUST yield a DID Document in which that exact `kid`/`skid` is present under the correct relationship (`keyAgreement` for `skid`/recipient `kid`; `authentication` for signer `kid`), honoring the verification method's `controller`. A `kid` whose DID subject equals `from` but which is **not** present under the required relationship in `from`'s resolved document (e.g. a key actually controlled by a different DID) MUST be rejected. | **Negative:** a `skid` of the form `did:example:alice#k` that does not appear in alice's resolved `keyAgreement` (or is controlled by another DID) ⇒ `ConsistencyException`, even though `DidSubjectOf` matches. |
| FR-CONSIST-07 | MUST | **Same-document key provenance (#56).** On the provenance-capable key-service path (`IDidKeyBindingService`; the built-in `NetDidKeyService` implements it), the public key that satisfied a cryptographic check (JWS verify / authcrypt 1PU / recipient decrypt / `from_prior` rotation-JWT verify) and the controller/relationship evidence used to authorize the corresponding identity MUST come from the **same resolved verification method in the same DID document** — one resolution per (kid, relationship) per unpack, captured by a per-operation lookup context (no process-global, `AsyncLocal`, TTL, or cross-message state). Sender/signer authority MUST NOT be re-resolved after crypto succeeds; recipient evidence is resolved exactly once after the decrypting kid is known; `from_prior` (FR-ROT-01) MUST take authority and verifying key from one resolution. Duplicate/shadowed relationship entries for the same normalized kid MUST fail closed. The captured evidence (kid, DID subject, controller, relationship, RFC 7638 key thumbprint, and the asserted DID the controller rule was evaluated against — null when the plaintext carried no `from`, i.e. key evidence only) MUST be surfaced as immutable, consumer-unforgeable `VerifiedKeyBinding` properties on `UnpackResult`, the verified inbound snapshot, and `InboundObservation`. Custom `IDidKeyService` implementations without the capability keep the pre-1.4.0 two-resolution behavior and surface no binding (kid/flag output is not same-resolution provenance). | **Negative:** a resolver that serves document A (key `KA` under the kid) to the crypto lookup and document B (same kid, key `KB` or a different controller) afterwards ⇒ the second document is never consulted for sender/signer authority (exactly one resolution observed) and the surfaced binding pins `KA`'s thumbprint; a controller splice across versions ⇒ `ConsistencyException`; a rotation JWT signed by a replacement key under an authorized kid ⇒ rejected; a duplicate kid under the relationship ⇒ rejected. **Positive:** concurrent unpacks retain independent bindings; a synthetic `UnpackResult` yields no binding. |
| FR-CONSIST-08 | MUST | **Decrypted recipient entry ↔ its label.** The recipient `kid` reported by an encrypt layer is the label of whichever recipient entry the derived KEK opened, and those labels are attacker-authored (`apv` commits only to the sorted kid list, so labels can be permuted without invalidating the envelope). The reported recipient `kid` MUST equal the kid whose key agreement produced the KEK. The rejection MUST be reported as the same uniform decryption failure (`CryptoException`, identical message) a non-holder receives, since reaching the check implies a local key opened an entry and a distinct exception category would reintroduce the recipient-possession oracle the constant-work decrypt path closes. Recipient-key selection MUST therefore follow the envelope's recipient order, matching the parser's scan order, so an envelope wrapping one key under two ids cannot fail this check. | **Negative:** a two-recipient JWE with the two entries' `kid` labels swapped ⇒ rejected, even though the ciphertext, tag, and `apv` are untouched; a holder and a non-holder receive the same exception type and message. **Positive:** the same envelope unswapped unpacks and reports the local kid; an envelope wrapping the same key material under two ids unpacks regardless of the order `FindPresentAsync` returns. |
| FR-CONSIST-09 | MUST | **Resolved document identity.** Authorization and binding evidence record the *requested* DID, so every resolution MUST verify that the returned document's `id` is that DID and fail closed otherwise (W3C DID Core §3.1). Without it a cross-wired composite resolver, a mis-keyed cache, or a hostile resolver could have another subject's document attributed to the asserted DID. Dereferencing a verification-relationship reference MUST normalize relative and absolute ids on both sides and MUST reject zero or multiple normalized matches. | **Negative:** a resolver returning Eve's document for Alice ⇒ `DidResolutionException` on projection, authorization, and binding paths; a reference matching two same-id top-level methods ⇒ rejected. **Positive:** `#key-1` dereferences against an absolute method id and vice versa. |
| FR-CONSIST-10 | MUST | **Recipient provenance requires key proof.** The recipient key is supplied by the local `ISecretsResolver` while its evidence comes from a DID resolution, so a `VerifiedKeyBinding` for the recipient MUST be surfaced only when the resolved public key equals the locally-held key's public identity. When that identity is unavailable (an opaque resolver exposing no public material) or differs (our own document rotated the kid while we still hold the old private key), the binding MUST be absent rather than asserted. Absence MUST NOT reject the message — a message encrypted to a still-held, rotated-out key remains valid. | **Negative:** own-document rotation of the recipient kid ⇒ message unpacks, `RecipientKeyBinding` is null. **Positive:** matching key ⇒ binding surfaced with the local key's thumbprint. |

---

## 5. Functional Requirements — Cryptographic Envelopes

> This is the interoperability heart of the library. Every requirement here is validated against Appendix C vectors. The Concat KDF, `apu`/`apv` derivation, and the "encrypt-then-derive-KEK-using-tag" ordering for 1PU are the highest-risk areas.

### 5.1 Envelope formats & media types (§IANA Media Types, §Message Formats)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ENV-01 | MUST | Plaintext media type = `application/didcomm-plain+json`; Signed = `application/didcomm-signed+json`; Encrypted (anon or auth) = `application/didcomm-encrypted+json`. The encrypted type is identical for anoncrypt and authcrypt. | `typ` set correctly per envelope. |
| FR-ENV-02 | MUST | Support exactly these legal envelope compositions for a single hop: `plaintext`, `signed(plaintext)`, `anoncrypt(plaintext)`, `authcrypt(plaintext)`, `anoncrypt(sign(plaintext))`, `anoncrypt(authcrypt(plaintext))`. | Each composition packs and unpacks. |
| FR-ENV-03 | SHOULD NOT | Do not emit `authcrypt(sign(plaintext))`; MAY accept it on receive (subject to FR-CONSIST-05). | Library never produces this; can ingest it. |
| FR-ENV-04 | MUST NOT | Do not emit any single-hop composition other than those in FR-ENV-02 (e.g. never `anoncrypt(authcrypt(sign))`). | No disallowed nesting produced. |
| FR-ENV-04a | MUST | On **receive**, accept exactly the compositions matching the grammar `anoncrypt? authcrypt? sign? plaintext` (at most one anoncrypt outermost, then at most one authcrypt, then at most one signature innermost, then the plaintext). This is a superset of the FR-ENV-02 emit set that additionally admits the receive-only `authcrypt(sign(plaintext))` (FR-ENV-03) and the protect-sender-plus-sign `anoncrypt(authcrypt(sign(plaintext)))` (spec Appendix C.3). Reject any other layering — sign-outside-encrypt (FR-ENV-05), `anoncrypt(anoncrypt)`, `authcrypt(authcrypt)`, `authcrypt(anoncrypt)`, more than one signature — as malformed. | Every spec Appendix C unpack vector succeeds; illegal layer orderings are rejected with `MalformedMessageException` before any content/consistency processing. |
| FR-ENV-05 | MUST | When signing+encrypting, sign first, then encrypt (nested JWM). | Sign-then-encrypt order enforced. |
| FR-ENV-06 | MUST | Encrypted form is a JWE in **General JSON Serialization** (multi-recipient). | Output contains `protected`/`recipients`/`iv`/`ciphertext`/`tag`. |
| FR-ENV-07 | SHOULD | Set envelope `typ` in the JWE/JWS protected header. | `typ` present in protected header. |

### 5.2 Key-agreement curves & point validation (§Curves)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ENC-01 | MUST | Support key agreement on **X25519, P-384, P-256**. | Round-trip on each curve via vectors. |
| FR-ENC-02 | MAY | Support **P-521** key agreement (optional curve). | P-521 vectors pass when enabled. |
| FR-ENC-03 | MUST | When decrypting from a **NIST curve**, verify the received ephemeral public key (`epk`) lies on the stated curve before use (invalid-curve / weak-point attack defense). Do not assume the JOSE library does this. | A crafted off-curve `epk` is rejected before key agreement. |
| FR-ENC-04 | MUST | A message addressed to multiple DIDs MUST be encrypted for each DID independently; if a DID has multiple key types, each type requires a separate encryption. **Not yet met:** `PackEncryptedAsync` builds one envelope for every DID in `Recipients` and uses one curve per DID; meeting this changes the pack result's shape and needs an API decision (separate issue). | Multi-DID, multi-keytype recipients each get a valid envelope. |

### 5.3 Content encryption (§Curves and Content Encryption Algorithms)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ENC-05 | MUST | Implement `A256CBC-HS512` content encryption (authcrypt + anoncrypt). | C.3 P-384/A256CBC-HS512 and 1PU/X25519/A256CBC-HS512 vectors pass. |
| FR-ENC-06 | SHOULD | Implement `A256GCM` content encryption (anoncrypt). | C.3 P-521/A256GCM vector passes. |
| FR-ENC-07 | MAY | Implement `XC20P` (XChaCha20-Poly1305) content encryption (anoncrypt). | C.3 X25519/XC20P vector passes. |
| FR-ENC-08 | MUST | Choose nonces/IVs using a cryptographically secure RNG. | IVs are unique across repeated packs of identical plaintext. |
| FR-ENC-09 | MUST NOT | Do not use `A256GCM` or `XC20P` for authcrypt (1PU mandates the AES_CBC_HMAC family). | Authcrypt with GCM/XC20P is refused. |

### 5.4 Key wrapping & protected headers (§Key Wrapping, §ECDH-1PU…, §ECDH-ES…)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ENC-10 | MUST | Anoncrypt uses `ECDH-ES+A256KW`; authcrypt uses `ECDH-1PU+A256KW`. `alg` set accordingly in `protected`. | `alg` matches mode in all vectors. |
| FR-ENC-11 | MUST | Generate one `epk` for all recipients; `epk` MUST be the same type and curve as the recipient keys. | Single shared `epk` per envelope. |
| FR-ENC-12 | MUST | Set common `epk`, `apv`, `alg` (and `apu`, `skid` for authcrypt) in the **protected** section, shared across recipients. | These headers are in `protected`, not per-recipient. |
| FR-ENC-13 | MUST | `apv` = base64url-no-pad( SHA-256( alphanumerically-sorted recipient `kid`s joined with `.` ) ). | Matches `apv` in C.3 vectors exactly. |
| FR-ENC-14 | MUST | For authcrypt, `apu` = base64url-no-pad( `skid` value ). Absent for anoncrypt. | `apu` present/absent per mode; matches vectors. |
| FR-ENC-15 | MUST | Encrypt payload FIRST, then use the resulting authentication `tag` in the KEK derivation when wrapping the CEK (1PU requirement and the common-protected-header requirement). | 1PU vectors decrypt only when ordering is correct. |
| FR-ENC-16 | MUST | Per recipient, produce `{ header: { kid }, encrypted_key }`; `kid` MUST be a DID-URL pointing to a `keyAgreement` verification method. | Recipient entries match vectors. |
| FR-ENC-17 | MUST | On authcrypt unpack, resolve sender `kid` from `skid` if present, else **from `apu`** (skid is not mandated by the 1PU draft). | A vector with no `skid` still authenticates via `apu`. |
| FR-ENC-18 | MUST | Implement the JOSE Concat KDF (RFC 7518 §4.6) for ECDH-ES and the 1PU variant per draft-madden-jose-ecdh-1pu-04 §2 (Z = Ze ‖ Zs). | KDF output matches vectors. |
| FR-ENC-19 | MUST | **Encrypt to every `keyAgreement` key of each recipient (#81).** This implements the spec's SHOULD (encryption §Key IDs: "the default recipients … SHOULD include all the `keyAgreement` entries representing Bob"), so any of the recipient's devices can decrypt. For each recipient DID, `PackEncryptedAsync` addresses every `keyAgreement` key on the negotiated curve, in document order, whose kid's DID part is that DID (a key whose kid names another DID is skipped, so one DID's document cannot shadow another's key). Kids are deduped (ordinal, first wins) across the envelope; keys without a kid are skipped. A bare-DID entry contributes at most 64 automatically selected keys, in document order, and the rest are skipped; a key the caller pins by DID URL anywhere in `Recipients` is taken as given and never counts toward that allowance, so the set does not depend on whether the pin or the bare DID comes first. Keys on other curves are skipped and logged at Debug (counts only). Override: a DID URL `did#fragment` in `Recipients` pins exactly that key, which MUST be one of the DID's `keyAgreement` keys (else `DidResolutionException`); a bare DID and a pin for the same DID combine, and the pin's curve constrains negotiation. Recipient kids over the FR-API-09 limits are still included. | Through `PackEncryptedAsync`: the spec's Appendix A/B Bob yields his three X25519 kids in document order, and `apv` equals the C.3 vector (same for authcrypt and the other curves); a device holding only the third key decrypts; a pin yields exactly that entry; `[bob, bob]` yields three entries, not six; with 65 same-curve keys, `[bob#x1, bob]`, `[bob, bob#x1]`, `[bob, bob#x65]` and `[bob#x65, bob]` each yield all 65 entries while `[bob]` yields 64. |

### 5.5 Signing (§Message Signing)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-SIG-01 | MUST | Verify EdDSA(Ed25519), ES256(P-256), ES256K(secp256k1). MUST sign with at least one; SHOULD support signing with all three. | C.2 EdDSA, ES256, ES256K vectors all verify. |
| FR-SIG-02 | MUST | Produce JWS in **JSON serialization**; accept both **General and Flattened** forms on receive. MAY emit either; didcomm-dotnet emits **General** (a `signatures` array) for every signer count, matching the shape of the Appendix C signed vectors and of what both SICPA reference implementations parse. | Flattened and General both unpack; a single-signer pack emits General. |
| FR-SIG-03 | MUST | The JWS `kid` MUST be a DID-URL in the signer's `authentication` relationship. Verification MUST confirm the key is authorized there before/while verifying the signature; reject if not, regardless of cryptographic validity. | A cryptographically valid signature by a non-`authentication` key is rejected. |
| FR-SIG-04 | MUST | Signed payload is the base64url-encoded plaintext JWM. Signed envelope `typ` SHOULD be set (`application/didcomm-signed+json` on the envelope; construction header may use `JWM`). | Matches C.2 protected-header decoding. |
| FR-SIG-05 | SHOULD | A standalone signed message SHOULD contain a `to` header. | A standalone signed message includes `to`. |
| FR-SIG-06 | MUST | When signing **and** encrypting (sign-then-encrypt), the inner signed JWM MUST contain a `to` header (anti-surreptitious-forwarding). The library MUST refuse to produce, and MUST reject on unpack, a sign-then-encrypt message whose inner signed JWM lacks `to`. | Sign-then-encrypt without inner `to` is refused on both pack and unpack. |

### 5.6 DID Rotation (§DID Rotation)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ROT-01 | MUST | Support the `from_prior` header: a JWT with `sub`=new DID, `iss`=prior DID, `iat`=rotation time, signed by a key authorized in the prior DID. Header `kid` identifies that key. | A rotation JWT round-trips and validates. |
| FR-ROT-02 | MUST | On receiving a message from an unknown DID, check for `from_prior`; validate JWT signature against the prior DID's authorized key (`kid`); extract `iss`; bind context to the known sender; use the new DID/DID-Doc thereafter. | Validation fails on bad signature or unauthorized kid. |
| FR-ROT-03 | MUST | The rotation message MUST be encrypted, MUST use the new DID, and MUST authenticate the sender (authcrypt `skid` or an inner signature) — on a plain anoncrypt envelope `from` is attacker-settable, so a rotation assertion there is not bound to a held key. | Plaintext-only and anoncrypt (unauthenticated-sender) rotation rejected; authcrypted/signed rotation validates. |
| FR-ROT-04 | MUST | Include `from_prior` on each message until a message arrives addressed to the new DID; thereafter it MAY be ignored. | Surfaced in `UnpackResult` metadata. |
| FR-ROT-05 | MUST | Messages sent before a rotation but received after it MUST be ignored (compromise-mitigation). | Library enforces `exp`/`nbf` freshness (with clock skew) and sender-authentication on the rotation JWT; full monotonic out-of-order detection needs per-relationship state and is delegated to the application, which receives `iss`/`iat`/`exp` in `UnpackResult.FromPrior`. |
| FR-ROT-06 | MAY | Support relationship termination: omit `sub` in `from_prior` and send without `from`. | Termination form parses. |

---

## 6. Functional Requirements — DID Resolution & Secrets

> **DID resolution is provided by net-did (NetDid), not reimplemented here.** NetDid is a W3C-DID-Core-conformant .NET 10 library exposing `NetDid.IDidResolver` (`Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions?, CancellationToken)` + `bool CanResolve(string did)`) with method implementations for `did:key`, `did:peer`, `did:webvh`, `did:dht`, and `did:ethr`. didcomm-dotnet's job is to *consume* that resolver and adapt its `DidResolutionResult` into the key material DIDComm needs. **`did:web` is deliberately excluded on security grounds** (§6.2, DD-08). See DD-01, DD-08, DD-09.

### 6.1 Resolver adapter over net-did (DD-01; §Key IDs, §DID Document Keys)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-DID-01 | MUST | Depend on `NetDid` and consume `NetDid.IDidResolver` directly. Do NOT define a competing DID-method resolver hierarchy or reimplement `did:key`/`did:peer`/etc. didcomm-dotnet accepts an injected `NetDid.IDidResolver`; if none is supplied, build NetDid's default composite resolver (key + peer + webvh + dht + ethr). | A NetDid resolver injected via DI is used for all resolution; unknown method surfaces as `DidResolutionException`. |
| FR-DID-02 | MUST | Provide `IDidKeyService` + `NetDidKeyService` adapter that maps a NetDid `DidResolutionResult` / `DidDocument` into DIDComm needs: enumerate `keyAgreement` verification methods, enumerate `authentication` verification methods, resolve embedded vs referenced methods, and expose `publicKeyJwk`/`publicKeyMultibase` as usable JWKs. | Appendix B.1/B.2 DID Docs, when returned by a stub NetDid resolver, yield correct keyAgreement/authentication key sets. |
| FR-DID-03 | MUST | **Key-relationship mapping (per §Key IDs / §Message Signing):** Authcrypt **sender AND recipient** encryption keys are sourced from **`keyAgreement`** — authcrypt is an ECDH-1PU key-agreement operation, and on unpack the sender `skid` is resolved through the sender's `keyAgreement` section. **Anoncrypt** recipient keys likewise come from `keyAgreement`. Only **signed-JWM** keys come from **`authentication`** (the JWS `kid` MUST be an authentication/authorization key — cf. FR-SIG-03). The adapter selects per relationship and maps NetDid key representations (Ed25519, X25519, P-256, secp256k1, and any P-384/P-521 present) to `ICryptoProvider` inputs. | Authcrypt pack/unpack uses `keyAgreement` keys for both parties (skid resolves via sender's `keyAgreement`); signed messages use `authentication` keys; selection is correct for each curve. |
| FR-DID-04 | MUST | If NetDid does not already cache, the adapter caches resolutions: indefinite for deterministic methods (`did:key`, `did:peer`), TTL for network methods (`did:webvh`, `did:ethr`, `did:dht`; default 5 min, configurable; honor HTTP cache headers where available). Concurrent resolutions of the same DID coalesce. Detect NetDid's own caching to avoid double-caching. | Repeated deterministic resolves do no extra work; concurrent network resolves hit the source once. |
| FR-DID-05 | MUST | Verify (and pin via integration test) that NetDid's `did:key` and `did:peer` resolvers cover the curves DIDComm requires for key agreement — X25519, P-256, **P-384** — and the signing curves Ed25519/P-256/secp256k1, including Ed25519→X25519 `keyAgreement` derivation for did:key. If a required curve is missing from NetDid, raise it against the NetDid backlog rather than working around it in didcomm-dotnet. | A did:key with a P-384 key resolves to a usable keyAgreement key; an Ed25519 did:key yields a derived X25519 keyAgreement key. |

### 6.2 `did:web` is excluded (DD-08)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-DID-06 | MUST NOT | The library MUST NOT resolve `did:web` DIDs. When a `did:web:` DID is supplied anywhere (as `from`, an entry in `to`, a `next`/routing target, a service-endpoint `uri`, an OOB invitation `from`, etc.), fail fast with a specific, documented error (`UnsupportedDidMethodException` carrying method=`web`) whose message states that `did:web` is intentionally unsupported for security reasons and recommends `did:webvh`. This MUST be a distinct, recognizable failure — not a generic "method not found" or a silent fallthrough. | Passing a `did:web:` DID throws `UnsupportedDidMethodException` with the documented message at every entry point. |
| FR-DID-07 | MUST | The exclusion MUST be observable and consistent: `did:web` MUST NOT be advertised as a supported method via Discover Features (FR-PROTO-05) or any capability surface; the rationale MUST be documented in README and API docs; and no `did:web` resolver may be registered in the default DI graph (and registering one MUST NOT silently re-enable it). | Discover-features disclosures never include `did:web`; default DI graph contains no `did:web` resolver. |

> Rationale (DD-08): `did:web` anchors trust in DNS + web PKI + domain control and provides no verifiable history or key pre-rotation, so a domain takeover or CA/TLS compromise allows silent, undetectable key substitution. `did:webvh` ("DID Web + Verifiable History") is the hardened successor and is supported via net-did; consumers needing a web-hosted DID should use it.

### 6.3 Secrets (DD-02, DD-09)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-SEC-01 | MUST | Define `ISecretsResolver` (find one / find present subset). Provide NO production key store in `DidComm.Core`. | Interface present; no default in-memory store ships in `Core`. |
| FR-SEC-02 | MUST | When no `ISecretsResolver` is registered, fail fast with an actionable error pointing to docs. | DI build throws with guidance message. |
| FR-SEC-03 | MUST | Key lookup is two-phase: resolve DID via net-did → discover kids; then `ISecretsResolver` supplies the private key for sign/decrypt/1PU-sender. | Decryption requests only the kids present in `recipients`. |
| FR-SEC-04 | SHOULD | Provide an optional adapter (separate package or `Core` extension) that backs `ISecretsResolver` with a `NetDid.IKeyStore`, so an application that already mints DIDs with net-did can surface those private keys to didcomm-dotnet without a second key store. The adapter must not weaken DD-02 (still no production store shipped). | A `NetDid.IKeyStore` populated via NetDid create-ops resolves the matching kid for decryption. |
| FR-SEC-05 | SHOULD | Ship a test-only in-memory `ISecretsResolver` in the **test** assembly seeded from Appendix A. | Vector tests use it. |
| FR-SEC-06 | SHOULD | Support **non-extractable (opaque) custody** — HSM / cloud KMS / OS keychain / MPC / `NetCrypto.IKeyStore` — where the private scalar never leaves the secure boundary. Define an optional `IOpaqueKeyResolver` capability (`ResolveSignerAsync` → `ISigner`, `ResolveKeyAgreementAsync` → `IEcdhKey`) an `ISecretsResolver` MAY also implement; when present the facade routes the **only two** private-key operations — a raw JWS signature and an ECDH shared-secret derivation — through those handles instead of decoding a private `Jwk`. Everything downstream of the signature/`Z` (Concat-KDF, A256KW key wrap, AEAD, header assembly, signature normalization) is public-data math and is unchanged. The extractable `ISecretsResolver` path stays the default and is byte-for-byte unchanged; the two coexist and may be mixed per kid. `NetDidKeyStoreSecretsResolver` becomes a sufficient sole resolver for an HSM-backed agent. **Timing caveat:** the JWE decrypt is constant-work whether or not a recipient key is held (the library always runs key agreement, with a decoy when unheld), but resolving a held key first incurs one backing-store round-trip only on the held path — on a network HSM/KMS that prologue is a residual held-vs-unheld timing signal this layer cannot mask, mitigated by fronting the receive endpoint with auth / a rate-limiter and the FR-API-07 receive-rejection floor (#35). | A wallet whose keys live only in a non-extractable `IKeyStore` can authcrypt / anoncrypt / sign on send and unpack on receive with no private key bytes leaving the store (interop round-trips); the keystore resolver returns public-only JWKs (`d` absent). |

---

## 7. Functional Requirements — Pack / Unpack API

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-API-01 | MUST | `PackEncryptedAsync` supports: recipient DID(s); optional `from` (authcrypt vs anoncrypt); optional `signFrom` (sign-then-encrypt); content-encryption selection; `protectSender` to apply `anoncrypt(authcrypt(...))`; `forward` to apply routing. | All FR-ENV-02 compositions reachable via params. |
| FR-API-02 | MUST | `PackSignedAsync` produces `signed(plaintext)`; `PackPlaintextAsync` produces plaintext. | Outputs match expected media types. |
| FR-API-03 | MUST | `UnpackAsync` auto-detects envelope by structure (`ciphertext`→JWE; `signatures`+`payload`→JWS; `typ`/plain→plaintext), unwraps recursively (anon→auth→sign→plaintext), and applies all FR-CONSIST checks. | A triple-nested `anoncrypt(sign(plaintext))` and `anoncrypt(authcrypt(plaintext))` unpack to the original plaintext. |
| FR-API-04 | MUST | `UnpackResult` exposes metadata: encrypted?, authenticated?, non-repudiation?, anonymous-sender?, enc alg, kw alg, sig alg, signer kid, sender kid, recipient kids, `from_prior` (if any), and the detected envelope stack — plus, on the provenance-capable key-service path, the same-document `VerifiedKeyBinding` evidence for sender/signer/recipient (FR-CONSIST-07), and the advisory FR-CONSIST-04 recipient-addressing outcome (`RecipientAddressing`, #59): non-positional, public-get/internal-init properties so external callers can read but never forge them, and additive over the v1.3.0 positional constructor (binary compatible). | Metadata correct for each composition; bindings present iff the key service implements `IDidKeyBindingService`. |
| FR-API-05 | MUST | Enforce `expires_time` on unpack: an expired message is reported (configurable: reject vs warn). | Expired message flagged. |
| FR-API-06 | MUST | Honor `max_receive_bytes` if configured: oversized inbound message is rejected with problem code `me.res.storage.message_too_big` (and/or transport 413). | Oversized message rejected with that code. |
| FR-API-07 | MUST | Typed exception hierarchy under `DidCommException`: `MalformedMessageException`, `CryptoException`, `DidResolutionException`, `UnsupportedDidMethodException` (incl. the deliberate `did:web` rejection, FR-DID-06), `SecretNotFoundException`, `TransportException`, `ProtocolException`, `ConsistencyException`. | Each failure path throws the right type. |
| FR-API-08 | SHOULD | Provide DI registration: `services.AddDidComm(b => …)` with fluent `UseNetDidResolver(...)` (accepts an existing `NetDid.IDidResolver` or builds NetDid's default), `UseSecretsResolver`, `UseTransport`, `AddProtocol`, `Configure`. Interop cleanly with `NetDid.Extensions.DependencyInjection` when the host already registered net-did. `DidComm` registered as singleton (thread-safe per NFR-03). | DI builds a working singleton that resolves via the registered NetDid resolver. |
| FR-API-09 | MUST | **Bound wire identifiers before DID parsing (#80).** Wire-supplied DIDs (`from`, each `to` entry, `from_prior` `iss` and `sub`) MUST NOT exceed **2048** UTF-16 code units. Wire-supplied key ids MUST NOT exceed **4096**, and their DID part (everything before the first `/`, `;`, `?` or `#`) MUST NOT exceed **2048**. That covers every JWS signer `kid` the verifier looks up (protected or per-signature header, at any layer), every JWE `recipients[].header.kid`, the authcrypt sender kid (from `skid`, or decoded from `apu`), the `from_prior` header `kid`, and forward `body.next`. A kid is its DID plus a `#fragment`, hence the headroom, and the DID-part rule holds the DID inside it to the DID limit. Both checks are linear scans, never a regex. An over-long signer, sender, `from_prior` or `next` value is rejected before any DID-URL parse, DID resolution or secrets lookup sees it: as `MalformedMessageException` from `UnpackAsync` and `ForwardMessage.TryParse`, and as `ProtocolException` from `FromPriorValidator` (its malformed-JWT type). An over-long **recipient** kid is not a rejection. It is treated as a key the agent does not hold and never reaches the secrets resolver, so one recipient's long kid cannot make the envelope undeliverable to the others. That choice reads only envelope content and adds no possession signal. When kids are skipped, the parser MUST scan the kept entries first, in envelope order, so a skipped alias of a held key never wins the scan; the FR-CONSIST-08 label check is unchanged. On pack, an own signer or authcrypt sender key whose kid exceeds these limits is never selected. Error messages MUST NOT echo the value. As a backstop, `DidSubjectOf` and `RejectUnsupportedMethod` never pass NetDid more than 4096 characters. The bound is per value: it does not cap how many identifiers an envelope carries. | For each rejected position: a value exactly at each cap is accepted; cap + 1 (in total length and in DID-part length) and a near-`max_receive_bytes` value are rejected with the stated type, without echoing the value, and without reaching the DID resolver or the secrets resolver. A multi-recipient JWE carrying another recipient's over-long kid unpacks for a holder, the over-long kid never reaches the secrets resolver, and a stranger's failure is unchanged. A held key also published under an over-long alias kid listed first still unpacks, whatever order `FindPresentAsync` returns, and a label swap between kept entries is still rejected. Pack skips an own key whose kid exceeds the limits and, when none is left, fails with the existing no-usable-key exception types. |

---

## 8. Functional Requirements — Routing & Mediation

(§Routing Protocol 2.0, §Service Endpoint, §Using a DID as an endpoint)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-ROUTE-01 | MUST | Implement the `forward` message (`https://didcomm.org/routing/2.0/forward`) with `next` in `body` (REQUIRED) and the onward payload as REQUIRED `attachments`. | Forward message matches §Messages shape. |
| FR-ROUTE-02 | MUST | Sender forwarding algorithm: encrypt for final recipient; then loop the recipient DID-Doc's `serviceEndpoint.routingKeys` **in reverse order**, wrapping+anoncrypting a `forward` for each key; transmit outermost to the endpoint `uri`. | 1-hop and 2-hop routes build correctly. |
| FR-ROUTE-03 | MUST | **Conformance shape:** parse a `DIDCommMessaging` `serviceEndpoint` as a single **object** or an **array of objects** (the 2.1 form; the `uri` is the string field inside the object). Order indicates preference; deliver to exactly one endpoint. A bare-string `serviceEndpoint` is **not** the DIDComm conformance shape — accepting it is an explicitly labeled compatibility tolerance (DD-10), off by default or clearly documented, and MUST NOT be treated as the canonical form. | Object and array-of-objects parse as conformant; a bare string is handled only via the DD-10 tolerance and is flagged as non-canonical. |
| FR-ROUTE-04 | MUST | Support `uri` being a **DID** (mediator-as-endpoint): resolve it, require a `DIDCommMessaging` service, and **implicitly prepend the mediator's `keyAgreement` keys** to `routingKeys`, as one forward layer addressed to all of the mediator's keys on one curve (routing Example 1). A `routingKeys` entry that is a bare DID is likewise one layer addressed to all that DID's keys, as didcomm-python and didcomm-jvm do; a DID URL entry addresses exactly that key. At most 8 forward layers, counting a mediator-as-DID layer, are built. A longer route is refused as soon as the recipient's service entry is read: the recipient's own keys and service entry are necessarily resolved first, since the route is in that entry, but no mediator or routing-key DID is resolved for an over-long route. Avoid recursive endpoint resolution (mediator DID-Docs use plain transport URIs). | Endpoint Example 1 & 2 from spec route correctly. |
| FR-ROUTE-05 | MUST | Mediator role: receive `forward`, read `next`, transmit the payload attachment onward; if recipient pre-registered extra routing keys, re-wrap once per key (between read and transmit). | Mediator relays singly- and multiply-keyed routes. |
| FR-ROUTE-06 | MAY | Support rewrapping mode (mediator re-anoncrypts payload into a fresh `forward` to keep onion size constant; outer `to` is the receiver). The `expires_time` and `delay_milli` headers of a `forward` are parsed here; their relay-time semantics are FR-ROUTE-09 (expiry, MUST) and FR-ROUTE-10 (delay, MAY). | Rewrapping produces a valid double-packaged message. |
| FR-ROUTE-07 | MUST NOT | Do not honor `please_ack` on `forward` messages at a mediator. | Mediator ignores `please_ack`. |
| FR-ROUTE-08 | SHOULD | On transmission failure, try another endpoint or retry later (failover). | Failover attempted across multiple endpoints. |
| FR-ROUTE-09 | MUST | **Relay-time expiry.** A `forward` whose `expires_time` has already passed when the handler processes it, and a queued payload whose enclosing forward's `expires_time` passes before it is delivered, pushed or picked up, MUST be discarded and never delivered, regardless of the FR-API-05 reject-vs-warn setting. Expiry is judged with `DidCommOptions.Clock` and the same clock-skew allowance the unpack path uses for FR-API-05 (this is stricter than the spec's optional honoring and consistent with FR-API-05, which already refuses expired inbound at unpack). Only the forward's own header is enforceable: the inner payload's `expires_time` is encrypted and invisible to the mediator. The discard is logged with the forward's message id only — no DIDs, no payload. | A forward with `expires_time` in the past reaches the handler and nothing is queued or sent; a queued payload whose expiry passes before `delivery-request` is purged and `status.message_count` excludes it; the log entry carries the id and no DID. |
| FR-ROUTE-10 | MAY | **`delay_milli` as a scheduling hint.** A mediator MAY honor `delay_milli` on a `forward`: a positive value is the earliest delivery time (`received + n` ms); a negative value means a uniformly random delay in `[0, \|n\|]` ms, drawn once at receipt. The result is stored as an absolute not-before time on the queued or pushed payload. A mediator that does not honor the hint MAY send a problem report but is not required to (spec §Routing). Honoring the hint never delays the transport acknowledgement (202 is returned immediately). | `delay_milli: 500` → the payload is not deliverable (queue or live) before 500 ms on an injected clock; `delay_milli: -500` → the not-before time lies in `[now, now + 500 ms]` across repeated runs; the HTTP response is 202 before the delay elapses. |
| FR-ROUTE-11 | MUST | **Forward as a registered protocol handler; silent drop of unroutable forwards.** `https://didcomm.org/routing/2.0/forward` is dispatched through the protocol handler registry like any other PIURI and is advertised with `roles: ["mediator"]` only when the mediator role is registered. A forward is relayed only when the outer envelope was encrypted and decrypted with a key the mediator holds (`RecipientKid` non-null) and `body.next` (a DID or DID URL, reduced to its DID subject per §4.3 `DidSubjectOf`) names a registered recipient — one known to the mediator's recipient registry: a Coordinate Mediation grant (FR-MED-05) or a host-registered recipient directory (sample 04). A forward that is plaintext, signed-only, or otherwise not decrypted by a held key, or whose `next` is unknown, is dropped and logged (id only) with 202; the recipient lookup is constant-work with respect to registration state; no problem report is sent toward the sender (D4); the transport result is 202 in every case. `please_ack` stays ignored (FR-ROUTE-07). | discover-features discloses `routing/2.0` with `roles: ["mediator"]` only with the mediator role registered; a plaintext forward and a signed-only forward are dropped (202, nothing relayed, a log entry); unknown `next` → 202, nothing queued, no outbound message; the 202 is written before the forward sink runs (the sink is detached from the request), so response timing does not depend on registration state; the registry lookup itself is constant-work. |

### 8a. Return-Route and Queue Transport extension (FR-RR-*)

(Source: "DIDComm Messaging Return-Route and Queue Transport Extension", `decentralized-identity/didcomm-messaging` → `extensions/return_route/main.md`. It is an unversioned extension with no status line and no didcomm.org page; Coordinate Mediation 3.0 and Message Pickup 3.0 both state "The `return_route` extension must be supported by both agents". Placement (D1): header model in `DidComm.Core`, receive side in `DidComm.AspNetCore`, client side in `DidComm.Transports.*`. FR-TRN-03/10 are amended to defer to this section.)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-RR-01 | MUST | **Header and values.** Recognize the plaintext header `return_route` with values `none` (default; an absent header means `none` for that message — "No messages should be returned over this connection" — on an established WebSocket session the session mode is unchanged, FR-RR-05), `all` ("Send all messages for this DID over the connection") and `thread` (send only messages matching the DID and the thread named in `return_route_thread`; the extension does not say where that attribute lives — it is read as a sibling top-level header, DD-12). An unrecognized or non-string value is treated as `none` and logged; `thread` without a usable `return_route_thread` — absent, not a string, longer than 2048 characters, or not a possible thread id (a `thid` is made of RFC 3986 unreserved characters, FR-MSG-02/11) — is treated as `none` and logged, and the value is never surfaced; it never fails unpack or dispatch. Provide `MessageBuilder.WithReturnRoute(mode, thread?)` emitting the header exactly (`"return_route":"all"`). | Header parse table: absent → none; `none` / `all` / `thread` (+ `return_route_thread`) → the respective mode; garbage or non-string, and `thread` whose `return_route_thread` is absent, over-long or not made of unreserved characters → none with a log entry and the message still dispatched; the builder emits `"return_route":"all"` byte-exact. |
| FR-RR-02 | MUST | **Scope: direct peers only.** The header is honored only between the immediate sender and the agent it connects to directly ("not valid when routing keys are in use"): it is never honored on a `forward`, never propagated into a forward's payload, and never acted on by a relay. | An authcrypt `forward` carrying `return_route: all` opens no session and is relayed exactly as it would be without the header. |
| FR-RR-03 | MUST | **Eligibility: authcrypt sender; session bound to that DID.** A return-route session is created only when the envelope is encrypted and contains an authcrypt layer (`authcrypt(…)`, `authcrypt(sign(…))` or `anoncrypt(authcrypt(…))`: the unpack result carries an authcrypt sender key id, `SenderKid` — `UnpackResult.Authenticated` alone does not suffice, because a verified signature also sets it; FR-CONSIST-05/06 satisfied) and the mode is `all` (`thread` is parsed and surfaced, FR-RR-01, but opens no session in this version: CM 3.0 and pickup 3.0 use only `all`; see §15.2); the session peer is `DidSubjectOf(SenderKid)`. Anoncrypt, signed-only, `anoncrypt(sign(…))` and plaintext inbound never open a session ("trust … MUST be associated with DIDComm encryption or signing, not from the socket connection itself"). Everything sent over the session MUST be addressed to exactly that peer (`to = [peer]`) and authcrypted from the DID whose key decrypted the inbound; a reply that would address anyone else is not sent. Live (WebSocket) sessions are registered and bounded, per DID and in total. The per-DID bound is applied first: a DID at its bound has its oldest session evicted to make room — also when the total bound is reached, because the total does not grow; only a registration that would grow the total is refused at the total bound, and another DID's session is never evicted. A message whose session was refused is still dispatched, without a session: nothing is written back for it on the socket (FR-RR-05). Evicting a WebSocket session closes the socket so the peer observes it. An HTTP session lives for one request (FR-RR-04): it is never registered, never counted against either bound, never evicted and raises no session notifications, so a burst of POSTs from a peer can never evict that peer's live WebSocket session. The eligibility and addressing rule is the single rule shared by HTTP and WebSocket and by the pre-existing `AllowSameSocketReplies` convenience (FR-TRN-10). | Eligibility matrix: anoncrypt / signed-only / `anoncrypt(sign)` / plaintext → no session; authcrypt and `authcrypt(sign)` → session with peer = sender DID subject; `anoncrypt(authcrypt)` → session; a message Mallory authcrypts with `return_route: all` can never receive Bob's traffic; a replayed message cannot open a session for another DID; registry bounds and eviction tested on WebSocket sessions; a peer at its own bound replaces its oldest session while the registry is full; at the total bound a new DID's message is dispatched without a session and no frame is written; a flood of fresh DIDs cannot evict an existing DID's session; HTTP `return_route: all` requests leave the registry count unchanged, raise no notification and never evict that DID's live WebSocket session. |
| FR-RR-04 | MUST | **HTTP binding (receive side).** On the ASP.NET Core receive endpoint, when a session exists and a handler produced a message, respond `200` with the packed message as the body and `Content-Type: application/didcomm-encrypted+json`; otherwise `202` with an empty body. At most one message is ever returned per request ("HTTP transports will only be able to receive at most one message at a time"); a further push on an HTTP session fails (`TrySendAsync` returns false) and the transport does not queue it. The 400 / 413 / 415 paths and the rejection-timing floor are unchanged. The 200-vs-202 distinction is observable only by the authenticated sender, by design. No separate switch: an authcrypted `return_route: all` request (FR-RR-03) is honored on HTTP by default (the response is addressed only to the authenticated sender). | POST an authcrypt `ping` with `return_route: all` → 200 with the exact media type and a body that unpacks to an authenticated `ping-response` addressed to the sender; the same POST without the header → 202 empty (existing tests unchanged); a second push on the same HTTP session returns false. |
| FR-RR-05 | MUST | **WebSocket binding (receive side).** A WebSocket connection becomes a session at its first eligible message (FR-RR-03, admitted under FR-RR-08) and lasts for the connection lifetime unless the registry evicts it (FR-RR-03); the session mode changes only when a later message from the session's own peer on that connection carries `return_route`; a header-less message leaves the session mode unchanged (CM 3.0: the header is set "once per established websocket", so `all` asserted once is sufficient). A later `none` downgrades the session — nothing more is pushed until a later `all` resumes it — and so do `thread` (this version opens no thread-scoped session, FR-RR-03, and pushing everything would exceed what the peer asked for; §15.2) and a malformed value, which FR-RR-01 reads as `none`. On HTTP each POST is its own connection, so an absent header is `none` (FR-RR-01). Multiple messages may be pushed, each as one WebSocket message (FR-TRN-09), through a single writer so frames never interleave; nothing is pushed after the receive side has processed a downgrade or a close (a frame already being written completes). Pending pushes are bounded per session (host-configurable, default 16, plus the one being written): capacity is reserved before a push is packed, a push beyond the bound is refused at once (`TrySendAsync` returns false) without being packed, and a push never waits on the socket. On close the session is unregistered and its own `Closed` signal fires — the authoritative, prompt per-session signal (pickup live mode resets on it, FR-PICK-06); the registry's `SessionOpened` / `SessionClosed` notifications are best-effort: never raised inline in registration or unregistration, delivered one at a time in the order of the state changes by one notifier per registry, and bounded (host-configurable, default 1024 waiting) — under subscriber backpressure the oldest are dropped and counted — so a subscriber can never delay a registration, an eviction, a lookup or the closing of an evicted session, and a blocked one holds at most one thread. Two sockets for one DID are two sessions. Enabled by the existing `AllowSameSocketReplies` switch (default off); not available under STOMP. The mediator composition (`MapDidCommMediator`, §12 Phase 7) enables return-route on both bindings. The socket's peer is fixed by its first eligible message — also when the total bound refused that session, in which case only that peer may open it with a later eligible message — and a session that has closed is never reopened on its socket; a message authenticated by another DID on the same socket is dispatched without a session: it neither opens, re-binds nor changes the session, and its reply is not written. | Two handler pushes arrive on the same socket in order; close → registry empty, the session's `Closed` signalled and `SessionClosed` raised after the unregistration; a subscriber that blocks in `SessionOpened` or `SessionClosed` under reconnect churn keeps the outstanding notifications within the bound and delays neither a registration nor the close of an evicted socket; a subscriber that keeps up receives every notification once, in state-change order; a second socket for the same DID → two sessions; a later header-less message on the socket does not stop pushes; a later `return_route: none`, `thread` or malformed value from the session's peer does, and a later `all` resumes them; pushes racing a `none` are refused from the moment it is processed; with the writer blocked, pushes beyond the pending bound return false at once and are never packed; another DID's message on the session socket is dispatched and its reply is not written; STOMP → no session and no push. |
| FR-RR-06 | MUST | **Queue transport URI.** `didcomm:transport/queue` is recognized as a `DIDCommMessaging` `serviceEndpoint.uri` meaning "messages are held at the sender for pickup by the recipient". Parse-and-recognize only: no transport is registered for it, and an attempt to send to such an endpoint — resolved from the recipient's service entry or supplied as an explicit endpoint override — fails before any transport activity with a `TransportException` whose text names the queue URI and says the sender holds the message for pickup (return-route / message-pickup), and does not echo the DID. A resolved endpoint is known only once the recipient's DID is resolved and the message packed, because the endpoint lives in the service entry; that resolution may itself use the network (e.g. `did:webvh`), but nothing is handed to a transport. | A DID document whose service `uri` is `didcomm:transport/queue` parses; `SendAsync` to it — resolved, or as an endpoint override — throws `TransportException` before any transport activity (no transport is called), and the exception text carries no DID. |
| FR-RR-07 | MUST | **Client side.** `SendOptions.ReturnRoute` is nullable: `null` (the default) emits no header and leaves any `return_route` the builder already set (FR-RR-01) untouched; `ReturnRouteMode.None` emits `"return_route":"none"` explicitly; `All` and `Thread` (+ `ReturnRouteThread`) emit those values — stamped before packing, and a non-`null` `SendOptions.ReturnRoute` overrides a builder-set header. Everywhere below, "a send with `ReturnRoute: All`/`Thread`" means the effective header after this step. `ReturnRoute` of `All` or `Thread` is refused with `InvalidOperationException` once the recipient's service entry is read and the route would contain at least one forward layer (`routingKeys` non-empty or `uri` is a DID) — before any mediator or routing-key DID is resolved and before packing (FR-RR-02); a direct recipient (plain `uri`, no `routingKeys`) is allowed even though `SendAsync` packs with `Forward: true` by default, because that route has zero forward layers. HTTP: a `200` response whose content type is `application/didcomm-*+json` is read into `TransportResult.ResponsePayload`, capped at `DidCommOptions.MaxReceiveBytes` (an over-cap body is discarded and logged; the send still counts as accepted); `202` or a non-DIDComm content type yields no payload. The facade unpacks the payload into `SendResult.Returned` (no dispatch — the caller asked for it) only when the response unpacks as encrypted **and** authenticated and `DidSubjectOf(SenderKid)` equals the DID the request was addressed to; anything else is discarded and logged (the send still counts as accepted). The same rule applies to messages the WebSocket inbound loop dispatches. An unpack failure surfaces as `TransportException`, never a raw parser exception (FR-API-07 parity). WebSocket: an opt-in inbound receive loop (`ReceiveInbound`, default off) surfaces pushed messages, size-capped, to the protocol dispatcher. **Connection affinity (WebSocket).** Affinity is separate from header emission. A send with `ReturnRoute: All` (or `Thread`) from local DID L to peer P at endpoint E opens, or reuses, the session socket S(E, L, P), selected by (endpoint, local sender DID, remote peer DID) and never by endpoint alone, because the receive side binds a socket to its first authenticated sender (FR-RR-05). While S exists, **every** message from L to P at E uses S regardless of the header: `null` (omitted) preserves the session — the receive side leaves the mode unchanged (FR-RR-05), which is how CM 3.0's once-per-socket `all` and a headerless Pickup 4.0 `messages-received` reach the session and travel over it; an explicit `None` is sent **on S** so the receiver downgrades it (FR-RR-05), after which the client retires S once the send completes (a graceful close that drains frames already received; the receiver pushes nothing after it processes the `None`), and later sends from L to P use the shared endpoint-keyed pool until a new `All` opens a fresh session socket. Traffic with no session for (E, L, P) — unrelated one-way application sends — keeps the shared endpoint-keyed pool. Pushed or returned messages arriving on S are unpacked for, and attributed to, L. | Header stamped exactly `"return_route":"all"`; a routed recipient + `ReturnRoute` → `InvalidOperationException`, no mediator DID resolved, nothing packed; a direct recipient + `ReturnRoute` succeeds; 200 + DIDComm type → `Returned` is the authenticated reply; 202 → null; an anoncrypt body, or one authenticated by a different DID, under a DIDComm content type → `Returned` is null and a log entry; a 100 MB body is cut at the cap without being allocated; garbage under a DIDComm content type → wrapped `TransportException`; a WS push reaches a registered test handler and a socket drop ends the loop with no unobserved exception; two local DIDs enrolling through one `WebSocketDidCommTransport` instance at one mediator endpoint with `ReturnRoute: All` use two distinct sockets and each receives its own grant (`to` = its own DID, `thid` = its request); with `ReturnRoute` unset (no session) both share one pooled socket; on one session socket the sequence `All` → headerless request → explicit `None` yields the headerless request's reply over that same socket, no push after `None`, and a later `All` opens a new socket; a headerless Pickup 4.0 `messages-received` sent after live deliveries travels over the session socket and the pushes continue. |
| FR-RR-08 | MUST | **Replay.** Replay protection and the `created_time` admission rule below apply to exactly two classes of inbound, both carrying an authcrypt layer (FR-RR-03: an authcrypt sender key id — a verified signature alone does not count): **(a)** a message that carries `return_route` other than `none` (a malformed value is `none`, FR-RR-01), and **(b)** a message whose `type` is under a mediator-service PIURI (`https://didcomm.org/coordinate-mediation/2.0`, `https://didcomm.org/coordinate-mediation/3.0`, `https://didcomm.org/messagepickup/3.0`, `https://didcomm.org/message-pickup/4.0`) — the messages that can open a session or mutate mediator state. The replay key's peer is the DID subject of that authcrypt sender key id, never the plaintext `from`. A message that is not admitted is not dispatched and opens or changes no session; HTTP answers it `202` with an empty body and a WebSocket writes nothing. The rule applies on every receive endpoint that dispatches to protocol handlers or hands the host a return-route session, and one cache serves all of a host's endpoints, so a request admitted over HTTP is a duplicate over WebSocket and vice versa; the pre-existing callback-only endpoints, which can open no session and host no mediator-service handler, pass unpacked messages to the host's callback unchanged. **Ordinary application traffic is exempt:** a message without `return_route` whose `type` is outside those PIURIs is dispatched as today with only FR-API-05 expiry applied, so a timestamp-free or delayed application message is never rejected by this rule; `forward` (routing/2.0) is exempt likewise (anoncrypt, FR-ROUTE-11). Plaintext, anoncrypt, signed-only and `anoncrypt(sign(…))` messages are outside the scope even when they carry `return_route` or a mediator-service type: they pass replay admission and are dispatched, can open no session (FR-RR-03), and can mutate no mediator state because the mediator-service handlers drop them (FR-MED-05, FR-PICK-01). (1) **Freshness window.** A request is admitted only if `created_time` is present and lies in `[now − replay_window, now + ExpiresClockSkew]`, where `replay_window` is host-configurable (default 5 minutes); a request older than the window is dropped and logged (id only) with 202, exactly like a duplicate. (2) **Timestamp-free requests.** By default (`require_created_time = true`, applying to the scope above and nothing else) a request without `created_time` is dropped and logged with 202. A host MAY set `require_created_time = false`; such a request is then admitted once and its id retained for `replay_window`, after which the identical captured request is accepted again — a stated limitation of that configuration, not a guarantee of this row. (3) **Atomic admission.** Admission is an atomic check-and-insert on the key (peer DID subject, `id`) under one lock or lock stripe for that key, so concurrent duplicates admit exactly one. (4) **Retention.** An entry is retained until at least `created_time + replay_window` — the instant from which the same request is rejected on age (`created_time < now − replay_window`) — and in practice until `created_time + replay_window + ExpiresClockSkew`, a harmless over-retention; entries are never evicted earlier, so no gap exists between cache retention and age rejection. (5) **Budget.** The cache has a global entry budget (host-configurable) covering all peers; per-peer buckets are bounded and the number of buckets is bounded by the same global budget, so fresh DIDs cannot grow it without limit. When the budget is saturated a new (peer, `id`) that cannot be recorded is **not admitted** (dropped, logged, 202, a metric) — admitting unrecorded requests would reopen replay; admission resumes as entries age out. Saturation therefore degrades availability, never integrity, and the budget is sized to the endpoint's accepted rate × `replay_window`. An in-scope message whose `id` exceeds a host-configurable length cap (default 512 characters) is not admitted either (dropped, logged, 202), so an oversized id can never be stored budget-many times. A replayed request re-executes nothing (`recipient-update`, `messages-received`, `mediate-request`) and opens no session; the session holder is the connection that presented the admitted message, and confidentiality rests on `to = [peer]` authcrypt, not on session ownership. | Immediate replay → 202, handler invoked once in total, no second session. Clock advancement (injected clock): a replay just inside `replay_window` is rejected by the cache; the clock is advanced past `created_time + replay_window + skew` and the same request is rejected on age; an entry is still present until that instant. Timestamp-free request → dropped by default; with `require_created_time = false` it is admitted once, rejected within the window, and admitted again after it (the documented limitation). N concurrent POSTs of one message → exactly one handler invocation; a request admitted over HTTP is dropped when replayed over the host's WebSocket endpoint. Capacity: with the global budget full, a new message from a new peer is dropped with 202 and no existing entry is evicted; after entries age out it is admitted. Fresh-DID flood: bucket count never exceeds the budget and existing peers' entries survive. Scope: a timestamp-free trust-ping without `return_route` is dispatched normally; the same ping with `return_route: all` and no `created_time` → 202, dropped, logged; a timestamp-free authcrypt `status-request` (no `return_route`) → dropped; a signed-only or `anoncrypt(sign)` message carrying `return_route: all` without `created_time`, and an unauthcrypted pickup request, pass admission and reach their handlers with no session; an in-scope message whose `id` is over the cap → dropped; a 10-minute-old application message without `return_route` is subject to FR-API-05 only and reaches its handler. |
| FR-RR-09 | MUST | **Client timestamps.** Every outgoing message that falls under the FR-RR-08 scope carries `created_time`. `DidCommClient.SendAsync` stamps `created_time` from `DidCommOptions.Clock` — the same clock FR-RR-08 and FR-API-05 use — whenever the outgoing plaintext's `return_route` header is `all` or `thread` after `SendOptions.ReturnRoute` is applied (whether it came from `SendOptions` or from `MessageBuilder.WithReturnRoute`, FR-RR-01), or the message `type` is under a mediator-service PIURI, and the message has no `created_time` yet. `MediationClient` (FR-MED-09), `PickupClient` (FR-PICK-11) and the Discover Features bootstrap query (FR-MED-09) therefore need no caller action; acknowledgements are included (`messages-received`, which under Pickup 4.0 carries no `return_route` and is caught by the PIURI clause). A `created_time` the caller supplied is **preserved, never refreshed**: a stale request is sent as-is and rejected by the receiver (FR-RR-08), not laundered into a fresh one. `MessageBuilder.WithCreatedTime` stays the explicit path; `TrustPing.CreatePing` and `DiscoverFeatures.CreateQuery` are unchanged (stamping happens at send). Messages outside the scope are sent without a `created_time` being added. | Default client against default server with no caller-supplied timestamps: a trust-ping with `ReturnRoute: All` over HTTP → 200 `ping-response` (FR-RR-04); discover-features → `mediate-request` → `recipient-update` → `delivery-request` → `messages-received` → `status` 0 succeeds over HTTP and over WebSocket; a request built with an explicit `created_time` 10 minutes old is sent with that value unchanged and gets 202 with no reply; an application message without `return_route` is sent with no `created_time` added; the stamped value equals the injected clock; a message carrying `return_route: all` via `MessageBuilder.WithReturnRoute` with `SendOptions.ReturnRoute` unset is stamped and, over WebSocket, uses the session socket. |

### 8b. Coordinate Mediation (FR-MED-*)

(Source: https://didcomm.org/coordinate-mediation/3.0/, status Production; PIURI `https://didcomm.org/coordinate-mediation/3.0`; roles `mediator`, `recipient`; "No protocol specific errors exist." Package: `DidComm.Mediator` (D1). The protocol page is silent on threading and on proof of DID ownership; DD-11 and DD-14 record the choices.)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-MED-01 | MUST | **Protocol identity and advertisement.** Implement `https://didcomm.org/coordinate-mediation/3.0` with roles `mediator` and `recipient`. A host that registers the mediator role discloses the PIURI with `roles: ["mediator"]` through Discover Features 2.0 (FR-PROTO-05); an ordinary agent does not. | A discover-features `protocol` query matching `https://didcomm.org/coordinate-mediation/*` discloses 3.0 with `roles: ["mediator"]` only when the mediator role is registered. |
| FR-MED-02 | MUST | **Message shapes (exact).** `mediate-request` (`body: {}`) → `mediate-grant` `{"routing_did": [string, …]}` or `mediate-deny` (`body: {}`) — for `mediate-request` / `mediate-deny` emit `body: {}`; an absent `body` is accepted on receive as a tolerance (the didcomm.org examples omit it); `recipient-update` `{"updates": [{"recipient_did", "action": "add" \| "remove"}]}` → `recipient-update-response` `{"updated": [{"recipient_did", "action", "result": "client_error" \| "server_error" \| "no_change" \| "success"}]}` with entries in input order; `recipient-query` `{"paginate"?: {"limit", "offset"}}` ("if present must include `limit` and `offset`") → `recipient` `{"dids": [{"recipient_did"}], "pagination"?: {"count", "offset", "remaining"}}`. Parsers reject an unknown `action` or `result`, a non-array `updates`, and a `paginate` missing `limit` or `offset` with the existing `MalformedMessageException` / `ProtocolException` types and no echo of wire values. A `mediate-grant` whose `routing_did` is a string (the 2.0 shape) is accepted on parse; the library always emits the array. | Byte-exact round trips against checked-in fixtures derived from the didcomm.org 3.0 page examples and normalized to valid JSON; a rejection table covering each malformed shape; string `routing_did` accepted, array emitted. |
| FR-MED-03 | MUST | **Return-route and transport.** Recipient requests carry `return_route: all` — on every HTTP POST and once per WebSocket — and the mediator's replies go back over the session (FR-RR-04/05). A request without the header is still answered: the handler produces the reply and the endpoint decides delivery; a missing header is the recipient's defect and never faults the mediator. | Over the TestServer, `mediate-request` with `return_route: all` returns the grant as the HTTP 200 body; the same request without the header returns 202 and the handler still produced the grant. |
| FR-MED-04 | MUST | **Threading (DD-11).** Every mediator response sets `thid` = the request's `thid` if present, else its `id` (FR-THR-01; CM 3.0 is silent; pickup 4.0 does this; harmless to peers that ignore it). The recipient role correlates on `thid` and falls back to message type + mediator DID for peers that omit it. | Each response's `thid` equals the request's `thid` (else its `id`) in the fixtures; a client awaiting a grant completes on a reply that carries no `thid` from the queried mediator. |
| FR-MED-05 | MUST | **Peer identity and ownership policy (D5, DD-14).** The peer of every request is the authenticated sender DID (`DidSubjectOf(SenderKid)`); a `mediate-request`, `recipient-update` or `recipient-query` without an authcrypt layer (plaintext, anoncrypt, signed-only or `anoncrypt(sign(…))`; FR-RR-08's replay scope leaves these to this rule) is never granted or applied and is not answered — no session can exist for it (FR-RR-03) — and is logged with the id only (same rule as FR-PICK-01). One grant exists per peer DID. A `mediate-request` from a peer that already holds a grant is idempotent: the existing grant is re-issued with the same `routing_did` and registrations retained; `mediate-deny` is produced only by policy. A `recipient_did` may be registered under one grant only: `add` of a DID already under the same grant → `no_change`; under another grant → `client_error`; `remove` of a DID not under the caller's grant → `client_error`; a store fault → `server_error` (caught and logged, never thrown through dispatch). The policy is pluggable: `IMediationPolicy` decides grant/deny (default: grant) and may require stronger proof of DID ownership (the protocol page's endnote acknowledges squatting). `recipient_did` MUST be a bare DID (no path, query or fragment); a DID URL is answered `client_error`, never normalized. The registry is keyed by DID subject (§4.3), never by raw string; for the FR-ROUTE-11 lookup `next` is reduced to its DID subject. Because the spec fixes the `result` enum, an enrolled peer can learn whether a DID is registered under another grant (`client_error`); this is accepted — it is visible only to authenticated, enrolled peers and is removable by an `IMediationPolicy` that requires proof of ownership. | An anoncrypt, signed-only or `anoncrypt(sign)` `mediate-request` → no grant stored, no reply produced, a log entry; Mallory cannot `add` a DID Bob registered (`client_error`) nor `remove` it (`client_error`); Bob re-adding his own DID → `no_change`; a faulting store → `server_error` with the dispatcher unaffected; a custom policy that denies is honored. |
| FR-MED-06 | MUST | **Pagination bounds.** `limit` is clamped to a host-configurable maximum page size (default 100); `limit` ≤ 0 yields an empty page; `offset` beyond the end yields an empty page with `remaining: 0`. `pagination.count` is the number returned, `offset` the offset applied, `remaining` the number left after the page. Without `paginate` the first page at the maximum size is returned and `pagination` is still present. Non-integer `limit` / `offset` are malformed. | `limit: 0`, `offset > count`, `limit > max`, and the no-`paginate` case produce the stated pages; `limit: "10"` is rejected as malformed. |
| FR-MED-07 | MUST | **`routing_did` shape.** Each `routing_did` in `mediate-grant` MUST be a DID whose document has a `DIDCommMessaging` service whose `serviceEndpoint.uri` is a URI whose scheme the transport router handles (`http(s)` / `ws(s)`, FR-TRN-01; TLS in production per §9.2), never a DID, so senders resolve it per FR-ROUTE-04 (implicit `keyAgreement` prepend) with no recursive endpoint resolution ("A DID representing a mediator SHOULD NOT use alternative endpoints in its own DID Document"). The recipient's resulting service entry is `{uri: routing_did, accept: ["didcomm/v2"]}`; no explicit `routingKeys` are needed for the one-mediator case. | A mediator configured with a routing DID whose service `uri` is itself a DID fails fast at startup; Alice's `SendAsync(Forward: true)` to Bob (service `uri` = `routing_did`) builds exactly one forward layer addressed to the mediator's keys (the FR-ROUTE-04 Example 1 assertion). |
| FR-MED-08 | MUST | **Identifier caps and store bounds (D7).** `recipient_did` and `routing_did` are wire DIDs under FR-API-09 (≤ 2048 UTF-16 code units, linear scan, rejected before any DID parse or resolver call, never echoed in error text, exceptions or logs; a protocol reply carries the value only where the protocol requires it (`updated[].recipient_did`, `dids[].recipient_did` in `recipient` and `keys[].recipient_did` in the 2.0 `keylist`, `status.recipient_did`, `delivery.recipient_did`) and only after the FR-API-09 cap check passed). The in-memory store bounds grants and recipients per grant (host-configurable); exceeding the per-grant bound yields `client_error`. Grant lookup by `recipient_did` (used by FR-ROUTE-11) is constant-work with respect to registration state. No DID appears in log messages at Information or above. | cap + 1 `recipient_did` → `MalformedMessageException` without echo and without a resolver call; 10k adds → `client_error` once the bound is hit, memory bounded; registered-vs-unregistered lookup timing statistically indistinguishable (existing timing-harness style). |
| FR-MED-09 | MUST | **Recipient role (client).** Provide `MediationClient`: `RequestMediationAsync` (sends `mediate-request` with `ReturnRoute: All`; over HTTP the reply arrives in `SendResult.Returned`, over WebSocket via inbound dispatch; `mediate-deny` is a result, not an exception), `UpdateRecipientsAsync`, `QueryRecipientsAsync` (page walker), and `DidCommServiceBuilder.ForMediation(grant)` producing the FR-MED-07 service entry (plus `.WithRoutingKeys(...)` for a two-mediator route). Correlation uses a bounded pending map with a timeout whose error names the request id, not the DID. The client negotiates the version through Discover Features first, preferring 3.0 and falling back to 2.0 (FR-MED-10); the Discover Features query is itself sent with `ReturnRoute: All`; every request, including that query, is timestamped per FR-RR-09; a mediator that discloses neither version fails the enrollment with a typed exception naming the mediator's disclosed features, not a DID. | Against the Phase 7 TestServer: enroll → DID document → Alice sends → delivered, over HTTP and over WebSocket; deny path returns a denied result; timeout → `TimeoutException` naming the request id; a 2.0-only fake mediator is driven through the 2.0 shapes. |
| FR-MED-10 | SHOULD | **Coordinate Mediation 2.0 compatibility.** (SHOULD for the library; required for the Phase 7 interop exit criterion, since the available external mediators speak only 2.0.) Also handle `https://didcomm.org/coordinate-mediation/2.0` (what the Identus and Blocktrust mediators speak): `mediate-grant.body.routing_did` is a **string**; `keylist-update` / `keylist-update-response` / `keylist-query` / `keylist` replace `recipient-update` / `recipient-update-response` / `recipient-query` / `recipient`; `keylist.body.keys` (not `dids`); the inner fields (`recipient_did`, `action`, `result`) and their values are identical; requests carry `return_route: all`. 2.0 and 3.0 share one registry (a DID registered through either is the same tenant) and both PIURIs are disclosed. Enabled by default. | 2.0 fixtures round-trip byte-exact; a 2.0 `keylist-update` and a 3.0 `recipient-update` from one peer mutate one grant; discover-features discloses both PIURIs; disabling 2.0 removes its disclosure and handler. |

### 8c. Message Pickup (FR-PICK-*)

(Source: https://didcomm.org/messagepickup/3.0/, status Production; PIURI `https://didcomm.org/messagepickup/3.0`; roles `mediator`, `recipient`; "the `return_route` header extension must be set to `all` in all request submitted by the `recipient`". Package: `DidComm.Mediator` (D1). 3.0 does not define how a live-delivered message is framed; DD-13 records the choice. Message Pickup 4.0 (`https://didcomm.org/message-pickup/4.0`, note the renamed PIURI) is the follow-up in FR-PICK-12.)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-PICK-01 | MUST | **Protocol identity and advertisement.** Implement `https://didcomm.org/messagepickup/3.0` with roles `mediator` and `recipient`; the mediator role is disclosed with `roles: ["mediator"]` only when registered. Every recipient request carries `return_route: all` (every HTTP POST / once per WebSocket); a request without an authcrypt layer (plaintext, anoncrypt, signed-only or `anoncrypt(sign(…))`; FR-RR-08's replay scope leaves these to this rule) is neither applied nor answered (logged, id only). | Disclosure present only with the pickup mediator role registered; an anoncrypt, signed-only or `anoncrypt(sign)` `status-request` → no reply and a log entry; such a `messages-received` removes nothing. |
| FR-PICK-02 | MUST | **`status-request` → `status`.** `status-request` body: `recipient_did` (optional; when present the mediator MUST report only that DID's queue). `status` body: `message_count` (REQUIRED) and, optionally, `recipient_did` (MUST echo the request's value when it had one), `longest_waited_seconds`, `newest_received_time` and `oldest_received_time` (UTC epoch **seconds** as integers), `total_bytes`, `live_delivery`. A `status` message MUST NOT be put on the queue and MUST only be sent while the recipient is actively connected (an HTTP request awaiting its response, or a WebSocket session). | Fixture round-trip for a `status` with only `message_count`; `recipient_did` filter scoping (one grant cannot see another grant's queue); times are integer seconds; a `status` produced with no session is never stored in the queue. |
| FR-PICK-03 | MUST | **`delivery-request` → `delivery` or `status`.** `delivery-request` body: `limit` (REQUIRED non-negative integer; the mediator SHOULD deliver up to it) and `recipient_did` (optional filter). The mediator clamps `limit` to a host-configurable batch maximum (default 10) and rejects a non-integer or negative `limit` as malformed without allocation; `limit: 0` delivers nothing and is answered with `status` (not malformed). `delivery` carries `thid` = the request `id`, `body.recipient_did` only when the request had one, and `attachments: [{id, data: {base64}}]` holding the encrypted DIDComm messages exactly as received — "The ONLY valid type of attachment … is a DIDComm v2 Message in encrypted form" — relayed as bytes (base64 pass-through, no re-encoding or inner validation). Over HTTP the single 200 response carries one `delivery` with up to `limit` attachments. The batch is bounded by **count and bytes**: the mediator selects the longest FIFO prefix of eligible messages, up to `limit`, whose estimated packed `delivery` size — the `delivery` JSON (per attachment `4 × ⌈n/3⌉` base64 bytes plus its descriptor) expanded again by `4 × ⌈m/3⌉` for the envelope's base64url ciphertext, plus the fixed JWE overhead — does not exceed a host-configurable `max_delivery_bytes` (default: `DidCommOptions.MaxReceiveBytes`, 1 MiB; hosts set it to what their recipients accept, since a recipient discards an over-cap response, FR-RR-07). After packing, the actual size is verified against the budget and, if the estimate was short, the last attachment is removed and the message re-packed until it fits. So that every retained payload is deliverable, a payload whose single-attachment `delivery` would exceed `max_delivery_bytes` is **not admitted** at enqueue (dropped, logged with id only, 202 — FR-PICK-10); The packed size of a single-attachment `delivery` is about 16/9 × the payload (inner attachment base64, then the envelope's base64url ciphertext) plus fixed overhead, so the largest admissible payload is `max_admissible_payload` = min(`MaxReceiveBytes`, the largest n whose single-attachment packed `delivery` ≤ `max_delivery_bytes`) — about 9/16 of `max_delivery_bytes` (≈ 576 KiB at the 1 MiB default; hosts that must relay `MaxReceiveBytes`-sized payloads raise `max_delivery_bytes` and their recipients' `MaxReceiveBytes` together); the mediator computes and logs it at startup and fails fast only if `max_delivery_bytes` cannot carry any payload (below the fixed overhead). A count-limited batch therefore never produces a response the recipient cannot accept, and pickup 3.0's "deliver up to the limit" is satisfied with fewer items. If no message is available a `status` MUST be sent immediately instead. Delivery is FIFO per recipient; a message whose not-before time (FR-ROUTE-10) has not passed or whose expiry has passed (FR-ROUTE-09) is never delivered. | HTTP `delivery-request{limit: 3}` with 5 queued → 200 with one `delivery` carrying 3 attachments and `thid` = request id; empty queue → `status`; `limit: 2^31-1` clamped, `limit: 0` → `status`, `limit: -1` and `limit: "10"` rejected, all without allocation; a delayed message is skipped until its not-before time; five 300 KiB payloads queued under a 1 MiB `max_delivery_bytes`, `limit: 10` → each `delivery` carries exactly 1 attachment (two would pack to ≈ 1,092 KB after the inner base64 and the envelope's base64url, over the budget; one packs to ≈ 534 KiB), the recipient acks after each, five deliveries then `status` — over HTTP and over a WebSocket session; every packed response is asserted ≤ `max_delivery_bytes`; a payload whose single-item delivery would exceed the budget is refused at enqueue with 202 and a log entry; with the 1 MiB default a 600 KiB payload is refused at enqueue (its packed single-item `delivery` would exceed the budget) while a 500 KiB payload is admitted; a `max_delivery_bytes` below the fixed envelope overhead fails startup. |
| FR-PICK-04 | MUST | **Attachment ids and acknowledgement.** Each attachment `id` is a mediator-generated opaque value, unique to the mediator and scoped to the grant — never the inner message id, which the mediator cannot read. Delivered messages MUST NOT be deleted until acknowledged by `messages-received` `{"message_id_list": [id, …]}`; delivered-but-unacknowledged messages hold a lease (not re-delivered within it) and become deliverable again when the lease expires or, for a WebSocket session, when that session closes; HTTP leases expire by timeout only. On `messages-received` the mediator removes the listed messages (unknown or foreign ids are ignored and never echoed) and SHOULD reply with an updated `status`. | A second `delivery-request` within the lease does not re-deliver (lease timeout injected via the clock); after lease release it does; an ack removes the messages and `status.message_count` drops; an ack with unknown ids is ignored and `status` is still sent; a recipient cannot ack ids delivered to another grant. |
| FR-PICK-05 | MUST | **Multiple recipients.** A message that arrives addressed to several registered recipients (reachable through the host-facing enqueue API; a routing/2.0 `forward` names a single `next`) MUST be queued for each recipient independently; one recipient's acknowledgement MUST NOT remove the other recipients' copies. | An enqueue for N recipients yields N independent copies with distinct ids, each acknowledged separately. |
| FR-PICK-06 | MUST | **Live mode rules.** Live mode is disabled by default and only activated by the recipient (`live-delivery-change`); it MUST only be enabled on a persistent transport (a WebSocket session, FR-RR-05); messages that arrive while it is off MUST be queued; the live flag is per session, and a new inbound connection starts with live mode disabled (reset when the session's `Closed` signal fires, FR-RR-05); messages already in the queue are not affected and must still be requested with `delivery-request`. | `live-delivery-change{true}` over WS → `status{live_delivery: true}`; socket close → the next connection's `status{live_delivery: false}`; queued messages remain queued when live mode turns on. |
| FR-PICK-07 | MUST | **`live-delivery-change` reply and problem report.** `live-delivery-change` body: `live_delivery` (boolean). The mediator MUST answer with `status`. When `live_delivery: true` arrives on a connection incapable of live delivery (HTTP, or no session) the mediator SHOULD instead send a Report Problem 2.0 message, type `https://didcomm.org/report-problem/2.0/problem-report`, with `pthid` = the thread of the request (`thid` if present, else `id`), `body.code` = `e.m.live-mode-not-supported` and `body.comment` = `Connection does not support Live Delivery` (exact strings), and the live flag stays off. | HTTP `live-delivery-change{true}` → a problem report with the exact type, code, comment and `pthid`; WS → `status`. |
| FR-PICK-08 | MUST | **Live-mode framing under 3.0 (DD-13).** A live-delivered message is pushed as a `delivery` message with exactly one attachment and no `thid` (there is no `delivery-request`), and the recipient is expected to acknowledge it with `messages-received`; a live delivery is the store's own copy under a lease (FR-PICK-04), so an unacknowledged one simply has its lease released on lease expiry or disconnect — no second copy is ever made and the FR-PICK-10 budget is never exceeded by live mode. This is the Pickup 4.0 rule applied under 3.0, where the page is silent. | With live mode on, an enqueue for the recipient arrives as a `delivery` push with one attachment; `messages-received` clears it; a push not acknowledged before the socket closes is deliverable by `delivery-request` on the next connection. |
| FR-PICK-09 | MUST | **Delivery order (D6).** For a routed payload the mediator tries, in order: (1) a live return-route session for the recipient's grant — the payload is first admitted to the store (FR-PICK-10 budget) and pushed under a lease, (2) the pickup queue, (3) an optional host-registered direct push (off by default); a direct-push transport failure falls back to the queue so an accepted message is never lost; a direct push that returns 2xx counts as delivery and the queued copy is removed (or never created); a transport failure leaves it queued. Pushes and queue placement respect the not-before time (FR-ROUTE-10). | Live session present → the payload is admitted to the store, pushed, and held leased until `messages-received` (retained count +1 until the ack, 0 after); no session → queued; direct push configured and failing → queued. |
| FR-PICK-10 | MUST | **Limits (D7).** Per-grant quotas `max_queue_messages` and `max_queue_bytes` are host-configurable and bound the grant's **total retained storage**: queued, leased (delivered-not-acknowledged, FR-PICK-04) and live-delivered-not-acknowledged payloads (FR-PICK-08) all count against the same budget — a live push never bypasses it. Capacity is reserved by admitting the payload to the store before any push; a payload that cannot be admitted is not pushed. `DidCommOptions.MaxReceiveBytes` applies to every retained payload (nothing larger is admitted), and so does the single-item delivery bound of FR-PICK-03. A payload exceeding a quota is dropped and logged (id only); the transport result stays 202 (no sender-side oracle) and the recipient observes the cap through `status.total_bytes`. Attachment ids and `message_id_list` entries are bounded by FR-API-09 (never echoed); the length of `message_id_list` is bounded by a host-configurable `max_ack_ids` (default: the delivery batch maximum × 10), checked before any allocation; sessions per DID are bounded (FR-RR-03). | A flood past `max_queue_bytes` stays within the bound and still returns 202; a live session that reads a stream of N pushes without acknowledging, then disconnects, never has more than `max_queue_bytes` / `max_queue_messages` retained at any instant — before the pushes, during, and after the disconnect releases the leases (no requeue copy is made) — and pushes beyond the budget are dropped (202, logged), not retained; a 10^6-entry `message_id_list` (past `max_ack_ids`) is rejected as malformed without allocation; a cap + 1 id → malformed, no echo. |
| FR-PICK-11 | MUST | **Recipient role (client).** Provide `PickupClient`: `GetStatusAsync`; `FetchAsync` (sends `delivery-request`, unpacks each attachment with the recipient's own client, and acknowledges only the attachments that unpacked — a failed unpack is reported to the caller and left on the queue, never silently acknowledged); `AckAsync`; `SetLiveDeliveryAsync` (WebSocket only; refused with `InvalidOperationException` over HTTP before anything is sent); `ReceiveLiveAsync` (acknowledges each item after the consumer has taken it). An in-thread `e.m.live-mode-not-supported` problem report surfaces as a typed protocol exception carrying the code. A rewrapped (double-packaged) delivery unwraps at the recipient (FR-ROUTE-06, receiver side). Every request and acknowledgement is timestamped per FR-RR-09, and acknowledgements reuse the session socket (FR-RR-07 affinity). | Fetch → unpack → ack → `status` 0 over HTTP; live mode over WS; a batch with one undecryptable attachment returns the good ones and leaves the bad one queued; HTTP `SetLiveDeliveryAsync` throws before any send. |
| FR-PICK-12 | SHOULD | **Message Pickup 4.0 follow-up.** Also implement `https://didcomm.org/message-pickup/4.0` (renamed protocol; coexists with 3.0 and is selected per session by what the recipient last spoke). Differences from 3.0: `delivery-request.limit` → `message_count_limit`, and the attached count MUST NOT exceed it; times in UTC epoch **milliseconds**; `longest_waited_seconds` removed; empty queue → a `delivery` with an empty `attachments` array (not `status`); `status` carries `thid` = the `status-request` id; `delivery.thid` is optional (absent for live deliveries); `messages-received` no longer carries `return_route`; no `status` is sent in reply to `messages-received` under 4.0 (the 3.0 SHOULD is gone and the 4.0 request carries no `return_route`); live mode MUST use `delivery` + `messages-received` and an unacknowledged message MUST be re-queued (FR-PICK-08 made normative); problem-report type `https://didcomm.org/message-pickup/4.0/problem-report` with the same code and comment. The DIDComm v1 form is out of scope (§2.2). | 4.0 fixtures byte-exact; empty queue → empty `delivery` (4.0) vs `status` (3.0) on one store; a 3.0 and a 4.0 recipient on one mediator interleave without cross-talk; both PIURIs disclosed. |

---

## 9. Functional Requirements — Transports

(§Transports)

### 9.1 Common

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-TRN-01 | MUST | Define `IDidCommTransport` (scheme, send, can-handle) and a router selecting transport by `serviceEndpoint.uri` scheme. | Router dispatches http(s)/ws(s). |
| FR-TRN-02 | MUST | Transports carry the IANA media type of the content (e.g. via `Content-Type`). | Correct media type set per envelope. |
| FR-TRN-03 | MUST | Transports are delivery-only — no message effects/results return on the same channel — **unless the return-route extension (§8a) is in effect for a peer authenticated by an authcrypt layer (FR-RR-03)**, in which case replies flow back exactly as FR-RR-04/05 state. A sender MUST NOT assume a synchronous business response otherwise. | No synchronous business response assumed without `return_route`; with `return_route: all` from an authcrypt sender (FR-RR-03) the §8a behavior applies; from a signed-only or `anoncrypt(sign)` sender it does not. |

### 9.2 HTTPS (§HTTPS)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-TRN-04 | MUST | Send via HTTPS POST with `Content-Type` = the message's media type. | POST issued with correct header. |
| FR-TRN-05 | MUST | Treat any 2xx as success (202 recommended). | 202 and 200 both succeed. |
| FR-TRN-06 | SHOULD | Follow only temporary (307) redirects; do not follow 301/308. | 307 followed; 308 surfaced. |
| FR-TRN-07 | MUST | Provide an ASP.NET Core receive endpoint (`MapDidCommEndpoint`) that validates `Content-Type`, unpacks, dispatches to a protocol handler, and returns 202 (or 2xx). Enforce `max_receive_bytes`→413 (FR-API-06). | TestServer round-trip returns 202. |
| FR-TRN-08 | SHOULD | Add resilience (retry w/ backoff, circuit breaker, timeout) on send. | Retries on 5xx/timeout; breaker opens on sustained failure. |

### 9.3 WebSocket (§WebSockets)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-TRN-09 | MUST | Each DIDComm message is transmitted individually as one unit of encryption/signing; trust derives from the envelope, not the socket connection. One **WebSocket message** carries exactly one packed DIDComm message. (A WebSocket message MAY be split across multiple WebSocket frames at the protocol level; the implementation MUST reassemble before processing.) | One WebSocket message yields one packed DIDComm message; a fragmented WebSocket message reassembles and processes correctly. |
| FR-TRN-10 | MUST | Treat receive transports as one-way by default — a protocol reply is not an HTTP response body and no same-WebSocket reply is assumed — **unless the return-route extension (§8a) is in effect**, in which case replies flow back per FR-RR-04/05 under the key-binding rule below (FR-RR-03 is the same rule). Applications normally deliver replies out of band under explicit egress policy. The registry-aware WebSocket endpoint MAY opt into `AllowSameSocketReplies`, the switch for WebSocket return-route sessions (FR-RR-05); this convenience is not general egress and MUST fail closed: a same-socket reply or push is written only on a session opened by an encrypted inbound with an authcrypt layer (an authcrypt sender key id; a verified signature alone does not count) carrying `return_route: all`, and only from the local DID whose key decrypted that inbound to exactly its sender's DID (FR-RR-03). A header-less message on a socket without a session gets no reply. | Default HTTP/WS receive sends no protocol result; with `AllowSameSocketReplies` on, same-socket replies are written only on a session opened by an authcrypted `return_route: all` inbound and only for its key-bound pair; a header-less message on a socket without a session gets no frame; signed-only/`anoncrypt(sign)`/plaintext/anoncrypt/keyless/cross-tenant/fan-out cases emit no frame; with `return_route: all` from an authcrypt sender, HTTP 200 body / WS push per §8a. |
| FR-TRN-11 | SHOULD | Manage connection lifecycle: pooling by endpoint, keep-alive, reconnect with exponential backoff (1s base, 30s cap, 0.5 jitter — DD-05). Expose lifecycle events. | Reconnect after drop; events fire. |
| FR-TRN-12 | MAY | Support `application/didcomm-encrypted+json` content type when using STOMP over WebSocket. | STOMP framing optional. |

---

## 10. Functional Requirements — Protocols & Cross-Message Concerns

### 10.1 Protocol identity (§Protocol Identifier URI, §Message Type URI, §Semver Rules)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-PROTO-01 | MUST | Parse/validate MTURIs into (doc-uri, protocol-name, version, message-type) using the spec regex; match protocol+message ignoring case and punctuation. | Regex captures the four groups for all spec examples. |
| FR-PROTO-02 | MUST | Apply semver compatibility: same major + differing minor may interoperate at the older minor; patch is not used in URIs. | Version-matching logic unit-tested. |
| FR-PROTO-03 | MUST | Provide `IProtocolHandler` + a registry that dispatches by PIURI prefix. | Handler resolution by type works. |

### 10.2 Built-in spec protocols

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-PROTO-04 | MUST | **Trust Ping 2.0**: handle `ping` (`response_requested` default true) → reply `ping-response` with `thid`=ping.id; suppress reply when false. | Ping/response thread linkage correct. |
| FR-PROTO-05 | MUST | **Discover Features 2.0 (responder)**: handle `queries` (feature-types `protocol`, `goal-code`, `header`, `constraint`; `*` wildcard, prefix match) → `disclose`; ignore unrecognized feature-types; empty disclosures ≠ "unsupported". Support the `max_receive_bytes` constraint query/disclosure. | Wildcard + `max_receive_bytes` flows work. |
| FR-PROTO-05a | MUST | **Discover Features 2.0 (initiator)**: provide an async round-trip that sends a `queries` and awaits the correlated `disclose` — `QueryFeaturesAsync(from, to, queries, timeout, …)`. Correlate the reply by `thid` == the query `id`; the `timeout` bounds send + await and a transport failure MUST propagate unchanged (not be relabeled a timeout). Successful unpack MUST preserve one immutable snapshot of the exact verified plaintext plus copied trust metadata before mutable application/handler code sees the `Message`. A `disclose` may atomically claim a pending query ONLY when: the envelope authenticates the sender (authcrypt or verified signature); every present sender/signer key id has the same DID subject (§4.3) as plaintext `from`; that subject is the queried responder; signed plaintext `to` includes the requester; and an encrypted reply's `RecipientKid` names the requester that actually decrypted it. A rejected disclosure MUST NOT complete or cancel the waiter. Correlation runs through a guarded, synchronous, internal `IInboundCorrelator`, NOT the best-effort observer queue. Its inline work is limited to bounded immutable header/trust checks and an atomic claim; body parsing runs on the awaiting requester's continuation, including for reentrant/loopback sends. Response and cancellation compete through one terminal transition, so only a response winner parses. The responder-side handler is unchanged: an inbound `disclose` remains a terminal leaf. **Out-of-band reply delivery is responder application policy** per FR-TRN-10 (`DidCommClient.SendAsync`, key-bound to the authenticated peer/local decryptor); the library performs no automatic out-of-band endpoint egress. The existing opt-in same-WebSocket convenience is separate: it is the WebSocket return-route session (FR-RR-05, FR-TRN-10) and may send only on a session opened by an encrypted inbound with an authcrypt layer carrying `return_route: all`, from the local DID that decrypted it to exactly its sender (FR-RR-03); signed-only, `anoncrypt(sign(…))`, plaintext, anoncrypt, keyless, cross-tenant and fan-out cases, and a header-less message on a socket without a session, fail closed. | Two-agent HTTP round-trip returns correlated disclosures with no manual injection; timeout and transport failures retain their categories; forged wrong-peer/requester/decryptor/key-binding replies are ignored; response/cancellation races settle once; real authcrypt + signed disclosures complete and tampered ones fail signature/AEAD at unpack; real same-socket tests prove decrypting-tenant selection and no frame for signed-only or `anoncrypt(sign)` input or for a header-less message on a socket without a session. |
| FR-PROTO-06 | MUST | **Empty 1.0**: support `https://didcomm.org/empty/1.0/empty` for header-only transmissions (used by ACKs). | Empty message packs/unpacks. |
| FR-PROTO-07 | MUST | **Report Problem 2.0**: build/parse `problem-report` with REQUIRED `pthid` (the failing thread's `thid`) and `code`; optional `comment` (with `{n}` interpolation from `args`), `args`, `escalate_to`, optional `ack`. | Example problem-report round-trips; interpolation honored (missing arg→`?`, extras appended). |
| FR-PROTO-08 | MUST | Implement the problem-code taxonomy: sorter (`e`/`w`), scope (`p`/`m`/state-name), descriptors (`trust`, `trust.crypto`, `xfer`, `did`, `msg`, `me`, `me.res[.net/.memory/.storage/.compute/.money]`, `req`, `req.time`, `legal`). Match by prefix. | Prefix matching recognizes `e.p.xfer.*` from a longer code. |
| FR-PROTO-09 | SHOULD | Implement warning→error escalation reply rules: reply `e.*` with scope ≥ original; new `id`; same thread. | Escalation reply constructed correctly. |
| FR-PROTO-10 | SHOULD | Implement cascading-problem guards: per-thread max-error count; on breach emit `e.p.req.max-errors-exceeded` and cease responding on that `thid`. | Breach stops further responses. |
| FR-PROTO-11 | MAY | **Trace 2.0**: support honoring the `trace` header by POSTing a `trace_report` to its URI when explicitly enabled. | On-demand trace report posts when enabled. |
| FR-PROTO-11a | MUST | Tracing MUST default to **off**: a `trace` request MUST be rejected/ignored unless the operator has explicitly configured safeguards (privacy/loop protection). | With default config, a `trace` header produces no report. |
| FR-PROTO-12 | MUST | **Inbound observer seam**: provide `IProtocolObserver` that lets an application observe inbound traffic whose PIURI is owned by a built-in handler (e.g. react to inbound `report-problem`) without replacing that handler. **Opt-in only:** `AddBuiltInProtocols()` registers NO default observer; loss-sensitive Discover Features correlation uses the separate inline hook in FR-PROTO-05a, so a default deployment has no observer/firehose surface. **Execution model:** successful unpack creates one immutable exact-plaintext/trust snapshot before dispatch. For each matching observer, enqueue MUST atomically reserve both one item and the snapshot's exact UTF-8 byte count **before** observer-private `Message` deserialization. Bounds cover queued plus in-flight work; count-full, cumulative-byte-full, oversized, write-failed, and shutdown-racing admissions perform no materialization and release/retain reservations exactly once. The dispatcher returns immediately, so slow/hung/faulting observers cannot gate reply delivery or change outcomes, and independent per-observer pumps prevent starvation. Overflow drops the newest item with rate-limited structured logging of the real message id/capacity/budget/drop count. Delivery is **best-effort / at-most-once**. Each accepted observer materializes its own deep clone from the immutable snapshot off-path; later handler/caller mutation cannot change it and observers cannot share mutable message state. No observer code (including `ProtocolUriFilter`) runs on dispatch: the filter is read once and guarded at construction. **Lifecycle:** shutdown atomically stops admission, completes writers, cancels cooperative callbacks, drains queued snapshots/barriers, releases their reservations, and observes every pump fault before disposing the CTS. A trusted callback that ignores cancellation cannot be killed by .NET; at most its single in-flight admitted snapshot may remain past the async-disposal grace until that callback returns. **Threat model:** observers are host-trusted DI-composition components, not a capability sandbox; payload cloning enforces message-mutation isolation only. | No default observer; handled/`NoHandler`/dropped outcomes observed; full item/byte bounds cause zero extra materialization under concurrent producers; exact Unicode byte accounting; private clones; filter/materializer/callback/logging faults isolated; enqueue/flush/dispose races leak no reservations; cooperative shutdown completes, while a non-cooperative callback retains only its unavoidable in-flight item and the rest of its queue is drained. |

### 10.3 Out-of-Band 2.0 (§Out Of Band Messages)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-OOB-01 | MUST | Build/parse `out-of-band/2.0/invitation` with REQUIRED `id`, `from`; optional `goal_code`, `goal`, `accept`; `attachments` carrying alternative protocol messages. | Spec invitation example round-trips. |
| FR-OOB-02 | MUST | Encode an invitation as a URL: `https://<domain>/<path>?_oob=<base64url(whitespace-stripped plaintext JWM)>`. Decode the same. | Spec base64url example matches byte-for-byte. |
| FR-OOB-03 | MUST | The invitation `id` becomes the `pthid` of the recipient's response (enables multiple independent threads from one invitation). | Response correlation by `pthid`. |
| FR-OOB-04 | SHOULD | Support short-URL form (`?_oobid=<guid>`) requiring an HTTP GET to retrieve the full message; do not use public URL shorteners. | Short-form GET retrieval works. |
| FR-OOB-05 | MAY | Support `web_redirect` (status + redirectUrl) on concluding ack/problem-report messages. | Redirect block parses. |

### 10.4 Threading & ACKs (§Threading, §ACKs)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-THR-01 | MUST | A thread is identified by `thid`; if absent on the first message, `id` is treated as `thid`. All subsequent messages MUST carry the same `thid`; differing `thid` ≠ same thread. | Thread grouping correct. |
| FR-THR-02 | MUST | `pthid` links a child thread to its parent and obeys `id` constraints; in >2-party child protocols, ensure each party learns `pthid` from the first child message they see (recommend echoing `pthid` on every child message). | Parent/child linkage preserved. |
| FR-THR-03 | SHOULD | Support `please_ack` (array of message ids; `""`=current) and `ack` (array of acknowledged ids, oldest→newest). An `ack`-bearing message is an explicit ACK regardless of type; use Empty 1.0 when only an ACK is needed. | ACK request/response honored. |
| FR-THR-04 | MUST | Prevent ACK loops: never honor more than one ACK request per message; never send a pure ACK that requests an ACK; never honor a pure ACK arriving in response to one's own ACK request. | Loop-guard unit-tested. |

### 10.5 Profiles & i18n (§Negotiating Compatibility, §i18n)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-PROF-01 | MUST | Recognize profile identifiers, emit/consume `didcomm/v2`, and read `accept` arrays on service endpoints / OOB to choose a compatible profile. | `accept`-based selection works. |
| FR-PROF-02 | SHOULD | On profile mismatch, attempt a mutually supported profile; optionally emit a problem-report. | Mismatch handled without crash. |
| FR-I18N-01 | SHOULD | Advertise/consume `accept-lang` (ranked IANA language codes) and `lang` headers; treat all other strings as locale-independent UTF-8. | Headers round-trip. |
| FR-I18N-02 | MUST | **Thread-scoped language preference.** A received `accept-lang` preference applies from that point **until it is changed or until the current protocol thread (`thid`) ends**, and the implementation MUST honor it for subsequent human-readable strings it emits on that thread when a matching language is available. The preference is **thread-scoped state**: it MUST NOT leak into other already-running interactions/threads. | A second message on the same `thid` is produced in the earlier-declared language; a concurrent message on a different `thid` is unaffected. |
| FR-I18N-03 | MUST | **`lang` interpretation.** When a `lang` header is present and the active protocol defines human-readable fields, those protocol-defined human-readable strings MUST be interpreted as being in that language (and, when this library produces them, emitted in that language where available). | A message with `lang=fr` carrying a protocol-defined human-readable field is treated/produced as French. |
| FR-I18N-04 | MAY | Emit `w.msg.bad-lang` / `e.msg.bad-lang` problem reports when no acceptable language is available. | Bad-lang report constructed. |

---

## 11. Non-Functional Requirements

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| NFR-01 | MUST | Target `net10.0`, C# 13, nullable enabled, warnings-as-errors, file-scoped namespaces. | `dotnet build /warnaserror` clean. |
| NFR-02 | MUST | Public API fully XML-documented; ship symbol packages (snupkg). | Doc coverage 100% of public surface. |
| NFR-03 | MUST | `DidComm` and all resolvers/transports/registries are thread-safe (singleton-friendly); shared state uses concurrent collections. | Concurrent pack/unpack stress test passes. |
| NFR-04 | MUST | No private key material or plaintext body is ever logged or placed in OpenTelemetry attributes. | Log/span audit test confirms redaction. |
| NFR-05 | SHOULD | OpenTelemetry `ActivitySource` "didcomm-dotnet" spans for pack/unpack/send/resolve/handle with semantic attributes (message type, alg, recipient count, did method). | Spans emitted with attributes. |
| NFR-06 | SHOULD | Structured logging via `ILogger<T>` using `LoggerMessage` source-gen. | Allocations-free logging on hot path. |
| NFR-07 | SHOULD | Performance (P99, excluding network/resolution): anoncrypt pack 1-rcpt < 2 ms; authcrypt pack 1-rcpt < 3 ms; unpack < 2 ms; `did:key` resolve < 0.1 ms. Provide BenchmarkDotNet suite. | Benchmarks meet targets on reference HW. |
| NFR-08 | MUST | Apache 2.0 with NOTICE; CI (GitHub Actions) builds+tests on Linux & Windows; release workflow packs+pushes all packages on tag. | CI green; `dotnet pack` emits all packages. |
| NFR-09 | SHOULD | Constant-time comparisons for tags/MACs; clear secret buffers after use where the platform allows. | Code review + targeted tests. |
| NFR-10 | MUST | Deterministic JSON for signing/`apv` inputs (stable member ordering, no incidental whitespace) so signatures and `apv` hashes are reproducible. | Re-serialization reproduces identical `apv`/signature. |

---

## 12. Phased Implementation Plan

Each phase lists the requirement IDs it closes and **exit criteria** that must all pass before the next phase starts. A short **agent kickoff prompt** is provided to seed an autonomous coding session; hand the agent this PRD plus the named FR sections.

---

### Phase 0 — Repository & JOSE-Composition Substrate

**Closes:** NFR-01, NFR-08 (scaffold), parts of FR-ENC-05/06/07 (the JOSE-specific AEADs that net-did does not ship), FR-ENC-18 (1PU wrapper around net-did's Concat KDF). FR-ENC-01/02/03 and FR-SIG-01 are **satisfied by `NetDid 1.3.0`** (raw ECDH for all four curves, off-curve point validation, sign/verify with format choice) and are exercised here only as integration smoke tests.

**Build:** solution + `Directory.Build.props` + `.editorconfig`; `DidComm.Core` (referencing `NetDid 1.3.0+`) and test project; a DIDComm-shaped `ICryptoProvider`/`DefaultCryptoProvider` that **delegates** sign/verify and raw ECDH to `NetDid.Core.ICryptoProvider` and **owns** the JOSE composition layer:

- `A256CBC-HS512` AEAD (RFC 7518 §5.2.5 — encrypt-then-MAC composition; not a generic primitive).
- `A256GCM` AEAD (thin wrapper over `System.Security.Cryptography.AesGcm`).
- `XC20P` AEAD (thin wrapper over `NSec.Cryptography.AeadAlgorithm.XChaCha20Poly1305`).
- `A256KW` key wrap (RFC 3394; implemented in DidComm.Core because BCL does not expose AES-KW publicly).
- `ECDH-1PU` KDF wrapper: composes `Z = Ze ‖ Zs` from two `NetDid.ICryptoProvider.DeriveSharedSecret` calls, threads the AEAD tag into `SuppPubInfo`, and calls `NetDid.Core.Crypto.Kdf.ConcatKdf.DeriveKey`.
- Base64url-no-pad codec.
- A DIDComm-shaped JWK overlay (`kid`, JOSE `alg`/`enc` hints) over `NetDid.Core.Jwk.JwkConverter`.

**Vendor:** Stage fixtures under `tests/DidComm.InteropTests/fixtures/` for now (schema + smoke manifest only). Migrate to the standalone `didcomm-dotnet-fixtures` git submodule (PRD §13.3) before Phase 2 closes. Add the `DidComm.InteropTests` data-driven runner skeleton (FR-IX-01/02) so fixtures execute as XUnit theories from Phase 2 onward.

**Exit criteria:**
- `DidComm.Core` builds against `NetDid 1.3.0` with `dotnet build /warnaserror` clean on Linux + Windows CI.
- A256CBC-HS512 round-trip + RFC 7518 §B.3 known-answer test passes.
- A256KW round-trip + RFC 3394 §4.1 (256-bit KEK wraps 256-bit data) known-answer test passes.
- A256GCM and XC20P round-trip + tampering rejection tests pass.
- ECDH-1PU KDF wrapper reproduces a known-answer derivation (own KAT built from a published 1PU test vector — Appendix C.3 1PU vector lands in Phase 2; Phase 0 uses a synthetic vector with hand-computed expected output).
- Smoke integration tests confirm net-did delegation works: sign/verify on EdDSA, ES256, ES256K and `DeriveSharedSecret` on X25519/P-256/P-384/P-521 all complete without exception via DidComm.Core's `ICryptoProvider`.
- A malformed JWK (off-curve EC point) propagates `CryptographicException` through DidComm.Core's pipeline (inherited from net-did's `EcPointValidator`).
- `DidComm.InteropTests` data-driven runner emits one XUnit theory case per fixture manifest.

> **Kickoff prompt:** "Scaffold the didcomm-dotnet solution per PRD §3 and §11 (NFR-01/08), reference `NetDid 1.3.0+`, and implement the DIDComm JOSE-composition layer per PRD §12 Phase 0: a DIDComm-shaped `ICryptoProvider`/`DefaultCryptoProvider` that delegates sign/verify and raw ECDH to `NetDid.Core.ICryptoProvider`, plus locally-owned A256CBC-HS512 (RFC 7518 §5.2.5), A256GCM, XC20P, A256KW (RFC 3394), an ECDH-1PU KDF wrapper around `NetDid.Core.Crypto.Kdf.ConcatKdf`, a base64url-no-pad codec, and a DIDComm JWK overlay over `NetDid.Core.Jwk.JwkConverter`. Use NSec only for XC20P. Provide round-trip + RFC-7518/RFC-3394 known-answer tests for every JOSE primitive owned here, plus smoke integration tests confirming the net-did delegation path. Stage fixtures inline (`tests/DidComm.InteropTests/fixtures/`) with the v1 schema and one smoke manifest; add the data-driven runner per PRD §13.2–§13.4 (FR-IX-01/02). Do not implement envelopes yet."

---

### Phase 1 — Message Model & Consistency

**Closes:** FR-MSG-01..15, FR-ATT-01..05, FR-CONSIST-01..06 (model + check hooks; FR-CONSIST-06 resolver-backed check stubbed until Phase 3 wiring), FR-PROTO-01..02 (MTURI parsing), NFR-10.
**Exit criteria:**
- Appendix C.1 plaintext round-trips byte-stable; `apv`/signing serialization is reproducible (NFR-10).
- `body`-absent message unpacks to empty body (FR-MSG-10); unknown headers don't fail unpack (FR-MSG-12) and survive round-trip (FR-MSG-15).
- Message-`id` uniqueness contract enforced/documented: default generator passes a large no-collision run; custom-generator obligation documented (FR-MSG-14).
- Attachment `id` reserved-character rejection works (FR-ATT-04).
- MTURI regex captures all four groups for every spec example.
- Consistency checks exist as pure functions with positive (`did:example:alice` vs `…#key-1`) and negative (cross-DID kid) unit tests (FR-CONSIST-01..05); the resolver-backed authorization check (FR-CONSIST-06) is wired into unpack in Phase 3.

> **Kickoff prompt:** "Implement the plaintext message model, attachments, and MTURI parsing per PRD §4 and §10.1 (FR-MSG-*, FR-ATT-*, FR-PROTO-01/02), plus deterministic JSON (NFR-10) and the addressing-consistency check functions (FR-CONSIST-*). Validate against Appendix C.1. No crypto envelopes yet."

---

### Phase 2 — Envelopes (Signed, Anoncrypt, Authcrypt)

**Closes:** FR-ENV-01..07, FR-ENC-04, FR-ENC-09..19, FR-SIG-01..06, FR-IX-01, FR-IX-03 (inbound static).
**This is the interop gate.** Implement against the vectors continuously.
**Exit criteria — every Appendix C vector passes:**
- C.2: EdDSA, ES256, ES256K signed messages verify (General + Flattened) (FR-SIG-02).
- C.3 anoncrypt: X25519/XC20P (3 recipients), P-384/A256CBC-HS512, P-521/A256GCM all decrypt and re-encrypt to structurally equivalent envelopes.
- C.3 authcrypt: X25519/A256CBC-HS512 (3 recipients) decrypts; sender recovered from `apu` when `skid` absent (FR-ENC-17).
- C.3 `anoncrypt(sign(...))` (EdDSA inside, P-521 outer) and `anoncrypt(authcrypt(...))` unpack to C.1.
- `apv` equals the vector value exactly (FR-ENC-13); 1PU tag-in-KDF ordering verified (FR-ENC-15).
- **Inbound interop:** static vectors harvested from `didcomm-rust`, `didcomm-python`, and `didcomm-jvm` into `source`-tagged manifests all unpack/verify (FR-IX-03), covering the §13.5 matrix cells those libs support.

> **Kickoff prompt:** "Implement JWE (General JSON, multi-recipient) anoncrypt (ECDH-ES+A256KW) and authcrypt (ECDH-1PU+A256KW) and JWS signing per PRD §5 (FR-ENV-*, FR-ENC-04/09–19, FR-SIG-*). Drive development entirely by the fixture suite — every `source: spec-v2.1` manifest must pass (FR-IX-01), including `apv` exactness and the encrypt-then-derive-KEK ordering for 1PU. Support General and Flattened JWS on receive. Then harvest static vectors from sicpa-dlab/didcomm-rust, didcomm-python, and didcomm-jvm into `source`-tagged fixtures (§13.2–§13.4) and make them all pass as inbound cases (FR-IX-03)."

---

### Phase 3 — Pack/Unpack Facade, net-did Integration, Secrets, Rotation

**Closes:** FR-DID-01..07, FR-SEC-01..05, FR-API-01..08, FR-CONSIST-01..06 (wired; FR-CONSIST-06 resolver-backed authorization now active via net-did), FR-ROT-01..06.
**Depends on:** net-did being available as a package reference. Confirm FR-DID-05 curve coverage (esp. P-384) early; if a required curve is missing from net-did, raise it on the net-did backlog before building the adapter.
**Exit criteria:**
- The `NetDidKeyService` adapter extracts correct `keyAgreement`/`authentication` keys from NetDid resolution results for the Appendix B docs (returned via a stub `NetDid.IDidResolver`).
- Live resolution through NetDid works for `did:key` (all required curves) and `did:peer` (numalgo 0/2); a `did:web:` DID throws `UnsupportedDidMethodException` at every entry point (FR-DID-06).
- End-to-end Alice→Bob round-trips for every FR-ENV-02 composition using NetDid resolution + Appendix A secrets.
- Consistency violations (FR-CONSIST) throw `ConsistencyException`.
- `from_prior` rotation validates and surfaces in metadata; out-of-order pre-rotation message ignored (FR-ROT-05).
- Missing `ISecretsResolver` fails fast (FR-SEC-02); optional `NetDid.IKeyStore`→`ISecretsResolver` bridge resolves a kid (FR-SEC-04).

> **Kickoff prompt:** "Implement the `DidComm` facade (FR-API-*), integrate **net-did** for DID resolution via the `IDidKeyService`/`NetDidKeyService` adapter (FR-DID-01..05) — do not reimplement DID methods — **explicitly reject `did:web` per DD-08 (FR-DID-06/07) with `UnsupportedDidMethodException`**, implement the secrets contract incl. the optional NetDid `IKeyStore` bridge (FR-SEC-*), wire the consistency checks (FR-CONSIST-*), and DID rotation (FR-ROT-*) per PRD §6–7. Use a stub `NetDid.IDidResolver` returning the Appendix B documents for deterministic tests, then a real NetDid resolver for integration tests. Provide end-to-end Alice↔Bob tests for all legal compositions using Appendix A keys, plus a negative test that every entry point rejects `did:web`."

---

### Phase 4 — Routing & Mediation

**Closes:** FR-ROUTE-01..08.
**Exit criteria:**
- 1-hop and 2-hop forward wrapping matches spec Endpoint Examples 1 & 2 (FR-ROUTE-04).
- serviceEndpoint parses as object and array-of-objects (conformant); bare string handled only via the DD-10 tolerance (FR-ROUTE-03).
- Mediator relays and re-wraps per pre-registered keys; `please_ack` ignored on forwards (FR-ROUTE-07).
- Rewrapping mode produces a valid constant-size onion (FR-ROUTE-06).

> **Kickoff prompt:** "Implement Routing Protocol 2.0 per PRD §8 (FR-ROUTE-*): sender reverse-order forward wrapping, mediator relay + optional rewrapping, mediator-as-DID endpoint with implicit routingKey prepend, and the conformant `serviceEndpoint` object and array-of-objects forms (plus the DD-10 bare-string tolerance only if explicitly enabled). Validate against the spec's Endpoint Examples."

---

### Phase 5 — Transports

**Closes:** FR-TRN-01..12, FR-API-06 (413 path).
**Exit criteria:**
- HTTPS send (202/200), 307-only redirect, ASP.NET Core receive via TestServer round-trips (FR-TRN-04..07).
- WebSocket send/receive, reconnect with backoff, one WebSocket message per packed DIDComm message with frame reassembly (FR-TRN-09..11).
- `max_receive_bytes`→413 enforced.

> **Kickoff prompt:** "Implement the HTTP and WebSocket transports per PRD §9 (FR-TRN-*) including the ASP.NET Core receive endpoint and resilience. Round-trip over TestServer and an in-memory socket."

---

### Phase 6 — Protocols, Cross-Message Concerns, Live Interop, Samples, Release

**Closes:** FR-PROTO-01..11 + FR-PROTO-11a, FR-OOB-01..05, FR-THR-01..04, FR-PROF-01..02, FR-I18N-01..04, FR-IX-04..09 (live harness + publishing), **FR-DX-01..09 (samples + API-coverage gate)**, NFR-02,05,06,07,09.
**Exit criteria:**
- Trust Ping, Discover Features (incl. `max_receive_bytes`), Empty, Report Problem (taxonomy + interpolation + escalation + cascade guard), Trace (off by default), OOB (URL encode matches spec base64url) all pass unit tests.
- Threading + ACK loop-guards verified.
- **i18n state:** an `accept-lang` preference persists across messages on the same `thid` and does **not** leak to a concurrent thread (FR-I18N-02); a `lang`-tagged protocol-defined human-readable field is interpreted in that language (FR-I18N-03).
- **Live interop:** a CI job round-trips didcomm-dotnet↔`sicpa-dlab/didcomm-demo` Python and JVM CLIs over `did:peer` for every supported §13.5 composition — outbound (they unpack ours, FR-IX-04) and inbound (we unpack theirs, FR-IX-05). didcomm-dotnet publishes its own `source: didcomm-dotnet` vector set (FR-IX-06). The offline interop suite gates every PR; the live harness runs nightly + on release (FR-IX-08).
- **Samples & DX:** all 10 sample apps + `02-Cookbook` (§14.3) build and run on .NET 10; the README quickstart works unmodified (FR-DX-05); the **API-coverage test reports 0 undemonstrated public members (FR-DX-01)** and matches the §14.4 matrix (FR-DX-09).
- OTel spans + redaction (NFR-04) verified; benchmarks meet NFR-07; all packages publish via the release workflow.

> **Kickoff prompt:** "Implement the spec protocols and cross-message concerns per PRD §10 (FR-PROTO-*, FR-OOB-*, FR-THR-*, FR-PROF-*, FR-I18N-*), add OpenTelemetry + structured logging with strict secret redaction (NFR-04/05/06), the BenchmarkDotNet suite (NFR-07), and the NuGet release workflow (NFR-08). Build the **full sample set and DX gate** per PRD §14: the 10 sample apps + `02-Cookbook` with one runnable section per §14.2 task, the `samples/_shared` offline helpers (FR-DX-07), the README quickstart (FR-DX-05), and the **API-coverage test that fails CI on any undemonstrated public member (FR-DX-01)** asserting the §14.4 matrix. Build the **live cross-implementation harness** per PRD §13.6 round-tripping both ways against the `sicpa-dlab/didcomm-demo` Python/JVM CLIs over `did:peer` (FR-IX-04/05), publish a `source: didcomm-dotnet` fixture set (FR-IX-06), and wire offline interop into PR CI with the live harness nightly (FR-IX-08). Confirm the OOB URL base64url matches the spec example exactly."

---

### Phase 7 — Mediator service (post-v1.0)

**Closes:** FR-ROUTE-09..11, FR-RR-01..09, FR-MED-01..10, FR-PICK-01..12; the amended FR-TRN-03/10 and FR-ROUTE-06; sample 11 and cookbook CC–FF (§14.3); the §13.5 "Mediation" dimension.
**Issues:** epic #88; #89 (this PRD change) → #90 routing core (FR-ROUTE-09..11) → #91 / #92 return-route receive / client (FR-RR-*) → #93 CM 3.0 (FR-MED-01..06, 08) and #94 pickup 3.0 (FR-PICK-01..08) in parallel → #95 `DidComm.Mediator` pipeline + `AddMediator()` (FR-MED-07, FR-PICK-09/10) → #96 recipient client (FR-MED-09, FR-PICK-11) → #97 sample 11 + cookbook + docs → #98 live interop + adversarial gate; #99 CM 2.0 compat (FR-MED-10) and #100 pickup 4.0 (FR-PICK-12) after their parents. Design decisions D1–D7 in #88 are fixed.
**Exit criteria:**
- `forward` dispatches as a registered handler and is advertised only for the mediator role; expired forwards are dropped; unknown `next` → 202 silent drop with constant-work lookup (FR-ROUTE-09..11).
- The §8a eligibility matrix passes: HTTP 200-body / 202-empty, WebSocket session lifetime and `SessionClosed`, forward never opens a session, the FR-RR-08 replay matrix passes (window, timestamp-free, concurrent duplicates, saturation, fresh-DID flood); two local DIDs enrol over WebSocket through one transport and each receives its own reply; a default client and default server complete ping, enrollment and pickup with no caller-supplied timestamps, and the `All` → headerless → `None` sequence keeps the session socket and stops pushes only after `None`; the client reads `SendResult.Returned` only when it is authenticated by the addressed DID (FR-RR-01..09).
- CM 3.0 request → grant → update → query byte-exact against the didcomm.org fixtures; ownership policy, pagination bounds and identifier caps enforced; the 2.0 compat handler shares the registry (FR-MED-01..10).
- Pickup 3.0 status / delivery / ack / live over HTTP and WebSocket: `status` never queued, delivered-not-acked never deleted, retained storage (queued + leased + live-unacknowledged) bounded, byte-budgeted delivery batches make progress on large payloads, the exact `e.m.live-mode-not-supported` report (FR-PICK-01..11); pickup 4.0 coexists (FR-PICK-12).
- End to end: Bob enrolls, Alice sends with `Forward: true`, Bob fetches over HTTP and receives live over WS — in the TestServer suite and in `samples/11-MediatedAgent`; the DX gate is green with `DidComm.Mediator` in the shipped-assembly list.
- Live interop: at least one external-mediator leg (Identus or Blocktrust: CM 2.0 + pickup 3.0) green nightly against a pinned image; the adversarial report is posted and every High/Medium finding closed (#98).
- `docs/codebase-architecture.md` has its mediator-pipeline section filled in.

> **Kickoff prompt:** "Implement the mediator service per PRD §8 (FR-ROUTE-09..11), §8a (FR-RR-*), §8b (FR-MED-*) and §8c (FR-PICK-*) in the issue order of epic #88: make `forward` a registered handler with relay-time expiry and delay; add the return-route session model (HTTP single response, WebSocket session, authenticated-sender binding) on the receive and client sides; build `DidComm.Mediator` with the Coordinate Mediation 3.0 and Message Pickup 3.0 handlers, the in-memory stores and policy, the live-session → queue → direct-push pipeline and `AddMediator()`; then the recipient-side `MediationClient` / `PickupClient`, sample 11, the CM 2.0 compat handler and the pickup 4.0 follow-up. Test each handler through `ProtocolDispatcher` without transports first, then end-to-end over TestServer, then against an external mediator. Keep D1–D7 from #88 and the FR-API-09 / no-echo / constant-work posture on every new wire field."

---

## 13. Test Strategy & Interoperability Fixtures

Interoperability is the whole point of a DIDComm library: a message this library packs MUST be readable by other ecosystems' implementations, and vice-versa. Interop is therefore a **first-class, gating deliverable**, not a stretch goal. §13.1 lists the general test layers; §13.2–§13.6 specify the interoperability fixture suite, its format, the cross-implementation harness, and the conformance matrix.

### 13.1 Test layers

| Layer | Approach |
|---|---|
| Known-answer (crypto) | RFC 7518/7748 KATs for the Concat KDF, ECDH, AES-KW, AES-CBC-HMAC, AES-GCM, XC20P. |
| **Spec vectors (authoritative baseline)** | Appendix A (secrets) + B (DID Docs) + C (vectors) vendored verbatim. Decrypt every C.3 vector; verify every C.2 signature; for deterministic algs, re-encrypt and compare structurally. Primary gate for Phase 2. |
| **Cross-implementation interop** | The fixture suite + live harness in §13.2–§13.6. **Gating, not stretch.** |
| Unit | ≥ 90% line coverage of `DidComm.Core`; XUnit + AwesomeAssertions + NSubstitute. |
| Property-based | Round-trip invariants (pack∘unpack = identity for every legal composition) via FsCheck. |
| Integration | ASP.NET Core TestServer (HTTP), in-memory socket (WS), full Alice↔Mediator↔Bob route. |
| Negative/security | Off-curve `epk` (FR-ENC-03), consistency violations (FR-CONSIST), unauthorized signer kid (FR-SIG-03), expired/oversized messages, ACK loops, `did:web` rejection (FR-DID-06). |

### 13.2 Interoperability fixture sources

The Appendix A/B/C keys are the same `did:example:alice` / `did:example:bob` keys used by the SICPA reference implementations, so the spec vectors and the SICPA libraries share one cryptographic baseline. Fixtures are harvested from, and validated against, the following sources. Each fixture records its provenance (`source`) so a failure is traceable to an origin.

| Source | Repo / origin | Language | Role | Notes |
|---|---|---|---|---|
| **Spec v2.1** | `decentralized-identity/didcomm-messaging` (Appendix A/B/C) | — | Authoritative baseline | Vendored verbatim; the common oracle. |
| **didcomm-rust** | `sicpa-dlab/didcomm-rust` | Rust | Primary reference impl | Canonical SICPA implementation; harvest its test vectors. |
| **didcomm-python** | `sicpa-dlab/didcomm-python` | Python | Reference impl + live harness | Has a runnable CLI; usable in CI for live round-trips. |
| **didcomm-jvm** | `sicpa-dlab/didcomm-jvm` | Kotlin/JVM | Reference impl + live harness | Pairs with python in the demo CLI. |
| **didcomm-demo** | `sicpa-dlab/didcomm-demo` | Python + JVM | **Live cross-impl CLI** | Python/JVM CLIs with identical `pack`/`unpack` interface over **peer DIDs**; the spec-blessed way to prove "pass a packed message from a 3rd-party lib to the demo's `unpack`". |
| **didcomm-rs** | `decentralized-identity/didcomm-rs` | Rust | Secondary reference | Older DIF Rust impl; harvest opportunistically, don't gate on it. |
| **Credo-TS** | `openwallet-foundation/credo-ts` | TypeScript | Emerging target | DIDComm v2 support is partial/in-progress; track and add fixtures as it matures — do not gate v1.0 on it. |
| **DIF Interop-a-thon** | DIF events | mixed | Manual validation | Periodic live multi-vendor sessions; record outcomes as regression fixtures. |

> **Resolution note.** Spec/SICPA vectors use `did:example:*` and `did:peer:*`. `did:example` is not a real method; the fixture harness supplies the Appendix B DID Documents through a **test-only static resolver** (an `IDidKeyService`/`NetDid.IDidResolver` stub seeded from the fixtures) — it is never shipped in `DidComm.Core`. Live interop with the SICPA demo CLIs uses **`did:peer`** (resolved via net-did), since those CLIs operate on peer DIDs only.

### 13.3 Fixture repository & layout

Fixtures live in a **standalone, versioned repository** `didcomm-dotnet-fixtures`, consumed by the test projects as a **git submodule** (mirroring the `zcap-ld-fixtures` pattern). This lets fixtures version independently, be shared across languages, and be contributed back upstream (e.g., to DIF).

```
didcomm-dotnet-fixtures/                 (git submodule)
├─ manifest/                             one JSON manifest per fixture (schema §13.4)
│  ├─ spec/                              from spec Appendix A/B/C
│  ├─ didcomm-rust/                      harvested from sicpa-dlab/didcomm-rust
│  ├─ didcomm-python/
│  ├─ didcomm-jvm/
│  └─ authored/                          hand-authored edge cases (negative, boundary)
├─ secrets/                             keysets (Appendix A; per-source secret sets) — JWK
├─ diddocs/                             DID Documents (Appendix B; per-source) — JSON
├─ payloads/                            canonical plaintext messages (Appendix C.1, etc.)
├─ packed/                              packed envelopes referenced by fixtures (JWE/JWS JSON)
└─ schema/
   └─ didcomm-fixture.v1.schema.json    JSON Schema for the manifest format
```

`DidComm.InteropTests` enumerates `manifest/**/*.json`, loads referenced secrets/diddocs/payloads, and executes each fixture as an XUnit theory case. New fixtures require **no test code** — dropping a manifest is enough (data-driven).

### 13.4 Canonical fixture manifest (schema)

Every fixture is one JSON document conforming to `didcomm-fixture.v1`. One uniform schema covers spec vectors, harvested third-party vectors, and authored cases.

```jsonc
{
  "schema": "didcomm-fixture/v1",
  "id": "anoncrypt-x25519-xc20p-3rcpt-spec",          // unique, stable
  "description": "Anoncrypt · X25519 · XC20P · 3 recipients (spec C.3 ex.1)",
  "source": "spec-v2.1",                              // provenance (see §13.2)
  "source_ref": "Appendix C.3 example 1",
  "direction": "inbound",                             // inbound = we consume theirs; outbound = they consume ours; roundtrip
  "operation": "unpack",                              // pack-encrypted | pack-signed | pack-plaintext | unpack | sign | verify
  "tags": ["anoncrypt", "x25519", "xc20p", "multi-recipient", "ecdh-es"],
  "refs": {                                           // pointers into secrets/ diddocs/ payloads/ packed/
    "secrets": "secrets/appendix-a.json",
    "diddocs": ["diddocs/alice.json", "diddocs/bob.json"],
    "plaintext": "payloads/c1-lets-do-lunch.json"
  },
  "input": {
    "packed": "packed/c3-anoncrypt-x25519-xc20p.json" // for unpack/verify; inline JSON also allowed
    // for pack-* fixtures, input carries: message, from, to[], sign_from?, enc, protect_sender?, forward?
  },
  "expected": {
    "outcome": "success",                             // success | error
    "error_code": null,                               // e.g. "trust.crypto" / exception type for negative fixtures
    "plaintext": "payloads/c1-lets-do-lunch.json",    // expected unpacked message (for unpack)
    "metadata": {                                     // asserted UnpackResult fields (FR-API-04)
      "encrypted": true, "authenticated": false, "non_repudiation": false,
      "enc": "XC20P", "kw": "ECDH-ES+A256KW", "anonymous_sender": true
    },
    "match": "structural"                             // exact | structural | roundtrip
      // exact: byte-equal output (deterministic algs only)
      // structural: same recipients/headers/alg, ignoring ephemeral epk/iv/tag
      // roundtrip: pack then unpack equals the source plaintext (non-deterministic algs)
  }
}
```

Match semantics matter because anoncrypt/authcrypt outputs are **non-deterministic** (fresh `epk`, random IV): for `pack` fixtures use `roundtrip` or `structural`; reserve `exact` for signing and other deterministic paths and for `unpack` results.

### 13.5 Conformance matrix

The suite MUST cover the cartesian product below for **both directions**. "Inbound" = a fixture packed by source X that didcomm-dotnet unpacks; "Outbound" = didcomm-dotnet packs and source X unpacks (via §13.6 harness or published vectors).

| Dimension | Values |
|---|---|
| Envelope composition | plaintext · signed · anoncrypt · authcrypt · anoncrypt(sign) · anoncrypt(authcrypt) |
| Key-agreement curve | X25519 · P-256 · P-384 · (P-521 optional) |
| Content encryption | A256CBC-HS512 · A256GCM · XC20P |
| Signing alg | EdDSA · ES256 · ES256K |
| Recipients | single · multi (≥3, mixed DID methods; one key-agreement curve per envelope — a JWE carries a single `epk`, so recipient curves cannot be mixed within one message) |
| Routing | direct · 1 mediator · 2 mediators (forward) |
| DID method (live) | did:peer (SICPA demo) · did:key |
| Direction | inbound (we unpack theirs) · outbound (they unpack ours) |
| Mediation (Phase 7) | enroll · pickup HTTP · pickup WS live · CM 2.0 peer |

The Mediation row is a standalone dimension, not crossed with the envelope rows: its cells are the Phase 7 TestServer suite (enroll, pickup over HTTP, pickup over a live WebSocket session) and the §13.6 live-harness mediator leg against an external CM 2.0 + pickup 3.0 peer.

Cells that an external impl does not support (e.g. a lib that omits P-384 or XC20P) are marked `n/a` per source and excluded from that source's gate — but MUST still pass against the spec baseline.

An `n/a` is a **declaration, not a skip**: it MUST name the counterpart behaviour that makes the cell unrunnable, with reproducible evidence (the counterpart's own error text or source reference), and it MUST be recorded in the live harness's summary artifact alongside the executed cells. A cell that runs and fails stays red — it is never reclassified as `n/a` to keep a job green. The live harness's current `n/a` set, with the evidence backing each entry, is maintained in `tools/interop-live/README.md`; as of the §13.6 live matrix these are:

| Dimension / cell | Source | `n/a` reason (evidence) |
|---|---|---|
| `signed` ES256K — outbound, inbound, and the published `signed-es256k` vector (three declarations, matching the harness) | didcomm-jvm | JDK 16 removed secp256k1 from SunEC and didcomm-jvm 0.3.2 bundles no BouncyCastle, so the counterpart stack can neither sign nor verify the curve (validated on JDK 25): signing throws `SignatureException: Curve not supported`, and on verify nimbus swallows that exception and reports a valid signature as invalid. A JRE curve-availability gap, not an envelope-format issue — the vendored spec vector fails identically while its EdDSA/ES256 siblings pass. Tracked at **didcomm-dotnet#71**. |
| P-384 / P-521 key agreement (live) | didcomm-python, didcomm-jvm | Not reachable through a two-key `did:peer:2` live identity; covered instead by the offline `source: didcomm-dotnet` and `source: spec-v2.1` fixture vectors, which do exercise both curves. |

An `n/a` is retired the moment its stated cause is gone — **by execution, not by inference**. Three entries have been retired this way, each because the cause it named turned out to be falsifiable and was then falsified:

- outbound `signed` and outbound `anoncrypt(sign)` — `DataProofsDotnet.Jose` 1.3.0 moved `kid` to the unprotected per-signature header and emits General JSON serialization at every signer count (dataproofs-dotnet#17, #25).
- inbound `signed` ES256K against didcomm-python — `NetCrypto` 1.5.0 normalizes `(r, n−s)` to low-S before verification (crypto-dotnet#23), so RFC 8812-valid high-S signatures now verify. Re-measured by S parity rather than by run count, since high-S occurs only ~50% of the time: 8/8 high-S and 12/12 low-S verified.

All three were re-run live against didcomm-python and pass, so the rows are gone rather than annotated. The python leg now declares **no `n/a` cells**; see `tools/interop-live/README.md` for the recorded run.

### 13.6 Cross-implementation harness (interop FRs)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-IX-01 | MUST | Vendor the spec Appendix A/B/C as `source: spec-v2.1` fixtures and pass them all (the baseline gate). | Every C.2/C.3 vector passes as an inbound fixture. |
| FR-IX-02 | MUST | Provide `DidComm.InteropTests`, a data-driven runner that executes every `manifest/**/*.json` fixture (§13.4) with no per-fixture code, using the test-only static resolver seeded from the fixture's diddocs. | Adding a manifest adds a test case automatically. |
| FR-IX-03 | MUST | **Inbound:** harvest static vectors from `didcomm-rust`, `didcomm-python`, and `didcomm-jvm` test resources into `source`-tagged fixtures and unpack/verify them all (across the §13.5 matrix cells those libs support). | All harvested inbound fixtures pass; failures are attributable by `source`. |
| FR-IX-04 | MUST | **Outbound (live):** a CI job packs messages with didcomm-dotnet (across the matrix) and feeds them to the `sicpa-dlab/didcomm-demo` Python and JVM `unpack` CLIs over `did:peer`; the CLIs MUST recover the original plaintext. | A scripted CI job round-trips didcomm-dotnet→counterpart for every cell of the executed live matrix enumerated in `tools/interop-live/README.md` (the acceptance basis: 23 compositions × both directions per leg, exercising every §13.5 dimension, including multi-key recipients decrypting on their last device and a kid-pinned `to` (FR-ENC-19)). A §13.5 combination absent from that enumeration is a disclosed slice choice (e.g. XC20P is paired with X25519 only), not a silent skip; combinations a counterpart cannot run at all are in the documented `n/a` set above, each carrying its evidence. Cells that execute and fail keep the job red. |
| FR-IX-05 | SHOULD | **Inbound (live):** the reverse — the demo CLIs `pack`, didcomm-dotnet unpacks. | didcomm-dotnet recovers plaintext packed by the demo CLIs. |
| FR-IX-06 | MUST | **Publish our vectors:** emit didcomm-dotnet-produced fixtures (manifest + packed envelopes + the secrets/diddocs needed to verify) so other ecosystems can test against this library; contribute back upstream where appropriate. | A published fixture set decrypts/verifies with at least one external impl. |
| FR-IX-07 | SHOULD | Track Credo-TS DIDComm v2 support; add `source: credo-ts` fixtures as the matrix cells become available. Do not gate v1.0 on Credo-TS. | Credo fixtures present but non-gating. |
| FR-IX-08 | MUST | The interop suite runs in CI on every PR for offline (static/inbound) fixtures; the live harness (FR-IX-04/05, requires Python+JVM) runs at least nightly and on release. | CI shows green offline interop on PRs; nightly live-harness job exists. |
| FR-IX-09 | SHOULD | Record manual DIF Interop-a-thon outcomes as regression fixtures (`source: interop-a-thon-<date>`). | Event results captured as fixtures. |

---

## 14. Developer Experience & Sample Applications

Developer experience is a primary goal, not an afterthought: the test of this library is whether a .NET developer can go from `dotnet add package` to a working authcrypt round-trip in minutes, and discover how to do anything else by reading a runnable sample. To enforce that, **every public API member MUST be demonstrated by compilable, runnable sample code**, and a CI check fails the build if any public member is undemonstrated. The snippets below are illustrative of the *intended ergonomics* (final signatures are the implementer's, constrained by §3 and the FRs); the sample projects in §14.3 are required deliverables.

### 14.1 Developer-experience requirements (FR-DX-*)

| ID | Keyword | Requirement | Acceptance |
|---|---|---|---|
| FR-DX-01 | MUST | **100% public-API demonstration.** Every public type and member of every shipped package is exercised by at least one sample in `samples/`. A coverage test reflects over the public surface of each package and over a registry of members touched by the samples; CI fails if any public member is undemonstrated. | Coverage report lists 0 undemonstrated public members; test is part of CI. |
| FR-DX-02 | MUST | Samples are real, compilable .NET 10 projects under `samples/`, built and run in CI — not inert doc fragments. Each is self-contained (no external KMS/network/secrets). | `dotnet build` + `dotnet run` succeed for every sample in CI. |
| FR-DX-03 | MUST | Each sample ships a `README.md` stating what it demonstrates, how to run it, and the expected console output; the program narrates each step it performs. | Running a sample matches its README's stated output. |
| FR-DX-04 | MUST | A `samples/02-Cookbook` project contains one minimal, labeled, runnable snippet per API task in §14.2 (the canonical snippets), each printing its result. This is the backbone of FR-DX-01 coverage. | Cookbook covers every §14.2 task; runs end-to-end. |
| FR-DX-05 | MUST | The repository README contains a **≤ 5-minute quickstart**: install, create two `did:peer` identities with the test in-memory secrets, authcrypt a message, unpack it, print metadata — ≤ 25 lines. | A new developer can copy-run the quickstart unmodified. |
| FR-DX-06 | SHOULD | Documentation snippets are sourced from the compiled sample files (e.g. `#region`/snippet includes) so prose examples cannot drift from compiling code. | Docs reference live sample regions; a drift check passes. |
| FR-DX-07 | MUST | Ship reusable sample helpers (in a `samples/_shared` or test-support package, NOT in `DidComm.Core`): an in-memory `ISecretsResolver`, a `did:peer` identity-pair generator (via net-did), and a console "narrator". These keep DD-02 intact while making samples runnable offline. | Helpers exist outside `Core`; samples use them. |
| FR-DX-08 | SHOULD | Every major public type carries an XML `<example>` doc comment that points to the demonstrating sample (ties into NFR-02). | Public types link to a sample in their docs. |
| FR-DX-09 | MUST | Maintain the **API → sample coverage matrix** (§14.4) as living documentation and assert it in the FR-DX-01 test. | Matrix matches the coverage test output. |

### 14.2 Canonical usage snippets (one per public API task)

These define the ergonomics each public API group must expose. The `02-Cookbook` sample implements every one as a runnable section.

**A. Dependency-injection setup (facade, resolver, secrets, transports, protocols)**
```csharp
services.AddDidComm(b =>
{
    b.UseNetDidResolver();                       // build net-did's default composite resolver (key/peer/webvh/dht/ethr)
    b.UseSecretsResolver<MyVaultSecretsResolver>(); // consumer-supplied (FR-SEC-01)
    b.UseHttpTransport(o => o.Timeout = TimeSpan.FromSeconds(15));
    b.UseWebSocketTransport();
    b.AddProtocol<TrustPingHandler>();
    b.AddProtocol<DiscoverFeaturesHandler>();
    b.Configure(o => o.DefaultEncryption = ContentEncryptionAlgorithm.A256CbcHs512);
});
var didcomm = serviceProvider.GetRequiredService<DidComm>();
```

**B. Build a message (builder; id/typ auto-populated, FR-MSG-13)**
```csharp
var msg = Message.Builder("https://didcomm.org/basicmessage/2.0/message")
    .From("did:peer:alice")
    .To("did:peer:bob")
    .WithBody(new { content = "Hello, Bob." })
    .WithCreatedTime(DateTimeOffset.UtcNow)
    .Build();
```

**C. Pack plaintext (debug/inspection only)**
```csharp
PackPlaintextResult plain = await didcomm.PackPlaintextAsync(msg);
Console.WriteLine(plain.PackedMessage); // application/didcomm-plain+json
```

**D. Pack signed (non-repudiable, no confidentiality)**
```csharp
var signed = await didcomm.PackSignedAsync(new PackSignedParams(msg, SignFrom: "did:peer:alice"));
```

**E. Pack anoncrypt (confidential, anonymous sender — omit From)**
```csharp
var anon = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, To: ["did:peer:bob"]));                 // From == null ⇒ anoncrypt (FR-MSG-08)
```

**F. Pack authcrypt (confidential + sender authenticated — the default)**
```csharp
var auth = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, From: "did:peer:alice", To: ["did:peer:bob"]));
```

**G. Sign-then-encrypt (add non-repudiation)**
```csharp
var nr = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, From: "did:peer:alice", To: ["did:peer:bob"], SignFrom: "did:peer:alice"));
```

**H. Protect the sender (anoncrypt wraps authcrypt; hides skid from mediators)**
```csharp
var hidden = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, From: "did:peer:alice", To: ["did:peer:bob"], ProtectSender: true));
```

**I. Choose content encryption explicitly**
```csharp
var xc = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, To: ["did:peer:bob"], Encryption: ContentEncryptionAlgorithm.Xc20p)); // anoncrypt only (FR-ENC-09)
```

**J. Multi-recipient**
```csharp
var multi = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, From: "did:peer:alice", To: ["did:peer:bob", "did:peer:carol"]));
```

**K. Unpack and inspect metadata (FR-API-04)**
```csharp
UnpackResult res = await didcomm.UnpackAsync(packedMessage);
Console.WriteLine($"from={res.Message.From} encrypted={res.Metadata.Encrypted} " +
                  $"authenticated={res.Metadata.Authenticated} nonRepudiation={res.Metadata.NonRepudiation} " +
                  $"enc={res.Metadata.Encryption} signedBy={res.Metadata.SignedBy}");
```

**L. Attachments (inline json / base64 / linked-with-hash)**
```csharp
var withAtt = Message.Builder("https://didcomm.org/x/1.0/m")
    .From("did:peer:alice").To("did:peer:bob")
    .WithAttachment(Attachment.Json(id: "report", new { total = 42 }))
    .WithAttachment(Attachment.Base64(id: "logo", bytes, mediaType: "image/png"))
    .WithAttachment(Attachment.Link(id: "video", links: ["https://cdn/x.mp4"], multihash))
    .Build();
```

**M. Threading & ACKs (thid/pthid, please_ack/ack — FR-THR-*)**
```csharp
var reply = Message.Builder(type).From("did:peer:bob").To("did:peer:alice")
    .WithThid(received.Message.Id)               // continue the thread
    .WithPleaseAck()                             // request ACK of this message
    .Build();
bool acked = received.Message.Acks.Contains(sentId);
```

**N. DID rotation (from_prior — FR-ROT-*)**
```csharp
// Mint the rotation JWT, signed by a key authorized under the PRIOR DID's `authentication`
// relationship (fetch oldSignerPrivateJwk from your ISecretsResolver). Pass a short lifetime so the
// token is freshness-bounded and cannot be replayed past the window (FR-ROT-05).
var fromPrior = await FromPriorBuilder.BuildAsync(
    new FromPriorClaims(Sub: "did:peer:alice2", Iss: "did:peer:alice",
                        Iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
    oldSignerPrivateJwk, lifetime: TimeSpan.FromMinutes(5));

var rotated = new MessageBuilder().WithType(type)
    .WithFrom("did:peer:alice2").WithTo("did:peer:bob")
    .WithFromPrior(fromPrior)                       // ride the JWT on a sender-authenticated envelope (FR-ROT-01/03)
    .Build();
// On unpack: unpacked.FromPrior is populated and validated (FR-ROT-01..05).
```
> `MessageBuilder.WithDidRotation(...)` is a possible future DX convenience; today the rotation JWT is
> minted explicitly via `FromPriorBuilder.BuildAsync` and attached with `WithFromPrior`.

**O. Routing via a mediator (automatic from the recipient's routingKeys — FR-ROUTE-*)**
```csharp
// Forward wrapping is automatic when the recipient DID-Doc has routingKeys:
var packed = await didcomm.PackEncryptedAsync(new PackEncryptedParams(
    msg, From: "did:peer:alice", To: ["did:peer:bob"], Forward: true));
// packed.ServiceEndpoint tells the transport where to send the outermost envelope.
```

**P. Send over a transport (HTTP/WS chosen by endpoint scheme — FR-TRN-01)**
```csharp
SendResult sent = await didcomm.SendAsync(msg, new SendOptions(
    From: "did:peer:alice", To: ["did:peer:bob"]));   // packs + routes + transmits
```

**Q. Receive over HTTP (ASP.NET Core — FR-TRN-07)**
```csharp
app.MapDidCommEndpoint("/didcomm");                 // validates, unpacks, dispatches to protocol handlers
```

**R. Receive / chat over WebSocket (FR-TRN-09..11)**
```csharp
app.MapDidCommWebSocket("/ws/didcomm");
await wsTransport.SendAsync(endpoint, packedBytes, ct);  // one WebSocket message per packed DIDComm message
```

**S. Trust Ping (liveness — FR-PROTO-04)**
```csharp
var ping = TrustPing.CreatePing(to: "did:peer:bob", responseRequested: true);
await didcomm.SendAsync(ping, new SendOptions(From: "did:peer:alice", To: ["did:peer:bob"]));
// Bob's TrustPingHandler auto-replies ping-response with thid == ping.Id.
```

**T. Discover Features (FR-PROTO-05 / FR-PROTO-05a)**
```csharp
// Responder side is automatic once AddBuiltInProtocols() is called: an inbound `queries`
// is answered with a `disclose` listing supported protocols/goal-codes/headers/constraints.

// Initiator side — send a `queries` and await the peer's correlated `disclose`:
var client = serviceProvider.GetRequiredService<DiscoverFeaturesClient>();
var disclosures = await client.QueryFeaturesAsync(
    from: "did:peer:alice",
    to: "did:peer:bob",
    queries: new[] { new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" } },
    timeout: TimeSpan.FromSeconds(30));
// disclosures lists what Bob supports. Only an authenticated `disclose` from Bob completes
// the call; a timeout throws TimeoutException. Empty list = "no matches", not "unsupported".
```

**U. Report a problem (FR-PROTO-07/08)**
```csharp
var problem = ProblemReport.Create(code: "e.p.xfer.cant-use-endpoint", pthid: failingThid,
    comment: "Unable to use the {1} endpoint for {2}.",
    args: ["https://agents.r.us/inbox", "did:peer:bob"]);
```

**V. Out-of-Band invitation (build + URL/QR encode/decode — FR-OOB-*)**
```csharp
var invitation = OutOfBand.CreateInvitation(from: "did:peer:alice", goal: "Connect", goalCode: "connect");
string url = OutOfBand.ToUrl(invitation, baseUrl: "https://example.com/path");   // ?_oob=<base64url>
OutOfBandInvitation parsed = OutOfBand.FromUrl(url);                              // recipient side
// recipient's response uses invitation.Id as its pthid (FR-OOB-03)
```

**W. Empty message (header-only — FR-PROTO-06)**
```csharp
var headerOnly = Message.Empty().From("did:peer:alice").To("did:peer:bob").WithAck(prevId).Build();
```

**X. Custom protocol handler (extension point — FR-PROTO-03)**
```csharp
public sealed class LunchHandler : IProtocolHandler
{
    public string ProtocolUri => "https://didcomm.org/lets_do_lunch/1.0";
    public Task<Message?> HandleAsync(Message m, ProtocolContext ctx, CancellationToken ct) => /* ... */;
}
// register: b.AddProtocol<LunchHandler>();
```

**Y. Custom `ISecretsResolver` (KMS) and the net-did `IKeyStore` bridge (FR-SEC-01/04)**
```csharp
public sealed class MyVaultSecretsResolver : ISecretsResolver { /* fetch JWKs from Vault/KMS */ }

// Or bridge keys already minted with net-did:
b.UseSecretsResolver(sp => new NetDidKeyStoreSecretsResolver(sp.GetRequiredService<NetDid.IKeyStore>()));
```

**Z. Custom transport (extension point — FR-TRN-01)**
```csharp
public sealed class LibP2pTransport : IDidCommTransport
{
    public string Scheme => "libp2p";
    public bool CanHandle(string uri) => uri.StartsWith("libp2p://");
    public Task<TransportResult> SendAsync(string endpoint, byte[] packed, CancellationToken ct) => /* ... */;
}
// register: b.UseTransport<LibP2pTransport>();
```

**AA. net-did integration + the deliberate `did:web` rejection (FR-DID-01/06)**
```csharp
// Use an existing net-did resolver (e.g. already registered by the host app):
b.UseNetDidResolver(sp => sp.GetRequiredService<NetDid.IDidResolver>());

// did:web is rejected on purpose (DD-08):
try { await didcomm.UnpackAsync(msgFromDidWebSender); }
catch (UnsupportedDidMethodException ex) when (ex.Method == "web")
{ /* ex.Message recommends did:webvh */ }
```

**BB. Profiles & i18n (`accept` negotiation, `lang`/`accept-lang` — FR-PROF/I18N)**
```csharp
var localized = Message.Builder(type).From(a).To(b)
    .WithLang("fr").WithAcceptLang("fr", "en")
    .WithBody(new { comment = "C'est échec et mat." }).Build();
```

**CC–FF. Mediator service tasks (Phase 7)**

Defined with sample 11 (#97): **CC** Enroll with a mediator · **DD** Pick up queued messages · **EE** Live delivery over WebSocket · **FF** Run a minimal mediator in 20 lines. Snippets land with the APIs (FR-MED-09, FR-PICK-11); until then §14.3 row 02's A–FF reference is forward-looking.

### 14.3 Sample applications (required deliverables)

All live under `samples/`, build on .NET 10, and are referenced by the FR-DX-01 coverage test. The numbering is a suggested build order. "Covers" lists the §14.2 task letters and the FR groups each exercises.

| # | Project | What it demonstrates | Covers |
|---|---|---|---|
| 01 | `samples/01-Quickstart` | The ≤ 25-line README quickstart: two `did:peer` identities (net-did) + in-memory secrets, authcrypt round-trip, print metadata. | A, B, F, K · FR-DX-05, FR-API |
| 02 | `samples/02-Cookbook` | One runnable, narrated section per §14.2 task (A–BB; CC–FF added in Phase 7). The backbone of API-coverage. | **A–FF (all)** · FR-DX-01/04 |
| 03 | `samples/03-EnvelopesAndMessages` | Every envelope composition (plaintext/signed/anoncrypt/authcrypt/sign-then-encrypt/protect-sender), each content-encryption alg, multi-recipient, attachments, threading + ACKs, DID rotation — printing each packed form and the unpacked metadata. | C–N · FR-ENV, FR-ENC, FR-SIG, FR-ROT, FR-THR |
| 04 | `samples/04-MediatorAgent` | ASP.NET Core **routing relay only**: receive endpoint + Routing 2.0 `forward` relay through the registered forward handler, delivering to a host-registered recipient endpoint (direct push) — Alice→Mediator→Bob over HTTP using `did:peer` routingKeys. Enrollment and pickup are in 11. | O, P, Q · FR-ROUTE-01..08, FR-ROUTE-11, FR-TRN-04..08 |
| 05 | `samples/05-WebSocketChat` | Two agents over WebSocket: trust-ping liveness, discover-features handshake, bidirectional chat, reconnect after drop. | R, S · FR-TRN-09..11, FR-PROTO-04/05 |
| 06 | `samples/06-OutOfBand` | Build an OOB invitation, encode to a URL + QR, decode on the other device, correlate the response via `pthid`. | V · FR-OOB |
| 07 | `samples/07-ProblemsAndProtocols` | Report-problem taxonomy (codes, interpolation, warning→error escalation, cascade guard), empty-message ACK, and a **custom `IProtocolHandler`** (`lets_do_lunch`). | U, W, X · FR-PROTO-03/06/07/08/09/10 |
| 08 | `samples/08-Extensibility` | A custom `ISecretsResolver` (mock KMS), the **net-did `IKeyStore`→`ISecretsResolver` bridge**, and a custom `IDidCommTransport`. | Y, Z · FR-SEC-01/04, FR-TRN-01 |
| 09 | `samples/09-NetDidIntegration` | Mint `did:peer` and `did:key` identities with net-did, wire the resolver adapter, message between them — and show a `did:web` DID being rejected with `UnsupportedDidMethodException`. | AA · FR-DID-01/05/06 |
| 10 | `samples/10-ProfilesAndI18n` | `accept`-based profile selection and `lang`/`accept-lang` localized messaging (the chess-comment example from the spec). | BB · FR-PROF, FR-I18N |
| 11 | `samples/11-MediatedAgent` | The full mediator service (`AddMediator()` + `MapDidCommMediator`): Bob discovers the mediator from `/oob`, enrolls (CM 3.0), registers his DID and builds his DID document from the grant; Alice sends with `Forward: true`; Bob fetches over HTTP (status → delivery → ack) and then receives live over WebSocket; a policy-driven `mediate-deny`. | CC, DD, EE, FF · FR-MED, FR-PICK, FR-RR, FR-ROUTE-09..11 |
| — | `samples/_shared` | Reusable, offline sample infrastructure: in-memory `ISecretsResolver`, `did:peer` pair generator, console narrator. NOT shipped in `DidComm.Core`. | FR-DX-07 |

> Samples grow with the phases (§12): the Cookbook gains a section as each API lands (Phase 1 adds B/C; Phase 2 adds D–L; Phase 3 adds K/N + AA; Phase 4 adds O; Phase 5 adds P/Q/R; Phase 6 adds S–BB and finalizes 01–10 + the FR-DX-01 coverage gate). This keeps "every public API is demonstrated" true at every phase boundary, not just at the end. Phase 7 adds CC–FF and sample 11.

### 14.4 API → sample coverage matrix

This matrix is the traceability artifact behind FR-DX-01/09. Each public API group MUST map to ≥ 1 sample; the coverage test asserts the reverse (no public member missing).

| Public API group | Members (illustrative) | Demonstrated in |
|---|---|---|
| Facade | `DidComm.PackEncryptedAsync/PackSignedAsync/PackPlaintextAsync/UnpackAsync/SendAsync` | 01, 02, 03 |
| Params/results | `PackEncryptedParams`, `PackSignedParams`, `UnpackParams`, `*Result`, `UnpackMetadata`, `SendOptions` | 02, 03 |
| Message model | `Message`, `Message.Builder`, `Attachment` (+factories), `ContentEncryptionAlgorithm` | 02, 03 |
| Threading/ACKs | `WithThid/WithPthid/WithPleaseAck/WithAck`, `Message.Empty` | 03, 07 |
| Rotation | `FromPriorBuilder.BuildAsync`, `MessageBuilder.WithFromPrior`, `UnpackResult.FromPrior` | 03 |
| DI | `AddDidComm`, `DidCommBuilder.UseNetDidResolver/UseSecretsResolver/UseTransport/AddProtocol/Configure` | 02, 08 |
| Resolution | `IDidKeyService`, `NetDidKeyService`, `UseNetDidResolver`, `UnsupportedDidMethodException` | 09 |
| Secrets | `ISecretsResolver`, `Secret`, `NetDidKeyStoreSecretsResolver` | 08 |
| Transports | `IDidCommTransport`, `UseHttpTransport`, `MapDidCommEndpoint`, `UseWebSocketTransport`, `MapDidCommWebSocket` | 04, 05, 08 |
| Routing | forward wrapping, `PackEncryptedParams.Forward`, mediator role | 04, 11 |
| Protocols | `IProtocolHandler`, `ProtocolContext`, `TrustPing`, `DiscoverFeatures`, `ProblemReport`, `OutOfBand`, `Empty` | 05, 06, 07 |
| Profiles/i18n | `accept` selection, `WithLang/WithAcceptLang` | 10 |
| Exceptions | `DidCommException` + all subtypes | 03, 07, 09 |
| Mediation | `AddMediator`, `MapDidCommMediator`, `IMediationStore` / `IMessageStore` / `IMediationPolicy`, `MediationClient`, `PickupClient`, `DidCommServiceBuilder`, `SendOptions.ReturnRoute`, `SendResult.Returned` | 04 (routing relay), 11 |

---

## 15. Design Decisions & Spec Ambiguities

### 15.1 Decisions (where the spec leaves a choice)

| ID | Decision |
|---|---|
| DD-01 | **DID resolution is delegated to net-did (NetDid), not reimplemented.** didcomm-dotnet depends on `NetDid`, consumes `NetDid.IDidResolver`, and adapts results via `NetDidKeyService`. This inherits `did:key`, `did:peer`, `did:webvh`, `did:dht`, `did:ethr` for free and keeps a single DID-method codebase across the TurtleShell.id stack. Consumers can inject any `NetDid.IDidResolver`. **net-did resolution is also the basis of the resolver-backed message-layer authorization rule (FR-CONSIST-06):** consistency is not mere string matching — the `skid`/`kid` must resolve to a verification method genuinely authorized (correct relationship + `controller`) by the asserted `from`/`to` DID. This is a deliberately stronger guarantee than the wire format alone requires, enabled by having a real resolver in the dependency graph. |
| DD-02 | Ship the `ISecretsResolver` abstraction only — no production key store — to avoid encouraging insecure key handling. Test-only in-memory impl lives in the test assembly. (Parallels NetDid's own "no production key store" stance with its `IKeyStore`.) |
| DD-03 | Hybrid packaging: `DidComm.Core` bundles crypto; transports and protocols are separate packages. |
| DD-04 | Apache 2.0 (patent grant; enterprise-friendly). |
| DD-05 | WebSocket reconnect: exponential backoff 1s base, 30s cap, 0.5 jitter (spec is silent). |
| DD-06 | Default content-encryption when unspecified: `A256CBC-HS512` (the only "Required" alg, and the only one valid for authcrypt). |
| DD-07 | Default recipient selection: all `keyAgreement` keys of the recipient DID whose type matches the chosen `epk` curve (per §DID Document Keys "encrypt for as many keys as practical"), in document order, deduped by kid, at most 64 automatically selected keys per bare-DID entry; keys pinned by DID URL are exempt from that allowance. A DID URL recipient pins one key, as didcomm-python and didcomm-jvm accept a DID URL `to`. The curve preference (X25519 > P-256 > P-384 > P-521, common to every recipient and, for authcrypt, a held sender key) is unchanged. |
| DD-08 | **`did:web` is explicitly NOT supported, on security grounds.** It anchors trust in DNS, web PKI, and domain control with no verifiable history or key pre-rotation, so a domain takeover or CA/TLS compromise enables silent, undetectable key substitution. The library actively rejects `did:web` DIDs at every entry point with `UnsupportedDidMethodException` (FR-DID-06) and never advertises it (FR-DID-07). The supported web-hosted alternative is **`did:webvh`** (verifiable history), available via net-did. This is a deliberate, permanent exclusion — not a backlog gap. |
| DD-09 | **`IKeyStore`→`ISecretsResolver` bridge.** Provide an optional adapter so private keys minted/held by net-did's `IKeyStore` can satisfy didcomm-dotnet's `ISecretsResolver`, avoiding a duplicate key store for apps that use both libraries. Ships as an opt-in adapter (not in `Core`'s default graph) so DD-02 still holds. |
| DD-10 | **Bare-string `serviceEndpoint` is a compatibility tolerance, not the conformance shape.** DIDComm v2.1 defines a `DIDCommMessaging` `serviceEndpoint` as an object or array of objects (the `uri` lives inside). Some DID documents in the wild still use a bare string; the library MAY accept it as a Postel's-law receive-side tolerance (FR-ROUTE-03), but this is a product extension that MUST be clearly labeled and MUST NOT be presented as the canonical/conformant form. It is disabled by default or explicitly documented as non-canonical. |
| DD-11 | **Coordinate-mediation responses carry `thid` = the request's `thid`, else its `id`.** CM 3.0 says nothing about threading; Message Pickup 4.0 threads `status` to its request. Every mediator response (grant/deny, update-response, recipient) sets `thid` to the request's `thid` if present, else its `id` (FR-THR-01), and the client correlates on it with a type + mediator-DID fallback for peers that omit it (FR-MED-04). |
| DD-12 | **`return_route_thread` is a sibling top-level header; unknown `return_route` values mean `none`.** The extension names the attribute but not its placement; it is read next to `return_route` in the plaintext headers. A bad header value never breaks delivery (FR-RR-01). `thread` is recognized and surfaced but opens no session until a protocol needs it; on an established WebSocket session it stops pushes like `none`, because a peer that asks for one thread is not asking for all (FR-RR-05). |
| DD-13 | **Live delivery under Pickup 3.0 is framed as a `delivery` with one attachment, acknowledged by `messages-received`.** 3.0 does not define the framing; 4.0 makes exactly this normative, so one implementation serves both and unacknowledged live deliveries are re-queued (FR-PICK-08). Interop risk for the gate (#98): a 3.0 peer that expects raw pushes in live mode will not understand `delivery` wrappers; the live harness must observe which framing the external mediator uses. |
| DD-14 | **Unknown `next` is dropped silently; one grant per `recipient_did`.** The spec's Mediator Process is one-way (it treats sourceward communication as a burden a mediator avoids), and the CM endnote acknowledges squatting. A forward for an unregistered recipient is dropped with constant-work lookup and 202 (no registration oracle, FR-ROUTE-11); a `recipient_did` belongs to the first grant that registers it, and `IMediationPolicy` lets a host require proof of ownership (FR-MED-05). |

### 15.2 Ambiguities to watch (raise if the chosen reading proves wrong)

| Area | Ambiguity | Working interpretation |
|---|---|---|
| `apv` sort | Spec says "alphanumerically sorted" `kid`s. | Ordinal sort of the raw `kid` strings, then join with `.`, SHA-256, base64url-no-pad. Confirm against C.3 vectors (authoritative). |
| `skid` absence | 1PU draft doesn't mandate `skid`. | Always populate `apu`=base64url(skid) on send; on receive, recover sender kid from `skid` else `apu` (FR-ENC-17). |
| Signed `typ` | Construction example shows `"typ":"JWM"` but envelope media type is `application/didcomm-signed+json`. | Use `application/didcomm-signed+json` as the envelope `typ`; tolerate `JWM` on receive. |
| `did:peer` numalgo 4 | Still evolving. | Implement 0 and 2 as stable; gate 4 behind `[Experimental]`. |
| Content-type negotiation failure | No defined behavior when a peer doesn't `accept` the sender's envelope. | Emit a `trust`/`msg`-scoped problem report and, over HTTP, 415/406 as appropriate. |
| `thid` vs rotation | Does the thread id change when `from` rotates? | No — `thid` tracks the interaction; rotation changes identity, not thread. |
| `return_route: thread` | The extension defines the value but no in-scope protocol uses it, and the placement of `return_route_thread` is unspecified. | Parse and surface it; open sessions only for `all` (FR-RR-03); on an established WebSocket session `thread` downgrades like `none` (FR-RR-05), since pushing everything would exceed the request. Revisit when a protocol requires per-thread return routing. |

---

## 16. Glossary

- **Plaintext / Signed / Encrypted message** — the three DIDComm formats; "DIDComm message" usually means the encrypted (outermost) one.
- **anoncrypt** — anonymous-sender encryption via `ECDH-ES+A256KW`; recipient cannot cryptographically identify the sender.
- **authcrypt** — authenticated-sender encryption via `ECDH-1PU+A256KW`; recipient (and only the recipient) can verify the sender. The default wrapping.
- **CEK / KEK** — content-encryption key (encrypts the payload, once) / key-encryption key (wraps the CEK per recipient).
- **skid / apu / apv / epk** — sender key id; agreement PartyUInfo (=base64url(skid) for authcrypt); agreement PartyVInfo (=base64url(SHA-256(sorted recipient kids joined by `.`))); ephemeral public key (one per envelope, same curve as recipients).
- **kid** — a DID-URL identifying a specific verification method (key) in a DID Document.
- **PIURI / MTURI** — Protocol Identifier URI (`…/routing/2.0`) / Message Type URI (`…/routing/2.0/forward`).
- **forward** — the routing-protocol message that wraps an encrypted payload for relay by a mediator (`next` names the onward party).
- **mediator** — a partly trusted relay that unwraps a `forward` and passes the payload onward; never sees plaintext.
- **from_prior** — a signed JWT header proving a DID rotation (`iss`=old DID, `sub`=new DID).
- **profile** — a named bundle of envelope/signing/plaintext/routing choices (`didcomm/v2`) advertised in a service endpoint's `accept` array.

---

*Normative source: DIDComm Messaging v2.1 Editor's Draft (Working Group Approved), DIF. All section references (§…) point to that document. Appendix A/B/C test vectors from the same source are the authoritative interop oracle for this implementation.*
