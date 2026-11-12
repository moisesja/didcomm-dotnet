# Releasing didcomm-dotnet

This is the maintainer runbook for publishing the `DidComm.*` packages to
[NuGet.org](https://www.nuget.org/). Releases are **tag-driven**: pushing a
`vMAJOR.MINOR.PATCH` tag runs [`.github/workflows/release.yml`](.github/workflows/release.yml),
which builds, tests, packs, and pushes every package. Publishing to NuGet is
**irreversible** (packages can be unlisted but not deleted), so the workflow is
gated behind a reviewer-approved environment — see step 2 below.

> **Status:** Published releases to date — `0.1.0-preview.1`, `1.0.0`, `1.1.0`, `1.2.0`,
> `1.3.0`, `1.4.0`, `1.4.1`, `1.5.0`. The one-time setup below is done (the `nuget-release` reviewer
> gate was added 2026-10-29, before 1.5.0, and Trusted Publishing replaced the stored API key on
> 2026-11-02, before 1.5.1); cutting a release starts at [Cutting a release](#cutting-a-release).

---

## What gets published

A `v*` tag publishes all six packages plus their `.snupkg` symbol packages
(SourceLink + deterministic build are already configured in
[`Directory.Build.props`](Directory.Build.props)):

| Package | Contents |
|---|---|
| `DidComm.Core` | Message model, JWE/JWS envelopes, pack/unpack, routing, rotation, threading, and the built-in protocols (Trust Ping, Discover Features, Empty, Report Problem, Trace, Out-of-Band) |
| `DidComm.Extensions.DependencyInjection` | `AddDidComm(...)` wiring with net-did resolution |
| `DidComm.AspNetCore` | `MapDidCommEndpoint` / `MapDidCommWebSocket` / `MapDidCommOobEndpoint` receive endpoints |
| `DidComm.Transports.Http` | HTTPS sender transport binding |
| `DidComm.Transports.WebSocket` | WebSocket sender transport binding |
| `DidComm.Adapters.NetDid` | Optional bridge from a NetDid key store to `ISecretsResolver` |
| `DidComm.Mediator` | Mediator service: Coordinate Mediation 3.0 and Message Pickup 3.0 (mediator role), store and policy contracts |

Tests and samples set `IsPackable=false`, so they are never published.

---

## One-time setup

Do this once per repository (you need **admin** on `moisesja/didcomm-dotnet`).

### 1. Create the Trusted Publishing policy

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
(OIDC), so no API key is stored and none can expire. The `publish` job exchanges a GitHub OIDC token
for an API key that lasts one hour.

On nuget.org, click your username → **Trusted Publishing** and add a policy (case-insensitive):

| Field | Value |
|---|---|
| Policy owner | `moisesja` |
| Repository Owner | `moisesja` |
| Repository | `didcomm-dotnet` |
| Workflow File | `release.yml` (file name only, no path) |
| Environment | `nuget-release` |
| Scope, if offered | new versions of existing packages, glob `DidComm.*` |

Keep the environment set: it means a token is honored only from the environment that requires
approval. A policy covers every package its owner has unless a scope narrows it.

> A policy can start as *temporarily active* for 7 days, usually on private repositories. The first
> successful publish makes it permanent.

### 2. Create the `nuget-release` GitHub Environment

The publish job declares `environment: nuget-release`, so the push **waits for
approval** once that environment has protection rules.

1. Repo **Settings → Environments → New environment** → name it **`nuget-release`**.
2. Add **Required reviewers** (yourself / the release approvers). This is the gate
   that pauses each tag's run until a human approves the irreversible push.
3. *(Optional)* Under **Deployment branches and tags**, restrict to the `v*` tag
   pattern so the environment can only be used by release tags.

No secret is needed.

---

## Versioning policy

- **SemVer.** Pre-1.0 (`0.y.z`) already signals the public API may change between
  minors; `1.0.0` is reserved for the spec-complete release (see the README).
- **Tag-driven.** The package version comes from the **git tag**, not a file: a tag
  `vX.Y.Z` is stripped of its leading `v` and passed as `-p:DidCommVersion=X.Y.Z` to
  build/pack. The `<DidCommVersion>` in
  [`Directory.Build.props`](Directory.Build.props) is only the **local dev default**
  for non-tagged builds — you do not edit it to release.
- **Prereleases.** A SemVer prerelease tag (e.g. `v0.1.0-preview.1`, `v0.2.0-rc.1`)
  publishes a NuGet prerelease; consumers must opt in with `--prerelease`.

---

## Cutting a release

1. **Green `main`.** Ensure CI is green on the commit you intend to tag.
2. **Update the CHANGELOG.** In [`CHANGELOG.md`](CHANGELOG.md):
   - Rename `## [Unreleased]` → `## [0.1.0-preview.1] - YYYY-MM-DD` (the release date).
   - Add a fresh empty `## [Unreleased]` above it.
   - Update the link footer: add
     `[0.1.0-preview.1]: https://github.com/moisesja/didcomm-dotnet/releases/tag/v0.1.0-preview.1`
     and point `[Unreleased]` at
     `.../compare/v0.1.0-preview.1...HEAD`.
   - Land this through a normal PR (never commit to `main` directly).
3. **Tag the merge commit** on `main` and push the tag:
   ```bash
   git checkout main && git pull --ff-only
   git tag v0.1.0-preview.1
   git push origin v0.1.0-preview.1
   ```
4. **Approve the deployment.** The tag triggers the **Release** workflow. Its `pack` job
   builds → tests → `dotnet pack`, then `publish` pauses on the `nuget-release` environment.
   Open the run (Actions tab or `gh run watch`) and **approve** it. `publish` then logs in
   through OIDC and runs `dotnet nuget push` (the `.snupkg` symbols ride along automatically;
   `--skip-duplicate` makes re-runs safe). If `publish` fails, **Re-run failed jobs** repeats
   it with the same packages. A failed login publishes nothing.

---

## Post-release verification

- The six packages appear at `https://www.nuget.org/packages/DidComm.Core` etc.
  (prerelease versions are hidden unless "Include prerelease" is checked).
- A scratch project can restore them:
  ```bash
  dotnet new classlib -o /tmp/oob-smoke && cd /tmp/oob-smoke
  dotnet add package DidComm.Core --prerelease
  ```
- Symbols resolve (step into library code with SourceLink in a debugger).
- Create a GitHub Release from the tag with the CHANGELOG section as the notes.

---

## Rollback / fixing a bad release

NuGet packages **cannot be deleted** once pushed. If a release is broken:

- **Unlist** the version on nuget.org (hides it from search/restore; existing
  pins still resolve) and/or mark it **deprecated** with a message.
- Ship the fix as a new patch/prerelease (e.g. `v0.1.0-preview.2`) — never re-push the
  same version.
