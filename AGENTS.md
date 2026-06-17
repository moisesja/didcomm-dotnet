# Agent & Contributor Instructions

This file provides instructions for AI agents and human contributors working in this codebase.

## Project Overview

**didcomm-dotnet** is an open-source .NET 10 library that implements the DIDComm Messaging v2.1 messaging specification in full: the plaintext message model, the three protective envelope formats (signed / anoncrypt / authcrypt) and their legal compositions, DID-based key discovery, mediated routing, transport bindings, and the protocols defined directly in the spec.

## Concept, Requirements and Design

Three top-level docs carry the design context. Read them in this order:

1. [`docs/didcomm-dotnet-prd.md`](docs/didcomm-dotnet-prd.md) — the engineering PRD / requirements spec. The target the implementation is measured against. The PRD aims to fulfill the desired functionality as specified in [`the DIF spec`](https://identity.foundation/didcomm-messaging/spec/v2.1/). If the PRD is incomplete, ask questions. After figuring out the answer, the PRD should be updated.

Maintenance rules:

- `codebase-architecture.md` must stay in sync with the code — when a service or flow changes, update it when changes in the code merit it.
- The PRD is the target. When PRD and codebase-architecture.md disagree, the PRD is the target and codebase-architecture.md is the map of what is actually built — resolve by updating whichever is stale, not by picking a side.
- `README.md` is a thin router — don't add design or runbook content there. It points at the three docs above.

## Workflow Orchestration

### 1. Plan Mode Fault

- Enter plan mode for ANY non-trivial task defined as a task that takes 3 steps or more or that requires architectural decisions.
- If something goes sideways, STOP and re-plan immediately - don't keep pushing
- Use plan mode for verification steps, not just building
- Write detailed specs upfront to reduce ambiguity
- After writing the plan to `tasks/todo{timestamp}.md`, stop and present the plan to the user before editing source, tests, or docs.
- A user instruction to "fix it", "implement it", or "update X to match Y" authorizes the task scope, not the specific implementation plan. Wait for explicit approval of the written plan before making source, test, or PRD edits.

### 2. Subagent Strategy

- Use subagents liberally to keep main context window clean
- Offload research, exploration, and parallel analysis to subagents
- Always use adversarial agents to attempt to exploit the code that is being generated. The adversarial agents must report in detail about any findings
- For complex problems, throw more compute at it via subagents
- One task per subagent for focused execution

### 3. Self-Improvement Loop

- After ANY correction from the user: update `tasks/lessons.md` with the pattern
- Write rules for yourself that prevent the same mistake
- Ruthlessly iterate on these lessons until mistake rate drops
- Review lessons at session start for relevant project

### 4. Verification Before Done

- Never mark a task complete without proving it works
- Diff behavior between main and your changes when relevant
- Ask yourself: "Would a staff engineer approve this,"
- Run tests, check logs, demonstrate correctness
- When validating identity-binding, signature, or integrity fixes, test both post-sign tampering and self-consistent forged documents whose signed identity fields disagree.

### 5. Demand Elegance (Balanced)

- For non-trivial changes: pause and ask "is there a more elegant way?"
- If a fix feels hacky: "Knowing everything I know now, implement the elegant solution"
- Skip this for simple, obvious fixes - don't over-engineer
- Challenge your own work before presenting it

### 6. Autonomous Bug Fixing

- When given a bug report: just fix it. Don't ask for hand-holding
- Point at logs, errors, failing tests - then resolve them
- Zero context switching required from the user
- Go fix failing CI tests without being told how

# Task Management

1. **Plan First**: Write plan to `tasks/todo{timestamp}.md` with checkable items
2. **Verify Plan**: Share the written plan with the user and wait for explicit approval before editing source, tests, or docs
3. **Track Progress**: Mark items complete as you go
4. **Explain Changes**: High-level summary at each step
5. **Document Results**: Add review section`to 'tasks/todo{timestamp}.md`
6. **Capture Lessons**: Update 'tasks/lessons.md' after corrections
7. **Update CHANGELOG.md**: After the successful validation of a task, update the CHANGELOG.md with sufficient details

## Cross-Repo Hygiene

- When multiple repositories are in play, explicitly distinguish between issues in this repository and issues in dependency or upstream repositories before proposing close/reopen actions or attributing where a fix belongs.

## Core Principles

- **Simplicity First**: Make every change as simple as possible. Impact minimal code.
- **No Laziness**: Find root causes. No temporary fixes. Top 1% Software Engineer standards.
- **Minimal Impact**: Changes should only touch what's necessary. Avoid introducing bugs.
