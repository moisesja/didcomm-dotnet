# 08 — Extensibility

The three extension points, end to end (PRD §14.3 sample 08, tasks Y/Z, FR-SEC-01/04/06,
FR-TRN-01): custom key custody twice over — a hand-written mock KMS and the shipped net-did
`IKeyStore` bridge — plus a custom transport, each registered through a different flavor of
the DI surface.

## What it demonstrates

- **A custom `ISecretsResolver` — the mock KMS (FR-SEC-01/06)** — `MockKmsSecretsResolver`
  implements BOTH resolver contracts: `FindAsync`/`FindPresentAsync` answer selection
  questions with PUBLIC-only JWKs (`D` is always null — the custody invariant), while the
  `IOpaqueKeyResolver` half performs the two private-key operations inside the "KMS
  boundary": `ResolveSignerAsync` (JWS signing) and `ResolveKeyAgreementAsync` (an
  `IEcdhKey` handle deriving the raw shared secret Z). The sample calls both handles
  explicitly, then drives the facade through the same KMS — sign-then-encrypt out
  (`Authenticated`/`NonRepudiation` true at Bob), authcrypt back in (the receive path
  decrypts through the opaque ECDH handle).
- **The generic DI overload** — Alice's container uses
  `b.UseSecretsResolver<MockKmsSecretsResolver>()`; because the type also implements
  `IOpaqueKeyResolver`, the SAME singleton is surfaced under both contracts (asserted with
  `ReferenceEquals`).
- **The net-did `IKeyStore` bridge (FR-SEC-04)** — keys imported into a NetCrypto
  `InMemoryKeyStore` under human-friendly aliases (deliberately NOT DID URLs), adapted by
  `NetDidKeyStoreSecretsResolver` with the **`kidToAlias` constructor mapping**, registered
  via the instance overload `b.UseSecretsResolver(bridge)`. Public-only `FindAsync`,
  alias-list-backed `FindPresentAsync`, and a sign-then-encrypt to Bob — no private byte
  ever leaves the store.
- **A custom `IDidCommTransport` (FR-TRN-01)** — `MemoryQueueTransport` (three members:
  `Scheme`, `CanHandle`, `SendAsync`) registered through DI on the builder's `Services`
  collection (`AddSingleton<IDidCommTransport, T>()` — exactly what the packaged
  `UseHttpTransport()`/`UseWebSocketTransport()` extensions do under the hood). `SendAsync`
  to a `memq://` endpoint is router-selected by scheme; the delivered bytes are a normal
  packed envelope Bob unpacks; an endpoint scheme with no registered transport is refused
  with `TransportException`.

Fully offline and deterministic: `did:peer:2` resolves locally and the only "network" is an
in-memory queue (FR-DX-02).

## Run it

```bash
dotnet run --project samples/08-Extensibility
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~ExtensibilitySmokeTests
```

## Expected output (shape)

DIDs change every run; the structure is stable:

```
== Section 1 — A custom ISecretsResolver — the mock KMS (FR-SEC-01/06) ==
    ISecretsResolver is the KMS = True
    IOpaqueKeyResolver is the SAME instance = True
    FindAsync → D = <null> (private scalar never leaves the KMS)
    ResolveSignerAsync signature bytes = 64
    ResolveKeyAgreementAsync → Crv = X25519
    DeriveAsync shared-secret bytes = 32
    Bob sees Authenticated = True
    Bob sees NonRepudiation = True
    Alice unpacked content = Round trip complete.

== Section 2 — The net-did IKeyStore bridge — NetDidKeyStoreSecretsResolver (FR-SEC-04) ==
    Bridge built with kidToAlias mapping = 2 entries
    Bridge FindAsync → D = <null> (keystore-held)
    FindPresentAsync via kidToAlias = True
    Bob sees Authenticated = True
    Bob sees NonRepudiation = True

== Section 3 — A custom IDidCommTransport, registered through DI (FR-TRN-01) ==
    Registered scheme = memq
    EndpointUsed = memq://bob-inbox/
    Delivered media type = application/didcomm-encrypted+json
    Bob unpacked content = Delivered over a transport invented in this file.
    https send = refused (TransportException: …)
```

## Where to go next

- [`samples/02-Cookbook`](../02-Cookbook/) sections Y/Z — the same API surface in
  reference-card form.
- [`samples/09-NetDidIntegration`](../09-NetDidIntegration/) — the resolution side of the
  net-did partnership.
