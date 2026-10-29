# Encrypt to every keyAgreement key of each recipient (FR-ENC-19)

## Context

`PackEncryptedAsync` encrypts to the **first** keyAgreement key on the chosen curve per recipient DID
(`DidCommClient.cs:384-388`, unchanged since Phase 3). DIDComm v2.1 says otherwise:

- encryption.md §Key IDs: "the default recipients of the envelope SHOULD include all the
  `keyAgreement` entries representing Bob. This allows Bob to decrypt his messages on any device he
  controls."
- routing.md §DID Document Keys: "Ideally, all keys declared in the `keyAgreement` section … are
  used as target keys"; routing Example 1 encrypts the forward to "the keyAgreement keys within the
  mediator DID Document".
- The Appendix C.3 vectors encrypt to all of Bob's keys on each curve. Their `apv` is the hash of
  all three X25519 kids. I recomputed it: `NcsuAnrRfPK6…`; first-key-only would give `QBHLILrN…`.

PRD FR-ENC-19 and DD-07 already require this. An old task file marked FR-ENC-19 done because of
EnvelopeWriter tests that are handed the keys directly; nothing tested the facade's own choice.

Interop: didcomm-python 0.3.2 and didcomm-jvm 0.3.2, our live-harness peers, both encrypt to every
key of the same type as the recipient's first keyAgreement key, in document order (python
`anoncrypt_keys_selector.py:87-97`, read; JVM `SenderKeySelector.kt:56-92`). Both accept a `to`
that is a DID URL with a fragment, which pins one key. Both wrap a forward layer to all of a
mediator's keys.

Today's harness misses this: the one multi-key cell unpacks with an identity that holds every key,
so first-key-only packing passes.

#80 interplay: a didcomm-dotnet receiver now treats an over-limit recipient kid as not held. With
first-key-only packing, a recipient whose first key has such a kid can't decrypt, even though they
publish a usable key. With all keys, the usable one is in the envelope.

Design: architect agent, 2026-10-29. Key claims spot-checked: the C.3 `apv`, python's selector, the
facade line.

**Approved 2026-10-29** ("go and do it"): D1–D12 as recommended; D11 a separate PR stacked on #80; D12 its own issue (drafted; posting it was blocked by the permission check).

## Decisions (recommendation first)

- **D1 — default set.** Per recipient DID: every addressable keyAgreement key on the negotiated
  curve, in document order. Envelope order is DID order, then document order. *Alt:* pick the curve
  covering the most keys. Neither peer does this.
- **D2 — curve negotiation stays** (`PickCommonCurve`, X25519 > P-256 > P-384 > P-521, common to all
  recipients and, for authcrypt, a held sender key). The spec has no curve-choice rule, and any
  published curve decrypts on all three implementations. It now runs per entry, so a pinned key
  (D3) constrains the curve. *Alt:* use the first listed curve, for exact python/JVM parity. That is
  a separate wire change and fixes no failure.
- **D3 — override: a DID URL in `Recipients`** (`did#fragment`), like python/JVM `to`.
  - It contributes exactly that key, which must be one of the DID's keyAgreement keys; otherwise
    `DidResolutionException`.
  - A path, query or params gives `ArgumentException`. An entry over 4096 characters gives
    `ArgumentException` without quoting the value.
  - A bare DID still contributes all its keys on the curve.
  - No new public member, and `SendOptions` gets it for free.
  - Today such an entry throws `DidResolutionException` ("not a syntactically valid DID").
  - The message's `to` stays the bare DID. `Forward` requires one distinct DID subject.
  - *Alt:* an init-only `RecipientKeyIds` property. That adds public API plus a sample (FR-DX-01).
- **D4 — key hygiene.**
  - Keys with no kid are skipped before curve negotiation. Today they escape as a raw DataProofs
    `MalformedJoseException`. A DID with no kid'd key gives `DidResolutionException`.
  - Kids are deduped, ordinal, first wins, across the envelope. Today `[bob, bob]` duplicates
    entries.
  - Over-limit recipient kids are included (spec; other implementations may accept them; ours skip
    them per entry).
- **D5 — forward layers.**
  - A mediator DID used as the service `uri` gets one layer addressed to all of the mediator's keys
    on one curve. Today it uses `mediatorKeys[0]`, and the remark claiming that's what reference
    implementations do is wrong.
  - DID-URL `routingKeys` are unchanged.
  - **D5b:** bare-DID `routingKeys` are accepted and wrapped to all keys, as python and JVM do. Today
    they throw. Interop: we can then route through documents those peers produce.
- **D6 — `ForwardProcessor` `ReanoncryptToNext`** uses the same selector. It already takes all keys
  on one curve, but it doesn't dedupe and can mix curves in its fallback.
- **D7 — one internal helper**, `Composition/RecipientKeySelection.cs`: addressable keys (kid'd,
  supported curve, deduped), `PickCommonCurve` (moved), and on-curve selection. Used by the facade,
  `MediatorEndpointExpander` and `ForwardProcessor`.
- **D8 — routing plumbing stays internal.** The expander returns forward layers, each a DID plus its
  recipient JWKs, and `ForwardWrapper` takes them. Public `ResolvedRoute` is unchanged.
