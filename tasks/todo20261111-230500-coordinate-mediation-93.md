# todo20261111-230500 — `DidComm.Mediator`: Coordinate Mediation 3.0, mediator role (#93)

Issue: #93 (epic #88; builds on #89 PR #101, #90 PR #102, #91 PR #103, #92 PR #104 merge `5f52033`). Branch: `feat/coordinate-mediation-93` (L-019: re-verify before every commit).
Normative source: https://didcomm.org/coordinate-mediation/3.0/ (Production). PRD rows (the target): §8b FR-MED-01..06 and -08 (#93's rows); FR-MED-07 startup validation and FR-MED-09/10 are #95/#96/#99's. D1–D7 unchanged.
Binding refinements: the "#93" paragraphs of the four "Planning update" comments on #88 override the issue body where they differ. In short: `recipient_did` is a bare DID (a DID URL is `client_error`, never normalized) and the registry is keyed by DID subject; a request without an authcrypt layer is neither applied nor answered (logged, id only); a second `mediate-request` is idempotent; `thid` = request `thid`, else `id` (DD-11); `body: {}` is emitted on `mediate-request` / `mediate-deny` and an absent body is tolerated; protocol-required echoes are allowed after the FR-API-09 cap check; the grant store feeds `IForwardRecipientRegistry`; handlers read `context.Session` and never re-check replay; `SessionOutbound` stamps `created_time` on replies.
The owner delegated this run to proceed without a plan-approval stop; the PR review is the checkpoint.

Build/verify (an item is done only when all three are green):
`dotnet build DidComm.sln -c Release /warnaserror` · `dotnet test DidComm.sln -c Release --no-build` · `dotnet pack DidComm.sln -c Release --no-build`.

## Files this change may touch

- NEW project `src/DidComm.Mediator/` (`DidComm.Mediator.csproj`, `EnablePackageValidation=false` until its first release; references `DidComm.Core` and `DidComm.Extensions.DependencyInjection`; no ASP.NET Core). Namespace `DidComm.Mediator.Coordination`:
  `CoordinateMediation.cs`, `MediationBodies.cs` (internal), `RecipientUpdate.cs` (records + enums), `MediationGrant.cs`, `MediationCoordinator.cs` (internal), `IMediationStore.cs`, `InMemoryMediationStore.cs`, `IMediationPolicy.cs`, `DefaultMediationPolicy.cs`, `IMediatorIdentity.cs`, `CoordinateMediationOptions.cs`, `CoordinateMediationHandler.cs`, `MediationForwardRecipientRegistry.cs` (internal), `CoordinateMediationBuilderExtensions.cs`.
