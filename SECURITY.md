# Security Policy

didcomm-dotnet implements security-critical cryptographic primitives — JWE (anoncrypt and authcrypt), JWS, ECDH-1PU key wrapping, AES-256-CBC-HMAC-SHA512, and AES-256-KW. Bugs in this code can compromise confidentiality, authenticity, or both. Please read this policy before reporting.

## Supported versions

The project is in pre-alpha development. Until a `v1.0.0` is published, only the `main` branch is supported.

| Version | Supported |
|---|---|
| `0.x.x` (pre-alpha) | Yes — `main` only |

Once `v1.0.0` ships, the most recent minor release will receive security fixes; an updated table will replace this one.

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

### How to report

1. Use GitHub's [private vulnerability reporting](https://github.com/moisesja/didcomm-dotnet/security/advisories/new), or email the maintainer privately.
2. Include:
   - A description of the vulnerability and its impact
   - Steps to reproduce (a failing test or fixture is ideal)
   - The affected commit/version
   - Your name and contact info for credit, if desired

### What to expect

- Acknowledgement within **48 hours**
- A plan for remediation within **7 days**
- A coordinated-disclosure timeline agreed with the reporter
- Credit in the published security advisory unless you ask to remain anonymous

## Scope

Security issues in any of the following areas are in scope:

- **Cryptographic correctness** — incorrect key agreement, KDF inputs, AEAD tag handling, JWS signature verification, JWE key unwrap, point validation for NIST curves
- **Key material handling** — private-key exposure via logs, exceptions, `ToString()`, or serialization; insufficient entropy in nonce generation; key reuse
- **Envelope / parser attacks** — malformed JWE/JWS that bypasses verification, recipient confusion, header smuggling, algorithm confusion, ZIP-bomb-style payloads
- **DID-resolution trust** — resolution result spoofing, cache poisoning, `serviceEndpoint` injection
- **Routing & mediation** — forward-message tampering, mediator authorization bypass, onion-layer leakage
- **Replay & freshness** — `expires_time` not enforced, `id`-uniqueness not enforced, threading guards bypassable
- **DoS via cryptographic input** — pathological JOSE structures that exhaust CPU or memory beyond `max_receive_bytes` (PRD `FR-API-06` / `FR-TRN-07`)

## Out of scope

- Vulnerabilities in upstream dependencies — please report these to the respective project (`NSec.Cryptography`, `Portable.BouncyCastle`, `NetDid`, etc.)
- Issues that require physical access to the host machine
- Denial of service through resource exhaustion that is **not** trivially exploitable from a single unauthenticated message
- Misuse-resistance critiques that don't correspond to a concrete exploit (file these as enhancement issues)
- `did:web` resolution — explicitly out of scope by design (PRD DD-08)

## Best practices for users

Once the library is published, consumers should follow these practices:

- **Never log or serialize private key material** — pass keys via `ISecretsResolver` and avoid `ToString()` on key types
- **Back `ISecretsResolver` with an HSM, cloud KMS, or secure enclave in production** — the in-memory implementations are for development and testing only
- **Validate DID Documents from untrusted sources** before trusting `keyAgreement` or `authentication` verification methods (the resolver does signature/log verification; you choose whom to trust)
- **Enforce `max_receive_bytes`** on every receiving endpoint to bound parser cost
- **Honor `expires_time` on receive** — the library surfaces it but enforcement is application-layer
- **Track `from_prior` rotations** — accepting a rotated DID without verifying the prior signature defeats the rotation guarantee

## Cryptographic-agility commitments

The library implements **only** the algorithm choices specified by DIDComm v2.1. Adding or removing algorithms requires a design discussion — please open an issue rather than a PR. The supported set is documented in PRD §5.

## Coordinated disclosure

If a vulnerability affects the DIDComm specification itself or other implementations (`didcomm-rust`, `didcomm-python`, `didcomm-jvm`), the maintainer will coordinate disclosure with the DIF DIDComm working group and the affected projects before publishing the advisory.
