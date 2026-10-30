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
- Optional Product-change details: immutable internal identity and initial or old/new values, including the Product ID at the event. Existing non-Product events may omit these details.

**Acceptance criteria:**
- The contract supports an unknown actor.
- The contract distinguishes an unidentified target from an activity with no applicable target.
- The submission interface reports success or failure to its caller, separately from the outcome of the audited action.

### REQ-AUDIT-02 — Durable Audit Submission

**Parent need:** UN-AUDIT-01

**Requirement:** The system shall provide a working implementation of the common audit submission contract that durably records submitted audit information.

**Acceptance criteria:**
- A successful submission stores the supplied actor, occurrence time, action, target, action outcome, and any supplied Product-change details.
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
- Non-administrators cannot retrieve audit events, including their details, through the view or by direct requests. Anonymous, password-restricted, and revoked-session users cannot retrieve details either.

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

## Product Changes and Future Business-Record Coverage

### REQ-SYS-AUDIT-003 — Trace Business-Record Changes

**Type:** System requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Requirement:** The system shall make business-record creation and changes attributable and preserve the initial values or old and new values against an immutable record identity.

This is a cross-cutting expectation for future features. The present increment implements Product coverage only; it does not assert coverage of other business records or repair historical gaps.

### REQ-SW-AUDIT-007 — Record Product Changes

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02, UN-PRODUCT-001

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The software shall durably audit successful Product creation, actual field edits, retirement, and reactivation with actor, occurrence time, action, target, outcome, and Product values.

**Acceptance criteria:**
- The target is the immutable internal UUID. Creation records initial Product ID, name, kind, description, and active status; subsequent changes retain complete old and new values of these fields.
- The Product ID is retained as it was at the event, even if subsequently changed or reused. The internal UUID remains an internal identifier, not an editable Product field.
- Actions distinguish creation, edit, retirement, and reactivation. Reactivation with edits is one event containing both changes.
- Reads, no-op saves, ordinary validation failures, uniqueness conflicts, and stale writes produce no Product change event.
- Existing events without Product details remain readable and unchanged; historical gaps remain as-is.

### REQ-SW-AUDIT-008 — Commit Product Changes with Audit Events

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-PRODUCT-001

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The software shall commit a successful Product change and its required audit event together, so neither commits without the other.

**Acceptance criteria:**
- Audit write failure prevents the Product change from committing.
- Success is reported only after confirmed commit. Failure or cancellation after commit is attempted is reported as an unconfirmed outcome, not as confirmed rollback or success; the user is told to retrieve the Product before retrying.
- No automatic retry is performed for an uncertain outcome.

### REQ-SW-AUDIT-009 — Audit Unauthorized Product-Change Attempts

**Type:** Software requirement

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The software shall deny unauthorized Product-change attempts and submit an audit event without submitted Product field values.

**Acceptance criteria:**
- Each denied mutation callback records the attempted action, actor if known (otherwise unknown), occurrence time, failure outcome, and an unknown target; submitted identifiers and expected or proposed field snapshots are not retained.
- Coverage includes anonymous, password-restricted, and revoked-session callbacks for creation, editing, description changes, retirement, and reactivation. Denied page reads are not change attempts.
- If submission fails or throws, the change remains denied and the audit failure is operationally logged without submitted values, provider exception details, or credentials.
- An unsuccessful or uncertain submission cannot guarantee an event was stored; operational logging does not replace an audit record.

### REQ-SW-AUDIT-010 — Inspect Individual Audit Events

**Type:** Software requirement

**Parent need:** UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-001

**Requirement:** The software shall allow administrators to open individual recorded events from the audit list and inspect their available details.

**Acceptance criteria:**
- Details show recorded actor, exact occurrence time in UTC, action, event-time target, and outcome, preserving unknown and not-applicable states.
- Product creation shows initial Product ID, name, kind, description, and active status. Subsequent Product events show changed fields with their exact old and new values; unchanged fields are omitted.
- Product targets display the event-time Product ID, not the internal UUID. Historical values are not replaced by current Product values.
- Events without Product details or differing fields remain viewable with an explicit no-recorded-field-changes message. Empty recorded values remain distinguishable from missing prior values on creation.
- Values, including Markdown, are displayed as encoded plain text, without executing HTML or creating active links. Long values wrap, preserve line breaks, and are not truncated.
- Opening details does not mutate audit records or discard list filters.

## Approved Architectural Direction

Place the audit submission interface and information types in `src/Retravion.Application/AuditTrail/`, without creating a separate project.

Implement PostgreSQL persistence under `src/Retravion.Infrastructure/AuditTrail/`, using the existing database. Register the implementation through application composition. Callers depend on the submission contract, not storage details.

## Scope

REQ-AUDIT-01 defines the submission interface and audit information types; REQ-AUDIT-02 adds durable submission and database storage; REQ-AUDIT-03 integrates login decisions. A successful login event records the authentication decision at the initial credential recheck, not proof of session creation. A later recheck may deny a session after that event has been submitted. Defining or resolving the contract alone does not claim that audit information has been stored.

The original REQ-AUDIT-01–03 increment excludes integration of other application operations and audit browsing/search UI. The later inspection and account-administration requirements above specify additional future behavior, not evidence of its implementation. Retention, export, tamper-evidence mechanisms, retry queues, and duplicate-submission guarantees remain outside the original increment. Audit storage and session creation are not atomic: a failed or uncertain submission may have committed an event even when access is denied. Automatic retries are not provided.

The Product increment adds optional structured Product details to the common contract and audit table, preserving existing consumers and events. Product mutation orchestration uses an explicit transaction contract; Product persistence and audit insertion share the existing PostgreSQL transaction. The audit query returns supplied details. The individual-event inspection increment adds inline details to the existing authorized list using those returned values; it introduces no new retrieval endpoint or persistence contract. A separate field-history view remains outside scope.

These requirements are incremental contributions to UN-AUDIT-01, not delivery of a complete audit trail or evidence of compliance with a particular regulatory standard.
