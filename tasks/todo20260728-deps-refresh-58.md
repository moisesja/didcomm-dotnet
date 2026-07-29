# Dependency refresh (NetDid 3.0.0 / NetCrypto 1.4.0 / DataProofs.Jose 1.1.1) + close #58

## Context

**Where the branch is.** `feat/same-doc-key-provenance-56` is 8 commits ahead of `main`, open as
**PR #57** (v1.4.0, closes #56 — same-document key provenance), **green on both CI runners** and
mergeable. v1.4.0 is *not* released: no `v1.4.0` tag, and nuget.org's newest `DidComm.Core` is
1.3.0. So the `## [1.4.0]` CHANGELOG section is still the in-flight release and can be appended to.

**What changed upstream.** All three first-party dependency lines shipped new versions since this
branch's pins were set. The one that matters most: **DataProofsDotnet.Jose 1.1.1 contains the fix
for dataproofs-dotnet#15**, which is the upstream root cause of this repo's open **issue #58**
(`UnpackAsync` leaking a raw `InvalidOperationException` from a non-string unprotected JWS
`header.kid` — an FR-API-07 violation reachable pre-authentication by any peer that can deliver
bytes). That release also wraps `JwsParser`'s top-level `JsonDocument.Parse`, closing the same
fault class for malformed JSON.

**Outcome.** Pins move to current, the dependency graph converges on one NetCrypto and one
DataProofsDotnet.Core version (no `NU1605`), and #58 closes with a guard that holds independently
of the pin.

## Decisions (confirmed with the user)

1. **Land on the #57 branch** — one PR, `feat/same-doc-key-provenance-56`.
2. **Scope: first-party + framework patches.** Test-stack majors are explicitly out (see below).
3. **Fold in #58** — bump *and* add the JWS boundary guard. The guard stays regardless of the pin:
   it defends the whole untyped-fault class (FR-API-07), not the one input 1.1.1 fixed.

## Version matrix — [Directory.Packages.props](Directory.Packages.props)

| Package | From | To | Why it is safe |
|---|---|---|---|
| `NetDid.Core`, `.Method.Key`, `.Method.Peer`, `.Method.WebVh`, `.Extensions.DependencyInjection` | 2.3.0 | **3.0.0** | net-did's own CHANGELOG: *"Upgrading from 2.3.0 requires no code changes."* The major signals a new `did:ethr` package (not referenced here) and the DataProofs major crossing beneath `did:webvh`. Every 2.x-facing public-API change is additive. |
| `NetCrypto` | 1.2.0 | **1.4.0** | 1.3.0 adds `KeyTypeExtensions.ToUncompressed`; 1.4.0 adds `IRecoverableDigestSigner` + `RecoverableSignature`. Purely additive; the `IKeyStore` member ships as a throwing default interface implementation, so external stores stay source- and binary-compatible. 1.4.0 is also NetDid.Core 3.0.0's own pin → direct and transitive agree. |
| `DataProofsDotnet.Jose` | 1.1.0 | **1.1.1** | The dataproofs#15 fix (root cause of #58) + the `JwsParser` JSON-parse wrap. Brings `DataProofsDotnet.Core` 1.1.1, which `NetDid.Method.WebVh` 3.0.0 also requires → graph converges. |
| `Microsoft.Extensions.Caching.Memory`, `.Logging.Abstractions`, `.DependencyInjection`, `.DependencyInjection.Abstractions`, `.Http` | 10.0.8 | **10.0.10** | .NET 10 servicing patches, at or above NetDid.Core 3.0.0's floor. |
| `OpenTelemetry.Api` | 1.15.3 | **1.17.0** | Current line; the existing comment's GHSA rationale is superseded and needs rewording. |
| `Polly` | 8.5.0 | **8.7.0** | Minor within v8; `ResiliencePipeline` surface unchanged. |
| `Microsoft.AspNetCore.TestHost` | 10.0.0-**preview**.1.25120.3 | **10.0.10** | Real staleness fix: a .NET 10 *preview* pin on a GA runtime. Test/sample-only. |

**Deliberately held back** (call out in the PR description so it reads as a decision, not an
oversight): `FluentAssertions` 7.0.0 — v8 moved to an Xceed license (free for OSS, paid for
commercial) *and* carries breaking API changes across the whole suite; that is a separate project.
Also held: `NSubstitute` 5, `xunit.runner.visualstudio` 2, `Microsoft.NET.Test.Sdk` 17,
`coverlet.collector` 6, `Microsoft.SourceLink.GitHub` 8, `xunit` 2.9.3, `BenchmarkDotNet` 0.14.0.

