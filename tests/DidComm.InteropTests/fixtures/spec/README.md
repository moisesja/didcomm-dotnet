# DIDComm v2.1 spec fixtures (Routing Protocol 2.0)

Verbatim JSON snippets pinned from the DIDComm Messaging v2.1 specification
([decentralized-identity/didcomm-messaging](https://github.com/decentralized-identity/didcomm-messaging),
Apache-2.0). Used as KAT-style anchors for Phase 4 routing tests — pinning these
**before** the wrapping code runs is a direct application of lesson L-005
("self-round-trip tests do NOT prove spec interop"). If a future spec revision
changes the shapes, update both the fixture file and the test expectations.

## Files

| File | Source | Spec section |
|------|--------|--------------|
| [`endpoint-example-1.json`](endpoint-example-1.json) | `docs/spec-files/routing.md` (lines 184–191) | §Service Endpoint / Using a DID as an endpoint — "Endpoint Example 1: Mediator" |
| [`endpoint-example-2.json`](endpoint-example-2.json) | `docs/spec-files/routing.md` (lines 195–203) | §Service Endpoint / Using a DID as an endpoint — "Endpoint Example 2: Mediator + Routing Keys" |

Each file is a single DIDComm `DIDCommMessaging` service entry. They are not
full W3C DID Documents on their own — the Checkpoint B fixtures
(`fixtures/diddocs/spec/{bob-with-routing,mediator1,mediator2,charlie}.json`)
embed the same shapes inside complete documents.

## Forward message shape (no fixture file — pinned in code)

The spec's canonical forward example (`docs/spec-files/routing.md` lines 41–54)
is consumed directly by `ForwardMessageTests.Create_emits_canonical_spec_shape`
to keep the test self-contained.
