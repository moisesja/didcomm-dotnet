# Live cross-implementation interop harness

The live half of the PRD §13.6 interop gate: prove, in an automated job, that didcomm-dotnet
and the SICPA reference implementations can exchange packed messages over `did:peer:2` —
**outbound** (didcomm-dotnet packs, they unpack; FR-IX-04, MUST) and **inbound** (they pack,
didcomm-dotnet unpacks; FR-IX-05, SHOULD). The offline (static fixture) half gates every PR in
`ci.yml`; this harness runs nightly, on release, and on demand
(`.github/workflows/interop-live.yml`, FR-IX-08).

## What runs

| Piece | Role |
|---|---|
| `tools/InteropCli` | The didcomm-dotnet side: `mint` / `pack` / `unpack` over the **real** `DidCommClient` + `UseNetDidResolver()` wiring (net-did composite resolver, did:key + did:peer). |
| `python/interop_peer.py` | The didcomm-python side: same CLI shape, driving `didcomm==0.3.2` (sicpa-dlab/didcomm-python) directly. Pins in `python/requirements.txt`. |
| `jvm/src/InteropPeer.java` | The didcomm-jvm side: same CLI shape, driving `org.didcommx:didcomm:0.3.2` — plain `javac`/`java`, jars pinned by version + SHA-256 in `jvm/fetch-deps.sh` (no Gradle/Maven). |
| `run-python-leg.sh` / `run-jvm-leg.sh` | One leg each: mint both identities, run the matrix below in both directions, print/emit a per-cell PASS/FAIL/N-A table, exit nonzero on any FAIL. |
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

`did:peer:2` handling in both drivers is implemented against the current did:peer spec
(decode `.Ez`/`.Vz` multibase(multicodec) segments; name keys `#key-N` in order of
appearance — the numbering net-did emits) instead of the 2022-era `peerdid` packages, so kids
agree byte-for-byte across all three implementations. Service (`.S`) segments are ignored:
the harness runs direct, without mediators (`forward=false` everywhere).

## Matrix and current results (validated locally 2026-08-03, macOS arm64)

Both legs, both directions. Signing is EdDSA (the only curve pair a two-key did:peer:2
carries); key agreement is X25519; authcrypt content encryption is A256CBC-HS512 (the only
authcrypt `enc` either counterpart supports — same FR-ENC-09 profile didcomm-dotnet defaults
to).

| composition | enc | dotnet→python | python→dotnet | dotnet→jvm | jvm→dotnet |
|---|---|---|---|---|---|
| plaintext | — | PASS | PASS | PASS | PASS |
| signed (EdDSA) | — | PASS¹ | PASS | **N-A²** | PASS |
| anoncrypt | A256CBC-HS512 | PASS | PASS | PASS | PASS |
| anoncrypt | A256GCM | PASS | PASS | PASS | PASS |
| anoncrypt | XC20P | PASS | PASS | PASS | PASS |
| authcrypt | A256CBC-HS512 | PASS | PASS | PASS | PASS |
| anoncrypt(sign EdDSA) | A256CBC-HS512 | **N-A¹** | PASS | **N-A²** | PASS |
| anoncrypt(sign EdDSA) | XC20P | **N-A¹** | PASS | **N-A²** | PASS |
| anoncrypt(authcrypt) | A256CBC-HS512 | PASS | PASS | PASS | PASS |

¹ ² — see the two findings below. All other 30 cells are genuine, unshimmed cross-library
round-trips.

### Known counterpart deviations (¹ — didcomm-python/jvm are the nonconformant side)

The DIDComm v2.1 spec (§Message Signing) says: *"Either the General or Flattened form of a
JWS is valid. **Message recipients MUST be able to process both forms.** Message senders using
signed messages MAY use either form. Flattened form is sufficient."* didcomm-dotnet emits
Flattened (PRD FR-SIG-02, deliberately); **both** SICPA counterparts only parse General —
didcomm-python's `validate_jws` requires a `signatures` array, and didcomm-jvm's envelope
detector falls through to plaintext-JWM parsing.

- For **standalone signed** envelopes the python driver applies a lossless RFC 7515
  re-serialization (Flattened → General; payload, protected header, and signature bytes are
  byte-identical) before handing the message to didcomm-python — all verification is still
  didcomm-python's. The cell is reported PASS with that shim on record (¹).
- For **anoncrypt(sign)** the inner JWS sits inside the ciphertext where no wire-level
  normalization can reach it, so the outbound cell is **N-A** against didcomm-python (¹).

### Known didcomm-dotnet defect (² — ours; fix belongs upstream in dataproofs-dotnet)

didcomm-dotnet's signed envelopes carry `kid` in **both** the protected header and the
per-signature unprotected header. RFC 7515 §7.2 requires those header-parameter sets to be
disjoint; nimbus-jose-jwt (inside didcomm-jvm) enforces this and rejects the envelope
(`"The parameters in the JWS protected header and the unprotected header must be disjoint"`).
didcomm-jvm *additionally* requires the kid in the unprotected per-signature header, so no
lossless post-sign transform exists (moving kid out of the protected header would invalidate
the signature). Net effect: **didcomm-jvm cannot verify any didcomm-dotnet signed envelope
today** — outbound `signed` and `anoncrypt(sign)` are N-A against the JVM (²).

The duplication originates in `DataProofsDotnet.Jose` 1.1.1's `JwsBuilder` (it stamps `kid`
into the protected header it signs *and* renders the unprotected `{"kid": …}` carrier), i.e.
in the dependency repo, not in didcomm-dotnet's own code. The conformant shape — used by the
spec's own C.2 vectors and accepted by python, jvm, and nimbus alike — is `kid` **only** in
the unprotected per-signature header (protected: `alg` + `typ`). Once dataproofs-dotnet ships
that, the four N-A cells above are expected to flip to PASS with no harness changes (the
python shim simply stops firing for the inner-JWS case too).

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
`INTEROP_SUMMARY_DIR` to also emit the markdown summaries CI uploads.

## CI cadence and what is deferred to the nightly

`interop-live.yml` runs on `schedule` (nightly 03:17 UTC), `workflow_dispatch`, and
`release: published`; it uploads `summary.md` + per-leg tables as the `live-interop-summary`
artifact and fails on any FAIL cell. Everything in the results table above was validated
locally (macOS arm64, CPython 3.9.6, Java 25); the first nightly additionally proves the same
matrix on ubuntu-latest with CPython 3.9 and Temurin 17 — the pinned stacks are
platform-independent pure wheels/jars, so no divergence is expected, but that run is the
remaining unproven claim.
