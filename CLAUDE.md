# Agent Behaviour

## Security Rules (non-negotiable)

1. **Never hard-code credentials.** Credentials come from the Windows login (current user, no password) or a masked prompt at run time; they are never stored. No literals in source files, no `.env` files committed to version control.

2. **Never log or print secrets.** Do not write credentials, session tokens, or session keys to stdout, stderr, log files, or any output. Mask or omit them entirely.

3. **Clear secrets immediately after use.** Zero out every credential and session variable as soon as it is no longer needed. Do not keep them alive across unrelated steps.

4. **Credentials must flow in this order:** Windows login, or masked prompt (SecureString) → convert to plain text only for the login call → clear credential variable. Never pass passwords as command-line arguments.

5. **Prompt before any destructive or irreversible action.** Any action that cannot be undone requires explicit user confirmation before proceeding.

## Agent Rules

- If a request fails, log the error and exit cleanly — do not retry in a tight loop.
- Do not install new Python packages or PowerShell modules without user approval.
- `README.md` will contain project header and index to main PRD

## Agent Workflow

Every non-trivial task follows three phases. Do not skip phases or reorder them.

### Phase 1 — Preparation

1. **Research** — read relevant PRD files in `docs/prd/`, project source files, and any referenced docs before forming a plan.
2. **Interview** — use the `AskUserQuestion` tool to gather requirements. Ask only what cannot be derived from existing docs. Batch questions into a single call where possible.
3. **Critique** — after drafting a plan, self-critique it:
   - Identify the riskiest assumption.
   - Identify the simplest alternative approach.
   - Identify what could go wrong.
   - State whether each concern is addressed or accepted.
4. **Gate** — if the critique reveals an unresolved blocker (an assumption with no answer, a risk with no mitigation), return to step 2 and ask the user to resolve it. Otherwise, present the final plan and proceed to Phase 2.

### Phase 2 — PRD Update

1. Ask the user for explicit confirmation before writing any PRD file.
2. Create or update the relevant file(s) under `docs/prd/`, following **PRD Format** below.
3. Do not touch PRD files outside the scope of the current task.

### Phase 3 — Coding

1. Re-read the relevant PRD file(s) immediately before writing any code.
2. Ask the user for explicit confirmation before writing or modifying source files.
3. Implement the change. Follow all rules in **Security Rules** and **Agent Rules** above.
4. Verification — after the change is complete:
   - Run any automated checks available for this project (tests, linters, etc.).
   - If no automated checks exist, note this explicitly and ask the user to perform manual verification.
5. Closure — once the user confirms the change is verified, set the PRD's `status` to `closed`. Do not move or delete the file.

## PRD Format

One requirement per file, at `docs/prd/<kebab-case-slug>.md`. If a task spans two requirements, write two files. Copy `docs/prd/_template.md` to start a new PRD; the template itself is not a requirement and is excluded from any status count.

Frontmatter is required on every PRD:

```yaml
---
description: <one line — what this requirement is>
when-to-use: <when an agent should read this file>
tags: [<tag>, <tag>]
status: pending | in-progress | updated | closed | parked
---
```

### Status lifecycle

| Status | Meaning | Set when |
| --- | --- | --- |
| `pending` | Agreed but not started | The PRD is first written (Phase 2) |
| `in-progress` | Implementation underway | Phase 3 starts |
| `closed` | Implemented and verified by the user | Phase 3 closure |
| `updated` | Was closed; scope has since changed | A closed requirement is reopened. Returns to `in-progress` when work restarts |
| `parked` | Deliberately not being built; kept for a later revisit | The user decides to skip the requirement. A dated line under the title says why and when to revisit. Returns to `pending` when revived |

Normal path: `pending → in-progress → closed`. Rework path: `closed → updated → in-progress → closed`. Skip path: `pending | in-progress → parked → pending`.

PRD files are never moved or deleted. Closure is a `status` change only.
