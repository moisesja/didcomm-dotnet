# Move releases to NuGet Trusted Publishing, then publish 1.5.1

## Context

The `v1.5.1` Release run failed at the push: `NUGET_API_KEY` has expired (403). Nothing was published;
all six packages are still at 1.5.0, and no GitHub Release exists. The user has decided to replace the
stored key with [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):
the workflow exchanges a GitHub OIDC token for an API key that lasts one hour, so no key is stored and
none can expire unnoticed.

`credentials-dotnet`, `crypto-dotnet` and `dataproofs-dotnet` already publish this way, with
`NuGet/login@v1` and `user: moisesja`. `moisesja` is the nuget.org owner of the `DidComm.*` packages,
and the repo is public.

**This is not a one-line swap, for two reasons.**

### 1. Re-running the failed run cannot work, so `v1.5.1` has to move

A re-run replays the workflow file from the tagged commit. At `d676182`, `release.yml` reads the expired
secret and has no `id-token: write`, so it can never use OIDC. The tag must point at a commit that has
the new workflow.

**Decision: move `v1.5.1` to the new merge commit.** This is safe because nothing was ever published
from it: no package, no GitHub Release, and no tag-protection rules are in the way. RELEASING.md's "never
re-push the same version" is about versions on NuGet, and 1.5.1 is not on NuGet. The alternative, cutting
1.5.2, leaves a dead tag and a hole in the version history for no benefit.

### 2. `id-token: write` should not sit on the job that runs build and test

`id-token: write` is a job-level permission, so every step in the job can mint an OIDC token. Today's
single job restores packages, runs their MSBuild targets, and runs every test. Dropping OIDC into it
would let any of that code request a publishing key. The stored secret reached only the push step, so a
straight swap would widen the exposure. A policy also covers every package its owner has unless a scope
narrows it, so that key could reach beyond `DidComm.*`.

**Decision: split `release.yml` into two jobs**, as `crypto-dotnet` does:

- `pack` builds, tests, packs and uploads the packages. It has no OIDC permission and no environment.
- `publish` needs `pack`, declares `environment: nuget-release`, and alone has `id-token: write`. It does
  not check out the repo, so it runs none of our code or our dependencies' code.

This also fixes the gate's position. Approval currently happens before anything is built. After the split
the approver is asked only once build and test have passed. And a failed push can be re-run on its own,
with the same packages and no rebuild.

## Policy (user, on nuget.org)

Username → **Trusted Publishing** → add a policy. Values are case-insensitive.

| Field | Value |
|---|---|
| Policy owner | `moisesja` |
| Repository Owner | `moisesja` |
| Repository | `didcomm-dotnet` |
| Workflow File | `release.yml` (file name only, no path) |
| Environment | `nuget-release` |
| Scope, if offered | new versions of existing packages, glob `DidComm.*` |

The Environment value matters: it means a token is honored only from the environment that requires your
approval. The docs say a policy can start as "temporarily active" for 7 days, usually on private repos.
Either state works, and a successful publish makes it permanent.

## Plan

- [x] 1. Branch `chore/trusted-publishing` off `main`.
- [x] 2. **`.github/workflows/release.yml`:** split as below, and replace the `NUGET_API_KEY` header
      comments. Pack steps are unchanged.

      ```yaml
      publish:
        name: publish to NuGet
        needs: pack
        runs-on: ubuntu-latest
        environment: nuget-release
        permissions:
          id-token: write # the only job that can request a NuGet key
        steps:
          - name: Setup .NET
            uses: actions/setup-dotnet@v4
            with:
              dotnet-version: 10.0.x

          - name: Download packages
            uses: actions/download-artifact@v4
            with:
              name: nupkgs
              path: artifacts

          - name: NuGet login (OIDC → short-lived API key)
            id: nuget-login
            uses: NuGet/login@v1
            with:
              user: moisesja # nuget.org profile name, not an email

          - name: Push to NuGet.org
            run: dotnet nuget push "artifacts/*.nupkg" --api-key "${{ steps.nuget-login.outputs.NUGET_API_KEY }}" --source https://api.nuget.org/v3/index.json --skip-duplicate
      ```

- [x] 3. **`RELEASING.md`:** replace "Create the NuGet API key" and "Add the `NUGET_API_KEY` secret" with
      the policy above, and state that no secret is needed. Update step 4 of "Cutting a release": the run
      builds and tests first, then pauses for approval.
- [x] 4. **`CONTRIBUTING.md:121`:** replace "the one-time `NUGET_API_KEY` secret" with the Trusted
      Publishing policy.
- [x] 5. **`CHANGELOG.md`, `[1.5.1]`:** note that releases now publish through Trusted Publishing, with no
      effect on the packages. Set the section date to the real publish date.
- [ ] 6. Open the PR. Wait for CI. **You merge.** `actionlint` did not run: the permission check blocked
      downloading its binary. The workflow was checked instead with a local YAML parse and structural
      assertions, and the `pack` steps were confirmed byte-identical to the version that passed on the tag.
- [ ] 7. After CI is green on the merge commit, move the tag: delete `v1.5.1` from the remote, re-create
      it on the merge commit with the same message, and push.
- [ ] 8. `pack` runs on its own, then `publish` pauses. **You approve the deployment.**
- [ ] 9. Run steps 8 and 9 of [the release plan](todo20261102-release-v1.5.1.md): verify the six packages,
      smoke-restore, create the GitHub Release, and wait for `interop-live`.
- [ ] 10. Delete the `NUGET_API_KEY` repo secret, only after a successful publish, so the old path stays
      recoverable until the new one is proven. The expired key can also be deleted on nuget.org.

## Verification limits

**The OIDC exchange cannot be tested without a real tag.** PR CI does not run `release.yml`, and the
policy is only exercised at step 8.

If it fails, it fails safely. `NuGet/login` runs before the push, so a policy mismatch stops the job with
nothing published. Fix the policy, then use **Re-run failed jobs**. That repeats only `publish`, with the
same packages, and needs no new tag.

## Deliberately not changing

- **The `nuget-release` reviewer gate stays.** OIDC replaces the credential, not the human approval.
- **`NuGet/login@v1` stays on a tag,** matching the docs, the sibling repos and this repo's action pins.

## Cross-repo note (not this repo)

`dataproofs-dotnet` and `credentials-dotnet` run restore, build and test in the same job that holds
`id-token: write`, so reason 2 applies to them. `crypto-dotnet` splits the job but declares no
environment. Any fix belongs in those repos.

## Review

_(filled in after the release)_
