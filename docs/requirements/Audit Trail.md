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

### REQ-AUDIT-02 — Durable Audit Submission

**Parent need:** UN-AUDIT-01

**Requirement:** The system shall provide a working implementation of the common audit submission contract that durably records submitted audit information.

**Acceptance criteria:**
- A successful submission stores the supplied actor, occurrence time, action, target, and action outcome.
- Unknown actors and the different target variants remain distinguishable after storage.
- Occurrence time preserves the supplied instant; storage may normalize it to UTC.
- Submission reports success only after the database transaction has committed.
- Expected storage failures report submission failure independently of the audited action's outcome. Unexpected faults remain exceptions.
- Successfully committed records survive an application restart.
- The submission interface provides no operation for modifying or deleting existing records.

### REQ-AUDIT-03 — Audit Login Attempts

**Parent need:** UN-AUDIT-01

**Requirement:** The system shall submit audit information for successful and failed authentication decisions and shall not grant a new authenticated session unless audit submission is confirmed successful.

## Approved Architectural Direction

Place the audit submission interface and information types in `src/Retravion.Application/AuditTrail/`, without creating a separate project.

Implement PostgreSQL persistence under `src/Retravion.Infrastructure/AuditTrail/`, using the existing database. Register the implementation through application composition. Callers depend on the submission contract, not storage details.

## Scope

REQ-AUDIT-01 defines the submission interface and audit information types; REQ-AUDIT-02 adds durable submission and database storage; REQ-AUDIT-03 integrates login decisions. A successful login event records the authentication decision at the initial credential recheck, not proof of session creation. A later recheck may deny a session after that event has been submitted. Defining or resolving the contract alone does not claim that audit information has been stored.

Integration of other application operations, audit browsing/search UI, retention, export, tamper-evidence mechanisms, retry queues, and duplicate-submission guarantees remain outside this increment. Audit storage and session creation are not atomic: a failed or uncertain submission may have committed an event even when access is denied. Automatic retries are not provided.

These requirements are incremental contributions to UN-AUDIT-01, not delivery of a complete audit trail or evidence of compliance with a particular regulatory standard.
