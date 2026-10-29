# Cap wire-supplied identifier length before DID parsing (#80)

## Context

Issue #80: wire-supplied key ids reach NetDid's `DidParser.ParseDidUrl`, a backtracking regex, with
no bound except `DidCommOptions.MaxReceiveBytes` (1 MiB). One maximum-size kid costs about 50 ms of
CPU and up to about 100 MB of transient allocation, before any authentication. Only `from` and `to`
are capped today (2048, a private constant in `Message.cs`, not stated in the PRD).

**What the code shows beyond the issue** (my sweep plus an independent architect sweep):

1. **The JWS signer kid and the authcrypt sender kid never pass through didcomm as values.**
   DataProofs extracts them and hands them to our lookup callbacks (`JwsParser.cs:194`,
   `JweParser.cs:725`). The cap has to wrap those callbacks.
2. **The sender kid can come from `apu`.** When `skid` is absent, DataProofs decodes it from
   `apu` (`JweParser.cs:709-718`). A check on `PeekRecipients().Skid` would miss it.
3. **Non-held JWE recipient kids never reach NetDid.** They reach the consumer's
   `ISecretsResolver` (`FindPresentAsync`) and the public `UnpackResult.AllRecipientKids`. Capping
   them still matters, for those two reasons, not for NetDid.
4. **`from_prior` has three uncapped DID-valued fields.** The header `kid` and claim `iss` are
   DID-parsed before the signature check (`FromPriorValidator.cs:176,196`), and `sub` is echoed in
   error messages. The termination form rides on plain anoncrypt (no `from`), so this path is
   reachable without sender authentication.
5. **Forward `next`.** In pass-through mode `ForwardProcessingResult.NextHop` hands it to the host
   unbounded, and the host resolves it. Rewrap mode is already bounded by accident:
   `ForwardMessage.Create` → `Validate` caps it at 2048.
6. **NetDid has a second parse entry point.** `NetDidKeyService.RejectUnsupportedMethod` calls
   `DidParser.ExtractMethod`, which runs `IsValid` (a regex). It also echoes the value in its
   `DidResolutionException`.
7. **`Message.Validate` echoes over-long `from`/`to`.** The fragment check, which includes the
   value in its message, runs before the length check (`Message.cs:179-182`, `:192-195`).

