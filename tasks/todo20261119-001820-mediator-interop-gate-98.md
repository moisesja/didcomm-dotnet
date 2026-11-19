# todo20261119-001820 — Mediator interop gate against Identus + adversarial review of the mediator service (#98)

The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Issue: #98 (epic #88; the gate). Branch: `feat/mediator-interop-gate-98` from origin/main `a6b1ebe` (L-019: re-verify before every commit). Worktree: `.claude/worktrees/orch-98`.
PRD rows (the target): §12 Phase 7 exit criteria ("Live interop: at least one external-mediator leg … green nightly against a pinned image; the adversarial report is posted and every High/Medium finding closed"), FR-MED-*, FR-PICK-*, FR-RR-*, FR-ROUTE-09..11, FR-TRN-13, FR-API-09, FR-IX-04/05/08. D1–D7 unchanged.

Build/verify (an item is done only when all three are green, plus a green live run on the branch):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`. Long local runs under `caffeinate -dimsu`.

## Binding inputs

1. **#98 body**: Scope 1 harness, Scope 2 adversarial dimensions, Scope 3 fix-forward + `SECURITY.md` mediator threat-model paragraph, acceptance list; **owner addendum 2026-11-18** (Identus `v1.2.1` only, tag commit `b85da0b`, digest-pinned; re-check wire shapes against scala-did `0.1.0-M48` with I-1..I-15 of `tasks/todo20261118-114952-coordinate-mediation-v2-99.md`; our client negotiates 2.0 with no pin; Blocktrust incompatible, no cell (bsandmann/blocktrust.Mediator#5); Affinidi: check versions and Docker-without-credentials, record, build no leg).
2. **#88 comments** — the acceptance checklist below (E-ids), each with its comment id.
3. `AGENTS.md` §4 (post-sign tampering and self-consistent forged documents); `tasks/lessons.md` (L-005, L-019, L-063, L-064, L-065 + corollary, L-066).
4. The #120 plan (legs, cells, nightly), the #99 plan (interop table I-1..I-15), `SECURITY.md`, `docs/codebase-architecture.md`.

### Acceptance checklist from #88 (verified against the comment text)

| E | Item | Comment |
|---|---|---|
| E-1 | FR-RR-08 replay matrix: window, timestamp-free, concurrent duplicates, saturation, fresh-DID flood | 6783818453 |
| E-2 | No-ack live stream then disconnect; retained bytes bounded throughout | 6783818453 |
| E-3 | Large-payload batch progress | 6783818453 |
| E-4 | `recipient_did` DID-URL rejection | 6783818453 |
| E-5 | DD-13: observe the external mediator's live-mode framing | 6783818453, 6804962656, 6811440268 |
| E-6 | Ack ordering: zero processor/sink invocations until the 202 is committed; abort before commit never relays | 6786453408 |
| E-7 | Queue byte budget under a slow sink | 6786453408 |
| E-8 | id-only/type-only log rendering with a real console provider | 6786453408 |
| E-9 | F15 suite (Mallory can't receive Bob's traffic; replays open nothing; oversized/invalid `return_route_thread`; HTTP body smuggling; fresh-DID floods on both bounded stores; `forward` + `return_route`) | 6804962656 |
| E-10 | Eviction close and its 5 s backstop; push-slot exhaustion; blocked lifecycle subscriber under churn | 6804962656 |
| E-11 | Documented limits W-2, W-3, W-4 (W-1 replaced by the availability price, E-19) | 6804962656, 6852453202 |
| E-12 | Synchronous-rendezvous tests raise the thread-pool floor | 6804962656 |
| E-13 | #92 QA/red-team baseline; throwing cancellation callback (ADV-01); DI cycles; over-cap HTTP response ± Content-Length; stalled body; flood on one session socket next to another; dispose with a consumer ignoring cancellation; clock skew between hosts | 6811440268 |
| E-14 | Observe: pushes from a mediator that doesn't stamp `created_time` are dropped by our client | 6811440268 |
| E-15 | Documented limit: per-pair send gates kept until dispose; memory under churn | 6811440268 |
| E-16 | #93 QA/red-team; faulting identity provider vs own-DID guard; oversized `updates` with poisoned entry; store fill by self-made DIDs (default policy, allow-list); DI mis-ordering; forward admitted before a revoke; lookup timing over the network | 6826025398 |
| E-17 | Documented limits: no DID-ownership proof (FCFS); `client_error` on `add` tells a DID is taken; open enrolment can be filled | 6826025398 |
| E-18 | Observe: `paginate: null` / omitted `body`; DID URL as `recipient_did`; reaction to our 400 and to "no reply" | 6826025398 |
| E-19 | FR-RR-08 additions: clock skew; out-of-order readings; host clock step back > Window + ClockSkew; timestamp-free retention after a step back; availability price documented | 6852453202 |
| E-20 | #94 QA/red-team; forward-progress matrix; connection churn vs slow store; many-key recipient over real endpoints; 5 × 300 KiB; delayed-message quota | 6841076730 |
| E-21 | Documented pickup limits (at-least-once, unacked live fills quota, never-fits, revoked leftovers, lazy expiry, open enrolment vs store-wide bounds, full store observable by self-send) | 6841076730 |
| E-22 | Observe: base64 alphabet/padding of `delivery` attachments; `thid` on 3.0 `status`; clients looping on `message_count`; reaction to "no reply" on a store fault | 6841076730 |
| E-23 | Observe: the external peer's clock offset (we drop pushes from > 60 s ahead) | 6852453202 |
| E-24 | Direct push is out (#110) | 6871566704 |
| E-25 | #95 QA/red-team baseline | 6878358421 |
| E-26 | Two-mediator route through an external mediator in rewrap mode | 6878358421 |
| E-27 | Replay of a captured forward to a live recipient (documented) | 6878358421 |
| E-28 | Captured `live-delivery-change` on a fresh socket takes the victim's live session (documented) | 6878358421 |
| E-29 | Age limit with a long-offline recipient; delay cap and unseen quota occupation | 6878358421 |
| E-30 | Reuse the `mediator-pickup` cell shape against the external mediator | 6878358421 |
| E-31 | Observe: does the external mediator rewrap; `please_ack` / `return_route` on forwards | 6878358421 |
| E-32 | Observe: does the external mediator lease fetched messages (if not, ADV5-4 applies; record against #111) | 6887124928, 6887693439 |
| E-33 | Known limits linking #111: ADV3-6, ADV3-3, ADV3-2, ADV2-2, ADV4-2, ADV4-3, ADV4-4, ADV5-3, ADV5-4 | 6884220396, 6887693439 |
| E-34 | #96 client baseline suites | 6887693439 |
| E-35 | Observe: `thid`, WS `created_time`, Discover Features answered, `to`, `paginate` ignored, `MaxAckIds`, DD-13 | 6887693439 |
| E-36 | The external-mediator wallet references only `DidComm.Mediator.Client` | 6894546318 |
| E-37 | Observe: burst tail; Push-Failed-but-arrived | 6918389034 |
| E-38 | Relay order: one WebSocket vs separate HTTP posts | 6918389034 |
| E-39 | L-065; `caffeinate -dimsu` | 6918389034 |
| E-40 | Python 3.12 stopgap; new python counterparts inside 3.9–3.12 | 6938333269 |
| E-41 | Interpreter binding from the setup step (`PYTHON`), never PATH | 6938333269 |
| E-42 | New legs see every image move first | 6938333269 |
| E-43 | A release run uses the workflow at the released tag | 6938333269 |
| E-44 | Sample 11 / cookbook mediator hosts are reusable | 6966574691 |
| E-45 | Check the shared HTTP circuit breaker | 6966574691 |
| E-46 | Sample 04 relay known limit (L-064) | 6966574691 |
| E-47 | A run with a real forward proxy via `TrustedProxy` on both transports | 6984801690, 6999785468 |
| E-48 | A run with a process proxy set, shown ignored | 6984801690, 6999785468 |
| E-49 | Adversarial coverage of the proxied path | 6984801690, 6999785468 |
| E-50 | Re-check I-1..I-15 against the pinned image (scala-did 0.1.0-M48) | 6999785468, 6998614084 |
| E-51 | Client negotiates 2.0 with Identus without a pin | 6999785468, 6998614084 |
| E-52 | Identus SDK's `keylist-update` without `return_route` | 6999785468 |
| E-53 | Unstamped answers over WebSocket: run HTTP, or record the WS result | 6999785468 |
| E-54 | Silent drops: 202 after 3 s; our client → `TimeoutException` | 6999785468 |
| E-55 | Repeated envelopes refused (dejavu); our client sends a fresh message per request | 6999785468 |
| E-56 | `RequireCreatedTime` trade-off documented, not a gate cell | 6999785468, 6998614084 |
| E-57 | Blocktrust: no cell, never contacted | 6998614084 |
| E-58 | #99 baseline suites | 6999785468 |
| E-59 | Documented limit ADV-1 (FCFS, now reachable from 2.0) | 6999785468 |
| E-60 | Lesson: never assert inside `Record.Exception*` or a `catch` | 6999785468 |
| E-61 | Follow-up candidate: `Message.Validate` echoes wire values (owner decides) | 6999785468 |

## Facts established at the start (2026-11-19)

- **Identus:** `docker.io/hyperledgeridentus/identus-mediator:1.2.1@sha256:0f0378d95c633fb084b9b85a67dd5e48f899ecce6f9b3fbba226f14bfdbf412b` (OCI index from `docker buildx imagetools inspect`; amd64 `sha256:b046dc37…`, arm64 `sha256:ef2f31fe…`, so no emulation on the Apple Silicon Mac; CI x86_64 is the authority). Image labels: `version=1.2.1`, `revision=b85da0bfc24a4aaba57223c98e7b15fbaf866948` (= the `v1.2.1` tag commit, via `gh api …/git/tags/f2ab790c`). Jars in `/opt/docker/lib`: `app.fmgp.did*_3-0.1.0-M48`, `zio-http_3-3.11.1`.
- **MongoDB:** `docker.io/library/mongo:6.0.28-jammy@sha256:a90cef0143bed76540c2b7e6f299a02165e59b07e459b2fd7fef031c5accf5fd` (the `6.0` floating tag resolves to a different index digest; the jammy tag is the one pinned). Identus's `initdb.js` vendored from `v1.2.1`.
- **Probe:** Identus answers `/health` ~4 s after start; `/did` is a `did:peer:2` with two `S` segments (`{"t":"dm","s":{"uri":…,"a":["didcomm/v2"]}}`, http then ws); `/invitation` is an OOB 2.0 invitation with goal code `request-mediate`.
- **WebSocket frame type (verified):** our transports send every DIDComm envelope as a Binary frame (`WebSocketDidCommTransport.cs:537,641`, `WebSocketReturnRouteSession.cs:381`); scala-did M48 `WebsocketJVMImp.scala:21` treats a Binary frame as an error and processes only Text frames. The DIDComm v2.1 transports section does not specify a frame type. So our client cannot talk to Identus over WebSocket at all, before E-53 (unstamped answers) even applies. **Owner decision O-98-1** (below).
- **`messages-received` (verified):** Identus v1.2.1 `PickupExecuter.scala:179-188` applies the acknowledgement and returns `NoReply` (HTTP 202 after 3 s). Pickup 3.0 says the mediator "**SHOULD** send an updated `status` message". Our `PickupClient.AckAsync` (unreleased) requires that status and throws `TimeoutException`. That is our client turning a SHOULD into a MUST (fix toward the spec; see F-1).
- From the harness architect's source read (to confirm live): Identus does not lease and ignores `limit`; every `NoReply` costs 3 s; an unregistered `next` gets HTTP 200 with a signed `e.p.req.not_enroll`; dejavu is keyed on the envelope hash; didcomm-python 0.3.2 does not wrap for a DID-valued service `uri` without `routingKeys`, so cells wrap explicitly (`wrap_in_forward`); scala-did M48 supports only X25519/Ed25519 for did:peer:2.

## Design calls (architect output reconciled; each with a reason)

- **HC-1 Layout.** New `tools/interop-live/mediator/` (MediatorWallet console project, `mediator_peer.py`, `images.env`, `squid/squid.conf`, vendored `identus/initdb.js`, `check_boundary.py`, `render_summary.py`) + `tools/interop-live/run-mediator-leg.sh`; `InteropCli mediator-serve`. Reason: InteropCli references `DidComm.Mediator.AspNetCore`, so E-36 can only be proven from a separate project.
- **HC-2 Cells.** Gating: B-0 boundary, M-1 dotnet recipient via Identus over HTTP (negotiation, enrol, register, python forward, fetch, ack, status 0), M-2 burst of 12, M-3 dotnet sender via Identus to a python recipient, M-4 HTTP via `TrustedProxy`, M-5 WebSocket via `TrustedProxy` (transport-level: the CONNECT tunnel), M-6 process proxy ignored, R-1 python recipient via our mediator (hand-rolled CM 2.0 + pickup 3.0), R-2 the same wallet against our mediator (attribution control). Reason: each proves one claim; R-2 separates "our wallet is broken" from "Identus differs".
- **HC-3 WebSocket against Identus was OBSERVED, not gated,** until O-98-1 was decided (1a, 2026-11-19); it is now gated. Reason: E-53 allows "record the WS result"; a gate that turns green on a broken exchange would read as interop.
- **HC-4 Stretch, cut first if the leg runs long:** M-7 (two-mediator route through Identus, E-26) and its observations O-17/O-18. Reason: Identus is a terminal mediator (it never relays), so the only route is our relay → Identus, and the rewrap variant is expected to be answered `not_enroll`; the local analogue (`MediatorTwoHopTests`, `MediatorRewrapAndRoutingKeysTests`) is the baseline.
- **HC-5 Observations** O-0..O-16, O-21 (architect's table) recorded as data in `mediator-observations.json` and the leg summary, none gating. Reason: they describe a digest-pinned third party; gating them would make the nightly a change detector for Identus, not for us.
- **HC-6 Per-run Identus keys** (never the compose demo keys), unique labelled Docker objects, `trap` cleanup by label, ports picked then retried on clash, tmpfs Mongo storage (no volumes).
- **HC-7 Squid** `ubuntu/squid` pinned by digest (re-verified at implementation), deny-by-default config that allows only the proxy-only Identus instance. Reason: canonical, multi-arch, a real forward proxy; tinyproxy has no official image.
- **HC-8 Same job** via `run-all.sh` (third leg), `timeout-minutes: 60`, `docker version` provenance step, optional `negative_controls` dispatch input; `continue-on-error` stays false (all targets digest-pinned).
- **HC-9 Python:** reuse the pinned didcomm-python 0.3.2 venv; `mediator_peer.py` imports `interop_peer.py` helpers, stdlib only; `PYTHON` required under `GITHUB_ACTIONS`. `run-python-leg.sh`, `run-jvm-leg.sh`, `interop_peer.py`, `requirements.txt` unchanged (byte-identical results).
- **AC-1 Adversarial:** six dimensions, one agent each, ~45 min, per the architect's plan: D1 authorization/identifiers, D2 information leakage (incl. real-Kestrel timing and a real console provider), D3 resource exhaustion, D4 sessions/return route/acknowledgement (real Kestrel), D5 crypto integrity/forged documents/rewrap/replay, D6 transports and the proxied path. Findings go to private scratch files only; new tests under `tests/<Project>/Adv98/D<n>/`; never committed by the agents.
- **AC-2 Release boundary:** "released" = reproduces on `v1.5.1` (`d8a100c`). High/Medium + released → STOP, private (owner's standing choice: private advisory). Public report covers only fixed or unreleased items; withheld items appear only as "N items handled privately".

## Owner decisions raised by this run

- **O-98-1 (WebSocket frame type): decided 1a (owner, 2026-11-19).** DIDComm messages are sent as text frames at every send site (client transport, ASP.NET Core session replies and pushes, mediator live delivery); STOMP unchanged; receive accepts both. The Identus WebSocket cells are gated, with the test wallet setting `RequireCreatedTime = false`; the cookbook and the interop README say what that gives up. **Correction (owner, 2026-11-19):** while building 1a the orchestrator added "bytes that are not valid UTF-8 keep the binary frame" (a relay passes forwarded bytes on unread; RFC 6455 makes a receiver fail the connection on an invalid text frame). That was our implementation choice, not the owner's, and the owner rejected it: the spec defines a forward's `attachments` as "the DIDComm message(s)" (routing.md L57), DIDComm encodings are `+json` (message_structure.md L69), and JSON between systems is UTF-8 (RFC 8259 §8.1), so such a forward is invalid. Rule now: every DIDComm frame is text, unconditionally (STOMP unchanged); a forward whose attachment is not UTF-8 JSON is refused at intake. How the mediator answers that refusal is not settled by the spec (routing.md L25 is a MAY for unsupported headers only): raised to the coordinator as a stop. Lessons L-073, L-074.
- **Upstream reports to Identus: yes (owner, 2026-11-19).** Drafted for approval, not posted: two public issues (one message per `messages-received`; HTTP 500 on a `keylist-update` for a DID another account holds) and four items for private disclosure under Identus's SECURITY.md.
- **Follow-ups filed (owner, 2026-11-19):** #131 (the HTTP transport's circuit breaker is shared by every destination; #88 candidate (b)), #132 (an open breaker reaches callers as `BrokenCircuitException`; fixed with candidate (a)), #133 (failure messages carry the endpoint URI and the peer's reason phrase). Not on #88's list.
- **F-1 (`AckAsync` and a mediator that sends no `status`).** Unreleased client, local, no public API change: after a `messages-received` that gets no answer, ask for the `status` with a `status-request`. Toward the spec (SHOULD). Announced to the coordinator; not a stop unless the coordinator says so.

## A. Harness (packets; contracts frozen first)

- [x] A1 `InteropCli mediator-serve` (ready file, stdin-EOF/SIGTERM shutdown, max lifetime, `--enable-v2`).
- [x] A2 `MediatorWallet` (enrol / fetch / ws-attempt / observe / boundary; JSON result per command; capturing logger; `--trusted-proxy`, `--allow-hosts`, `--expect-env-proxy`), in `DidComm.sln`; `MediatorWalletBoundaryTests`.
- [x] A3 `mediator_peer.py` (okp-keys, did-check, free-port, mint-routed, forward, post, post-many, cm2-enrol, pickup3).
- [x] A4 `run-mediator-leg.sh` + `images.env` + `squid.conf` + `initdb.js` + `check_boundary.py` + `render_summary.py`; `--negative-controls`, `--up`.
- [x] A5 `run-all.sh` third leg; `interop-live.yml` (timeout, docker provenance, dispatch input).
- [x] A6 Local green run (`caffeinate -dimsu`), python/jvm sections byte-identical to the last nightly.
- [x] A7 Live run from the branch (`gh workflow run interop-live.yml --ref feat/mediator-interop-gate-98`), run ids recorded.

## B. Adversarial review (D1–D6) and triage

- [x] B1 D1 · [x] B2 D2 · [x] B3 D3 · [x] B4 D4 · [x] B5 D5 · [x] B6 D6 (each: findings file, coverage table of owned E-ids)
- [x] B7 Triage: severity, released/unreleased, fix here / issue / private / documented limit.
- [x] B8 Fix-forward of local High/Medium in unreleased code, each with a test that fails on origin/main or under a named mutation.

## C. QA on the harness

- [x] C1 Negative control per cell, demonstrated (`--negative-controls` all FAIL as designed).
- [x] C2 Failure modes: image pull failure, mediator not ready, port clash, Docker absent.
- [x] C3 Nightly wiring and summary table checked on the dispatched run.
- [x] C4 L-065 sweep of the harness and new tests (no fixed sleeps).

## D. Docs

- [x] D1 `tools/interop-live/README.md`: mediator leg, results table, how to run locally, pins and provenance, I-1..I-15 live re-check, Affinidi answer.
- [x] D2 `SECURITY.md`: mediator threat-model paragraph (Supported-versions table untouched: separate owner decision).
- [x] D3 CHANGELOG, PRD (Phase 7 exit-criteria status), `docs/codebase-architecture.md`.
- [x] D4 `tasks/lessons.md`: E-60 lesson + anything surprising.

## E. Gate and PR

- [x] E1 Build / test / pack green; live run green on the branch.
- [ ] E2 PR (Closes #98, Part of #88; live-run table; public-safe adversarial summary; fix-forward list); CI green.
- [x] E3 Adversarial report comment on #98 (public-safe).

## Review

### PR

PR #130 (head `b32e8e3` at opening; CI green on ubuntu and windows). Adversarial report comment on #98: 7003391277. Follow-ups filed: #126, #127, #128, #129.

### How it was built (2026-11-19)

- Two architects (harness; adversarial plan), then in parallel: two harness engineers (`wip/h98-net`: `InteropCli mediator-serve`, `MediatorWallet`; `wip/h98-leg`: `mediator_peer.py`, `run-mediator-leg.sh`, pins, squid, workflow), the `AckAsync` engineer (`wip/f98-ack`) and six red-team agents (D1–D6, findings in private scratch files, tests uncommitted). Then fix-forward engineers (`wip/fx98-resp`, `wip/fx98-mem`, `wip/fx98-small`), a test curator (`wip/cur98-tests`, 77 of the red-team tests, 5 runs + 1 loaded run each, no flakes), a docs engineer (`wip/doc98`) and an independent harness QA (no must-fix; 8 should-fix fixed in round 2). All merged into the gate branch with merge commits.
- Design calls changed during the run: WebSocket against Identus was first OBSERVED because Identus reads only text frames; after owner decision O-98-1 = 1a it is gated; F-1 accepted by the coordinator under conditions (fix in `AckAsync`/fetch only, live stream byte-identical, no API change, one timeout budget); FX-2 option 1 (the sink gets an equal `UnpackResult`, rebuilt after the acknowledgement: `IForwardSink`/`ForwardHandler` are unreleased and identity was never a contract), and the recipient-kid list is charged; FX-3 answers `live-delivery-change{true}` on a downgraded session with the `e.m.live-mode-not-supported` report (the reply can't travel either; a flag already on stays on).

### Live runs (branch)

| run | head | result |
|---|---|---|
| 44473219657 | `7000bf4` | green: mediator leg 9/9; python 63/63 and jvm 60/60 (+3 n/a) tables byte-identical to main's last nightly (44298708738) |
| 44473234026 (`negative_controls`) | `7000bf4` | green: 14/14 sabotaged cells FAIL as designed |
| 44484556900 | `b32e8e3` (after the harness QA round) | green: mediator leg 9/9; python/jvm tables byte-identical to 44298708738 |
| 44484568467 (`negative_controls`) | `b32e8e3` | green: 18/18 sabotaged cells failed for their declared reason, 0 broken |

### Interop observations against Identus 1.2.1 (recorded, not gated)

- WebSocket: our frames are binary and Identus processes only text frames (`WS Error (type:Binary)`); its own pushes are one `delivery` per text frame, `thid` = the forward id, `to` = the routed DID, never taken as a push by our client (DD-13: reached by polling).
- No `created_time` on any answer (HTTP or WebSocket), no `from_prior`; `thid` = the request id (problem reports use `pthid`); `Date` header 0 s off.
- `messages-received` gets no answer (202 after 3 s) and removes only ONE message per request whatever the id count (Identus `UserAccountRepo.markAsDelivered` updates one entry), so a 12-message drain takes 13 rounds; no lease (the same ids on every fetch) and `limit` ignored, so ADV5-4 applies (#111).
- Every NoReply costs 3 s; a forward to an unregistered `next` gets HTTP 200 with a signed `e.p.req.not_enroll`; Identus accepts an envelope it cannot decrypt with 202; a repeated envelope gets `e.p.crypto.message.dejavu` (keyed on the envelope hash); Discover Features `match: "*"` gets HTTP 500 (Java regex); a `keylist-update` adding a DID another account holds gets HTTP 500; after a live socket closes with live mode on, some concurrent forwards to that account never get a response.
- Attachments come back JSON-equal to the inner envelope, not byte-equal; no `please_ack`/`return_route` on deliveries; Identus never relays (terminal mediator).
- Affinidi mediator (v0.53.0, 2026-11-15, `affinidi/affinidi-messaging-mediator`): DIDComm v2 trust-ping 2.0, routing 2.0, messagepickup 3.0, discover-features 2.0, report-problem 2.0; **no Coordinate Mediation for DIDComm v2** (a v2 client enrols through Affinidi's own `https://affinidi.com/atm/1.0/authenticate`, which auto-registers the DID; CM 1.0 only for DIDComm v1). Image `ghcr.io/affinidi/messaging-mediator:<version>` pulls anonymously, but needs Redis (or a Fjall build) and an identity made by `mediator-setup`. No leg built (owner instruction).

