# 03 — Envelopes & Messages

The envelope tour (PRD §14.3 sample 03, tasks C–N). One linear run through every protective
shape DIDComm v2.1 defines, printing each **packed wire form** (truncated) next to the
**unpacked metadata** — so you can see, shape by shape, what each composition actually proves
as opposed to what the message claims about itself.

Where the [Cookbook](../02-Cookbook/) is a reference card (one isolated section per API task),
this sample is the guided walk: same envelope ground, ordered as a comparison, with the wire
bytes on display.

## What it demonstrates

- **Every envelope composition** — plaintext (C), signed (D), anoncrypt (E), authcrypt (F),
  sign-then-encrypt (G), and protect-sender (H, `anoncrypt(authcrypt(...))` with the outer
  JOSE headers of both variants decoded so you can watch `skid` disappear from the wire).
- **Each content-encryption algorithm on the composition that allows it** (I) — anoncrypt with
  `A256CBC-HS512`, `A256GCM`, and `XC20P`; authcrypt with its single legal cipher; and the
  live refusal of the forbidden authcrypt + GCM combination (FR-ENC-09).
- **Multi-recipient packing** (J) — one envelope for Bob *and* Carol; the `recipients` fan-out
  counted on the wire.
- **The full `UnpackResult` metadata surface** (K) — every field printed against a
  sign-then-encrypt envelope (FR-API-04).
- **All three attachment shapes** (L) — inline JSON, inline base64, linked-with-hash — plus
  the refusal of a link without an integrity hash (FR-ATT-03).
- **Threading & ACKs** (M) — `WithThid` to continue a thread, `WithPthid` to cite a parent
  thread, `WithPleaseAck`/`WithAck` for receipts, `Message.Empty()` for the header-only pure
  ACK, and the `AckLoopGuard` predicates that stop ACK ping-pong (FR-THR-*).
- **DID rotation via `from_prior`** (N) — `FromPriorBuilder.BuildAsync` with a bounded
  lifetime (FR-ROT-05), packed under the new DID, validated claims surfaced on
  `UnpackResult.FromPrior`; and the relationship-**termination** form
  (`FromPriorClaims.ForTermination`, no `sub`, from-less anoncrypt message) distinguished via
  `FromPriorClaims.IsTermination` (FR-ROT-06).

Everything runs offline: `did:peer:2` identities resolve locally and the in-memory secrets
resolver from `DidComm.TestSupport` stands in for your KMS (FR-DX-02, DD-02).

## Run it

```bash
dotnet run --project samples/03-EnvelopesAndMessages
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~EnvelopesAndMessagesSmokeTests
```

## Expected output (shape)

Fresh `did:peer:2` identities are minted every run, so DIDs, key ids, and ciphertext differ
each time; the structure is stable. Four minted identities, then one banner per section,
C through N:

```
  • Minted alice  = did:peer:2.Ez6LS…
  • Minted bob    = did:peer:2.Ez6LS…
  • Minted carol  = did:peer:2.Ez6LS…
  • Minted alice2 = did:peer:2.Ez6LS… (rotation target)

== Section C — Plaintext (debug/inspection only) ==
    Packed (truncated) = {"id":"…","type":"https://didcomm.org/basicmessage/2.0/message",…
    Encrypted = False
    Authenticated = False
    NonRepudiation = False
    note: All three flags are false: …

== Section D — Signed (non-repudiable, no confidentiality) ==
    …
== Section E — Anoncrypt (confidential, anonymous sender) ==
    KeyWrap = ECDH-ES+A256KW
    …
== Section F — Authcrypt (confidential + sender authenticated — the default) ==
    KeyWrap = ECDH-1PU+A256KW
    …
== Section G — Sign-then-encrypt (add non-repudiation) ==
    Stack = Encrypted ⊃ Signed ⊃ Plaintext
    …
== Section H — Protect the sender (anoncrypt wraps authcrypt) ==
    Outer skid = <null>
    …
== Section I — Content encryption — each algorithm on the composition that allows it ==
    note: Refused as designed: A256GCM is forbidden for authcrypt envelopes (FR-ENC-09)…
== Section J — Multi-recipient (one envelope, several readers) ==
    Recipients on the wire = 2
    …
== Section K — Unpack metadata — every field, one envelope ==
== Section L — Attachments (inline json / base64 / linked-with-hash) ==
== Section M — Threading & ACKs (thid / pthid / please_ack / ack) ==
== Section N — DID rotation via from_prior ==
    Sub == message.From = True
    Termination FromPrior.IsTermination = True
```

## Where to go next

- [`samples/04-MediatorAgent`](../04-MediatorAgent/) — routing these envelopes through a
  mediator over HTTP.
- [`samples/02-Cookbook`](../02-Cookbook/) — the per-task reference for everything else
  (transports, protocols, extensibility).
