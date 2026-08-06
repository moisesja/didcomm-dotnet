# 06 — Out-of-Band

First contact (PRD §14.3 sample 06, task V, FR-OOB-01..05): how two agents that have never
met bootstrap a DIDComm relationship. Device 1 (Alice) builds an Out-of-Band 2.0 invitation
and encodes it into the URL that sits behind a QR code; Device 2 (Bob) — a separate container
with its own keys — decodes it, dereferences the short-URL form over HTTP, and answers with
an encrypted response that Alice correlates back to her invitation via `pthid`.

## What it demonstrates

- **Building an invitation (FR-OOB-01)** — `OutOfBand.CreateInvitation` with `from`, `goal`,
  `goal_code`, and `accept`. Invitations are deliberately public plaintext: they get printed
  and e-mailed, so they carry only what a stranger needs to start talking.
- **The `?_oob=` URL form (FR-OOB-02)** — `OutOfBand.ToUrl`, with the sample asserting the
  payload is padding-free base64url (no `=`, `+`, or `/`), and a terminal QR placeholder
  (a real app hands the URL string to any QR library; the sample stays dependency-free).
- **Decoding on the other device** — `OutOfBand.FromUrl`, which validates structure and
  refuses a fromless or malformed payload rather than half-parsing it.
- **The short-URL form over real HTTP (FR-OOB-04)** — `IOobInvitationStore` /
  `InMemoryOobInvitationStore` populated by the inviter, served by
  `app.MapDidCommOobEndpoint("/oob", store)` on a dynamic loopback port; the scanner side
  uses `TryGetShortFormId`, an HTTP GET (`200`, `application/didcomm-plain+json`), and
  `FromPlaintext`. The store's job is short-URL *hosting* — reply correlation (below) is
  plain application state keyed by invitation id.
- **Response correlation via `pthid` (FR-OOB-03)** — Bob's reply starts a new thread whose
  `pthid` is the invitation's id (`Message.Empty().WithPthid(...)`), travels as authcrypt to
  the DID the invitation delivered, and Alice matches the unpacked `pthid` against her
  pending-invitation registry. One QR code can spawn many independent threads this way.
- **`web_redirect` (FR-OOB-05)** — `OutOfBand.AddWebRedirect` / `ReadWebRedirect` on the
  concluding message.

Fully offline: `did:peer:2` resolves locally, the only HTTP is loopback on a dynamic port
(FR-DX-02).

## Run it

```bash
dotnet run --project samples/06-OutOfBand
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~OutOfBandSmokeTests
```

## Expected output (shape)

DIDs, ids, and the port change every run; the structure is stable:

```
== Section 1 — Device 1 (Alice) builds an Out-of-Band invitation ==
    Invitation id = <uuid>
    From = did:peer:2.Ez6LS…
    Goal = Establish a DIDComm connection with Alice
    GoalCode = connect
    Accept = didcomm/v2

== Section 2 — Encode to the ?_oob= URL (the QR code payload, FR-OOB-02) ==
    URL (truncated) = https://alice.example/invite?_oob=eyJib2R5…
    _oob is padding-free base64url = True
  • Shown on Alice's screen:
    (ASCII QR placeholder)

== Section 3 — Device 2 (Bob) scans and decodes the invitation ==
    Decoded id == original = True
    Decoded from == Alice = True

== Section 4 — Short-URL form — ?_oobid= served by the inviter (FR-OOB-04) ==
    Short URL = http://127.0.0.1:<port>/oob?_oobid=<uuid>
    GET status = 200
    Content-Type = application/didcomm-plain+json
    Fetched id == original invitation = True

== Section 5 — Bob responds; Alice correlates via pthid (FR-OOB-03/05) ==
    pthid == invitation.id = True
    Correlated to a pending invitation = True
    Responder (authenticated) = did:peer:2.Ez6LS…
    web_redirect = https://alice.example/welcome
```

## Where to go next

- [`samples/05-WebSocketChat`](../05-WebSocketChat/) — what the two connected agents do next.
- [`samples/02-Cookbook`](../02-Cookbook/) section V — the same API surface in reference-card
  form.
