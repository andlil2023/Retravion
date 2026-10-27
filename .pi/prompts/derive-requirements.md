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

## Template

Use this format for each proposed requirement:

### [Provisional Requirement ID] — [Title]

**Parent need:** [Approved Need ID]

**Requirement:** The system shall [observable behavior or constraint].

**Acceptance criteria:**
- [Only the conditions needed to verify this requirement.]

## Output

- Keep each requirement to the template above; do not add extra metadata.
- Add acceptance criteria only when they clarify verification beyond the requirement statement; otherwise omit that section.
- List uncertainties, scope exclusions, or blocking questions separately only when necessary (maximum three questions).
- Recommend the smallest useful first increment only when the proposed requirements span multiple increments.
- Do not treat proposed requirements as approved until explicitly confirmed.

## Input

$@
