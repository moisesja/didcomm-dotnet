# Live cross-implementation interop harness

The live half of the PRD §13.6 interop gate: prove, in an automated job, that didcomm-dotnet
and the SICPA reference implementations can exchange packed messages — **outbound**
(didcomm-dotnet packs, they unpack; FR-IX-04, MUST), **inbound** (they pack, didcomm-dotnet
unpacks; FR-IX-05, SHOULD), and **published vectors** (they verify our `source: didcomm-dotnet`
fixture set; FR-IX-06, MUST). The offline (static fixture) half gates every PR in `ci.yml`;
this harness runs nightly, on release, and on demand
(`.github/workflows/interop-live.yml`, FR-IX-08).

## What runs

| Piece | Role |
|---|---|
| `tools/InteropCli` | The didcomm-dotnet side: `mint` / `pack` / `unpack` / `unwrap-forward` over the **real** `DidCommClient` + `UseNetDidResolver()` wiring (net-did composite resolver, did:key + did:peer), and `mediator-pickup`, which runs a real didcomm-dotnet mediator in process (see [Through a running didcomm-dotnet mediator](#through-a-running-didcomm-dotnet-mediator-95)). |
| `python/interop_peer.py` | The didcomm-python side: same CLI shape, driving `didcomm==0.3.2` (sicpa-dlab/didcomm-python) directly. Pins in `python/requirements.txt`. |
| `jvm/src/InteropPeer.java` | The didcomm-jvm side: same CLI shape, driving `org.didcommx:didcomm:0.3.2` — plain `javac`/`java`, jars pinned by version + SHA-256 in `jvm/fetch-deps.sh` (no Gradle/Maven). |
| `run-python-leg.sh` / `run-jvm-leg.sh` | One leg each: mint the identity set, run the matrix in both directions, verify the published vectors, print/emit a per-cell PASS/FAIL/N-A table, exit 1 on any FAIL. A missing or empty `packed/didcomm-dotnet/` vector set hard-fails the leg — FR-IX-06 is a MUST, so an uninitialized fixtures submodule turns the leg red instead of silently shrinking the tally. The python leg exits 2 before running anything if its interpreter is outside CPython 3.9–3.12 (see Pinned versions). |
| `run-mediator-leg.sh` | The mediator leg: the Hyperledger Identus mediator in Docker against our wallet, our sender and our mediator (see [Mediator leg](#mediator-leg-98)). Exits 2 before running anything when Docker, curl, dotnet or a CPython 3.9–3.12 is missing. |
| `run-all.sh` | All three legs + a combined `summary.md` (the CI artifact). |

Every cell round-trips a fresh C.1-style payload and asserts (a) the recovered plaintext
equals the original (`id`, `type`, `body`, `from`, `to`, `created_time`) and (b) the unpack
metadata is exactly what the composition requires (flags, `enc`, `kw`, `sig_alg` — see
`expected_metadata` in `python/interop_peer.py`, mirrored in `InteropPeer.java`). The
multi-key cells also assert (c) the exact set of recipient kids in the envelope (see
[Multi-key recipients](#multi-key-recipients-fr-enc-19)).

### Counterpart choice (deviation from the PRD's first preference)

PRD §13.2 names `sicpa-dlab/didcomm-demo`'s CLIs as the preferred live counterpart. The demo
repo was last pushed in 2021, predates the final didcomm-python/jvm releases, and its
Python/Kotlin app scaffolding does not install on supported toolchains. The harness therefore
drives the **underlying reference libraries the demo wraps** — `didcomm` 0.3.2 (PyPI, final
sicpa-dlab/didcomm-python release) and `org.didcommx:didcomm` 0.3.2 (Maven Central, final
sicpa-dlab/didcomm-jvm release) — with thin, pinned drivers that do exactly what the demo
CLIs did: pack/unpack over peer DIDs. The PRD's intent (live proof against the SICPA
reference family, both directions) is met; only the wrapper differs.

`did:peer:2` and `did:key` handling in both drivers is implemented against the current specs
(decode `.Ez`/`.Vz` multibase(multicodec) segments; name keys `#key-N` in order of appearance,
`.S` service segments consuming no number — the numbering net-did emits) instead of the
2022-era `peerdid` packages, so kids agree byte-for-byte across all three implementations.

## Matrix dimensions covered

The §13.5 conformance matrix, as exercised live:

| Dimension | Live coverage |
|---|---|
| Envelope composition | plaintext · signed · anoncrypt · authcrypt · anoncrypt(sign) · anoncrypt(authcrypt) |
| Key-agreement curve | X25519 · P-256 (P-384/P-521 are offline-fixture-only — see the `n/a` notes) |
| Content encryption | A256CBC-HS512 · A256GCM · XC20P |
| Signing alg | EdDSA · ES256 · ES256K |
| Recipients | single · 3 DIDs (mixed DID **methods** — two `did:peer:2` + one `did:key`; all three key-agreement keys are X25519, necessarily, since a single JWE carries a single `epk`, so recipient curves cannot be mixed within one envelope) · one DID with 3 `keyAgreement` keys (FR-ENC-19): addressed by bare DID, the envelope must carry all three kids and a device holding only the **last** key must decrypt (anoncrypt and authcrypt); addressed by the DID URL `<did>#key-2`, the envelope must carry that one kid and the key-2 device must decrypt (authcrypt) |
| Routing | direct · 1 mediator · 2 mediators (real `forward` onion, both directions) · through a running didcomm-dotnet mediator (pickup, inbound) |
| DID method | `did:peer:2` · `did:key` |
| Direction | inbound · outbound · published-vector verification (FR-IX-06) |

The executed cell set is a **curated slice** of the §13.5 cross-product, not the full
cartesian product: every dimension value above appears in at least one executed cell, but not
every combination does (XC20P runs on X25519 only; ES256K appears only in `signed`
compositions; the mediated cells are authcrypt). The per-cell enumeration is the results
tables below, and that enumeration is the acceptance basis FR-IX-04 points at — a §13.5
combination absent from those tables is a disclosed slice choice, not a silent skip, while a
combination a counterpart cannot run at all is a declared `n/a` with evidence.

## Multi-key recipients (FR-ENC-19)

DIDComm v2.1 says an encrypted message's default recipients SHOULD include every
`keyAgreement` key of the recipient DID, so any of the recipient's devices can decrypt. A DID
URL in `to` (`<did>#key-2`) pins one key instead. didcomm-python and didcomm-jvm 0.3.2 both
encrypt to every key of the recipient's first key type, and both accept a DID URL `to`.

The older `authcrypt 3-rcpt` cell can't show either behaviour. Its receiver holds all three
secrets, so it decrypts whichever key the packer chose, and encrypting to the first key alone
passes. That cell proves one envelope can address three DIDs (outbound) and that an envelope
with several entries for one DID unpacks (inbound). Nothing more.

Three cells close the gap. Each addresses the receiving side's 3-key DID (`multi`, keys
`#key-1` to `#key-3`) and keeps the plaintext `to` as the bare DID:

| cell | packer's `to` | the envelope must carry exactly | unpacked by the device holding only |
|---|---|---|---|
| anoncrypt 3-key last device | the bare DID | `#key-1`, `#key-2`, `#key-3` | `#key-3` |
| authcrypt 3-key last device | the bare DID | `#key-1`, `#key-2`, `#key-3` | `#key-3` |
| authcrypt kid-pinned | `<did>#key-2` | `#key-2` | `#key-2` |

- **Devices.** Both peer drivers have a `device-identity` subcommand that writes a device's
  identity file: the same DID, with one secret. The last-device cells use the last key because
  a packer that encrypts to the first key only never reaches it.
- **Wire check.** The `assert-recipients` subcommand checks the envelope's recipient kids
  before the unpack: none missing, none extra, none repeated. The unpack still runs if that
  check fails, so a red cell's log shows both the recipient set and the device's error.
- **Native APIs.** Each side packs the DID URL through its own API: didcomm-python
  `pack_encrypted(to=...)`, didcomm-jvm `new PackEncryptedParams.Builder(message, to)`, and
  didcomm-dotnet a DID URL entry in `PackEncryptedOptions.Recipients`.
- **Why authcrypt for the pin.** The sender's key then has to be chosen against the single
  pinned key too.

**Red first, then green.** These cells were added before the didcomm-dotnet fix (#81) and run
on `feat/enc19-all-recipient-keys` at `d9ae685`, which doesn't have it. Results on both legs,
before the fix:

- **Inbound (the counterpart packs): all three PASS.** Both counterparts emitted `#key-1` to
  `#key-3` for the bare DID and only `#key-2` for the DID URL. didcomm-dotnet unpacked each
  envelope with the one-key device.
- **Outbound last device (anoncrypt and authcrypt): FAIL.** The wire check reports
  `ASSERT FAIL: envelope recipients: expected 3 kid(s), got 1; missing [<did>#key-2,
  <did>#key-3]`. Then the key-3 device can't decrypt. didcomm-python reports
  `DIDUrlNotFoundError: No secrets are found in secrets resolver for DID URLs:
  ['<did>#key-1']`, and didcomm-jvm reports `SecretNotFoundException: The Secret
  '<did>#key-1' not found`.
- **Outbound kid-pinned: FAIL** before any envelope exists: `InteropCli:
  DidResolutionException: DID resolution failed for '<did>#key-2': value is not a
  syntactically valid DID.`

These are FAIL, not `n/a`, because the defect is didcomm-dotnet's. To confirm the cells can
go green, each counterpart stood in for didcomm-dotnet once and packed to its own 3-key DID.
The cells' wire check, device unpack and plaintext assert then passed for all three cells on
both legs, and the key-3 device could not open the pinned envelope. With the #81 fix, all three
outbound cells pass on both legs (results below). A cell that goes red again means a regression.
It is not a reason to declare the cell `n/a`.

## Current results — python leg (executed 2026-10-29, macOS arm64, CPython 3.12.7)

`didcomm-dotnet @ feat/enc19-all-recipient-keys, with the #81 fix (DataProofsDotnet.Jose 1.5.0 +
NetCrypto 1.7.1)` ↔ `didcomm-python 0.3.2`.
**Executed 62 · passed 62 · failed 0 · declared n/a 0.** Exit code 0. Before the fix, the three
outbound FR-ENC-19 cells failed (see [Multi-key recipients](#multi-key-recipients-fr-enc-19)).

| composition | enc | routing | dotnet→python | python→dotnet |
|---|---|---|---|---|
| plaintext | — | direct | PASS | PASS |
| signed EdDSA | — | direct | PASS | PASS |
| signed ES256 | — | direct | PASS | PASS |
| signed ES256K | — | direct | PASS | PASS¹ |
| anoncrypt X25519 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt X25519 | A256GCM | direct | PASS | PASS |
| anoncrypt X25519 | XC20P | direct | PASS | PASS |
| anoncrypt P-256 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt P-256 | A256GCM | direct | PASS | PASS |
| authcrypt X25519 | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt P-256 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt(sign) | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt(sign) | XC20P | direct | PASS | PASS |
| anoncrypt(authcrypt) | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt 3-rcpt | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt 3-key last device | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| authcrypt 3-key last device | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| authcrypt kid-pinned | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| anoncrypt did:key | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt did:key | A256CBC-HS512 | direct | PASS | PASS |
| signed did:key | — | direct | PASS | PASS |
| authcrypt | A256CBC-HS512 | 1 mediator | PASS | PASS |
| authcrypt | A256CBC-HS512 | 2 mediators | PASS | PASS |

FR-IX-06 (didcomm-python verifying the 16 published `source: didcomm-dotnet` vectors):
**16 / 16 PASS**, each handed to didcomm-python byte-for-byte as published — no reshaping at
the driver boundary, so a green row means an external implementation really can read the file
this repo ships.

Every cell above is an unshimmed cross-library round-trip, and the leg declares **no `n/a`
cells at all** — every §13.5 dimension this pair supports executes for real. The driver's
former Flattened→General re-serialization has been **removed** (see ² below), so the
counterpart sees exactly the bytes didcomm-dotnet emits.

## Current results — jvm leg (executed 2026-10-29, macOS arm64, JDK 25.0.2)

`didcomm-dotnet @ feat/enc19-all-recipient-keys, with the #81 fix (DataProofsDotnet.Jose 1.5.0)`
↔ `didcomm-jvm 0.3.2`.
**Executed 59 · passed 59 · failed 0 · declared n/a 3.** Exit code 0. Before the fix, the same three
outbound FR-ENC-19 cells failed as on the python leg.

| composition | enc | routing | dotnet→jvm | jvm→dotnet |
|---|---|---|---|---|
| plaintext | — | direct | PASS | PASS |
| signed EdDSA | — | direct | PASS | PASS |
| signed ES256 | — | direct | PASS | PASS |
| signed ES256K | — | direct | **N-A³** | **N-A³** |
| anoncrypt X25519 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt X25519 | A256GCM | direct | PASS | PASS |
| anoncrypt X25519 | XC20P | direct | PASS | PASS |
| anoncrypt P-256 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt P-256 | A256GCM | direct | PASS | PASS |
| authcrypt X25519 | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt P-256 | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt(sign) | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt(sign) | XC20P | direct | PASS | PASS |
| anoncrypt(authcrypt) | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt 3-rcpt | A256CBC-HS512 | direct | PASS | PASS |
| anoncrypt 3-key last device | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| authcrypt 3-key last device | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| authcrypt kid-pinned | A256CBC-HS512 | direct | PASS (red before #81) | PASS |
| anoncrypt did:key | A256CBC-HS512 | direct | PASS | PASS |
| authcrypt did:key | A256CBC-HS512 | direct | PASS | PASS |
| signed did:key | — | direct | PASS | PASS |
| authcrypt | A256CBC-HS512 | 1 mediator | PASS | PASS |
| authcrypt | A256CBC-HS512 | 2 mediators | PASS | PASS |

FR-IX-06 (didcomm-jvm verifying the 16 published `source: didcomm-dotnet` vectors):
**15 / 15 runnable PASS**, each handed over byte-for-byte as published, with `signed-es256k`
declared `n/a` for the JDK curve gap in ³. The vector loop has the same declared-`n/a`
mechanism as the matrix (`vector_na_reason`), scoped to that one vector by exact name — any
other vector failure still fails the leg, verified by corrupting a second vector's ciphertext
and confirming exit 1.

Two didcomm-jvm API limits shape the table rather than appearing as failures. It packs to a
single recipient **DID** (`PackEncryptedParams.to` is a `String`), so the 3-recipient cell
addresses one DID carrying three `keyAgreement` keys inbound, and three distinct DIDs outbound
(didcomm-dotnet packs, didcomm-jvm unpacks); the driver rejects a multi-DID `--to` with an
explicit message rather than silently addressing only the first. That one `to` can also be a
DID URL, which pins a single key; the inbound `kid-pinned` cell uses it. Forward onions are built and
peeled with didcomm-jvm's own `Routing.wrapInForward` / `unpackForward`, `routingKeys[0]`
outermost — verified hop by hop in both directions.

### ¹ Resolved — inbound ES256K high-S signatures (crypto-dotnet#23, NetCrypto 1.5.0)

Kept as history because this is the model case of an `n/a` that named a **falsifiable** cause
and was then falsified by a fix rather than by argument.

didcomm-python emits secp256k1 signatures with a high-S scalar in roughly half of runs. **RFC
8812 imposes no low-S requirement, so those signatures are valid and didcomm-python was never
at fault.** didcomm-dotnet was the strict side: `NBitcoin.Secp256k1` inherits libsecp256k1's
anti-malleability policy, so `DefaultCryptoProvider.VerifySecp256k1` rejected every high-S
signature. Originally reproduced over 8 consecutive runs with exact correlation (every high-S
rejected, every low-S accepted), and confirmed independently by malleating S to n−S on a
NetCrypto 1.4.0 signature, which then verified `False`.

**NetCrypto 1.5.0 fixes it** (crypto-dotnet#23): `VerifySecp256k1` normalizes `(r, n−s)` to
low-S before handing the signature to the NBitcoin backend. Re-measured on 1.5.0 over 20 runs,
classifying each signature by S parity before verifying:

| S parity | signatures produced | verified |
|---|---|---|
| high-S | 8 | **8** (was 0) |
| low-S | 12 | 12 |

The `n/a` is retired and the cell executes. A single green run would not have been evidence
here — high-S occurs only ~50% of the time, so the parity split above is what actually
demonstrates the fix.

The JVM leg keeps its own, unrelated ES256K `n/a`: JDK 16 removed secp256k1 from SunEC and
didcomm-jvm 0.3.2 bundles no BouncyCastle, so the counterpart stack cannot do the curve at all
(didcomm-dotnet#71).

### ² Resolved — JWS serialization and `kid` placement (dataproofs-dotnet#17, #25)

Kept as history because the harness's shape was argued over, and because it is the clearest
worked example of *why* an `n/a` must name a falsifiable cause:

- **Through 1.1.1**, signed envelopes carried `kid` in **both** the protected and the
  per-signature unprotected header. RFC 7515 §7.2 requires those sets to be disjoint;
  nimbus-jose-jwt (inside didcomm-jvm) enforces it and rejected the envelope outright. Filed
  as **dataproofs-dotnet#17**.
- **1.2.0/1.2.1 fixed the disjointness violation** but moved `kid` to the **protected header
  only** — the opposite of what the spec's Appendix C.2 vectors and both SICPA libraries
  expect. Outbound `signed` then failed against *both* counterparts: didcomm-jvm with
  `MalformedMessageException: JWS Unprotected Per-Signature header must be present`
  (`Unpack.kt:63`), and didcomm-python with `MalformedMessageError: INVALID_MESSAGE`, because
  `core/validation.py:15-16` requires `signatures[0].header.kid` and `core/sign.py:43` reads
  it unconditionally, never consulting the protected header. Those cells were run red rather
  than reclassified `n/a` — the defect was ours, not a counterpart limitation.
- **1.3.0 (dataproofs-dotnet#25) resolves both.** `kid` is now emitted in the per-signature
  **unprotected** header (`protected = {alg, typ}`), matching the vendored spec vectors, and
  General JSON serialization is emitted at **every** signer count.

Two consequences, both verified by execution rather than inference:

1. The old **General-only parsing** `n/a` for outbound `anoncrypt(sign)` is retired. Its stated
   cause — an inner *Flattened* JWS sealed inside the ciphertext, unreachable by any
   wire-level shim — no longer exists, and the cell now runs and passes.
2. The drivers' **Flattened→General shim is deleted**. It never fired once across a full run
   on 1.3.0, so it was dead code; leaving it would have silently absorbed a regression back to
   Flattened and reported a green cell for an envelope the counterpart could not actually
   read. The full leg was re-run after deletion and stayed 55/55 (python) / 53 passed with the
   same single ES256K vector red (jvm).

### ³ Declared `n/a` — ES256K on the JVM leg, secp256k1 absent from the JDK

Not a DIDComm-level limitation and not ours: **JDK 16 removed secp256k1 from SunEC**, and
didcomm-jvm 0.3.2 bundles no BouncyCastle, so the counterpart stack cannot do secp256k1 at all
on any JDK ≥ 16 (reproduced on 25). EdDSA and ES256 are unaffected — this is curve
availability in the JRE, nothing about the ES256K envelope format. Both directions are
declared, each reproduced verbatim:

- **jvm signs** — `UnsupportedAlgorithm: The algorithm Unsupported signature algorithm is not
  supported` (`JWS.kt:58`), caused by `java.security.SignatureException: Curve not supported:
  java.security.spec.ECParameterSpec@..`.
- **jvm verifies** — the same missing curve makes nimbus's `ECDSAVerifier` **catch** that
  `SignatureException` and return `false` instead of throwing, so didcomm-jvm reports a
  perfectly valid signature as `MalformedMessageException: Invalid signature` (`JWS.kt:81`).
  A silent false negative that is indistinguishable from tampering — worth knowing before
  anyone debugs an ES256K cell here.

The fault is demonstrably not in our bytes: the **vendored spec vector**
`packed/spec/test-signed-didcomm-message-alice-key-3.json` fails identically, while its
key-1 (EdDSA) and key-2 (ES256) siblings verify clean through the same code path. Isolation
probes confirmed the chain end to end — `ECKey.parse` and `toECPublicKey()` both *succeed* for
secp256k1 (nimbus carries its own domain parameters), and the failure appears only when the
JCA `Signature` object is driven.

This also gates the `signed-es256k` published vector in FR-IX-06, which carries the same
declared `n/a` (`vector_na_reason` in `run-jvm-leg.sh`, matched on that exact name). Adding
BouncyCastle to `jvm/fetch-deps.sh` was rejected deliberately: it would test against a
*modified* didcomm-jvm rather than the real 0.3.2 a peer would run, trading a true negative
for a comfortable green. Pinning the leg to a JDK ≤ 15 would hold the whole run hostage to an
EOL runtime for one cell. Leaving it permanently red was rejected too — a red that can never
go green trains readers to ignore the signal, and then a real regression arrives and nobody
looks.

**The n/a is falsifiable and must not outlive its cause.** If BouncyCastle is ever added, the
counterpart fixes this, or the leg moves to a JDK that still carries secp256k1, this vector
and both ES256K matrix cells MUST start passing — and these three `n/a` declarations must then
be deleted. A green run with them still in place is a harness bug, not a pass.

## Through a running didcomm-dotnet mediator (#95)

The `1 mediator` / `2 mediators` cells peel each hop with `unwrap-forward`, the routing core
alone. One more inbound cell per leg, **authcrypt via a running dotnet mediator (pickup)**, sends
through the whole mediator service instead:

1. The counterpart authcrypts a fresh payload for `dotnet-base` and wraps one forward for the
   mediator `dotnet-medsvc` with its own routing code (`pack --route`).
2. `InteropCli mediator-pickup --mediator dotnet-medsvc.json --recipient dotnet-base.json --in
   <forward>` starts a real didcomm-dotnet mediator in process: Kestrel on `127.0.0.1` (an
   ephemeral port), `AddMediator()` with `dotnet-medsvc`'s DID as the routing DID,
   `MapDidCommMediator("/didcomm")`. Start-up runs the FR-MED-07 checks, which is why
   `dotnet-medsvc` is minted with `--endpoint https://mediator.invalid/didcomm`: its DID document
   must name an http(s)/ws(s) `DIDCommMessaging` endpoint, but nothing ever dials that address.
3. The verb enrols `dotnet-base` (Coordinate Mediation 3.0 over HTTP, `return_route: all`:
   `mediate-request`, then `recipient-update` adding its own DID), posts the counterpart's
   forward to the endpoint byte for byte (it must answer `202`), picks the message up
   (Message Pickup 3.0 `delivery-request`), prints the same `{message, metadata}` JSON as
   `unpack`, acknowledges it and requires `status` to count 0 afterwards.
4. The counterpart's `assert` checks the recovered plaintext and the authcrypt metadata, as for
   every other cell.

There is no outbound half: the mediator is didcomm-dotnet's, so the counterpart only sends
through it. The cell fails when the forward is not for this mediator (the endpoint answers
`400`), when its `next` is not the enrolled recipient (dropped, nothing is delivered before the
timeout), or when anything is left queued after the acknowledgement.

Executed 2026-11-13 on macOS arm64 (`didcomm-dotnet @ feat/mediator-pipeline-95`, CPython 3.12,
JDK 25):

| leg | new cell | leg totals |
|---|---|---|
| python (didcomm-python 0.3.2) | PASS | executed 63 · passed 63 · failed 0 · declared n/a 0 |
| jvm (didcomm-jvm 0.3.2) | PASS | executed 60 · passed 60 · failed 0 · declared n/a 3 (the ES256K cells and vector, unchanged) |

Every other cell gave the same result as in the tables above in the same runs; the totals are the earlier
ones plus this cell.

## Mediator leg (#98)

The external-mediator gate of PRD §12 Phase 7 ("at least one external-mediator leg … green
nightly against a pinned image"). `run-mediator-leg.sh` runs the **Hyperledger Identus
mediator v1.2.1**, pinned by digest, and drives it from both sides: our client as the recipient
and as the sender, a didcomm-python recipient through it, and the same python recipient and our
own wallet through **our** mediator, as the attribution control.

### What runs

| Piece | Role |
|---|---|
| `identus` | Identus mediator v1.2.1 on `127.0.0.1:<P>` (a free port, retried up to 5 times on a clash). Its DID advertises `http://127.0.0.1:<P>` then `ws://127.0.0.1:<P>/ws`. Keys are generated per run (`mediator_peer.py okp-keys`, passed in an env file), never the compose demo keys. Its MongoDB 6.0 runs with `--auth` on tmpfs (no volumes), initialised by Identus's own `initdb.js` (vendored, below). |
| `identus-px` | A second Identus with its own MongoDB, reachable only on the leg's Docker network under the name `identus-px`, port 8080, with no host port. Its DID advertises `http://identus-px:8080` and `ws://identus-px:8080/ws`. A client on the host can reach it only through the proxy. |
| `squid` | Squid (`ubuntu/squid`), deny by default (`mediator/squid/squid.conf`); its log and spool directories are tmpfs mounts, so a run leaves no anonymous volumes. It allows plain HTTP and CONNECT to `identus-px:8080` only, and answers 403 to everything else, 127.0.0.1 included. Before any cell runs, the leg checks all three: a request routed to `identus-px`, a CONNECT tunnel to it, and a 403 for `127.0.0.1:<P>`. |
| `mediator/MediatorWallet` | Our wallet: `DidComm.Mediator.Client`, the HTTP and WebSocket transports and DI, the reference set of sample 11's wallet. No mediator package, no ASP.NET Core. Commands `enrol`, `fetch`, `ws-attempt`, `observe` and `boundary`, each printing one JSON result. It is built inside the leg, so a build break fails this leg only. |
| `tools/InteropCli` | `pack --forward true` is the dotnet sender. `mediator-serve` runs a didcomm-dotnet mediator on `127.0.0.1` for the reverse cells. Its stdin is a FIFO the leg holds open, so the mediator stops when the leg does; its own lifetime limit equals the leg watchdog. |
| `mediator/python/mediator_peer.py` | didcomm-python 0.3.2, in the python leg's pinned venv (`python/requirements.txt`; helpers imported from `python/interop_peer.py`, unchanged). It wraps forwards explicitly (`wrap_in_forward`: didcomm-python 0.3.2 does not wrap for a service whose `uri` is a DID without `routingKeys`). It also enrols and picks up with Coordinate Mediation 2.0 and Message Pickup 3.0 messages built by hand. Every request carries `from`, exactly one `to`, `created_time` in seconds and `return_route: all`, and is authcrypted. Every HTTP call goes through an explicit `ProxyHandler`. |

Every Docker object has the label `org.didcomm-dotnet.interop-leg=mediator-<run id>`. On exit the
leg removes its objects by that label, whatever ended it (a failure, Ctrl-C, or the watchdog), and
it never prunes. A second Ctrl-C or TERM cannot interrupt the removal. The watchdog is 25 minutes
for the cells and 15 for `--negative-controls`, so both fit with the python and jvm legs (about 3
minutes in CI) inside the job's 60 minutes. A leg stopped before its end still writes a summary
that says so. Waits re-check the exact condition they need (a log line, a
status code, a count) at most 0.2 s apart. Time is only a hang guard, a background sleep, since
macOS has no `timeout(1)`. The script runs on bash 3.2.

### Cells

Gating: the leg exits 1 when any of these fails. `--negative-controls` runs each sabotage instead,
and exits 0 only when every sabotaged run FAILED **for the reason its sabotage causes**: each
control declares the failure detail it expects (a problem code, an HTTP status, an exception and
step, a missing log line). A sabotaged run that fails differently is reported `FAIL (wrong
reason)` and counts as a broken control, like one that passes.

| cell | what it proves | PASS when | sabotage (must FAIL) |
|---|---|---|---|
| B-0 | E-36: the wallet references only the client package | `check_boundary.py` finds no `DidComm.Mediator`, `DidComm.Mediator.AspNetCore`, `DidComm.AspNetCore` or `Microsoft.AspNetCore.*` in any of five views: the restore graph, the framework references, `runtimeconfig.json`, `deps.json`, and the compiled references from `MediatorWallet boundary`. `DidComm.Mediator.Client` must be present (positive control). | `forbid-client`: forbid the client package itself, in both `MediatorWallet boundary` and `check_boundary.py`; both must fail |
| M-1 | E-30, E-51; I-1, 2, 3, 5, 6, 7, 10, 14, 15: our client as a recipient through Identus, over HTTP | `/version` is `1.2.1` and `/did` matches the per-run keys and endpoints. The invitation is from that DID, with goal code `request-mediate`. `enrol` with no version pinned negotiates 2.0, and the captured negotiation record says so. The grant is for the wallet DID, with routing DIDs exactly [Identus], and a second request gets the identical grant. B and B2 are registered with `Success` each, and listing gives exactly {B, B2}. Python's forward gets 202. `fetch` waits for the status, fetches until drained (exactly one item), and both the drained status and a separate status count 0. `assert` passes on the authcrypt plaintext and metadata. | `pin-v3`: Identus answers with a problem report. `unregistered-next`: HTTP 200, not 202. `wrong-message`: the assert fails. `identus-stopped`: Identus stopped after the 202, so the fetch fails. |
| M-2 | E-35 (L-055 against a mediator that ignores `limit`) | 12 forwards are posted at once and all get 202. `fetch --expect-count 12 --limit 5` gets 12 unique items, exactly the 12 sent, and the final status is 0. | `expect-13`: the hang guard ends the wait |
| M-3 | Our sender to a python recipient through Identus | Python enrols at Identus (CM 2.0) and registers P2, whose service `uri` is the Identus DID. `InteropCli pack --forward true` wraps one forward for Identus (FR-ROUTE-04) and Identus accepts it with 202. Python's Pickup 3.0 gets the message; `messages-received` gets HTTP 202 and nothing else, as Identus does, then the status is 0. `assert` passes. | `no-forward`: Identus still answers 202 (it cannot decrypt the envelope and says nothing), so nothing is queued and the pickup's hang guard fails the cell |
| M-4 | E-47, the HTTP half: `TrustedProxy` | The wallet (`--trusted-proxy`, `--resolve-dns-names false`) enrols with `identus-px` and fetches through squid; python posts through squid; `assert` passes. `identus-px` has no other route (no host port, a name only the leg's network resolves), so the passing flow is the proof. Squid's log for the window is kept as evidence, with the wallet's POSTs counted apart from python's by User-Agent; the count is not gated. | `no-proxy`: enrolment fails at `request-mediation` with a direct-connect error for `identus-px` |
| M-5 | E-47, the WebSocket half, at transport level | `ws-attempt --ws-endpoint auto` through the proxy uses `ws://identus-px:8080/ws`. Squid logs `CONNECT identus-px:8080 200 TCP_TUNNEL`, and `identus-px`'s count of `WS Error (type:Binary)` rises, so our frame crossed the tunnel. The call's own outcome (a timeout, see below) is recorded, not gated. | `no-proxy`: no CONNECT line. `no-frame`: the tunnel opens but the handshake asks for a path with no WebSocket behind it, so no frame reaches Identus (exercises the frame check). |
| M-6 | E-48: the process proxy is ignored | All six variables `HTTP(S)_PROXY`, `ALL_PROXY` (both cases) point at squid and `NO_PROXY` is unset. Precondition (the wallet exits 3 otherwise): `HttpClient.DefaultProxy` would send each Identus address to squid. Then: (a) the M-1 flow against `127.0.0.1` passes, which squid's 403 makes impossible through squid, and a direct WebSocket attempt reaches Identus; (b) enrolment with `identus-px` fails at `request-mediation` with `HttpRequestException` over a `SocketException` for `identus-px` (a direct connection attempt), although squid could have reached it; (c) squid logs nothing in the window. | `with-trusted-proxy`: squid denies it (403), so the flow fails. `no-proxy-env`: `NO_PROXY=127.0.0.1` fails the precondition. `proxy-in-b`: `TrustedProxy` on the (b) enrolment only, which then succeeds. `squid-request-in-window`: one request through squid inside the window, which (c) must see. |
| R-1 | Python as the recipient through **our** mediator (I-4, I-5, E-52) | CM 2.0 by hand: the grant has the canonical type, `thid` = request id, a string `routing_did` equal to our mediator's DID, `from`, `to` and `created_time` (FR-RR-09). `keylist-update` answers `success`, and `keylist-query` lists P with `pagination`. Variants: a `mediate-request` with no body is granted; a `keylist-update` without `return_route` gets 202 and is still applied. Then forward, 202, Pickup 3.0 (status 1, delivery, unpack, `messages-received` answered with status 0), and `assert` passes. | `no-created-time`: 202 with no grant (FR-RR-08). `v2-disabled`: `mediate-request` is not answered. |
| R-2 | Control: the same wallet against our mediator | `enrol --expect-version 3.0 --endpoint-override <ready.json http>`, python forward, `fetch --explicit-ack`, and `assert` passes. `AckAsync` must answer a status of 0 (compare O-9); `FetchAsync` has already acknowledged those ids, so this proves AckAsync's answer, not its effect. Then the one gated WebSocket exchange: `ws-attempt` against our mediator's `ws` endpoint must end `ok` (a status over the WebSocket transport). | `expect-v2`: our mediator negotiates 3.0. `ws-wrong-path`: the WebSocket call goes to a path with no endpoint behind it. |

**WebSocket against Identus is observed, never gated** (an open owner decision on #98, O-98-1, whether to send text frames). Our transports
send every envelope as a Binary frame. The DIDComm v2.1 transports section names no frame type.
Identus (scala-did 0.1.0-M48) processes only Text frames and logs `WS Error (type:Binary):
Unexpected event` for the rest. So every WebSocket call from our client to Identus times out.
M-5 and M-6 gate only what the transport did: whether it went through the proxy, and whether
the frame arrived. The one WebSocket exchange that is gated is R-2's, against our own mediator.

### Identus behaviours the leg is built around

All are recorded as observations; none fails the leg.

- **No answer costs 3 s.** Identus answers HTTP 202 only after its 3 s outbound queue times out.
  That covers a successful forward, `messages-received`, an envelope it cannot decrypt (log: `No
  Agent to inform`), and a `keylist-query` from a DID that is not enrolled.
- **`messages-received` gets no `status`.** Pickup 3.0 says the mediator SHOULD send one. Also,
  each `messages-received` marks one message delivered, however many ids it names. So M-1 uses
  `FetchAsync` + `GetStatusAsync`, and M-2's fetch of 12 takes about 13 rounds of 3 s each.
  O-8 and O-9 record both behaviours.
- **No lease, and `limit` is ignored.** Every `delivery-request` returns every undelivered message.
- **An unregistered `next`** gets HTTP 200 with a signed `e.p.req.not_enroll` problem report
  (O-14; M-1's `unregistered-next` sabotage relies on it).
- **Dejavu.** Identus refuses an identical envelope twice, but accepts a re-encrypted repeat of
  the same message id (O-12).
- **A DID another account registered.** A `keylist-update` that adds it got HTTP 500 after 3 s
  in the local runs, not a `keylist-update-response` with `server_error` (O-16; first come, first
  served either way).
- **Identus logs its private keys at start-up.** They are per-run and thrown away with the
  containers. Uploaded failure logs (`mediator-logs/`) and set-up failure messages (which quote
  a log tail) have them, and the per-run Mongo root password, redacted.

### Observations (data, not gated)

`MediatorWallet observe` runs after the cells, against an observer wallet it enrolled with
`identus`. It is a raw probe that uses only `DidCommClient` pack/unpack, a proxy-less
`HttpClient` and a `ClientWebSocket`. It records O-0 (crypto in each direction), O-2 and O-2c
(Text-frame WebSocket answers, live push framing, forwards after a live socket closed), O-3
(lease, head-of-line), O-4, O-5 and O-6 (`thid`, `created_time` and `to` on every answer), O-8
(`limit`), O-9 (`messages-received`, `AckAsync`), O-11 (rewrap and `please_ack` /
`return_route`, against a python-built trigger forward), O-12 (dejavu), O-13 (a malformed
`keylist-update`, an unparseable body) and O-15 (the Discover Features disclosure). The leg adds:

- O-1: our client over WebSocket with the defaults;
- O-5-host: the container's clock against the host's;
- O-7: one page of `QueryRecipientsAsync(Limit 1)` from M-1's enrolment (Identus ignores
  `paginate`);
- O-10: delivery attachment encoding as python sees it, from Identus and from our mediator;
- O-11-python: the Identus delivery against python's inner envelope;
- O-14: an unregistered `next`;
- O-16: a second account adding a DID that M-1's wallet registered (first come, first served;
  ADV-1);
- O-21: provenance (`/version`, the scala-did jars, the image refs and architecture as run,
  Docker, the runner image, CPython, didcomm-python);
- O-19 and O-20: not applicable here, with the reason.

Each record is
`{"id","e":[],"i":[],"title","value","method","gateable":false}`. All of them go to
`mediator-observations.json`. The summary's second table shows one plain-language line per record
(the data stays in the JSON; no runner paths are recorded).

### The #99 interop table, re-checked against the pinned image (E-50)

#99 read the Identus source at `main` `5664f1d` with scala-did 0.1.2. The pinned image is `v1.2.1`
with scala-did 0.1.0-M48 (the Coordinate Mediation and Discover Features executers are unchanged
between the two). Each row of the #99 plan's table I-1..I-15
(`tasks/todo20261118-114952-coordinate-mediation-v2-99.md`), as the leg finds it:

| # | #99 (source read) | On the pinned image | Where |
|---|---|---|---|
| I-1 | `routing_did` is a string | confirmed: a JSON string, equal to the Identus DID | M-1, O-15 |
| I-2 | `result` is only `success` / `no_change` | confirmed for adds; adding a DID another account holds gets HTTP 500 after 3 s, not a `server_error` entry | M-1, O-16 |
| I-3 | `paginate` ignored, no `pagination` | confirmed: `Limit 1` returns both DIDs, no `pagination` | O-7 |
| I-4 | the Identus SDK sends `keylist-update` without `return_route` | not observable (no SDK in the leg); that shape is sent to **our** mediator in R-1 and is applied | R-1 |
| I-5 | Identus requires `from` and exactly one `to` | confirmed: our requests carry both and are accepted | M-1 |
| I-6 | a second `mediate-request` re-grants | confirmed: an identical grant | M-1 |
| I-7 | answers carry `thid` = the request id; no `created_time` | confirmed (problem reports use `pthid`) | O-4, O-5 |
| I-8 | anoncrypt and signed requests are accepted | confirmed: an anoncrypt `status-request` is answered with an authcrypt `status` | O-0 |
| I-9 | its own problem codes | confirmed: `e.p.msg.unsupported` (a 3.0 request), `e.p.req.not_enroll` (an unregistered `next`), `e.p.crypto.message.dejavu` (a repeated envelope) | M-1 `pin-v3`, O-14, O-12 |
| I-10 | Discover Features: CM 2.0 under role `responder`; `match` is a regular expression | confirmed; our `…/3.*` and `…/2.*` queries work and the client picks 2.0 with no pin; a `match` of `*` gets HTTP 500 | M-1, O-15 |
| I-11 | (Blocktrust) `from_prior` on first contact | not applicable: no Identus answer carries `from_prior` | observe |
| I-12 | the SDK sends `created_time`; Identus stamps none | confirmed for Identus's answers | O-5 |
| I-13 | (Blocktrust) `custom_headers` | not applicable | — |
| I-14 | Identus dispatches on the exact type string | confirmed with our canonical `/2.0/` types; R-1 checks that ours are canonical too | M-1, R-1 |
| I-15 | the grant's `from` is the mediator DID | confirmed: `from` = `routing_did` = the invitation's `from` | M-1 |

### Other mediators

- **Blocktrust** (`bsandmann/blocktrust.Mediator`): no cell. Our client cannot enroll with it, by
  design: its first answer comes from a new DID with a `from_prior` that has no `iat` and no `from`,
  and its `keylist-update-response` entries have no `result`; the library refuses both, as the spec
  requires (owner decision on #99; reported upstream as bsandmann/blocktrust.Mediator#5). The hosted
  instance is never contacted. Re-check only if upstream fixes it.
- **Affinidi** (`affinidi/affinidi-messaging-mediator`, checked at v0.53.0, 2026-11-15): no cell.
  For DIDComm v2 it speaks trust-ping 2.0, routing 2.0, Message Pickup 3.0, Discover Features 2.0 and
  Report Problem 2.0, but **not Coordinate Mediation**: a v2 client enrols through Affinidi's own
  authentication protocol (`https://affinidi.com/atm/1.0/authenticate`), which registers the DID
  (Coordinate Mediation 1.0 exists only for DIDComm v1). Its image,
  `ghcr.io/affinidi/messaging-mediator:<version>`, pulls without credentials, but needs Redis (or a
  build with the embedded store) and an identity made by its `mediator-setup` tool. A leg would need
  that proprietary authentication step, so none is built (owner instruction on #98).

### Pins and provenance

`mediator/images.env` pins three images by tag **and** digest (the multi-arch index). The leg
parses that file, never sources it. It refuses a ref without a digest, pulls each ref (3 attempts),
and then requires `docker image inspect` to list the pinned digest in `RepoDigests`.

| image | pin | verified |
|---|---|---|
| Identus mediator | `hyperledgeridentus/identus-mediator:1.2.1@sha256:0f0378d9…412b` | `docker buildx imagetools inspect` (amd64 `b046dc37…`, arm64 `ef2f31fe…`); labels `version=1.2.1`, `revision=b85da0bf…` (the `v1.2.1` tag commit); bundles `app.fmgp.did*_3-0.1.0-M48` |
| MongoDB | `library/mongo:6.0.28-jammy@sha256:a90cef01…f5fd` | `imagetools inspect` (the floating `6.0` tag is a different index) |
| Squid | `ubuntu/squid:6.6-24.04_beta@sha256:6a097f68…e029` | re-verified 2026-11-19 with `imagetools inspect` (amd64 `8fafd41d…`, arm64 `eecf3533…`); the image runs Squid 6.13 |

`mediator/identus/initdb.js` is Identus's file from tag `v1.2.1`, vendored verbatim with
`gh api "repos/hyperledger-identus/mediator/contents/initdb.js?ref=v1.2.1"`. The leg refuses to
start unless `git hash-object` gives its blob, `5d9bd69def43d27dc835a55ec936fe0c2de91cc7`. It is
Apache-2.0 and named in `NOTICE`.

**Pin bump** (its own PR; never a floating tag):

1. `docker buildx imagetools inspect <repo>:<new tag>`: note the index digest and check that
   linux/amd64 and linux/arm64 manifests exist.
2. For Identus:
   - Check the labels with `docker image inspect` (`version`, and `revision` = the release tag's
     commit: `gh api repos/hyperledger-identus/mediator/git/refs/tags/v<X>`, then the tag object).
   - Check the bundled scala-did version (`docker run --rm --entrypoint ls <ref> /opt/docker/lib`),
     and re-check I-1..I-15 of the #99 plan against it.
   - Vendor that tag's `initdb.js` again, and update `INITDB_BLOB` in `run-mediator-leg.sh` and
     the blob in `NOTICE`.
3. Update the ref and its provenance comment in `images.env`.
4. Run locally: `PYTHON=python3.12 bash tools/interop-live/run-mediator-leg.sh`, then the same with
   `--negative-controls`.
5. Dispatch the workflow on the branch with negative controls on
   (`gh workflow run interop-live.yml --ref <branch> -f negative_controls=true`), and record the
   run id in the results table below.

### Running the leg locally

Prereqs: Docker (Docker Desktop on macOS works: every image is multi-arch, so there is no
emulation on Apple Silicon), the .NET 10 SDK, `curl`, `git`, and CPython 3.9–3.12. Set `PYTHON`
when the default `python3` is 3.13+.

```bash
PYTHON=python3.12 bash tools/interop-live/run-mediator-leg.sh                       # the cells + observations
PYTHON=python3.12 bash tools/interop-live/run-mediator-leg.sh --negative-controls   # every sabotage must FAIL
PYTHON=python3.12 bash tools/interop-live/run-mediator-leg.sh --up                  # set up, print endpoints, Enter tears down
```

Exit codes: 0 when all cells passed (or, with `--negative-controls`, every sabotage failed); 1 on a
FAIL or a set-up failure (the summary says `Leg aborted at set-up (<step>: <reason>)`); 2 when the
environment is unsuitable before anything ran. Under GitHub Actions an unset `PYTHON` is exit 2.

Environment:
- `INTEROP_SUMMARY_DIR`: where `mediator-leg.md`, `mediator-observations.json` and, on failure,
  `mediator-logs/` go.
- `INTEROP_CLI`: a prebuilt InteropCli.
- `MEDIATOR_LEG_OWNER`: an extra `org.didcomm-dotnet.owner` label.

For development only (each is refused when `CI=true`):
- `MEDIATOR_LEG_WORK`: the scratch directory; a venv already in it is reused;
- `MEDIATOR_LEG_IMAGES_ENV`: another images file (the unpullable-image test);
- `MEDIATOR_LEG_ONLY=M-3,R-1`: run those cells only;
- `MEDIATOR_LEG_DEBUG_FIRST_PORT`: the port-clash test;
- `MEDIATOR_LEG_DEBUG_IDENTUS_MONGO_PASSWORD`: the "never ready" test;
- `MEDIATOR_LEG_DEBUG_WATCHDOG`: the leg watchdog's seconds (default 1500, or 900 with
  `--negative-controls`), to test it.

A local run takes about 4 minutes on an Apple Silicon Mac: about 10 s of set-up once the images are
present, the cells in about 2 minutes (M-2 alone about 45 s), and the observations in about 2 minutes.

### Results

| run | where | cells | negative controls | notes |
|---|---|---|---|---|
| [44484556900](https://github.com/moisesja/didcomm-dotnet/actions/runs/44484556900) | CI, `b32e8e3` (PR #130): Ubuntu 26.04 (`ubuntu26 20261111.196.1`), x86_64, Docker 29.4.2, CPython 3.12.16, Identus image `linux/amd64` with scala-did 0.1.0-M48 | 9/9 | — | python 63/63 and jvm 60/60 (+3 n/a) tables byte-identical to `main`'s nightly 44298708738 |
| [44484568467](https://github.com/moisesja/didcomm-dotnet/actions/runs/44484568467) | CI, `b32e8e3`, `negative_controls` | 9/9 | 18/18 failed, each for its declared reason; 0 broken | |
| [44473219657](https://github.com/moisesja/didcomm-dotnet/actions/runs/44473219657), [44473234026](https://github.com/moisesja/didcomm-dotnet/actions/runs/44473234026) | CI, `7000bf4` (the first version of the leg, before its QA round) | 9/9 | 14/14 failed | |
| local | macOS arm64, Docker 29.9.2, CPython 3.12.7, Identus image `linux/arm64` | 9/9 (236 s) | 18/18 for the declared reason (211 s) | failure modes and hygiene checked (no Docker objects or processes left) |

### Releases (E-43)

A `release: published` run uses the workflow file **at the released tag**. A release cut from a
commit before this leg merged runs the python and jvm legs only, so 1.6.0 must be cut after this
merges for its release run to include the mediator leg.

## Pinned versions

| Stack | Pin |
|---|---|
| didcomm-python | `didcomm==0.3.2` + full transitive freeze in `python/requirements.txt`; CPython 3.12 on CI. The stack imports only on CPython 3.9–3.12: its pydantic 1.9.2 (pydantic v1) breaks `import didcomm` on 3.13 (`TypeError: multiple bases have instance lay-out conflict`) and 3.14 (`ConfigError: unable to infer type for attribute "accept"`), and Authlib 1.6.12 needs 3.9+. The leg checks the interpreter first and exits 2 outside that range. CI runs the newest interpreter in that range; it is a stopgap that lasts while the runner image has a 3.12 build (3.12 reaches end of life in 2028-10). The lasting fix is a counterpart stack that is off pydantic v1. |
| didcomm-jvm | `org.didcommx:didcomm:0.3.2`, `tink:1.6.1`, `protobuf-java:3.14.0`, `gson:2.8.6`, `varint:1.0.0`, `kotlin-stdlib:1.9.24` — each SHA-256-pinned in `jvm/fetch-deps.sh`; Temurin 17 on CI (validated on 25 locally) |
| didcomm-dotnet | whatever the checkout builds (`tools/InteropCli`, Release) |
| Identus mediator, MongoDB, Squid | by digest in `mediator/images.env` (see [Pins and provenance](#pins-and-provenance)) |

## Running locally

```bash
bash tools/interop-live/run-all.sh          # all three legs + combined summary
bash tools/interop-live/run-python-leg.sh   # one leg
bash tools/interop-live/run-jvm-leg.sh
bash tools/interop-live/run-mediator-leg.sh # needs Docker; see "Running the leg locally" above
```

Prereqs: .NET 10 SDK, CPython 3.9–3.12 as `python3` or named by `PYTHON` (e.g.
`PYTHON=python3.12 bash tools/interop-live/run-python-leg.sh` when the default `python3` is
newer), a JDK (17+), `curl`. Each leg
prints its table, keeps its scratch dir for debugging (path on stderr), and honors
`INTEROP_SUMMARY_DIR` to also emit the markdown summaries CI uploads. Each summary ends with
an executed / passed / failed / declared-n-a tally, so a reader can tell coverage from silence.

## CI cadence

`interop-live.yml` runs on `schedule` (nightly 03:17 UTC), `workflow_dispatch`, and
`release: published`; it uploads `summary.md` + per-leg tables as the `live-interop-summary`
artifact and fails on any FAIL cell. The one job (`live round-trips (python + jvm + mediator)`,
`timeout-minutes: 60`) logs `docker version` and `docker info` before the harness runs. A manual
dispatch with the boolean input `negative_controls` also runs `run-mediator-leg.sh
--negative-controls` (its summary lands in `negative-controls/` inside the artifact). The python-leg results above were executed locally on
macOS arm64 / CPython 3.12.7. The nightly proves the same matrix on `ubuntu-latest` (Ubuntu
26.04 since 2026-11-08) with CPython 3.12 and Temurin 17. The workflow hands the leg the exact
interpreter `setup-python` installed (`PYTHON`), and the leg logs the CPython version it ran on.
First green run there: 43882021940 (2026-11-16, CPython 3.12.16, Temurin 17.0.21): python leg
63 executed · 63 passed · 0 n/a, jvm leg 60 executed · 60 passed · 3 n/a, the same cells as the
last 3.9 run (2026-11-07) plus the #95 mediator cell in each leg.

The nightly stopped on 2026-11-08, when `ubuntu-latest` moved to Ubuntu 26.04: that image has
no CPython 3.9 build, so `setup-python` failed before any test ran (#120). A move of the
floating runner image can drop an interpreter again, and the nightly then fails in its setup
steps. The fix is an interpreter the image still builds inside the 3.9–3.12 window or, once
none is left, a counterpart stack off pydantic v1; never a skipped leg.