- **D9 — observability.** A Debug `LoggerMessage` (counts only, NFR-04) when keys on other curves
  were skipped, which is the problems.md 'w' situation. `RecipientsTag` stays the entry count.
  *Deferred:* a `PackEncryptedResult.RecipientKids` property (python `to_kids`, JVM `toKids`).
- **D10 — cap of 64 keys per recipient DID**, taken in document order, with the rest skipped and
  logged. A peer's DID document chooses how many ECDH operations our pack runs, and auto-replies make
  that remotely triggerable. 64 is judgement, within routing.md's "as many … as is practical".
  *Alt:* no cap, for strict parity, plus an issue.
- **D11 — placement.** A separate branch, `feat/enc19-all-recipient-keys`, stacked on
  `feat/keyid-length-cap-80`, as its own PR (closes a new FR-ENC-19 issue), merged right after #80
  and in the same release. #80 stays a reviewable security change; this one is a wire-visible
  conformance change. *Alt:* fold it into the #80 PR.
- **D12 — out of scope here, needs your call.** encryption.md:5: "A message being sent to multiple
  DIDs MUST be encrypted for each DID independently. If a single DID has multiple key types, the keys
  of each type must be used in a separate encryption of the message."
  - Our facade puts several DIDs in one envelope (PRD FR-ENC-04 restates the MUST but the code
    doesn't follow it). For a DID with keys on several curves, the spec wants one envelope per type.
  - Neither peer sends multi-DID envelopes (one `to` per pack), and neither does per-type envelopes.
  - Meeting the MUST changes `PackEncryptedAsync`'s result: one envelope per DID, and per key type,
    each needing its own transport send.
  - Recommendation: its own issue with an API design (for example, a `PackEncryptedResult` carrying
    one envelope per DID and per type, or refusing multi-DID `Recipients` with a pointer to a
    per-recipient loop), decided separately.

## Steps

- [x] 0. Branch `feat/enc19-all-recipient-keys` off `feat/keyid-length-cap-80`. Open an issue for
      FR-ENC-19 (text for your OK first).
- [x] 1. Red-first facade tests (step 4's T1, T2, T7), confirmed failing on the branch base.
- [x] 2. `RecipientKeySelection` helper (D4, D7, D10) with unit tests.
- [x] 3. Facade: per-entry candidates, DID-URL pin (D3), all keys on the curve (D1/D2), the skip log
      (D9).
- [x] 4. Facade tests:
      - T1: spec Appendix A/B DID docs. `PackEncryptedAsync([bob])` gives x25519-1/-2/-3 in order,
        and `apv` equals the C.3 vector. The same for authcrypt, and P-256/P-384/P-521 with a
        curve-filtered key service.
      - T2: a device holding only key-3 decrypts.
      - T3: mixed curves.
      - T4: authcrypt sender constraint.
      - T5: `SignFrom` and `ProtectSender`.
      - T7: #80 case, over-limit first kid plus a usable kid. Both are included; Bob decrypts; a
        holder of only the long key fails like a stranger.
      - T8: pins.
      - T9: dedupe and null kid.
      - T10: the 64 cap.
- [x] 5. Routing: expander layers (D5, D5b, D8), `ForwardWrapper`, `ForwardProcessor` (D6). Update
      `MediatorEndpointExpanderTests` (the first-kid test becomes an all-keys test; with D5b, the
      lacks-fragment test flips).
- [x] 6. Live harness, both legs, both directions:
      - anoncrypt to the 3-key DID, unpacked by an identity holding only the third key;
      - the same for authcrypt;
      - a kid-pinned `to` (exactly one entry, unpacked with only that key).
      These run red on the base first; they fail outbound today.
- [x] 7. Docs:
      - PRD FR-ENC-19 (MUST row implementing the spec SHOULD; acceptance through
        `PackEncryptedAsync`), DD-07, FR-ROUTE-04; flag FR-ENC-04/D12.
      - Fix the stale "done" in `tasks/todo20260523T180017Z.md:109`.
      - XML docs on `PackEncryptedOptions`, `SendOptions`, `PackEncryptedAsync`, the expander remark,
        `UnpackResult.AllRecipientKids`, and `MultiRecipientPacker`.
      - Sample comments: cookbook Section J, samples/03, cookbook README row J.
      - The harness README.
      - CHANGELOG: "Changed (wire-visible)" section.
- [ ] 8. Verify: `-warnaserror` build, Core and Interop tests, package validation against 1.4.1, the
      live harness (both legs; `PYTHON=python3.12`, since 3.14 breaks didcomm-python's pydantic v1),
      and the sample smoke tests.
- [ ] 9. QA agents, then adversarial agents (CLAUDE.md §2). Focus: pack-side amplification, pin
      handling, the receive-side oracle with multi-entry envelopes.
- [ ] 10. Commit, push, PR (stacked on #80's branch; retarget to `main` after #80 merges).

## Risks

- Apps that count recipient entries, or assume `RecipientKid` is a DID's first key, see different
  values. CHANGELOG states this.
- Receive side: we try only the first *held* key in envelope order; python and JVM try each held key.
  More multi-key envelopes make that gap likelier to matter (a held key whose entry fails). Changing
  it needs a constant-work/oracle review, so it's a follow-up issue, not this PR.
- Mixing a bare DID and a pin for the same DID (bare + `#p256` forces P-256) is semantics neither
  peer can express. It's documented and tested.

## Review

(filled in after implementation)
