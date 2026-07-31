# 01 — Quickstart

The shortest useful didcomm-dotnet program, and the source of truth for the quickstart in the
[repository README](../../README.md). It is a compiled project on purpose: the README shows the body
of `Program.RunAsync`, so a snippet that stopped compiling would break the build rather than mislead
a reader (PRD FR-DX-05, and sample 01 of §14.3).

## What it demonstrates

- **`AddDidComm(...)` wiring** — the net-did resolver plus a secrets resolver, the two registrations
  the facade refuses to start without (FR-SEC-02).
- **Two `did:peer:2` identities**, minted through net-did by the shared `PeerIdentityFactory`.
  `did:peer:2` encodes its own DID document inside the DID string, so nothing here touches a network.
- **An authcrypt round-trip** — `PackEncryptedAsync` with `From:` set, which is confidential to Bob
  *and* cryptographically attributed to Alice. This is the envelope you usually want.
- **The unpack metadata (FR-API-04)** — what the envelope actually proved, as opposed to what the
  message claims about itself.

The in-memory `ISecretsResolver` comes from `DidComm.TestSupport`, which is deliberately **not**
shipped inside `DidComm.Core` (DD-02). In a real agent that is your KMS, HSM, or vault.

## Run it

```bash
dotnet run --project samples/01-Quickstart
```

## Expected output

The two DIDs are freshly generated on every run, so the `sender kid` line differs each time; every
other line is stable.

```
body          : {
  "text": "Hello, Bob."
}
authenticated : True
encrypted     : True
sender kid    : did:peer:2.Ez6LSi8yoKGbLoTZBB9FMrmpvtaaGcsNHEv4Cj6xAXwAdYbe7.Vz6Mkem…#key-1
addressed to  : Addressed
```

Reading the last four lines:

- **`authenticated: True`** — ECDH-1PU proved the sender controls Alice's key agreement key. This is
  the line that distinguishes authcrypt from anoncrypt; it is evidence, not a claim in the message.
- **`encrypted: True`** — the body was confidential in transit.
- **`sender kid`** — the exact key that authenticated the sender, as a DID URL.
- **`addressed to: Addressed`** — the FR-CONSIST-04 advisory check: one of this agent's own
  identifiers appears in the message's `to`. Treat `NotAddressed` as a warning worth acting on, but
  never treat `Addressed` as authorization — `to` is written by the sender.

## Where to go next

[`samples/02-Cookbook`](../02-Cookbook/) runs one narrated section per API task — threading and ACKs,
DID rotation, mediated routing, transports, and the built-in protocols.
