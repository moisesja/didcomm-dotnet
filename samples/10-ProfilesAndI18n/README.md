# 10 — Profiles & i18n

Which dialect to speak, and in which language (PRD §14.3 sample 10, task BB, FR-PROF-01/02,
FR-I18N-01..04). Two agents in separate containers play the spec's chess example.

## What it demonstrates

- **Profile selection over an OOB invitation (FR-PROF-01)** — Bob's invitation advertises
  `accept: [didcomm/aip1, didcomm/v2]`; Alice decodes it (`OutOfBand.FromUrl`) and
  `ProfileNegotiator.Choose` picks `didcomm/v2` (the `Profiles` constants name the
  identifiers; an absent accept array means "no claim" and defaults to v2).
- **The same accept array on the DID document** — Bob's `did:peer:2` embeds a
  `DIDCommMessaging` service with `accept`; Alice reads it through
  `IServiceEndpointResolver.ResolveAsync` (`DidCommServiceInfo.Accept`) and negotiates the
  same way — no invitation required.
- **Mismatch handling (FR-PROF-02)** — a peer offering only `didcomm/aip1`/`didcomm/v3`
  makes `Choose` return `null` (never a guess), and Alice reports it with a problem-report
  naming both sides' dialects.
- **The chess example (FR-I18N-01/03)** — Alice mates in French: `WithLang("fr")` marks the
  human-readable comment's language, `WithAcceptLang("fr", "en")` ranks the languages she
  wants answers in; both headers round-trip through authcrypt.
- **Thread-scoped preference (FR-I18N-02)** — Bob records the `accept-lang` against the
  thread (`IThreadStateStore`/`ThreadState.AcceptLang`, thid = the first message's id) and
  honors it for every reply on that thread — including Alice's follow-up that carries NO
  language headers. A concurrent thread has no recorded preference and gets Bob's default
  English: the preference never leaks sideways.
- **Bad-lang (FR-I18N-04)** — a thread demanding `accept-lang: [ja]` against Bob's en/fr
  catalog makes `ProblemReport.CreateBadLangForThread` produce `w.msg.bad-lang` with
  `pthid` = the failing thread and the available languages interpolated into the comment;
  the report travels to Alice as a normal authcrypt message.

Fully offline and deterministic: `did:peer:2` resolves locally, including Bob's service
block; the `https://bob.example` URI is advertised but never dialed (FR-DX-02).

## Run it

```bash
dotnet run --project samples/10-ProfilesAndI18n
```

Or via the smoke test (no process spawn — what CI runs):

```bash
dotnet test --filter FullyQualifiedName~ProfilesAndI18nSmokeTests
```

## Expected output (shape)

DIDs and ids change every run; the structure is stable:

```
== Section 1 — Profile selection over an OOB invitation's accept array (FR-PROF-01) ==
    Invitation accept = didcomm/aip1, didcomm/v2
    Negotiated profile = didcomm/v2

== Section 2 — The same accept array on Bob's DIDCommMessaging service ==
    Service uri = https://bob.example/didcomm
    Service accept = didcomm/v2
    Negotiated from service accept = didcomm/v2

== Section 3 — Profile mismatch — negotiate, then report (FR-PROF-02) ==
    Choose([didcomm/aip1, didcomm/v3]) = <null> (no shared profile)
    Report code = e.p.msg.unsupported

== Section 4 — Chess in French — lang / accept-lang headers (FR-I18N-01/03) ==
    Bob unpacks lang = fr
    Bob unpacks accept-lang = fr, en
    Reply lang = fr
    Reply comment = Échec et mat. Bien joué.

== Section 5 — The preference is thread-scoped (FR-I18N-02) ==
    Follow-up carries accept-lang = False
    Second reply on chess thread — lang = fr
    Concurrent-thread reply lang = en
    Chess preference did not leak = True

== Section 6 — No acceptable language — the bad-lang report (FR-I18N-04) ==
    Report code = w.msg.bad-lang
    Report pthid == failing thid = True
    Report comment = No acceptable language is available on this thread; languages available here: en, fr.
```

## Where to go next

- [`samples/02-Cookbook`](../02-Cookbook/) section BB — the same API surface in
  reference-card form.
- [`samples/07-ProblemsAndProtocols`](../07-ProblemsAndProtocols/) — the full Report
  Problem 2.0 story behind the bad-lang report.
