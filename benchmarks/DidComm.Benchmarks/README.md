# DidComm.Benchmarks — the NFR-07 suite

BenchmarkDotNet suite for the PRD §11 NFR-07 hot-path targets. Everything is in-process:
identities are `did:peer:2` / `did:key`, so "resolution" is DID parsing — no network anywhere,
matching NFR-07's "excluding network/resolution" framing while measuring exactly the path a
real application executes.

## Run it

```bash
dotnet run -c Release --project benchmarks/DidComm.Benchmarks -- --filter '*DidCommBenchmarks*'
```

(Add `--job short` for a faster, less rigorous pass.)

## Recorded results

`ShortRun` (3 warmup + 3 iterations), Apple M3 Max, .NET 10.0.0 Arm64 RyuJIT, macOS,
2026-08-03, single recipient, X25519 / A256CBC-HS512:

| Benchmark | Mean | Allocated | NFR-07 target (P99) | Verdict |
|---|---:|---:|---|---|
| `AnoncryptPack_1Recipient` | 102.9 µs | 33.9 KB | < 2 ms | met, ~19× headroom |
| `AuthcryptPack_1Recipient` | 168.6 µs | 48.6 KB | < 3 ms | met, ~17× headroom |
| `Unpack_Authcrypt` | 211.4 µs | 60.8 KB | < 2 ms | met, ~9× headroom |
| `Resolve_DidKey_KeyAgreement` | 97.9 µs | 4.4 KB | < 0.1 ms | **marginal** — mean is 2 µs under the line, so P99 will straddle it |

Notes on the marginal cell: `NetDidKeyService` deliberately holds no cache (FR-DID-04 —
"no double-caching"; callers register net-did's `CachingDidResolver` when they want one), so
this measures a full multibase decode + document projection every call. The recommended
production configuration (caching resolver) turns repeat resolutions into a dictionary hit,
far below the target; the uncached cold path sitting essentially *at* the 100 µs line is
recorded here as-is rather than gamed with a cache the benchmark's own config warns about.

The NFR-07 targets are stated against "reference HW" with P99 semantics; BenchmarkDotNet
reports means over steady-state iterations. Re-record this table when the crypto substrate
(DataProofsDotnet.Jose / NetCrypto) or the resolver stack changes materially.