### Adversarial review (public-safe record; the #98 comment carries the same)

- High: none. Medium: four, all in unreleased code, all fixed here (D3-F3 forward `202` held with no deadline; D3-F2 unread HTTP delivery re-served into more held answers; D3-F4 waiting forwards held 5–45× their charge; the uncharged recipient-kid list, ~9,000×). Low fixed: D4-F1. Interop fix: `AckAsync`/`FetchAsync` without a `status`.
- Docs made precise: D1-F3, D1-F4, D1-F5, D2-F3, D2-F5, D2-F7, D3-F8, D3-F9, D4-F2, D4-F3, D5-F2, D5-F4, D6-F7.
- Tracked: #126 (D1-F1/F2), #127 (D3-F3 residual), #128 (D2-F3/F4), #129 (D6-F3); #116 (E-61, existing).
- Info: D2-F6, D2-F8, D4-F4, D4-F6, D5-F3, D3-F10, D3-F11.
- **12 items are handled privately.**

### Coverage of the #88 checklist

| E | Covered by |
|---|---|
| E-1, E-19 | re-verified `InMemoryReplayGuardTests`, `ReplayClockSkew*` (+ `_purgedAt` mutation); new `D5TimestampFreeForwardJumpTests`; D5-F2 documented |
| E-2, E-3, E-10, E-20, E-29 | re-verified (`MessagePickupWebSocketTests`, `MessagePickupAdversarialResourceTests`, `ReturnRouteAdversarialTests` F15h, `MediatorTimeBoundsQaTests`, `MessagePickupForwardProgressTests`) |
| E-4, E-16, E-17, E-58, E-59 | re-verified with mutations; new `D1RegrantAfterRevokeTests`, `D1NoResolverBeforeCapTests`, `D3StoreFillBySelfMadeDidsTests`; D1-F5/D2-F5 documented; #126 |
| E-5, E-14, E-22, E-23, E-31, E-32, E-35 | live observations (above; `mediator-observations.json`) |
| E-6 | re-verified; new `D4KestrelAcknowledgementTests` (real Kestrel); D4-F3 documented |
| E-7 | re-verified `MediatorBoundsQaTests`; D3-F4 fixed (`ForwardHandlerRetentionTests`) |
| E-8 | new `D2RealConsoleEchoTests` (real JSON console with scopes) |
| E-9, E-11, E-25 | re-verified F15a–g and all `*Qa*`/`*Adv*` suites; W-2/W-3/W-4 documented; D4-F1 fixed |
| E-12, E-39 | `Qa.RaiseThreadPoolFloor` / serial collections in new tests; L-065 audits (curator, harness QA) |
| E-13, E-15 | re-verified `HttpTransportResponseTests`, `ReturnRouteClientRedTeamRegressionTests`, two-host skew suites; E-15 documented |
| E-18, E-37, E-52 | no external client in the harness: E-52 and the spec-example shapes emulated in R-1; E-37 n/a against Identus |
| E-21, E-27, E-28, E-33, E-46, E-56 | documented limits checked; D3-F8, D4-F2, D5-F2, D6-F7 made precise; #111 limits recorded |
| E-24, E-57 | direct push not built; Blocktrust never contacted |
| E-26 | local route re-verified + new `D5RewrapSwapTests`; no external route (Identus never relays) |
| E-30, E-50, E-51, E-55, E-54 | cells M-1/M-2/M-3/R-1; I-1..I-15 table in `tools/interop-live/README.md`; O-12 dejavu; O-9/O-13 silent 202 after 3 s; `AckAsync` fixed |
| E-34 | re-verified forged-answer suites |
| E-36 | cell B-0 + `MediatorWalletBoundaryTests` |
| E-38 | new `D4RelayOrderTests` (WebSocket order); HTTP order documented |
| E-40, E-41, E-42, E-43 | `PYTHON` from setup-python, 3.9–3.12 guard, refused empty under Actions; floating runner kept; README note on release runs |
| E-44 | the sample hosts are internal/context-bound; the tests carry their own Kestrel host |
| E-45, E-49 | checked; shared breaker is #88 candidate (b); #129; local `D6MediatedClientThroughTrustedProxyTests` |
| E-47, E-48 | cells M-4/M-5 (squid as `TrustedProxy`) and M-6 (process proxy ignored); local theory over `HTTP_PROXY`/`ALL_PROXY`/`DefaultProxy` |
| E-53 | O-1 + owner decision O-98-1 |
| E-60 | L-067 |
| E-61 | #116 |

