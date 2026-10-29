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

- [ ] 0. Branch `feat/keyid-length-cap-80` off `main`.
- [ ] 1. `IdentifierLimits`. `Message.cs` switches to it and reorders its length checks before
      the checks that echo the value.
- [ ] 2. Backstops: `DidSubjectOf`, `RejectUnsupportedMethod`, `OwnIdentifiers` message.
- [ ] 3. `EnvelopeReader`: lookup wrappers (signer, sender) and the recipient-kid check after peek.
- [ ] 4. `FromPriorValidator`: `kid`/`iss`/`sub` caps and `<exception>` doc.
- [ ] 5. `ForwardMessage.TryParse`: `next` cap and `<exception>` doc.
- [ ] 6. Tests. Each position gets a hostile case at cap + 1 and at 1 MiB, plus a control at
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
- [ ] 7. Docs.
      - PRD: a new row **FR-API-09 (MUST)** for wire identifier length bounds (both caps,
        positions, exception types). A note in the §4.3 comparison-primitive paragraph that
        `DidSubjectOf` treats input over the cap as not a DID.
      - CHANGELOG `[Unreleased]`: a Security entry, the accept-set narrowing, and the residual
        CPU cost (D3).
      - `codebase-architecture.md` does not exist in the repo, although CLAUDE.md refers to it,
        so there is nothing to sync.
- [ ] 8. Verify.
      - `dotnet build -warnaserror`, Core tests, and Interop tests (fixtures submodule).
      - Before/after measurement of a 1 MiB-kid unpack (time and allocation), plus the D3
        residual.
      - The live harness python leg, if its pinned venv installs locally. Otherwise I'll ask
        before triggering `interop-live.yml` on the branch.
- [ ] 9. QA agents (all permutations), then adversarial agents (CLAUDE.md §2). Fix what they
      find, and re-run 8.
- [ ] 10. Commit, push, and open a PR that closes #80. The body notes D1 and D2 against the
      issue's "Done when".

## Review

(filled in after implementation)