**Measured legitimate lengths.** Interop fixtures: longest kid 45 characters. Live harness
(`InteropCli mint`, same flags as `run-python-leg.sh`): base did:peer:2 kid 116; three P-256
keyAgreement keys plus an endpoint, 365; routed through two mediators (the `.S` segment carries
both mediators' kids), about 565. All are well under either cap below.

## Design

One shared definition of the limits. Typed rejections at each wire boundary give the error.
Backstops in the two places didcomm calls NetDid's parser bound everything else.

- **`IdentifierLimits`** (new, internal, `src/DidComm.Core/Consistency/IdentifierLimits.cs`):
  - `MaxDidLength = 2048`: `from`, `to[]`, `from_prior` `iss` and `sub`. Same value as today.
  - `MaxKeyIdLength = 4096`: `kid`, `skid` (including the `apu`-derived one), `from_prior` `kid`,
    forward `next`. A kid is a DID plus `#fragment`, and FR-CONSIST-01/03 require the skid and
    signer kid to share `from`'s DID subject. With a single 2048 cap, a legal `from` near 2048
    could never carry its own kid (L-041). Parse cost is linear, so 4 KiB is negligible.
- **Boundary rejections.** None of the messages includes the value; each names the field and
  the limit.
  - `EnvelopeReader.UnpackAsync`:
    - (a) Wraps `signerLookup`, so any JWS kid DataProofs asks for (protected or unprotected
      header, any layer) throws `MalformedMessageException` before the inner lookup runs.
    - (b) Wraps `senderLookup` the same way. This covers both `skid` and the `apu`-derived kid.
    - (c) Right after `PeekRecipients`, checks every recipient kid against `MaxKeyIdLength`,
      before `FindPresentAsync`.
    - Propagation is verified. The lookups sit outside DataProofs' catch blocks, and
      EnvelopeReader's filters let `MalformedMessageException` through.
    - **No possession oracle.** (c) depends only on envelope content and runs before key
      selection. (b) runs inside `ResolveSender`, which the held path and the decoy path both
      reach after the same work. Early exit is fine here: these checks compare wire input against
      a constant, not against our own identity list.
  - `FromPriorValidator`: `kid`, `iss` and `sub` are checked before any comparison or parse, and
    rejected as `ProtocolException` (see D1). Capping `sub` refuses nothing new: it must equal
    `from`, which is already capped.
  - `ForwardMessage.TryParse`: an over-long `next` is rejected as `MalformedMessageException`,
    the type it already throws for a missing `next`.
  - `Message.Validate`: uses `MaxDidLength`, and runs the length checks before the checks that
    echo the value.
- **Backstops at `MaxKeyIdLength`.** These use the larger cap, so nothing the boundary accepts
  is refused further down.
  - `DidSubject.DidSubjectOf` returns `null` ("not a DID") above the cap. This covers
    DID-document-supplied values such as `controller`; a did:webvh document comes off the network.
  - `NetDidKeyService.RejectUnsupportedMethod` throws `DidResolutionException` without the value.
  - The `OwnIdentifiers` construction error names the length when that is the cause.
- **The claim we can defend** (L-042): every NetDid parse that didcomm itself calls sees at most
  4096 characters, and every key id didcomm *looks up* is capped. It does **not** cover NetDid's
  internal parsing of resolved documents. It also does not cover a kid that is never looked up: in
  a multi-signature JWS whose first signature verifies, later kids are never requested.

## Decisions

D1 and D2 change what the issue asked for. They need your answer. D3 to D5 go ahead as
recommended unless you say otherwise.

**Approved 2026-10-28:** the plan as written, D1 and D2 as recommended. D4's net-did issue text
still needs a separate OK before posting.

- **D1. `from_prior` exception type: `ProtocolException` (recommended).** The issue says
  `MalformedMessageException`. But the validator's documented contract for a malformed JWT is
  `ProtocolException` (`FromPriorValidator.cs:34`), every sibling structural rejection throws it,
  and existing tests pin it (`FromPriorTerminationTests.cs:95`). `FromPriorValidator` is public API
  that applications call directly. The PR body will state the amended "Done when".
- **D2. Two caps: 2048 for DIDs, 4096 for key ids (recommended).** The alternative is the issue's
  single 2048 cap, which can refuse honest traffic at the edge described above.
- **D3. CPU per envelope is not bounded by a per-value cap.** Disclose it and measure it; don't
  cap entry counts here. About 500 `to` entries of 2048 characters fit in 1 MiB, and each is parsed
  about three times before authentication, so total parse CPU still grows with envelope size. Per
  value, memory becomes bounded (about 0.4 MB instead of about 100 MB). I'll measure the residual
  and state it in the CHANGELOG. I'll propose a follow-up issue only if it exceeds the issue's
  per-kid figure.
- **D4. Upstream net-did issue** (`RegexOptions.NonBacktracking` or a length guard in
  `ParseDidUrl`/`IsValid`). This is the only way to bound NetDid's own document parsing. I'll
  draft the text in the PR thread, and post it only if you approve.
- **D5. Release:** lands under `[Unreleased]` and ships with 1.5.0 (not yet tagged). No version
  bump.

## Steps

- [x] 0. Branch `feat/keyid-length-cap-80` off `main`.
- [x] 1. `IdentifierLimits`. `Message.cs` switches to it and reorders its length checks before
      the checks that echo the value.
- [x] 2. Backstops: `DidSubjectOf`, `RejectUnsupportedMethod`, `OwnIdentifiers` message.
- [x] 3. `EnvelopeReader`: lookup wrappers (signer, sender) and the recipient-kid check after peek.
- [x] 4. `FromPriorValidator`: `kid`/`iss`/`sub` caps and `<exception>` doc.
- [x] 5. `ForwardMessage.TryParse`: `next` cap and `<exception>` doc.
- [x] 6. Tests. Each position gets a hostile case at cap + 1 and at 1 MiB, plus a control at
      exactly the cap that **fully unpacks**. Every hostile case asserts that the message does
      not contain the value, and that the over-long value never reached `IDidResolver.ResolveAsync`
      or `ISecretsResolver` (spy stubs). The entry point is `DidCommClient.UnpackAsync` with
      `NetDidKeyService` over a static resolver (`RecipientLabelBindingTests` pattern). Kids are
      shaped as a short DID plus a long fragment.
      - JWS unprotected kid (`PackSignedAsync`) and protected kid (`JwsKidPlacement.Protected`).
      - JWE recipient kid: a non-held entry in a Bob plus Carol envelope, packed rather than
        edited so apv stays valid. Holder and stranger must get an identical failure
        (`SurrogateEscapeBoundaryTests` pattern).
      - Authcrypt `skid` with a matching `apu`. Then `apu` only, with `skid` removed; that case
        pins wrapper (b).
      - Inner layers: anoncrypt(sign) and anoncrypt(authcrypt).
      - `from_prior` `kid`/`iss`/`sub`. The termination form over anoncrypt covers the path with
        no sender authentication. Also call `ValidateAsync` directly with both the binding key
        service and the legacy one.
      - Forward `next` in pass-through and rewrap modes.
      - `DidSubjectOf` and `RejectUnsupportedMethod` at 4096 and 4097. A `from` of exactly 2048
        with its own kid is accepted, which pins D2's margin.
- [x] 7. Docs.
      - PRD: a new row **FR-API-09 (MUST)** for wire identifier length bounds (both caps,
        positions, exception types). A note in the §4.3 comparison-primitive paragraph that
        `DidSubjectOf` treats input over the cap as not a DID.
      - CHANGELOG `[Unreleased]`: a Security entry, the accept-set narrowing, and the residual
        CPU cost (D3).
      - `codebase-architecture.md` does not exist in the repo, although CLAUDE.md refers to it,
        so there is nothing to sync.
- [x] 8. Verify.
      - `dotnet build -warnaserror`, Core tests, and Interop tests (fixtures submodule).
      - Before/after measurement of a 1 MiB-kid unpack (time and allocation), plus the D3
        residual.
      - The live harness python leg, if its pinned venv installs locally. Otherwise I'll ask
        before triggering `interop-live.yml` on the branch.
- [x] 9. QA agents (all permutations), then adversarial agents (CLAUDE.md §2). Fix what they
      find, and re-run 8.
- [x] 10. Commit, push, and open a PR that closes #80. The body notes D1 and D2 against the
      issue's "Done when".

## Round 1: fixes from the adversarial review

**Approved 2026-10-29:** R1–R4 as written, and drafting the F1–F3 issue texts for review (none
posted without a separate OK).

- [x] R1 recipient-kid filter, with tests
- [x] R2 DID-part rule at every key-id site, with tests
- [x] R3 own signer/sender key selection, with tests
- [x] R4 docs, comment and near-maximum `from`/`to` tests
- [x] CHANGELOG and PRD FR-API-09 updated for R1–R3
- [x] Build, Core, Interop, live harness re-run; focused adversarial re-check of R1
- [x] F1–F3 issue texts drafted in the scratchpad (F1 re-measured on `main`)

Progress so far:
- Steps 0–6 and 8 are done. Commits `3732e51` (fix) and `041656a` (QA tests).
- Build clean. Core 959/959, Interop 283/283.
- Live harness on the branch: python 56/56; JVM 53/53 with the same three declared n/a cells.
- Measured on `main` → branch: a ~1 MB kid went from 54 ms to 0.7 ms, and 8 concurrent ones from
  ~800 MiB to 23 MiB.
- QA found no source defect. The adversarial agents found no bypass, no possession oracle (timing
  gap under 1 µs, same as before) and no contract break.

They did find these, introduced by this diff:

- **R1 (medium): honest group traffic refused.** Check (c) refuses the whole JWE if *any*
  recipient kid is over the limit. A group member who publishes a 4097-character kid in their own
  DID document (DID Core sets no limit) makes every group message undeliverable to every other
  member. Our own `PackEncryptedAsync` emits such envelopes without complaint.
  **Fix:** treat an over-limit recipient kid as *not held*. Filter those kids out before
  `FindPresentAsync` instead of refusing the envelope. The secrets resolver still never sees them,
  and the result is the same for any receiver whatever keys it holds, so no oracle is added.
  `AllRecipientKids` keeps reporting the envelope's kids verbatim, as it did before the diff.
- **R2 (low): a kid's DID part can exceed 2048.** A 4096-character kid can carry a 4090-character
  DID, and that DID reaches the resolver before FR-CONSIST-01/03 compare it with `from`. A bare-DID
  `next` between 2049 and 4096 passes `TryParse`.
  **Fix:** every wire key id must also have at most 2048 characters before its first `/`, `;`,
  `?` or `#`. That makes D2's rationale ("a DID ≤ 2048 plus a fragment") an enforced rule. It is a
  linear scan with no regex, applied at the same sites (signer kid, `skid`, `from_prior` `kid`,
  `next`, and the recipient-kid filter in R1).
- **R3 (low): our own pack emits kids our own receive refuses.** When a signer's or authcrypt
  sender's own DID document has a key id over the limits, the message is refused by every
  didcomm-dotnet receiver.
  **Fix:** don't select an own signer or sender key whose kid exceeds the limits (the same idea as
  L-007's filter-before-pick). If no other key is left, the existing "no usable key" error
  applies. Recipients' kids are left alone; after R1, they no longer hurt other recipients.
- **R4: overclaims, fixed in the docs and one comment.**
  - "Removing any one guard fails at least two tests" → each of the two reorder guards fails one.
  - "bound values from resolved documents" → it bounds the *parse cost*; the `controller` echo
    predates the diff.
  - "`DidCommOptions.OwnIdentifiers` refuses" → it's the `DidCommClient` constructor.
  - The PRD says every position has a near-maximum test → add near-maximum `from`/`to` cases.
  - EnvelopeReader's "after the same envelope-only work" → "not a new signal".

**Found, predating the diff, not fixed here.** I'll draft issues and post them only with your OK.
- **F1 (high), didcomm and DataProofs.** One JWS with ~2,100 signature entries over a 512 KiB
  payload costs **~1.6 s CPU and ~3 GB allocation**, before authentication. DataProofs runs a full
  verify per entry, and nothing caps the count. A variant does one DID resolution per signature,
  which for did:webvh is a network fetch. The fix is a signature-count cap in didcomm; DataProofs
  should also build the signing input once.
- **F2 (medium), didcomm and DataProofs.** Unbounded wire values are quoted in full in exception
  messages, and from there into transport logs and an OpenTelemetry tag: `id`, `type`, `thid`,
  `pthid`, `ack`, `please_ack`, `from_prior` `alg`, `ForwardProcessor`'s `type`, a resolved
  `controller`, and DataProofs' `apv`/`enc`/`alg`/`crv`.
- **F3, NetDid.** The upstream regex guard. It is drafted in the scratchpad as
  `netdid-issue-draft.md`.

## Review

- **Shipped:**
  - Every wire DID is capped at 2048. Every wire key id is capped at 4096, with a DID part of at most
    2048, checked before any NetDid parse, DID resolution or secrets lookup.
  - Over-limit recipient kids are treated as not held.
  - Own signer and sender keys over the limits are never selected, even when the secrets resolver
    lists more kids than it was asked about.
  - No refusal quotes the value.
- **Verified:**
  - `-warnaserror` clean; Core 1008/1008; Interop 283/283.
  - Live harness: python 56/56 (with `PYTHON` pointed at 3.12; 3.14 breaks didcomm-python's
    pydantic v1), JVM 53/53 with the same three n/a cells.
  - Mutation: every guard, and every DID-part rule cut to a length test, fails at least one test.
  - ~1 MB kid: 54 ms → 0.7 ms; 8 concurrent: ~800 MiB → 23 MiB.
- **Reviews:**
  - QA found no defects.
  - Adversarial round 1 found R1–R4, all fixed.
  - Adversarial round 2 found the superset-resolver gap and the curve error message, both fixed.
    Its recipient-pick finding traced to FR-ENC-19 (pack uses one key per recipient), which is #81,
    stacked on this branch.
- **Follow-ups drafted, not posted:**
  - the multi-signature JWS pre-auth amplification (didcomm and DataProofs);
  - unbounded wire values in messages, logs and spans (didcomm and DataProofs);
  - the NetDid regex length guard.
