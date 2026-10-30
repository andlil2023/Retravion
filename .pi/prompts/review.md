# Independent Change Review

Follow the independent-review gate in `AGENTS.md`. Review the current diff against the approved task, requirements, verification plan, invariants, and applicable established patterns. Do not rely on the implementer's claims.

Review changed code and directly affected behavior. Inspect unchanged code only when necessary to establish impact.

## Check

- Behavior, acceptance criteria, incorrect assumptions, and edge cases.
- Domain invariants, public contracts, and cross-module dependencies.
- Established MudBlazor application patterns for standard list/detail pages.
- Test coverage, meaningful assertions, regressions, and actual test execution.
- Unauthorized scope expansion or new architectural decisions.

## Audit gate

- Enumerate the new or changed business-record write paths in the diff and compare each with the PBI's explicit audit decisions, applicable requirement IDs, and verification plan from `AGENTS.md`. If none are affected, verify the PBI's `N/A` explanation.
- Check expected events and required actor, time, action, target/immutable identity, outcome, and initial or old/new values against the applicable requirements. Require explanations for excluded paths and decisions that no event is needed.
- Check successful changes, unauthorized attempts, no-op saves, and uncertain commit outcomes as applicable. Do not assume reads or validation failures all need events; verify the stated decisions and exceptions.
- Check change/audit commit consistency, audit-submission and commit failure behavior, uncertain-outcome reporting and retries, and safe operational logging without sensitive submitted values against the applicable requirements.
- Inspect focused tests and actual execution evidence for required and excluded events, failures, and transaction outcomes. Report missing decisions, unexplained exceptions, omitted tests, or unavailable evidence using the verdict rules below; do not silently skip them.
- Keep Product-specific behavior anchored to PBI #103's requirements (REQ-SW-AUDIT-007–009). Do not infer an automatic auditing framework, existing-feature coverage, or compliance with any specific regulation from this gate.

## Findings

- Blocking findings must identify a demonstrable defect or violated requirement.
- If evidence is insufficient, report the concern as a residual risk.
- Avoid stylistic nitpicks and speculative refactoring.

## Output

- **Verdict:** PASS | BLOCKED | NEEDS_HUMAN_DECISION
- **Findings:** Severity, file/line, evidence, impact, and smallest corrective action.
- **Checks verified:** Command and observed result; mark unknown if not run. Include the audit-gate comparison of affected write paths, requirement IDs, event decisions/exceptions, failure behavior, and test evidence (or the verified `N/A` explanation).
- **Residual risks:** Concise.

Only return BLOCKED for correctness, security, architectural, or material acceptance failures. Use NEEDS_HUMAN_DECISION for missing approval, unresolved policy, or unavailable required evidence.

Do not edit code during review.

## Input

$@
