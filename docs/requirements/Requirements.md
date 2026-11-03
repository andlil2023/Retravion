# Requirement Management

## Originating User Needs

- [UN-REQ-001 — Manage Requirements](../user-needs/UN-REQ-001%20-%20Manage%20Requirements.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

## Implementation Status

These requirements describe approved future behavior, not implemented or verified functionality. [PBI #124](https://github.com/andlil2023/Retravion/issues/124) is documentation only. Implementation requires separately approved work. Planned verification is specified in [Requirement Management — Test Specifications](../test-cases/Requirements.md).

## Requirements

### REQ-REQ-001 — Identify and Maintain a Requirement

**Parent need:** UN-REQ-001

**Requirement:** The system shall maintain a Requirement with one responsible Product, an internal GUID, a Product-scoped manual ID, title, Markdown statement, type, optional plain-text rationale, and status.

**Acceptance criteria:**
- The generated internal GUID and responsible Product identity are immutable.
- Manual ID, title, statement, and type are required.
- The manual ID is trimmed and unique case-insensitively among Requirements within its Product.
- Type is explicitly selected from System, Software, Hardware, Safety, Usability, or Regulatory.
- Creation starts in Draft; content can be edited only in Draft.
- Ordinary draft edits do not create new versions.
- Requirements remain readable under retired Products, but creation and all changes are blocked.
- Stale writes are rejected without overwriting concurrent changes.
- Deletion and ownership transfer are not provided.

### REQ-REQ-002 — Navigate, Find and View Requirements

**Parent need:** UN-REQ-001

**Requirement:** The web application shall provide a Requirements entry under each Product, with a searchable, filterable list, creation action, and access to individual Requirement details.

**Acceptance criteria:**
- Individual Requirements do not appear as navigation-tree entries.
- The list includes all statuses and displays ID, title, type, and status.
- Users can filter by type and status and search by ID or title.
- Search is case-insensitive; active filters and search apply together.
- Details show the responsible Product, ID, title, statement, type, rationale, status, and review history.
- Statements render as Markdown under REQ-CONTENT-001; rationale renders as plain text.

### REQ-REQ-003 — Review a Requirement

**Parent need:** UN-REQ-001

**Requirement:** The web application shall support Requirement review and retain the decisions and exact content to which they applied.

**Acceptance criteria:**
- Allowed transitions are Draft → In Review, In Review → Approved, In Review → Draft, and Approved → Draft.
- The same user may submit and approve.
- Review comments are optional plain text.
- Each transition retains actor, time, decision, optional comment, and the exact ID, title, statement, type, and rationale at that decision.
- Later edits and transitions do not alter earlier history.
- Draft edits do not erase history or create new versions.
- Approval is distinct from release. Product revision integration and released-version protection are not implemented in this increment.

### REQ-REQ-004 — Access Requirements through MCP

**Parent need:** UN-REQ-001

**Requirement:** The system shall provide authenticated local MCP access to list, read, create, and edit Requirements.

**Acceptance criteria:**
- Clients can list and read Requirements of every status.
- Creation and Draft editing follow the same validation, Product eligibility, access, and concurrency rules as the web application.
- MCP cannot change status, make review decisions, or edit In Review or Approved Requirements.
- Anonymous, password-restricted, and revoked-session access is denied, consistent with REQ-SYS-AUTH-002.
- Local clients can connect when the application runs in a container; remote MCP access is outside scope.

### REQ-REQ-005 — Audit Requirement Changes

**Parent needs:** UN-REQ-001, UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall audit Requirement changes and denied mutation attempts through web and MCP.

**Acceptance criteria:**
- Successful creation, actual Draft edits, and review transitions record actor, time, distinct action, immutable Requirement identity, success outcome, and initial or old/new values.
- Values cover responsible Product identity, manual ID, title, statement, type, rationale, and status. Review events also retain the decision and optional comment.
- The business change, any review-history entry, and required audit event commit together.
- Audit-write failure prevents a successful change; success is reported only after confirmed commit.
- An uncertain commit outcome is reported as uncertain, with advice to retrieve before retrying; no automatic retry occurs.
- Unauthorized mutation attempts remain denied and submit failure events with actor if known, time, action, and unknown target, without submitted identifiers, values, or comments.
- Audit-submission failures for denied attempts are safely logged without submitted values, credentials, or provider exception details. Logging does not replace an audit event.
- Reads, no-op saves, ordinary validation failures, duplicate-ID conflicts, and stale-write rejections produce no change events. Unauthorized-attempt auditing still applies.

## Boundaries and Open Details

The first increment excludes relationships, automatic ID suggestions, Test Cases, separate verification criteria, and Product revision integration. The test specifications linked above are repository verification plans, not an application Test Case feature. Requirements remain a flat Product-scoped list.

A separate follow-up is planned for a centralized graphical relationship page: selecting an entity, seeing its relationships, and managing links, including Requirement–User Need and parent–child Requirement links. Detailed requirements for that page remain to be interviewed and approved; no relationship storage or interface is authorized by this specification.

Released-version protection remains future revision work. Review history is distinct from the audit trail and from future released-version protection. This specification does not decide field length limits, exact Unicode comparison semantics, text-search matching beyond case-insensitivity, MCP transport or authentication mechanism, or additional roles. Implementation planning must resolve relevant details without weakening the approved behavior.
