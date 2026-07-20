# PR #51 review round 3 — decisive redesign (stop whack-a-mole)

Every finding traces to two ADDED convenience features. Remove the surface, don't guard it.

## Root-cause moves

### A. Remove `AutoSendReplies` entirely (findings 1 + 3)
Inbound-triggered inline auto-egress is inherently a reflector/oracle/DoS surface:
- cross-tenant: reply.From = handler-echoed inner `from`, not bound to the decrypting recipient kid;
- attacker-advertised endpoint (DID-doc service) → reflection to any victim URL + shared circuit-breaker DoS + inline-await request pinning.
Making it safe needs egress-authz policy + recipient-kid binding + destination-partitioned resilience + isolated queue — huge machinery for a convenience. Remove:
- [ ] delete `DidCommReceiveOptions.AutoSendReplies` + the endpoint auto-send call + `TrySendReplyOutOfBandAsync`.
- [ ] delete `AutoSendRepliesSecurityTests` (behavior gone).
- [ ] round-trip test: Bob delivers the disclose via the **onReceive callback** (app code), binding reply recipient = authenticated inbound sender, From = the identity that decrypted. Models correct, safe app delivery; still no manual injection into Alice's dispatcher.
- [ ] docs: reply delivery is the responder app's job (SendAsync), out-of-band per FR-TRN-10.

### B. Correlation off the lossy queue → internal inline correlator (finding 2 root)
- [ ] new internal `IInboundCorrelator { void OnInbound(UnpackResult) }` — fast, non-blocking, guarded.
- [ ] `DiscoverFeaturesClient` implements it instead of `IProtocolObserver`; correlation body becomes synchronous `OnInbound` (dict lookup + auth/subject checks + `TrySetResult`). O(1), no queue, no drops → immune to flood-crowding; runs inline (guarded) so a flood is O(1)/msg with no memory.
- [ ] dispatcher invokes correlators inline (guarded) after the outcome is computed; can't gate (void/sync) or clobber (guarded).
- [ ] DI registers `DiscoverFeaturesClient` as the correlator, NOT an observer. ⇒ `AddBuiltInProtocols` registers **zero** default observers ⇒ no default firehose/flood/memory surface.

### C. Harden the now opt-in-only observer queue (findings 2 residual, 4, 5)
- [ ] **Snapshot at enqueue** (finding 4): clone into `InboundObservation` synchronously at enqueue; queue the immutable snapshot, not the live `UnpackResult`. A handler/caller mutating the live message after dispatch can't change what an observer sees.
- [ ] **Byte-aware bound** (finding 2): per-observer byte budget (default small, e.g. 4 MiB) using the clone's JSON length; drop when exceeded. Small capacity too.
- [ ] **Rate-limited drop logging** (finding 2): first drop + periodic summary, not one log per drop.
- [ ] **Real shutdown** (finding 5): pumps take a shutdown `CancellationToken` cancelled by Dispose/DisposeAsync; `FlushAsync` honors its timeout for the whole op (bounded WriteAsync).

### D. Smaller items
- [ ] JWS tamper test: corrupt the **signature** (or interior payload byte) and assert the specific signature-verification failure, not "any exception."
- [ ] remove the DID-URL comment claiming a bare-DID round-trip covers DID-URL equivalence.
- [ ] `LoopbackTransport` comment: HTTP only (no WS receive pump).
- [ ] dispose apps/dispatchers/providers in new tests; no leaked 60s pending queries.

### E. Adversarial verification (BEFORE committing)
- [ ] subagent(s) attack the new design: inline correlator (gate/clobber/flood), the opt-in queue (memory/snapshot/shutdown), and confirm no default egress/observer surface remains.

## Verify
- [ ] Release build 0/0; full Core+Interop 3x; pack+ApiCompat; CI green both OS.

## Review
_(after implementation + adversarial pass)_
