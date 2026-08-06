# 09 — net-did Integration

DID resolution is delegated to net-did (DD-01) — this sample shows what that buys and where
the library deliberately says no (PRD §14.3 sample 09, task AA, FR-DID-01/05/06, FR-API-05,
DD-08).

## What it demonstrates

- **`UseNetDidResolver()` (FR-DID-01)** — one builder call registers net-did's composite
  resolver (`did:key` + `did:peer` by default) and the `NetDidKeyService` adapter every
  pack/unpack resolves through.
- **Minting `did:key` (FR-DID-05)** — `DidKeyCreateOptions` with an existing Ed25519 signer
  and `EnableEncryptionKeyDerivation`; the resolved document is inspected via
  `IDidKeyService.GetVerificationMethodsAsync` (kids + curves printed for both
  relationships), and the derived X25519 keyAgreement key is proven real by deriving it
  locally (`IKeyGenerator.DeriveX25519FromEd25519`) and matching public keys — one Ed25519
  seed serving both signatures and encryption.
- **Minting `did:peer` numalgo 0 and 2** — `DidPeerCreateOptions { Numalgo = Zero }` (the
  inception-key variant, put to work in a verified signed envelope) and the numalgo-2 shape
  via the shared `PeerIdentityFactory`.
- **Messaging across method boundaries** — a `did:key` sender authcrypts to a `did:peer:2`
  recipient; the unpacked metadata shows a `did:key` `SenderKid` and a `did:peer` `RecipientKid`
  in the same envelope.
- **`expires_time` under an injected clock (FR-API-05)** — `DidCommOptions.Clock` +
  `ExpiresClockSkew`: the same packed message is accepted at base+1min, rejected
  (`MalformedMessageException`) at base+1h with zero skew, and accepted again with a 2h
  skew — no real sleeps, the clock moves instead of the wall time.
- **The deliberate `did:web` rejection (FR-DID-06, DD-08)** — `UnsupportedDidMethodException`
  (with its `Method` property equal to `"web"`) from every reachable entry point:
  resolution (`GetVerificationMethodsAsync`), pack as recipient/sender/signer, `SendAsync`,
  and unpack of a wire message addressed from `did:web`. The supported web-hosted
  alternative is `did:webvh`.

Fully offline and deterministic: all three DID methods resolve locally (FR-DX-02).

## Run it

```bash
dotnet run --project samples/09-NetDidIntegration
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~NetDidIntegrationSmokeTests
```

## Expected output (shape)

DIDs change every run; the structure is stable:

```
== Section 1 — Mint a did:key — Ed25519 in, X25519 keyAgreement derived (FR-DID-05) ==
    did:key = did:key:z6Mk…
    - authentication = crv=Ed25519 kid=did:key:z6Mk…#z6Mk…
    - keyAgreement = crv=X25519 kid=did:key:z6Mk…#z6LS…
    Locally-derived X25519 matches the DID document = True

== Section 2 — Mint a did:peer numalgo 0 — the inception-key variant ==
    did:peer:0 = did:peer:0z6Mk…
    Signed envelope NonRepudiation = True
    SignerKid belongs to did:peer:0 = True

== Section 3 — did:key sender → did:peer:2 recipient (methods interoperate) ==
    Authenticated (authcrypt) = True
    SenderKid is a did:key kid = True
    RecipientKid is a did:peer kid = True

== Section 4 — expires_time + DidCommOptions.Clock / ExpiresClockSkew (FR-API-05) ==
    Clock at base+1min = accepted (…)
    Clock at base+1h, no skew = rejected (MalformedMessageException — message expired)
    Clock at base+1h, skew 2h = accepted (…)

== Section 5 — did:web is refused at every entry point (FR-DID-06, DD-08) ==
    Resolution (GetVerificationMethodsAsync) = refused (Method='web', Did='did:web:example.com')
    Pack — did:web recipient = refused (…)
    Pack — did:web sender = refused (…)
    Pack — did:web signer = refused (…)
    SendAsync — did:web recipient = refused (…)
    Unpack — plaintext from did:web = refused (…)
```

## Where to go next

- [`samples/02-Cookbook`](../02-Cookbook/) section AA — the same refusal in reference-card
  form.
- [`samples/08-Extensibility`](../08-Extensibility/) — the secrets side of the net-did
  partnership (the `IKeyStore` bridge).
