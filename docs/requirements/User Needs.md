# User Need Management

## Originating User Needs

- [UN-NEED-001 — Manage User Needs](../user-needs/UN-NEED-001%20-%20Manage%20User%20Needs.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

## Implementation Status

These requirements describe approved future behavior, not implemented or verified functionality. [PBI #117](https://github.com/andlil2023/Retravion/issues/117) is documentation only. Implementation, including audit integration and MCP transport, requires separately approved work.

## Requirements

### REQ-NEED-001 — Identify and Maintain a User Need

**Parent need:** UN-NEED-001

**Requirement:** The system shall maintain a User Need with one responsible Product, an internal GUID, a Product-scoped manual ID, title, Markdown statement, and status.

**Acceptance criteria:**
- The generated internal GUID is immutable; the responsible Product is identified by its immutable internal identity, not its editable display ID.
- Manual ID, title, and statement are required. The manual ID is trimmed and unique case-insensitively within the responsible Product; the same ID may be used under a different Product.
- Creation starts in Draft. Content can be edited only in Draft. An ordinary draft edit does not create a new version.
- Existing needs remain visible under retired Products, but creation and all changes, including review decisions, are blocked while the responsible Product is retired.
- A stale write is rejected without overwriting a concurrent change. Deletion is not provided in this increment.

### REQ-NEED-002 — Navigate, List and View User Needs

**Parent need:** UN-NEED-001

**Requirement:** The web application shall provide a User Needs entry under each responsible Product, with a list, creation action, and access to individual need details.

**Acceptance criteria:**
- Individual needs do not appear as tree entries. The Product-scoped list includes needs of all statuses and provides access to each need's details.
- A need's details show its ID, title, statement, status, responsible Product, and review history. Markdown statements are rendered as Markdown; REQ-CONTENT-001 applies.
- Search is not part of this initial increment.

### REQ-NEED-003 — Review a User Need

**Parent need:** UN-NEED-001

**Requirement:** The web application shall support review of User Needs and retain the decisions and the content to which they applied.

**Acceptance criteria:**
- Allowed transitions are Draft → In Review, In Review → Approved, In Review → Draft, and Approved → Draft. Comments are optional; the same user may submit for review and approve.
- Each transition retains the actor, time, decision, optional comment, and exact title and statement at the decision. Later edits or transitions do not alter earlier history. History is visible from the need.
- Draft edits do not themselves create new versions or erase review history. Future Product revision integration must protect exact need versions referenced by released revisions from in-place modification; it is not implemented by this increment.

### REQ-NEED-004 — Access User Needs through MCP

**Parent need:** UN-NEED-001

**Requirement:** The system shall provide authenticated MCP access to User Needs for local clients, including clients outside an application container.

**Acceptance criteria:**
- Authenticated clients can list and read needs of every status and create and edit Draft needs under active Products, subject to the same identity, validation, retirement, and stale-write rules as the web application.
- MCP cannot change status or make review decisions; it cannot edit an In Review or Approved need.
- Anonymous, password-restricted, and revoked-session access is denied, consistent with REQ-SYS-AUTH-002. Local clients can reach and authenticate to the application running in a container. Remote MCP use is outside this increment.

### REQ-NEED-005 — Audit User Need Changes

**Parent needs:** UN-NEED-001, UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall audit User Need changes and denied mutation attempts made through web and MCP.

**Acceptance criteria:**
- Successful creation, actual Draft edits, and each review transition record actor, occurrence time, distinct action, immutable need identity as target, success outcome, and initial or old/new values. Values include responsible Product identity, manual ID, title, statement, and status; review events also retain the decision and optional comment. The event retains values at the time of the change.
- The change, any review-history record, and its required audit event commit together. Audit submission failure prevents a successful change. Success is reported only after confirmed commit.
- If commit is attempted but its outcome cannot be confirmed, report uncertainty rather than success or rollback, advise retrieval before retry, and do not retry automatically.
- Denied mutation attempts remain denied and submit a failure event with action, time, known or unknown actor, and unknown target, without submitted identifiers, values, or comments. If submission fails or throws, safely log the audit failure without submitted values, credentials, or provider exception details; logging is not a substitute for an event, whose persistence may be uncertain.
- Reads, no-op saves, ordinary validation failures, duplicate-ID conflicts, and stale-write rejections do not produce change events. These exclusions do not override auditing unauthorized attempts.

## Boundaries and Open Details

The initial increment does not implement Product revision integration, released-baseline selection, centralized applicability or requirement mapping, remote MCP, deletion, or ownership transfer. Shared-need mapping and exact-version baselines remain future design work. This specification does not decide Unicode normalization or exact non-ASCII case comparison, field length limits, the MCP authentication mechanism or transport, or roles beyond existing authenticated-access restrictions; implementation work must resolve these without weakening the stated behavior. Review history is distinct from the audit trail and from future released-revision version protection.
