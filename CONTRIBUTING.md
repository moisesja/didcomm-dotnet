# Contributing to didcomm-dotnet

Thank you for your interest in contributing. This document covers how to get set up, how the project is organized, and the conventions used here.

## Before you start

didcomm-dotnet is a **spec-driven** project. The [Product Requirements Document](didcomm-dotnet_PRD.md) is normative — it defines every `MUST`, `SHOULD`, and `MAY` requirement, and traces each one back to the [DIDComm Messaging v2.1](https://identity.foundation/didcomm-messaging/spec/v2.1) section it derives from. Please skim the PRD's Table of Contents before opening a non-trivial PR. In particular:

- **§2 — Scope** tells you what is in and out of scope for v1.0
- **§12 — Phased Implementation Plan** tells you which phase the project is in and what exit criteria gate the next phase
- **§15 — Design Decisions** records every place where the spec was ambiguous and how it was resolved

If you find an apparent contradiction between the PRD and the spec, raise it as an issue rather than guessing — the PRD is meant to be precise, and ambiguities get a `DD-*` decision logged.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- An editor with C# support (Visual Studio, VS Code with C# Dev Kit, JetBrains Rider)

## Getting started

```bash
git clone https://github.com/moisesja/didcomm-dotnet.git
cd didcomm-dotnet

# Once Phase 0 lands:
dotnet restore
dotnet build
dotnet test
```

> **Project status note.** As of this writing the repository contains only the PRD and scaffolding. The first runnable code lands at the end of Phase 0 (see PRD §12). If you're picking up a phase, the **Kickoff prompt** at the end of each phase is the canonical brief.

## Code style

The project uses an [`.editorconfig`](.editorconfig) that enforces:

- **File-scoped namespaces**: `namespace Foo;` not `namespace Foo { }`
- **4-space indentation** (no tabs)
- **LF line endings**
- **UTF-8 encoding** without BOM
- **Expression-bodied members** for simple properties and accessors
- **System directives first** in `using` statements
- **Nullable reference types** enabled everywhere
- **Warnings as errors** in CI

Your editor should pick these up automatically.

## Testing conventions

Tests use **xUnit** with **FluentAssertions** for assertions and **NSubstitute** for mocking, matching the conventions in the sibling [NetDid](https://github.com/moisesja/net-did) project.

### Naming

```
MethodName_Condition_ExpectedResult
```

Examples:

```csharp
PackEncrypted_X25519_ThreeRecipients_DecryptsForEachRecipient()
Unpack_PlaintextWithoutBody_TreatedAsEmptyObject()
Mturi_MissingVersion_Rejected()
```

### Layout

Test file structure mirrors `src/` — e.g., `src/DidComm.Core/Envelopes/AnoncryptPacker.cs` is tested by `tests/DidComm.Core.Tests/Envelopes/AnoncryptPackerTests.cs`.

### Interop fixtures

Cryptographic correctness is gated by the DIDComm v2.1 **Appendix A/B/C** test vectors plus harvested vectors from the reference implementations in Python, JVM, and Rust. These live in a separate [`didcomm-dotnet-fixtures`](https://github.com/moisesja/didcomm-dotnet-fixtures) repository wired in as a git submodule. See PRD §13.

Every new envelope code path **MUST** be covered by at least one fixture before it merges. The data-driven runner in `tests/DidComm.InteropTests` enumerates fixtures as xUnit theories so coverage gaps fail CI loudly.

## Package management

didcomm-dotnet uses **Central Package Management** via [`Directory.Packages.props`](Directory.Packages.props). All NuGet version numbers are declared in that single file.

### Adding a dependency

1. Add the version to `Directory.Packages.props`:
   ```xml
   <PackageVersion Include="My.Package" Version="1.2.3" />
   ```
2. Reference it in the relevant `.csproj` (without a version):
   ```xml
   <PackageReference Include="My.Package" />
   ```

### Updating a dependency

Change the version only in `Directory.Packages.props`. All projects referencing that package pick up the new version.

## Pull request process

1. Fork the repository and create a feature branch from `main`
2. Reference the relevant PRD requirement ID(s) in the PR description (`FR-ENV-04`, `NFR-07`, etc.)
3. Ensure `dotnet build` is warning-free (warnings are errors in CI)
4. Ensure `dotnet test` passes, including the interop suite
5. Add or update tests — production code without tests will not be merged
6. Update the [CHANGELOG.md](CHANGELOG.md) under the `## [Unreleased]` section
7. Open the PR with a clear description of *what* changed and *why*

### Commit messages

Use imperative mood, summary under 72 characters, optional body explaining the *why*:

```
Implement ECDH-1PU key wrap (FR-ENC-15)

Order operations encrypt-then-derive-KEK so the JWE tag participates
in the KDF input, matching the Appendix C.3 authcrypt vectors.
```

## Reporting issues

When filing a bug, please include:

- The PRD requirement ID(s) you believe are involved (if known)
- The fixture file or test vector that fails, if applicable
- Expected vs. actual behavior
- A minimal repro

When filing a feature request, please first check whether the feature is in the PRD's **§2 Scope** table. Out-of-scope features need a design discussion before implementation.

## Security

Do not file security vulnerabilities as public issues. See [SECURITY.md](SECURITY.md) for the responsible-disclosure process.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to uphold its terms.

## License

By contributing, you agree that your contributions will be licensed under the [Apache License 2.0](LICENSE). The Apache 2.0 license includes an explicit grant of patent rights (§3) and an inbound=outbound contribution clause (§5) — please review them.
