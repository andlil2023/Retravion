# Domain — Retravion

## Product Intent

Retravion helps software teams define, link, review, and trace product-lifecycle documentation. It is not assumed to be a certified eQMS or to implement a particular regulatory standard by default.

## Candidate Concepts (Not Yet Approved Domain Model)

- **Product:** The item whose lifecycle is documented.
- **User Need:** A stakeholder problem, outcome, or capability desired.
- **Requirement:** A verifiable statement of expected behavior or constraint.
- **Test Case:** A procedure or specification used to verify expected behavior.
- **Revision:** A named product baseline or change point.
- **Feature:** An optional grouping of delivered capability.

These are vocabulary for discussion, not permission to create all entities or relationships now.

Candidate concepts and inferred relationships must not become established domain rules without explicit approval during feature design.

## Established Invariants

None yet. Record invariants only when confirmed during feature design.

Use this format:

### INV-001 — Short Name

- **Rule:** What must always be true.
- **Applies to:** Relevant entities or features.
- **Evidence:** Test or approved requirement.
- **Revisit when:** Changed domain assumption.

## Open Questions

- Scope and ownership of User Needs (product-specific or reusable).
- Whether Requirements and Test Cases are versioned in the first slice.
- Relationship cardinalities and lifecycle states.

Do not resolve these globally until a selected feature requires them.