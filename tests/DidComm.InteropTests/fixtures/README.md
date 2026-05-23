# Interop fixtures (Phase 0 staging)

This directory is **temporary scaffolding**. Before Phase 2 closes (the
[FR-IX-01](../../../docs/didcomm-dotnet_PRD.md) gate) it migrates to a standalone,
versioned `didcomm-dotnet-fixtures` repository consumed via git submodule, per
PRD §13.3. The directory layout matches the destination so the migration is a
`git rm -r` + `git submodule add` + a path update in the csproj.

## Layout (per PRD §13.3)

```
fixtures/
├─ schema/
│  └─ didcomm-fixture.v1.schema.json    JSON Schema for the manifest format (PRD §13.4)
├─ manifest/                            one JSON manifest per fixture
│  ├─ spec/                             from spec Appendix A/B/C (vendored in Phase 2)
│  ├─ didcomm-rust/                     harvested from sicpa-dlab/didcomm-rust
│  ├─ didcomm-python/
│  ├─ didcomm-jvm/
│  └─ authored/                         hand-authored edge cases
├─ secrets/                             keysets (Appendix A; per-source secret sets) — JWK
├─ diddocs/                             DID Documents (Appendix B; per-source) — JSON
├─ payloads/                            canonical plaintext messages (Appendix C.1, etc.)
└─ packed/                              packed envelopes referenced by fixtures
```

## Phase 0 contents

- `schema/didcomm-fixture.v1.schema.json` — the full v1 manifest schema.
- `manifest/spec/_smoke.json` — a single `operation: "noop"` manifest that proves
  the data-driven runner emits one xUnit theory case per file. No crypto is
  exercised.
- `payloads/_smoke-plaintext.json` — referenced by the smoke manifest.

The empty `secrets/`, `diddocs/`, and `packed/` directories are created on demand
when real fixtures land.

## Adding fixtures

Phase 2 begins vendoring real fixtures. Each manifest is one JSON file conforming
to `schema/didcomm-fixture.v1.schema.json`. The `DidComm.InteropTests` runner
picks up new manifests with no code changes (FR-IX-02 — data-driven).
