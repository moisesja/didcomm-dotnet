# Spec secrets (Appendix A) + Phase 4 mediator extensions

Vendored JWK private keys used by the interop test suite.

## Files

| File | Subject DID | Source |
|------|-------------|--------|
| `alice.json` | `did:example:alice` | DIDComm v2.1 Appendix A (vendored from `sicpa-dlab/didcomm-python` `tests/test_vectors/secrets/alice.py`) |
| `bob.json` | `did:example:bob` | DIDComm v2.1 Appendix A (same source) |
| `mediator1.json` | `did:example:mediator1` | Phase 4 — **reuses Bob's `key-x25519-1` and `key-p256-1` private bytes**, rekeyed with mediator1 kids. See note below. |
| `mediator2.json` | `did:example:mediator2` | Phase 4 — **reuses Bob's `key-x25519-2` private bytes**, rekeyed with the mediator2 kid. |

## Phase 4 mediator-secret reuse note

`tests/DidComm.InteropTests/fixtures/diddocs/spec/mediator1.json` and
`mediator2.json` are transcribed from
[`sicpa-dlab/didcomm-python`](https://github.com/sicpa-dlab/didcomm-python)
`tests/test_vectors/did_doc/did_doc_mediator{1,2}.py`. Those files include a
"FIXME build verification material — currently it's a copy-paste from Bob's
ones" comment: each mediator's `publicKeyJwk` x-coordinate is taken directly
from one of Bob's Appendix A keys. The Python tests therefore decrypt mediator
forward layers using the *same* private bytes as Bob's secrets, just under
different kids.

We follow the same construction for didcomm-dotnet: `mediator1.json` /
`mediator2.json` lift Bob's matching `d` values and assign them to the
mediator kids. This keeps the fixtures aligned with the canonical reference
implementation while letting the Phase 4 round-trip tests decrypt every
forward layer end-to-end without any new key generation. When real-world
fixtures land (e.g. via the standalone `didcomm-dotnet-fixtures` submodule),
they will replace these with independent material.
