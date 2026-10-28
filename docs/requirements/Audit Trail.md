# Audit Trail

## Originating User Needs

- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

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

## System Requirements — Audit Inspection and Account Administration

### REQ-SYS-AUDIT-001 — Inspect Audit Activity

**Type:** System requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Requirement:** The system shall enable administrators to find and inspect recorded application activity to support investigations and regulatory inspections.

### REQ-SYS-AUDIT-002 — Trace Account Administration

**Type:** System requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Requirement:** The system shall make successful and failed user-account administration attempts traceable.

## Software Requirements — Audit Inspection and Account Administration

These requirements are approved for future implementation; their inclusion is not evidence that they have been implemented or verified.

### REQ-SW-AUDIT-001 — View Audit Events

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-001

**Requirement:** The software shall provide administrators with a view of recorded audit events.

**Acceptance criteria:**
- Each event displays its recorded actor, occurrence time, action, target, and outcome.
- An unknown actor is distinguishable from an identified actor.
- An unknown target is distinguishable from an identified target and from a target that is not applicable.

### REQ-SW-AUDIT-002 — Restrict Audit Viewing

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-001

**Requirement:** The software shall restrict access to audit-event viewing to administrators.

**Acceptance criteria:**
- Non-administrators cannot retrieve audit events through the view or by direct requests.

### REQ-SW-AUDIT-003 — Filter Audit Events

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-001

**Requirement:** The software shall allow administrators to filter recorded audit events by date/time range, actor, action, outcome, and target.

**Acceptance criteria:**
- Multiple filters can be combined; an event must match all active filters.
- The date/time range includes events occurring exactly at either boundary.
- The actor filter supports selecting an unknown actor.
- The target filter supports selecting an unknown target or a target that is not applicable.

### REQ-SW-AUDIT-004 — Record Account-Administration Attempts

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-002

**Requirement:** The software shall submit audit information for successful and failed attempts to create, inactivate, reactivate, or issue a replacement temporary password for a user account.

**Acceptance criteria:**
- Failed attempts include unauthorized attempts and administrator attempts that cannot be completed.
- Events use the existing audit information contract.
- Audit records do not contain passwords, temporary passwords, password hashes, or other credential material.

### REQ-SW-AUDIT-005 — Commit Account Changes with Audit Events

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-002

**Requirement:** The software shall commit a successful account-administration change and its audit event together, so that neither is committed without the other.

**Acceptance criteria:**
- If the change cannot be committed with its audit event, it does not succeed.
- If the commit outcome cannot be confirmed, the application reports that uncertainty rather than claiming success or rollback.
- A usable replacement temporary password is displayed only when reset success is confirmed.
- Failure messages do not expose passwords or sensitive implementation details.

### REQ-SW-AUDIT-006 — Report Audit Failure for an Unsuccessful Attempt

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-002

**Requirement:** If audit submission fails for a failed or unauthorized account-administration attempt, the software shall keep the requested action unsuccessful and report the audit failure through operational logging.

**Acceptance criteria:**
- The operational log does not contain passwords or other credential material.

An unsuccessful or uncertain submission cannot guarantee that an event was recorded. Operational logging reports the failure; it does not replace the audit record.

## Approved Architectural Direction

Place the audit submission interface and information types in `src/Retravion.Application/AuditTrail/`, without creating a separate project.

Implement PostgreSQL persistence under `src/Retravion.Infrastructure/AuditTrail/`, using the existing database. Register the implementation through application composition. Callers depend on the submission contract, not storage details.

## Scope

REQ-AUDIT-01 defines the submission interface and audit information types; REQ-AUDIT-02 adds durable submission and database storage; REQ-AUDIT-03 integrates login decisions. A successful login event records the authentication decision at the initial credential recheck, not proof of session creation. A later recheck may deny a session after that event has been submitted. Defining or resolving the contract alone does not claim that audit information has been stored.

The original REQ-AUDIT-01–03 increment excludes integration of other application operations and audit browsing/search UI. The later inspection and account-administration requirements above specify additional future behavior, not evidence of its implementation. Retention, export, tamper-evidence mechanisms, retry queues, and duplicate-submission guarantees remain outside the original increment. Audit storage and session creation are not atomic: a failed or uncertain submission may have committed an event even when access is denied. Automatic retries are not provided.

These requirements are incremental contributions to UN-AUDIT-01, not delivery of a complete audit trail or evidence of compliance with a particular regulatory standard.