`Directory.Packages.props` carries a load-bearing rationale comment above each first-party pin.
**Rewrite those comments, don't just bump the numbers** — the NetDid comment's "2.3.0 is additive
over 2.0.1…net-wallet-sdk 0.2.0 graph" paragraph and the OpenTelemetry GHSA note are both now stale.

**Nothing is currently vulnerable** — `dotnet list package --vulnerable --include-transitive` is
clean, and DataProofs' AngleSharp advisory arrives via `DataProofsDotnet.Rdfc`, which this repo
does not reference.

## Issue #58 — the JWS boundary guard

[src/DidComm.Core/Composition/EnvelopeReader.cs](src/DidComm.Core/Composition/EnvelopeReader.cs),
`case EnvelopeKind.Signed` (~L300–321). The branch catches `MalformedJoseException`,
`JoseCryptoException`, and `ArgumentException`, then stops. Its sibling JWE branch (~L386) already
has a catch-all. Add the mirror after the existing `ArgumentException` handler:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException and not DidCommException)
{
    // FR-API-07 (#58): the delegated JwsParser enumerates attacker-supplied structure *before*
    // any signature check, so an untyped fault is remotely reachable pre-authentication (the
    // non-string unprotected 'kid' of dataproofs#15, fixed in Jose 1.1.1 — but the class is
    // wider than that one input, and the consumer's signer lookup can fault in ways this layer
    // cannot enumerate). Fold every non-cancellation, non-DidComm fault into the documented
    // contract type; InnerException is preserved.
    throw new MalformedMessageException("Malformed JWS.", ex);
}
```

Two deliberate choices:

- **Exclude `DidCommException`, not a hand-listed set.** The whole typed hierarchy
  (`DidResolutionException`, `SecretNotFoundException`, `ConsistencyException`, …) passes through
  untouched — the signer lookup on the built-in `NetDidKeyService` *is* the DID-resolution path,
  so swallowing `DidResolutionException` into "malformed JWS" would be a behavior regression.
- **Map to `MalformedMessageException`, matching the `ArgumentException` handler directly above.**
  Do **not** copy the JWE branch's narrower `is not MalformedMessageException and not
  CryptoException and not ConsistencyException` filter — that list is deliberate *there* (uniform
  failure shape closes the recipient-possession oracle) and there is no possession oracle on the
  signed path. Leave the JWE branch untouched.

## Files to change

- [Directory.Packages.props](Directory.Packages.props) — versions + rewritten rationale comments.
- [src/DidComm.Core/Composition/EnvelopeReader.cs](src/DidComm.Core/Composition/EnvelopeReader.cs) — the guard above.
- [tests/DidComm.Core.Tests/Envelopes/Composition/EnvelopeReaderTests.cs](tests/DidComm.Core.Tests/Envelopes/Composition/EnvelopeReaderTests.cs) — **two** tests:
  1. *Contract*: issue #58's exact repro — flattened JWS with `"header": {"kid": 123}` → a
     `DidCommException` subtype escapes `UnpackAsync`. Pins the contract, not the mechanism
     (post-bump it is satisfied by the existing `MalformedJoseException` handler).
  2. *Guard*: a consumer `signerLookup` that throws a raw `InvalidOperationException` →
     `MalformedMessageException`. This one exercises the new catch-all itself and stays meaningful
     no matter what the pin does.
  Add a third asserting a `DidResolutionException` from the signer lookup still propagates
  untouched, so the guard cannot silently widen later.
- [tests/DidComm.InteropTests/Transports/WebSocketTransportRoundTripTests.cs:212-218](tests/DidComm.InteropTests/Transports/WebSocketTransportRoundTripTests.cs#L212-L218) —
  the comment says the non-string kid *"currently surfaces a raw InvalidOperationException … (tracked separately)"*. Stale after this change; the test itself still passes.
- [docs/didcomm-dotnet_PRD.md](docs/didcomm-dotnet_PRD.md) — §3.1 dependency table (L132) and the
  §3.2 diagram (L164) hard-code `DataProofsDotnet.Jose 1.1.0`, `NetCrypto 1.2.0`, `NetDid.Core 2.3.x`.
- [CHANGELOG.md](CHANGELOG.md) — new entries under the existing `## [1.4.0]`: a *Changed*
  subsection for the dependency refresh and a *Fixed* subsection for #58.

## Verification