### Review round 1 (PR #130, review of `fec76c3`, comment 7010079291) and the owner decisions of 2026-11-19

| Finding | Fix | Proof |
|---|---|---|
| P1: the 10 s deadline could withdraw a 202 the client already held (a slow completion callback registered after ours); also, without any deadline, a client closing after reading while such a callback ran cancelled the acknowledgement | `ffd566a`: the endpoint completes the 202 (and the mediator's 200) itself; once the server has taken the whole response it is handed over and nothing can withdraw it; the deadline only withdraws a stalled write | `MediatorAcknowledgementDeadlineKestrelTests` (real Kestrel, 3 cases) fail on `7def86e`; `HttpResponseAcknowledgementTests`; 11 mutations caught |
| P2: a late status without `thid` could answer the next `AckAsync` request on a WebSocket | `efde6e6`, `c46e2dc`: after a WebSocket wait is given up, later requests in the call accept only a status whose `thid` names them (spec: a reply that continues a protocol MUST carry `thid`, problems.md L55) | `PickupClientAckThreadingTests` through the real matcher fail on `fec76c3`; live stream byte-identical |
| P2: reusing `MEDIATOR_LEG_WORK` broke on the leftover FIFO / `ready.json` | `8041050`: per-run directory, ready file checked against the server's pid | two runs in one work dir 10/10 each; stale-ready test fails on the old code |

- O-98-1 = 1a: text frames always (`e61343f`, `bc04a9a`, `d517a8b`, `bfd33fe`); Identus WebSocket cells gated (`b3b9078`, M-8; M-5/M-6 gate the WebSocket call); 20/20 negative controls.
- Frame-rule correction: the binary fallback was ours and is removed; the spec-faithful fix is to refuse a forward whose attachment is not UTF-8 JSON at intake. **Open (stop):** the spec doesn't say how a mediator answers an invalid forward; raised to the coordinator with options (recommendation: the existing malformed-forward answer, 202 and Dropped-Malformed).
- `bd86695`: the heap-retention probe is opt-in (it failed once at ×2.01 in a full parallel run: a heap delta counts other tests' allocations).
- O-1b (observed): against Identus our live stream receives its items at the next drain (one `LivePollInterval`), because Identus addresses its live push to the forward's `next`, which our client refuses on the account's socket (`LocalMismatch`); its acknowledgement on leaving waits one `RequestTimeout` for a status Identus never sends. The live stream is unchanged (owner rule).
- Follow-ups filed: #131, #132, #133. Identus upstream reports drafted, not posted.
