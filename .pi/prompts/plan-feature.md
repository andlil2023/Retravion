# Plan One Incremental Feature

Plan only the next feature from approved User Needs and requirements, using the proposed verification design. Follow `AGENTS.md`; consult supporting docs selectively.

## Output

- Goal, in-scope behavior, and explicit non-goals.
- Owning module and affected modules/contracts.
- Relevant domain invariants and established patterns (with exact paths).
- Uncertainties requiring a human decision (maximum three, blockers only).
- Fewest independently verifiable implementation increments that preserve coherent behavior.
- Observable acceptance criteria and verification tests for each increment, traced to Requirement IDs.
- Proposed file scope and context needed per increment.
- Architecture impact: none / existing boundary / new decision required, with reason.

## Rules

- Do not split tasks merely to make them smaller.
- Do not create ADRs, new patterns, or implementation code.
- Do not make irreversible choices based on hypothetical future needs.
- Present the plan and verification design with PBI drafts for the proposed increments, using the sections required by `AGENTS.md`. Obtain explicit approval covering the plan, verification design, and drafts before creating the GitHub issues and adding them to the project. Stop before implementation.

## Input

$@