- EDIT `src/DidComm.Core/DidComm.Core.csproj`: `InternalsVisibleTo DidComm.Mediator` (for `DidSubject`, `IdentifierLimits`, `ReturnRouteEligibility`; same pattern as AspNetCore / DI).
- EDIT `DidComm.sln`; EDIT `tests/DidComm.InteropTests/DidComm.InteropTests.csproj` (+ reference) and `DxCoverage/CoverageComputation.cs` (shipped list) and the two allowlists (`TODO(#97)` entries).
- NEW test project `tests/DidComm.Mediator.Tests/` with `fixtures/coordinate-mediation-3.0/*.json`.
- Docs: `CHANGELOG.md`, `docs/codebase-architecture.md`, `docs/didcomm-dotnet_PRD.md` (FR-MED rows as listed in H), `README.md` / `RELEASING.md` package tables (one row each), this file.
- NEW `tests/DidComm.InteropTests/AspNetCore/CoordinateMediationHttpTests.cs` (FR-MED-03 over the TestServer).
- NOT touched: `ForwardHandler`, `ProtocolDispatcher`, `DidCommBuilder`, transports, AspNetCore, samples (sample 04/11 are #97's).

## Public surface (L-006: every referenced type is already public)

- `CoordinateMediation` (static): `ProtocolUri`, seven MTURI constants, `MediatorRole` / `RecipientRole`; `CreateMediateRequest(from, mediator)`, `CreateGrant(from, to, routingDids, thid)`, `CreateDeny(from, to, thid)`, `CreateRecipientUpdate(from, mediator, updates)`, `CreateRecipientUpdateResponse(from, to, updated, thid)`, `CreateRecipientQuery(from, mediator, paginate?)`, `CreateRecipient(from, to, dids, pagination?, thid)` — each with a trailing optional `IMessageIdGenerator?` (adding it later would be binary-breaking, L-035); `ResponseThreadId(request)`; `TryParseMediateRequest`, `TryParseGrant`, `TryParseDeny`, `TryParseRecipientUpdate`, `TryParseRecipientUpdateResponse`, `TryParseRecipientQuery`, `TryParseRecipient`. A parser returns `false` for another message type and throws `MalformedMessageException` (no wire value quoted) for the right type with a bad shape, like `ForwardMessage.TryParse`. Types match as the registry matches them (FR-PROTO-01/02: case and `-`/`_` insensitive, any 3.x).
- `RecipientUpdate(RecipientDid, Action)`, `RecipientUpdateAction { Add, Remove }`, `RecipientUpdateResult(RecipientDid, Action, Result)`, `RecipientUpdateOutcome { ClientError, ServerError, NoChange, Success }`, `RecipientQueryPaging(Limit, Offset)`, `RecipientPagination(Count, Offset, Remaining)`.
- `MediationGrant(PeerDid, RoutingDids)`, `RecipientPage(Dids, Total)`, `RecipientStoreOutcome { Added, Removed, AlreadyRegistered, OwnedByAnotherGrant, NotRegistered, GrantLimitReached, StoreLimitReached, NoGrant }`.
- `IMediationStore`: `GetGrantAsync(peerDid)`, `GrantAsync(grant)` (get-or-add; `null` at capacity), `RevokeAsync(peerDid)`, `UpdateRecipientsAsync(peerDid, updates)` → one `RecipientStoreOutcome` per update, `ListRecipientsAsync(peerDid, offset, limit)`, `FindGrantByRecipientDidAsync(recipientDid)`. `InMemoryMediationStore` (+ `GrantCount`, `RecipientCount`).
- `IMediationPolicy` (`OnMediateRequestAsync(peerDid, context, ct)`, `MayRegisterAsync(peerDid, recipientDid, context, ct)`), `MediationDecision { Deny = 0, Grant = 1 }`, `DefaultMediationPolicy`.
- `IMediatorIdentity { IReadOnlyList<string> RoutingDids }`.
- `CoordinateMediationOptions`: `RoutingDids`, `MaxPageSize` (100), `MaxUpdatesPerMessage` (1024), `MaxGrants` (10 000), `MaxRecipientsPerGrant` (256), `MaxRecipients` (100 000), `UseAsForwardRecipientRegistry` (true, read at registration), `Validate()`.
- `CoordinateMediationHandler : IProtocolHandler, IProtocolHandlerRoles`.
- `CoordinateMediationBuilderExtensions.AddCoordinateMediation(this DidCommBuilder, Action<CoordinateMediationOptions>? = null)`.
- Internal: `MediationBodies` (version-neutral body codecs) and `MediationCoordinator` (version-neutral engine), so the 2.0 handler (#99) reuses both; `MediationForwardRecipientRegistry`; `BareDid`.

## Threat model (L-031)

- **Who talks to the handler.** Anyone who can reach the endpoint. Only an authcrypt sender is a peer: the peer is `DidSubjectOf(SenderKid)` through `ReturnRouteEligibility.TryGetPeer`, the same rule that opens a return-route session (L-041), never the plaintext `from`. Plaintext, anoncrypt, signed-only and `anoncrypt(sign)` requests are dropped before the body is looked at: nothing granted, nothing applied, no reply, one rate-limited log line with the id.
- **What a peer can do.** Get one grant; register up to `MaxRecipientsPerGrant` DIDs nobody else holds; remove its own; list its own. It cannot touch another grant: ownership is decided inside the store under one lock (atomic check-and-insert), not in the policy, so a custom policy cannot weaken D5 and there is no check-then-act gap.
- **Squatting.** The protocol has no proof of DID ownership (its endnote says so). First registration wins; `IMediationPolicy.MayRegisterAsync` is the seam for a host that wants proof. Accepted and documented (DD-14).
- **Oracle.** `client_error` on `add` tells an enrolled peer that a DID is taken (accepted, FR-MED-05). `remove` gives the same `client_error` for "someone else's" and "nobody's". Unauthenticated callers learn nothing: same drop for every shape.
- **Mediator's own DIDs.** A peer may not register the DID that decrypted the request, a routing DID or one of `OwnIdentifiers` as a recipient (`client_error`). These values are public, so nothing secret hangs on the comparison; it still walks the whole list (the D7 habit).
- **Self-made DIDs (accepted limit, like W-2).** The default policy grants any authcrypt sender and grants never expire. Fresh DIDs can fill `MaxGrants` (new peers are then denied) and `MaxRecipients` (new registrations then get `server_error`); existing grants and registrations keep working. At the defaults the store can pin about 110 000 keys of up to 2048 UTF-16 units, roughly 450 MB in the worst case. A public mediator needs an allow-list or rate limit in its policy, and admission control in front of the endpoint (#95).
- **Work per message.** `updates` is capped (`MaxUpdatesPerMessage`, default 1024) before any entry is parsed as a DID; a peer without a grant costs one store read, no DID parse and no policy call.
- **Forward lookup.** `FindGrantByRecipientDidAsync` is one lock-free probe of a concurrent index that returns the entry's grant; no second lookup for a hit (FR-ROUTE-11 / FR-MED-08).
- **Logs and errors.** The handler's own lines carry a message id (replaced when longer than 128 characters) and exception *type* names only: no DID, kid or body value at any level. `MalformedMessageException` leaves the handler, and the endpoints log exception objects (pre-existing follow-up from #91), so its text must be value-free by construction: it names the member and the rule only. Tested per rejection.
- **Faults.** A throwing store or policy never escapes dispatch: `recipient-update` answers `server_error` for the affected entries; `mediate-request` / `recipient-query` have no error shape in the protocol, so nothing is granted and no reply is produced.

## Judgement calls (architect review applied; each has a reason)

- JC-1. Ownership lives in the store, the policy is an extra gate. The issue drafted the default `MayRegisterAsync` as "not owned by another grant"; a check in the policy followed by a store write is a TOCTOU, so the store enforces it atomically and the default policy allows. The store returns store-level outcomes (`RecipientStoreOutcome`); the handler maps them to the wire `result`, so a custom store never encodes protocol vocabulary.
- JC-2. A peer that already holds a grant gets it again without the policy being asked (FR-MED-05: idempotent, same `routing_did`, registrations retained). The policy decides new grants only; a host ends a grant with `RevokeAsync`. (First draft asked the policy every time; the architect review pointed out that this contradicts FR-MED-05 and breaks retries under a one-time or rate-limited policy.)
- JC-3. Store at `MaxGrants` → `mediate-deny` (the mediator is unwilling right now). Store, policy or identity fault on `mediate-request` / `recipient-query` → no reply, logged. FR-MED-05 said "`mediate-deny` is produced only by policy"; the PRD row is amended (H3).
- JC-4. `recipient-update` / `recipient-query` from an authcrypt peer without a grant → all `client_error` / an empty page. "No grant" is decided inside the store call, atomically with `RevokeAsync`.
- JC-5. Total-recipient bound exhausted → `server_error` (the mediator's limit, not the peer's); per-grant bound → `client_error` (PRD).
- JC-6. `paginate.offset` negative → malformed; `limit` ≤ 0 → empty page (PRD); integers beyond `int` are clamped; `10.0` reads as 10 (JSON has one number type), `1.5` and `"10"` are malformed.
- JC-7. No configured routing DID → the grant carries the DID that decrypted the request (the DID the recipient already addressed). Fine for handler-only use; `AddMediator()` (#95) must always supply the identity, because the peer otherwise picks which held DID becomes its `routing_did` and nothing validates it against FR-MED-07.
- JC-8. `AddCoordinateMediation` registers the store as the forward recipient registry by implementation type. A second call recognises its own registration and does nothing; a foreign registration already present throws (it must run before `AddRoutingMediator`); `UseAsForwardRecipientRegistry = false` opts out. Options go through `AddOptions().Configure`, so repeated calls compose; the delegate also runs once on a scratch instance so bad settings fail at registration.
- JC-9. Malformed bodies from an authcrypt peer throw `MalformedMessageException` through dispatch (the endpoint's existing 400 path), as FR-MED-08 states. Unknown message types under the PIURI and the response types (`mediate-grant`, …) are ignored by the mediator role.
- JC-10. Unknown members inside `body` and inside entries are ignored (forward compatibility); a wrong JSON type for a known member is malformed.
- JC-11. An entry whose `recipient_did` is within the cap but not a bare DID (a DID URL, or not a DID) is answered `client_error` with the value echoed as the protocol requires. Bare means `DidSubjectOf(v)` ordinal-equals `v` and `v.Length` ≤ 2048.
- JC-12. More than `MaxUpdatesPerMessage` entries → `MalformedMessageException` (not a reply of that many `client_error` echoes).

## A. Package plumbing
- [ ] A1. `src/DidComm.Mediator/DidComm.Mediator.csproj`, sln entry under `src`, Core `InternalsVisibleTo`.
- [ ] A2. `tests/DidComm.Mediator.Tests` project, sln entry under `tests`.
- [ ] A3. InteropTests reference + `ShippedAssemblies` + allowlists (`TODO(#97)`).

## B. Messages
- [ ] B1. Constants, records, enums.
- [ ] B2. `Create*` helpers (`body: {}` on request/deny; `routing_did` always an array).
- [ ] B3. Strict parsers (FR-API-09 cap before anything else; string-or-array `routing_did`).

## C. Store, policy, identity
- [ ] C1. `IMediationStore`, `MediationGrant`, `RecipientPage`.
- [ ] C2. `InMemoryMediationStore` (one lock; owner index → grant entry; insertion-ordered recipients; three bounds).
- [ ] C3. `IMediationPolicy`, `MediationDecision`, `DefaultMediationPolicy`; `IMediatorIdentity`.

## D. Handler and DI
- [ ] D1. `CoordinateMediationHandler` (authcrypt gate → parse → policy/store → reply; DD-11 threading; faults contained; safe logging).
- [ ] D2. `CoordinateMediationOptions` + `Validate()`.
- [ ] D3. `AddCoordinateMediation` (`TryAdd` store/policy/identity; forward registry adapter; `AddProtocol`).

## E. Tests (new `tests/DidComm.Mediator.Tests`)
- [ ] E1. Fixtures from the didcomm.org page, normalized to valid JSON; byte-exact round trips.
- [ ] E2. Parser rejection table; `routing_did` string accepted, array emitted; over-cap without echo.
- [ ] E3. Store: ownership, bounds, paging, revoke, concurrency, constant-work lookup timing.
- [ ] E4. Handler: every result value, input order, pagination edges, DD-11, unauthenticated drop matrix, faults.
- [ ] E5. Dispatch through DI: request → grant → update → query; discover-features roles; forward registry integration with `AddRoutingMediator`.

## F. QA pass (separate agents, all permutations) — findings fixed or recorded
## G. Adversarial pass (separate agents) — findings fixed or recorded

## H. Docs
- [ ] H1. CHANGELOG `[Unreleased]`.
- [ ] H2. `docs/codebase-architecture.md`: new "Coordinate Mediation 3.0 (Phase 7, #93)" section + package layout.
- [ ] H3. PRD: FR-MED-05 (JC-1..4, own-DID refusal), FR-MED-06 (JC-6), FR-MED-08 (JC-5, total bound), §3.1 if the package row needs it.
- [ ] H4. README / RELEASING package tables.
- [ ] H5. Review section in this file.
