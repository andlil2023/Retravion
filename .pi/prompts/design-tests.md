# Design Verification Tests

Use approved requirements, not an imagined implementation. Design tests at the lowest appropriate level (domain/unit, integration, UI/end-to-end). Do not automatically require every level for every requirement.

## Rules

- Map each test to explicit Requirement IDs.
- Prefer the smallest test set that provides meaningful verification coverage.
- Combine related scenarios when appropriate, without hiding distinct failure conditions.
- Include normal, boundary, and failure behavior where applicable.
- Avoid tests that merely mirror a proposed implementation.
- Separate verification intent from automation framework details.
- Flag requirements that cannot be verified unambiguously.
- Do not modify code or create test files until explicitly requested.

## Output

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result | Automation candidate |
|---|---|---|---|---|---|---|

Then provide:
- Coverage gaps.
- Unresolved ambiguities.
- Deferred tests with rationale.

This design is provisional until explicitly approved with the feature plan; no separate test-design approval is required.

## Input

$@
