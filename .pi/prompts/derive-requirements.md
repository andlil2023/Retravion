# Derive Requirements from an Approved User Need

Use the supplied approved User Need as the authority. Do not infer missing approvals or silently expand scope.

## Rules

- Derive the minimum coherent set of requirements needed for the current increment.
- Each requirement must express a clear, verifiable obligation.
- Keep closely related conditions and acceptance criteria together when they describe the same behavior.
- Split requirements only when they represent independently meaningful obligations.
- Separate observable behavior from architectural or implementation decisions.
- Avoid prescribing database or UI technology unless explicitly required.
- Preserve traceability to the originating Need ID.
- Identify gaps and conflicts instead of guessing.
- Avoid unnecessary micro-requirements and speculative future functionality.
- Do not edit files or create issues until explicitly requested.

## Output

Provide a table:

| Provisional ID | Requirement | Type (functional/constraint) | Parent Need | Verification idea | Uncertainty |
|---|---|---|---|---|---|

Then provide:
- Explicit exclusions.
- Blocking questions, if any (maximum three).
- Recommendation for the smallest useful first increment.

Do not treat proposed requirements as approved until explicitly confirmed.

## Input

$@