1. `dotnet restore` — must be clean. `TreatWarningsAsErrors` is on repo-wide, so an `NU1605`
   downgrade (the real risk when crossing a major) or an `NU1902`/`NU1903` audit hit **fails the
   build**; this is the primary graph check.
2. `dotnet build -c Release` — Release also runs package validation / ApiCompat against
   `PackageValidationBaselineVersion` 1.3.0, proving the bump introduces no public-API break.
3. `dotnet test` — full suite (Core.Tests + InteropTests), zero regressions.
4. `dotnet list package --vulnerable --include-transitive` — still clean.
5. `dotnet list package --outdated` — confirm what remains is exactly the deliberately-held set
   above, nothing accidental.
6. **Adversarial re-check** (CLAUDE.md §2): replay issue #58's repro against the built client and
   confirm a `DidCommException`; then re-run it with the guard commented out to prove the new test
   actually fails without the fix — the guard must be shown to carry weight, not just coexist with
   a fixed dependency.
7. Push; confirm PR #57 goes green again on both runners.

## Risk

Low, and mechanically checkable. The one genuine unknown is whether NetDid 3.0.0's "no code
changes" claim holds for this repo's specific usage — steps 1–3 settle that definitively, and the
adapter surface is small: `NetDid.Core` reaches only
[src/DidComm.Adapters.NetDid/](src/DidComm.Adapters.NetDid/) and
[src/DidComm.Core/Resolution/NetDidKeyService.cs](src/DidComm.Core/Resolution/NetDidKeyService.cs),
and `NetCrypto` is referenced directly in exactly one file,
[src/DidComm.Core/Jose/Signing/JwsSignerFactory.cs](src/DidComm.Core/Jose/Signing/JwsSignerFactory.cs).
If NetDid 3.0.0 turns out to need source changes, stop and re-plan rather than patching through.

Per CLAUDE.md, this plan is also written to `tasks/todo{timestamp}.md` at the start of execution,
with a review section appended when it completes.

---

## Review (completed 2026-07-28)

### Outcome

All planned work landed on `feat/same-doc-key-provenance-56` (PR #57). **No source changes were
required by the dependency bump** — NetDid 3.0.0's "upgrading from 2.3.0 requires no code changes"
claim holds for this repo's resolution-only usage, proven by a 0-warning Release build under
`TreatWarningsAsErrors` plus the full suite.

### Verification evidence

| Check | Result |
|---|---|
| `dotnet restore` | Clean — no `NU1605` downgrade, no `NU1902`/`NU1903` audit hit |
| `dotnet build -c Release` | Build succeeded, **0 warnings, 0 errors** |
| `dotnet test -c Release` | **823 passed, 0 failed** (662 Core.Tests + 161 InteropTests) |
| `dotnet pack -c Release` | All 6 packages created — ApiCompat/package validation green against the 1.3.0 baseline |
| `dotnet list package --vulnerable --include-transitive` | Clean |
| `dotnet list package --outdated` | Only the 6 deliberately-held test-stack/SourceLink entries remain |

### Adversarial re-check (CLAUDE.md §2/§4) — the guard was proven load-bearing in both directions

1. **Guard disabled, dependency fixed** (`when (false && …)`, Jose 1.1.1):
   `Untyped_fault_from_the_delegated_JWS_parse_maps_to_MalformedMessageException` **fails** —
   `Expected a <MalformedMessageException> to be thrown, but found <System.InvalidOperationException>`.
   So the new test is not vacuous and the guard is not decorative.
2. **Guard enabled, dependency reverted** (Jose pinned back to **1.1.0**, the version carrying
   dataproofs#15): the reported #58 repro `Non_string_unprotected_kid_stays_inside_the_typed_exception_hierarchy`
   **passes**. So the guard alone closes #58 — the pin is a contract cleanup upstream, not the fix.

Both experiments were reverted; the committed state is guard-enabled on Jose 1.1.1.

### Deviations from the plan

None. The one judgment call made during execution: the plan named two tests plus a third; all three
were written as specified (contract / guard / typed-pass-through).

### Follow-ups not in scope

- **#59** (FR-CONSIST-04: `IsRecipientInTo` has no production caller) remains open and untouched.
- The test-stack majors (FluentAssertions 8 in particular, with its Xceed relicensing) are a
  separate project, documented as a deliberate hold in `Directory.Packages.props` and the CHANGELOG.
- `CLAUDE.md` references `docs/codebase-architecture.md`, which does not exist in the repo; only
  the PRD carries the dependency map, and it was updated (§3.1 table, §3.2 diagram).
