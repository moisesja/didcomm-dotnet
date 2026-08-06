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
| `tools/InteropCli` | The didcomm-dotnet side: `mint` / `pack` / `unpack` / `unwrap-forward` over the **real** `DidCommClient` + `UseNetDidResolver()` wiring (net-did composite resolver, did:key + did:peer). |
| `python/interop_peer.py` | The didcomm-python side: same CLI shape, driving `didcomm==0.3.2` (sicpa-dlab/didcomm-python) directly. Pins in `python/requirements.txt`. |
| `jvm/src/InteropPeer.java` | The didcomm-jvm side: same CLI shape, driving `org.didcommx:didcomm:0.3.2` — plain `javac`/`java`, jars pinned by version + SHA-256 in `jvm/fetch-deps.sh` (no Gradle/Maven). |
| `run-python-leg.sh` / `run-jvm-leg.sh` | One leg each: mint the identity set, run the matrix in both directions, verify the published vectors, print/emit a per-cell PASS/FAIL/N-A table, exit nonzero on any FAIL. |
| `run-all.sh` | Both legs + a combined `summary.md` (the CI artifact). |

Every cell round-trips a fresh C.1-style payload and asserts (a) the recovered plaintext
equals the original (`id`, `type`, `body`, `from`, `to`, `created_time`) and (b) the unpack
metadata is exactly what the composition requires (flags, `enc`, `kw`, `sig_alg` — see
`expected_metadata` in `python/interop_peer.py`, mirrored in `InteropPeer.java`).

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
| Recipients | single · 3 (mixed `did:peer:2` + `did:key`, mixed key types) |
| Routing | direct · 1 mediator · 2 mediators (real `forward` onion, both directions) |
| DID method | `did:peer:2` · `did:key` |
| Direction | inbound · outbound · published-vector verification (FR-IX-06) |

## Current results — python leg (executed 2026-08-05, macOS arm64, CPython 3.9.6)

`didcomm-dotnet @ DataProofsDotnet.Jose 1.3.0 + NetCrypto 1.5.0` ↔ `didcomm-python 0.3.2`.
**Executed 56 · passed 56 · failed 0 · declared n/a 0.** Exit code 0.

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

## Current results — jvm leg (executed 2026-08-05, macOS arm64, JDK 25.0.2)

`didcomm-dotnet @ DataProofsDotnet.Jose 1.3.0` ↔ `didcomm-jvm 0.3.2`.
**Executed 53 · passed 53 · failed 0 · declared n/a 3.**

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
explicit message rather than silently addressing only the first. Forward onions are built and
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

## Pinned versions

| Stack | Pin |
|---|---|
| didcomm-python | `didcomm==0.3.2` + full transitive freeze in `python/requirements.txt`; CPython 3.9 |
| didcomm-jvm | `org.didcommx:didcomm:0.3.2`, `tink:1.6.1`, `protobuf-java:3.14.0`, `gson:2.8.6`, `varint:1.0.0`, `kotlin-stdlib:1.9.24` — each SHA-256-pinned in `jvm/fetch-deps.sh`; Temurin 17 on CI (validated on 25 locally) |
| didcomm-dotnet | whatever the checkout builds (`tools/InteropCli`, Release) |

## Running locally

```bash
bash tools/interop-live/run-all.sh          # both legs + combined summary
bash tools/interop-live/run-python-leg.sh   # one leg
bash tools/interop-live/run-jvm-leg.sh
```

Prereqs: .NET 10 SDK, `python3` (3.9+ for the pinned stack), a JDK (17+), `curl`. Each leg
prints its table, keeps its scratch dir for debugging (path on stderr), and honors
`INTEROP_SUMMARY_DIR` to also emit the markdown summaries CI uploads. Each summary ends with
an executed / passed / failed / declared-n-a tally, so a reader can tell coverage from silence.

## CI cadence

`interop-live.yml` runs on `schedule` (nightly 03:17 UTC), `workflow_dispatch`, and
`release: published`; it uploads `summary.md` + per-leg tables as the `live-interop-summary`
artifact and fails on any FAIL cell. The python-leg results above were executed locally on
macOS arm64 / CPython 3.9.6; the nightly additionally proves the same matrix on
ubuntu-latest with CPython 3.9 and Temurin 17 — the pinned stacks are platform-independent
pure wheels/jars, so no divergence is expected, but that run is the remaining unproven claim.
