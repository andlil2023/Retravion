# Independent Change Review

Follow the independent-review gate in `AGENTS.md`. Review the current diff against the approved task, requirements, verification plan, invariants, and applicable established patterns. Do not rely on the implementer's claims.

Review changed code and directly affected behavior. Inspect unchanged code only when necessary to establish impact.

## Check

- Behavior, acceptance criteria, incorrect assumptions, and edge cases.
- Domain invariants, public contracts, and cross-module dependencies.
- Established MudBlazor application patterns for standard list/detail pages.
- Test coverage, meaningful assertions, regressions, and actual test execution.
- Unauthorized scope expansion or new architectural decisions.

## Findings

- Blocking findings must identify a demonstrable defect or violated requirement.
- If evidence is insufficient, report the concern as a residual risk.
- Avoid stylistic nitpicks and speculative refactoring.

## Output

- **Verdict:** PASS | BLOCKED | NEEDS_HUMAN_DECISION
- **Findings:** Severity, file/line, evidence, impact, and smallest corrective action.
- **Checks verified:** Command and observed result; mark unknown if not run.
- **Residual risks:** Concise.

Only return BLOCKED for correctness, security, architectural, or material acceptance failures. Use NEEDS_HUMAN_DECISION for missing approval, unresolved policy, or unavailable required evidence.

Do not edit code during review.

## Input

$@
