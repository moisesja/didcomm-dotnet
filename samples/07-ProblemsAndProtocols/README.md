# 07 — Problems & Protocols

How a DIDComm agent talks about failure — and how it grows new protocols (PRD §14.3
sample 07, tasks U/W/X, FR-PROTO-03/06/07/08/09/10/11/12). One offline container, two
`did:peer:2` identities, ten narrated sections.

## What it demonstrates

- **The problem-code taxonomy (FR-PROTO-08)** — `ProblemCode.Parse`/`TryParse` into
  `Sorter`/`Scope`/`Descriptor`, the `IsError`/`IsWarning`/`IsProtocolScoped`/`IsMessageScoped`
  predicates, and structural per-segment prefix matching (`StartsWith("e.p.xfer")` matches,
  `"e.p.xf"` does not — it is not a string-prefix test).
- **Building and reading reports (FR-PROTO-07)** — `ProblemReport.Create` with the REQUIRED
  `pthid` (omitting it throws at construction), `comment` + `args` with 1-based `{n}`
  interpolation via `RenderComment` (missing args render `?`, unreferenced extras are
  appended), `escalate_to`, and `ReadCode`/`ReadComment`/`ReadArgs` on the receive side.
- **Warning → error escalation (FR-PROTO-09)** — `ProblemReport.Escalate` flips the sorter
  to `e` while preserving the original scope; escalating a non-warning is refused.
- **The cascade guard (FR-PROTO-10)** — `ProblemReportOptions.CascadeThreshold` configured
  to 2 via the standard Options pattern; four error reports on one `pthid` dispatched
  through `ProtocolDispatcher` show the budget fill, the single
  `e.p.req.max-errors-exceeded` cascade-stop (with the breach count in its comment), then
  silence.
- **Bad-lang factories (FR-I18N-04)** — `ProblemReport.CreateBadLang` (warning and fatal
  forms) and the thread-aware `CreateBadLangForThread` over a `ThreadState`, including the
  null result when a preference is satisfiable.
- **Empty 1.0 ACK (FR-PROTO-06)** — `please_ack` on a request, `Message.Empty()` +
  `WithAck` for the header-only receipt, and the built-in `EmptyHandler` consuming it
  (`NoReply` — answering ACKs is how loops start).
- **A custom protocol (FR-PROTO-03)** — the spec's `lets_do_lunch` example as a one-class
  `IProtocolHandler` registered with `b.AddProtocol<LunchHandler>()` and routed by
  `ProtocolDispatcher` off the message type's protocol identifier.
- **A read-only observer (FR-PROTO-12)** — `b.AddProtocolObserver<T>()` watches inbound
  report-problem traffic (filtered by `ProtocolUriFilter`) WITHOUT replacing the built-in
  handler; delivery is on a background queue, so the sample waits on the observer's own
  signal — no sleeps.
- **A custom Discover Features provider (FR-PROTO-05)** — `b.AddFeatureProvider<T>()`
  advertises the `org.example.lunch` goal-code; an inbound `queries` message gets a
  `disclose` that includes it.
- **Trace 2.0 posture (FR-PROTO-11/11a)** — off by default (a trace header alone produces
  no report), honored only after `b.EnableTracing(...)` with an explicit `report_uri`
  allowlist, non-allowlisted URIs dropped, and `Enabled = true` without an allowlist
  refused at composition time.

Fully offline and deterministic: `did:peer:2` resolves locally and the only waiting is on
the observer's completion signal (FR-DX-02).

## Run it

```bash
dotnet run --project samples/07-ProblemsAndProtocols
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~ProblemsAndProtocolsSmokeTests
```

## Expected output (shape)

DIDs and message ids change every run; the structure is stable:

```
== Section 1 — The problem-code taxonomy (FR-PROTO-08) ==
    Sorter / Scope / Descriptor = e / p / xfer.cant-use-endpoint
    StartsWith("e.p.xfer") = True
    StartsWith("e.p.xf") = False

== Section 2 — Build and read a problem-report (FR-PROTO-07) ==
    Create without pthid = refused (ArgumentException — pthid is REQUIRED)
    Rendered comment = Unable to use the https://agents.r.us/inbox endpoint for did:peer:2.…
    Missing arg renders '?' = Field thid clashed with ?.
    Extra args are appended = Only first is referenced. [extra: second, third]

== Section 3 — Warning → error escalation (FR-PROTO-09) ==
    Escalated code = e.m.xfer.failed

== Section 4 — The cascade guard (FR-PROTO-10) ==
    report #1 outcome = NoReply
    report #2 outcome = NoReply
    report #3 outcome = ReplyProduced
      Cascade-stop code = e.p.req.max-errors-exceeded
    report #4 outcome = NoReply

== Section 5 — Bad-lang reports (FR-I18N-04) ==
    Code = w.msg.bad-lang
    Fatal form code = e.msg.bad-lang
    Satisfiable preference produces = <null> (no report warranted)

== Section 6 — Empty 1.0 — the header-only ACK (FR-PROTO-06) ==
    Empty dispatch outcome = NoReply
    Handled by = https://didcomm.org/empty/1.0

== Section 7 — A custom protocol — lets_do_lunch (FR-PROTO-03) ==
    Dispatched to = https://didcomm.org/lets-do-lunch/1.0
    Bob accepted = True

== Section 8 — A read-only observer on report-problem traffic (FR-PROTO-12) ==
    Observed problem-report count = 4
    Every observation was authenticated = True

== Section 9 — Advertising the lunch goal-code via Discover Features (FR-PROTO-05) ==
    - disclosed = goal-code: org.example.lunch

== Section 10 — Trace 2.0 — the opt-in posture (FR-PROTO-11/11a) ==
    ShouldReport (defaults) = False
    ShouldReport (opted in, allowlisted) = True
    ShouldReport (opted in, NOT allowlisted) = False
    EnableTracing without an allowlist = refused at composition time
```

## Where to go next

- [`samples/02-Cookbook`](../02-Cookbook/) sections U/W/X — the same API surface in
  reference-card form.
- [`samples/10-ProfilesAndI18n`](../10-ProfilesAndI18n/) — bad-lang in a live two-agent
  conversation.
