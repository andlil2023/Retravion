# Audit Trail

## Originating User Need

- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)

## Requirements

### REQ-AUDIT-01 — Audit Submission Contract

**Parent need:** UN-AUDIT-01

**Requirement:** The system shall define a common audit submission contract that enables application operations to supply:
- Who performed or attempted the action.
- When the action occurred.
- What action was performed or attempted.
- What the action affected or targeted.
- Whether the action succeeded or failed.

**Acceptance criteria:**
- The contract supports an unknown actor.
- The contract distinguishes an unidentified target from an activity with no applicable target.
- The submission interface reports success or failure to its caller, separately from the outcome of the audited action.

## Approved Architectural Direction

Place the audit submission interface and information types in `src/Retravion.Application/AuditTrail/`, without creating a separate project.

## Scope

The current increment defines only the submission interface and audit information types. It includes no working submission implementation, database storage, or integration of application operations. Defining the contract does not claim that audit information has been stored. Caller reactions to submission failure are not defined in this increment.

This contract is an incremental contribution to UN-AUDIT-01, not delivery of a complete audit trail or evidence of compliance with a particular regulatory standard.
