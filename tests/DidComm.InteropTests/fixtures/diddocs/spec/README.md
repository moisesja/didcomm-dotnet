# Spec DID Documents (Appendix B)

W3C DID Document fixtures for the DIDComm v2.1 Appendix B actors. The key material
(every `publicKeyJwk`) matches the corresponding entry in
[`../../secrets/{alice,bob}.json`](../../secrets/), so a packed envelope using a
recipient/sender kid here can be decrypted/verified using a private key from the
matching secrets file.

## Provenance

Translated from
[sicpa-dlab/didcomm-python](https://github.com/sicpa-dlab/didcomm-python)
(Apache-2.0) at commit `main` 2026-05-23. Source files:

- `tests/test_vectors/did_doc/did_doc_alice.py` — `DID_DOC_ALICE_SPEC_TEST_VECTORS`
- `tests/test_vectors/did_doc/did_doc_bob.py` — `DID_DOC_BOB_SPEC_TEST_VECTORS`

didcomm-python builds DID Documents programmatically through Python objects
(`DIDDoc`, `VerificationMethod`, `DIDCommService`); we transcribe each
verification method's `publicKeyJwk` into the standard W3C DID Core JSON shape so
the fixtures can be consumed by net-did's
`NetDid.Core.Serialization.DidDocumentSerializer.Deserialize` without method-
specific glue. The `@context` is intentionally omitted — net-did's deserializer
treats absence as the plain JSON (not JSON-LD) representation and the DIDComm
crypto layer does not rely on context-based interpretation.

The `WITH_NO_SECRETS` variants from didcomm-python (and the routing services /
mediator references on alice and bob) are not vendored here because Phase 3's
facade tests do not exercise routing; they will land alongside the
`mediator{1,2}` and `charlie` documents in Phase 4 (FR-ROUTE-*).

## Files

| File | Subject DID | Authentication keys | Key-agreement keys |
|------|-------------|---------------------|--------------------|
| [`alice.json`](alice.json) | `did:example:alice` | Ed25519, P-256, secp256k1 | X25519, P-256, P-521 |
| [`bob.json`](bob.json) | `did:example:bob` | _none (recipient-only)_ | X25519 ×3, P-256 ×2, P-384 ×2, P-521 ×2 |
