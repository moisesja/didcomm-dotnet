# Spec DID Documents (Appendix B)

W3C DID Document fixtures for the DIDComm v2.1 Appendix B actors. The key material
(every `publicKeyJwk`) matches the corresponding entry in
[`../../secrets/{alice,bob}.json`](../../secrets/), so a packed envelope using a
recipient/sender kid here can be decrypted/verified using a private key from the
matching secrets file.

## Provenance

Translated from
[sicpa-dlab/didcomm-python](https://github.com/sicpa-dlab/didcomm-python)
(Apache-2.0) at commit `main` 2026-05-23 (alice, bob) and 2026-05-24 (Phase 4
routing additions). Source files:

- `tests/test_vectors/did_doc/did_doc_alice.py` — `DID_DOC_ALICE_SPEC_TEST_VECTORS`
- `tests/test_vectors/did_doc/did_doc_bob.py` — `DID_DOC_BOB_SPEC_TEST_VECTORS` (verification methods) + `DID_DOC_BOB_WITH_NO_SECRETS` (service entry)
- `tests/test_vectors/did_doc/did_doc_mediator1.py` — `DID_DOC_MEDIATOR1_SPEC_TEST_VECTORS`
- `tests/test_vectors/did_doc/did_doc_mediator2.py` — `DID_DOC_MEDIATOR2` (incl. service entry)
- `tests/test_vectors/did_doc/did_doc_charlie.py` — `DID_DOC_CHARLIE`

didcomm-python builds DID Documents programmatically through Python objects
(`DIDDoc`, `VerificationMethod`, `DIDCommService`); we transcribe each
verification method's `publicKeyJwk` into the standard W3C DID Core JSON shape so
the fixtures can be consumed by net-did's
`NetDid.Core.Serialization.DidDocumentSerializer.Deserialize` without method-
specific glue. The `@context` is intentionally omitted — net-did's deserializer
treats absence as the plain JSON (not JSON-LD) representation and the DIDComm
crypto layer does not rely on context-based interpretation.

### Service-entry transcription notes (Phase 4)

didcomm-python's `DIDCommService` emits the legacy pre-2.1 flat service shape
(`serviceEndpoint` as a string + sibling `routingKeys` / `accept` fields). The
v2.1 spec moves `routingKeys` and `accept` **inside** `serviceEndpoint` as a
single object (or array of objects). The fixtures below have therefore been
re-shaped into the v2.1 canonical form so they exercise the
`IServiceEndpointResolver` against the format the parser is targeting. The
underlying values (uri, routingKeys, accept) are preserved verbatim from the
Python sources, with one normalization: `charlie`'s `service_endpoint` value in
didcomm-python is `did:example:mediator2#key-x25519-1`; we drop the fragment to
`did:example:mediator2` so the entry exercises the spec's "mediator-as-DID
endpoint" path (Endpoint Examples 1 & 2) with a resolvable DID rather than a key
URL.

## Files

| File | Subject DID | Authentication keys | Key-agreement keys | Service |
|------|-------------|---------------------|--------------------|---------|
| [`alice.json`](alice.json) | `did:example:alice` | Ed25519, P-256, secp256k1 | X25519, P-256, P-521 | _none_ |
| [`bob.json`](bob.json) | `did:example:bob` | _none (recipient-only)_ | X25519 ×3, P-256 ×2, P-384 ×2, P-521 ×2 | _none_ |
| [`bob-with-routing.json`](bob-with-routing.json) | `did:example:bob` | _none_ | same as `bob.json` | `DIDCommMessaging` → `http://example.com/path` + `routingKeys: [mediator1#key-x25519-1]` |
| [`mediator1.json`](mediator1.json) | `did:example:mediator1` | _none_ | X25519, P-256, P-384, P-521 (×1 each) | _none_ |
| [`mediator2.json`](mediator2.json) | `did:example:mediator2` | _none_ | X25519, P-256, P-384, P-521 (×1 each) | `DIDCommMessaging` → `http://example.com/path` + `routingKeys: [mediator1#key-x25519-1]` |
| [`charlie.json`](charlie.json) | `did:example:charlie` | Ed25519 | X25519 | `DIDCommMessaging` → `did:example:mediator2` + `routingKeys: [mediator1#key-x25519-1]` (mediator-as-DID-endpoint case) |
