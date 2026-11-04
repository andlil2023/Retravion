# User Need Management

## Originating User Needs

- [UN-NEED-001 — Manage User Needs](../user-needs/UN-NEED-001%20-%20Manage%20User%20Needs.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

## Implementation Status

These requirements describe approved future behavior, not implemented or verified functionality. [PBI #117](https://github.com/andlil2023/Retravion/issues/117) and the versioning specification in [PBI #132](https://github.com/andlil2023/Retravion/issues/132) are documentation only. User Need versioning implementation belongs to [#134](https://github.com/andlil2023/Retravion/issues/134) and requires separately approved work. Planned verification is in [User Need Management — Test Specifications](../test-cases/User%20Needs.md).

## Requirements

### REQ-NEED-001 — Identify and Maintain a User Need

**Parent need:** UN-NEED-001

**Requirement:** The system shall maintain a User Need with one responsible Product, an internal GUID, a Product-scoped manual ID, title, Markdown statement, and status.

**Acceptance criteria:**
- The generated internal entity GUID is immutable; the responsible Product is identified by its immutable internal identity, not its editable display ID. Each version also has a distinct immutable GUID under REQ-NEED-006.
- Manual ID, title, and statement are required. The manual ID is trimmed and unique case-insensitively among User Need entities within the responsible Product, not independently allocated per version; the same ID may be used under a different Product. It may be corrected before the first approval, then remains fixed across all successor versions.
- Creation starts in Draft. Content can be edited only in Draft. An ordinary draft edit does not create a new version.
- Existing needs remain visible under retired Products, but creation and all changes, including review decisions, are blocked while the responsible Product is retired.
- A stale write is rejected without overwriting a concurrent change. Deletion is not provided in this increment.

### REQ-NEED-002 — Navigate, List and View User Needs

**Parent need:** UN-NEED-001

**Requirement:** The web application shall provide a User Needs entry under each responsible Product, with a list, creation action, and access to individual need details.

**Acceptance criteria:**
- Individual needs do not appear as tree entries. The Product-scoped list includes all statuses, with one row per User Need entity. Use the working version's title/status when present, otherwise its latest Approved version's. When both exist, clearly distinguish them, for example v3 Draft · v2 Approved.
- Default details open the working version when present, otherwise the latest Approved version. Users can explicitly open any earlier version; an exact-version reference always opens that version without silently switching to a newer one.
- A need's details show its ID, version number, title, statement, status, responsible Product, review history, and successor-creation reason where applicable. Markdown statements are rendered as Markdown; REQ-CONTENT-001 applies.
- Search is not part of this initial increment.

### REQ-NEED-003 — Review a User Need

**Parent need:** UN-NEED-001

**Requirement:** The web application shall support review of User Needs and retain the decisions and the content to which they applied.

**Acceptance criteria:**
- A working version permits Draft → In Review, In Review → Approved, and In Review → Draft. Submission freezes editing; rejection returns the same version to Draft without changing its version number or losing review history. Comments are optional; the same user may submit for review and approve.
- An Approved version never transitions back to Draft. A separate web operation creates a successor Draft under REQ-NEED-006, leaving the Approved version unchanged.
- Each transition retains actor, time, decision, optional comment, immutable entity/version identities, version number, and exact title and statement at the decision. Later edits or transitions do not alter earlier history. History is visible from the need.
- Draft edits do not themselves create new versions or erase review history. Approved-version preservation applies independently of future Product revision integration; revision/baseline selection remains outside this increment.

### REQ-NEED-004 — Access User Needs through MCP

**Parent need:** UN-NEED-001

**Requirement:** The system shall provide authenticated MCP access to User Needs for local clients, including clients outside an application container.

**Acceptance criteria:**
- Authenticated clients can list and read needs of every status and create and edit Draft needs under active Products, subject to the same identity, validation, retirement, and stale-write rules as the web application.
- Clients may read exact versions without latest-version substitution. Reads without an exact-version selection use the same working-otherwise-latest-Approved default as the web application.
- MCP may create a new User Need starting at v1 and edit its existing Draft. It cannot create a successor version, change status, make review decisions, or edit In Review or Approved versions. Exact-version access does not bypass these restrictions.
- Anonymous, password-restricted, and revoked-session access is denied, consistent with REQ-SYS-AUTH-002. Local clients can reach and authenticate to the application running in a container. Remote MCP use is outside this increment.

### REQ-NEED-005 — Audit User Need Changes

**Parent needs:** UN-NEED-001, UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall audit User Need changes and denied mutation attempts made through web and MCP.

**Acceptance criteria:**
- Successful entity creation, actual Draft edits, review transitions, and successor Draft creation record actor, occurrence time, distinct action, immutable need entity/version identities, version number, success outcome, and initial or old/new values. Values include responsible Product identity, manual ID, title, statement, and status; review events also retain the decision and optional comment. The event retains values at the time of the change.
- Successor creation is a distinct operation: history and audit retain the source and new version identities, required immutable change reason, and initial copied content and owned links where applicable. Inherited links are initial creation values, not duplicate per-link success events (REQ-LINK-006); no ordinary outgoing User Need relationship is introduced by this specification.
- The change, any review-history record, and its required audit event commit together. Audit submission failure prevents a successful change. Success is reported only after confirmed commit.
- If commit is attempted but its outcome cannot be confirmed, report uncertainty rather than success or rollback, advise retrieval before retry, and do not retry automatically.
- Denied mutation attempts remain denied and submit a failure event with action, time, known or unknown actor, and unknown target, without submitted identifiers, values, or comments. If submission fails or throws, safely log the audit failure without submitted values, credentials, or provider exception details; logging is not a substitute for an event, whose persistence may be uncertain.
- Reads, no-op saves, ordinary validation failures, duplicate-ID conflicts, attempts to create a second working version, and stale-write rejections do not produce change events. These exclusions do not override auditing unauthorized attempts, including denied successor creation. The same safe logging, transaction atomicity, and uncertain-outcome protections cover the new operation.

### REQ-NEED-006 — Maintain Stable User Need Versions

**Parent need:** UN-NEED-001

**Requirement:** The system shall maintain a linear sequence of separately addressable User Need versions while preserving Approved versions.

**Acceptance criteria:**
- A new User Need starts as Draft v1. Version numbers increase sequentially to v2, v3, and so on only when creating a successor from the latest Approved version. Draft edits, submission, and rejection retain the same version GUID and number.
- At most one working version, either Draft or In Review, exists per entity. If one exists, users open it rather than create another. Concurrent creation requests must not produce multiple working versions.
- Only the latest Approved version can be the source of a successor; older versions remain readable but cannot be used to branch the version sequence.
- Successor creation is web-only and requires the same permission as content editing. Existing Product-retirement, access, and stale-write protections apply.
- The new Draft has its own immutable version GUID, retains the entity GUID and fixed manual ID, and initially copies the source title, statement, and owned links to the same exact target versions where applicable. No ordinary outgoing User Need relationship is introduced. Incoming Requirement links remain on their original exact target version (REQ-LINK-003).
- A nonblank change reason is required at successor creation and is fixed once created, separate from later edits and optional review comments. Retain actor, time, source/new version identities, reason, initial values, history, and audit under REQ-NEED-005.
- Earlier Approved versions retain their content, status, and owned links. Approving a newer version does not change older statuses; latest Approved is a designation, not another lifecycle state. Creating or approving a version never automatically retargets incoming links.
- Existing records become v1 with their existing immutable entity identity, Product, content, current status, and history preserved. Allocate an immutable version GUID without reconstructing earlier versions from review snapshots. Preserve earlier snapshot evidence as recorded; do not present inferred version assignments as historical fact.

## Boundaries and Open Details

The initial increment does not implement Product revision integration, released-baseline selection, centralized applicability or requirement mapping, remote MCP, deletion, or ownership transfer. [Relationship Management](Relationships.md) specifies separate future link functionality; release-baseline selection remains future design work. Exact entity versions are specified here, not deferred to baseline implementation. This specification does not decide Unicode normalization or exact non-ASCII case comparison, field length limits, the MCP authentication mechanism or transport, or roles beyond existing authenticated-access restrictions; implementation work must resolve these without weakening the stated behavior. Review history is distinct from the audit trail and from future released-revision version protection.
