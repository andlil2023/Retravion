# Retravion — Agent operating contract

This file is the only mandatory entry document for every coding session. Do not automatically read all files in `docs/`.

## Product and approach
Retravion is a product-lifecycle documentation application for software teams. Develop incrementally: do not invent future features or implement speculative infrastructure.

## Authority
1. The current approved task and acceptance criteria define requested behavior.
2. `docs/domain.md` defines established domain invariants. Read only sections relevant to the task.
3. `docs/architecture.md` defines technical boundaries. Read relevant sections when planning or changing structure.
4. `docs/patterns.md` indexes Established patterns and defines the fallback when none applies.
5. `docs/decisions.md` records consequential choices and revisit triggers; consult only affected decisions.
If instructions conflict, stop and report the conflict; do not silently pick one.
Use prompt input or confirmed conversation context; ask for missing required input and never infer approval.

## Task workflow
- Track every implementation task as a GitHub issue labeled `PBI` in https://github.com/users/andlil2023/projects/3. Present the draft for explicit approval before creating it.
- Each PBI must include **What**, **Why**, **Implementation Guide**, **Requirements**, and **Acceptance Criteria** sections. Reference implemented requirements by ID only. If none apply, use `N/A` with a brief explanation. When acceptance criteria do not apply, use `N/A` or explain their omission in that section.
- Before any change task, fetch `origin` and create a new branch and git worktree from the latest `origin/main`; make all changes there, never on `main`. Default to one PBI per worktree.
- Keep acceptance criteria in the PBI; report their verification in the PR.
- Each PBI must include an explicit audit decision and verification plan in its Implementation Guide for every new or changed business-record write operation, using the audit checklist below. If no such operations are affected, state `N/A` and explain why.

## Before editing
- Identify the owning feature/module and the smallest coherent change.
- Identify affected invariants, public contracts, and tests.
- Inspect explicitly named files first. State a reason before reading directly related files; request permission for broader exploration.
- State uncertainties that affect correctness. Ask at most the few essential questions; defer speculative questions.
- Do not create ADRs, design documents, or new patterns automatically.

## Audit checklist
- Identify each affected write operation/path and reference applicable audit requirements by ID in the PBI's Requirements section, including REQ-SYS-AUDIT-003 where applicable.
- For each operation, specify expected events and their actor, time, action, target/immutable identity, outcome, and initial or old/new values as required. Explain any decision that no event is required; do not silently omit paths or exceptions.
- Address successful changes, unauthorized attempts, no-op saves, and uncertain commit outcomes as applicable. State whether reads and validation failures require events; do not assume they all do.
- Specify change/audit commit consistency, behavior when audit submission or commit fails, uncertain-outcome reporting and retry behavior, and safe operational logging without sensitive submitted values, as required by the applicable requirements.
- Identify focused tests and a verification plan for required events, excluded events, failure behavior, and transaction outcomes; report actual verification evidence in the PR. Explain any inapplicable case or test omission.
- Use the Product audit requirements established by PBI #103 (REQ-SW-AUDIT-007–009) as the source for Product-specific behavior, not as an automatic policy for every feature. This checklist is a planning and review gate, not an automatic auditing framework, a claim that existing features have been audited, or evidence of compliance with any specific regulation.

## While implementing
- Follow `docs/architecture.md` for domain modeling rules.
- Follow established patterns and reuse shared UI components.
- For UI work, use the development-only `/development/ui-catalog` reference (`src/Retravion.Web/Features/UiCatalog/UiCatalog.razor`) for live theme values, component examples, and copyable snippets. Read its source when the app is not running. Reuse the existing theme and shared components; consult `docs/patterns.md` and the linked canonical implementations before defining new pages. Catalog examples do not promote provisional styling into established patterns.
- Do not introduce new packages, cross-module dependencies, persistence strategies, or public APIs without approval.
- Do not change files outside approved scope without reporting why and requesting permission.
- Prefer one complete, independently testable change to arbitrary micro-tasks.
- Never weaken tests or remove assertions merely to make the build pass.
- If the task cannot be completed within its constraints, stop and explain the blocker.

## Completion
- Run local and agent PostgreSQL database tests against a disposable PostgreSQL Podman container, never a locally installed PostgreSQL server. Configure `RETRAVION_TEST_POSTGRES` for that container; see `README.md` for setup and cleanup. CI may use its PostgreSQL service container.
- Run relevant build, tests, and available architecture checks; report exact commands and results.
- Summarize changed files, behavior, unresolved risks, and any deviations.
- Do not claim success without evidence or self-approve. Review must be by a different human or agent session against the current diff.
- Only a `PASS` verdict from `.pi/prompts/review.md` satisfies the review gate; changes after review require re-review. `BLOCKED` and `NEEDS_HUMAN_DECISION` do not pass. Passing review does not authorize merging.
- Temporary plans and agent reasoning are not permanent project documentation.
