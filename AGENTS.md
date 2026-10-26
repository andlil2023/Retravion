# Retravion — Agent operating contract

This file is the only mandatory entry document for every coding session. Do not automatically read all files in `docs/`.

## Product and approach
Retravion is a product-lifecycle documentation application for software teams. Develop incrementally: do not invent future features or implement speculative infrastructure.

## Authority
1. The current approved task and acceptance criteria define requested behavior.
2. `docs/domain.md` defines established domain invariants. Read only sections relevant to the task.
3. `docs/architecture.md` defines technical boundaries. Read relevant sections when planning or changing structure.
4. `docs/patterns.md` points to approved examples. Use only patterns marked Established.
5. `docs/decisions.md` records consequential choices and revisit triggers; consult only affected decisions.
If instructions conflict, stop and report the conflict; do not silently pick one.

## Before editing
- Identify the owning feature/module and the smallest coherent change.
- Identify affected invariants, public contracts, and tests.
- Inspect only explicitly named files first. Request additional files with a reason, rather than recursively reading the repository.
- State uncertainties that affect correctness. Ask at most the few essential questions; defer speculative questions.
- Do not create ADRs, design documents, or new patterns automatically.

## While implementing
- Follow `docs/architecture.md` for domain modeling rules.
- Follow established patterns and reuse shared UI components.
- Do not introduce new packages, cross-module dependencies, persistence strategies, or public APIs without approval.
- Do not change files outside approved scope without reporting why and requesting permission.
- Prefer one complete, independently testable change to arbitrary micro-tasks.
- Never weaken tests or remove assertions merely to make the build pass.
- If the task cannot be completed within its constraints, stop and explain the blocker.

## Completion
- Run relevant build, tests, and available architecture checks; report exact commands and results.
- Summarize changed files, behavior, unresolved risks, and any deviations.
- Do not claim success without evidence. Do not merge unless the review gate passes.
- Temporary plans and agent reasoning are not permanent project documentation.
