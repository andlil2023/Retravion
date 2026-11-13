# Requirement Management

## Originating User Needs

- [UN-REQ-001 — Manage Requirements](../user-needs/UN-REQ-001%20-%20Manage%20Requirements.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

- [UN-AI-001 — Efficiently Import Product Documentation](../user-needs/UN-AI-001%20-%20Efficiently%20Import%20Product%20Documentation.md)

## Implementation Status

These requirements describe approved future behavior, not implemented or verified functionality. [PBI #124](https://github.com/andlil2023/Retravion/issues/124) and the versioning specification in [PBI #132](https://github.com/andlil2023/Retravion/issues/132) are documentation only. Requirement versioning implementation belongs to [#133](https://github.com/andlil2023/Retravion/issues/133) and requires separately approved work. Planned verification is specified in [Requirement Management — Test Specifications](../test-cases/Requirements.md). The explicitly approved scope extension in [#144](https://github.com/andlil2023/Retravion/issues/144) replaces #132's unconditional Approved-version immutability with eligible reopening and the shared future release-protection rule REQ-LINK-009 in [Relationship Management](Relationships.md); it does not implement revision functionality.

### Expandable Requirements List — Documentation Scope

The hierarchy extension to REQ-REQ-002 below specifies requested future behavior, not an implemented or verified feature. It extends UN-REQ-001 and is limited to read-only browsing of existing parent–child relationships in the Product Requirements list. Implementation requires a separately approved PBI. Planned verification is TC-REQ-038–043 in the companion test specifications. The complete-decomposition declaration ("Fully refined by children"), test-case coverage calculations, contextual child creation, and relationship editing are outside this extension.

**Audit decision and verification plan:** N/A — this documentation change introduces or changes no application business-record write paths. The future list read, expansion/collapse, filtering and navigation must not mutate Requirements, relationships, lifecycle, history or audit records; reads require no change event under REQ-REQ-005 and REQ-LINK-006. Verification will compare business state/history/audit before and after these actions (TC-REQ-043). Mutation authorization, event payloads, transaction failure and uncertain-commit cases are inapplicable because no mutation is introduced; existing write policies are unchanged.

## Requirements

### REQ-REQ-001 — Identify and Maintain a Requirement

**Parent need:** UN-REQ-001

**Requirement:** The system shall maintain a Requirement with one responsible Product, an internal GUID, a Product-scoped manual ID, title, Markdown statement, type, optional plain-text rationale, and status.

**Acceptance criteria:**
- The generated internal entity GUID and responsible Product identity are immutable. Each version also has a distinct immutable GUID under REQ-REQ-006.
- Manual ID, title, statement, and type are required.
- The manual ID is trimmed and unique case-insensitively among Requirement entities within its Product, not independently allocated per version. It may be corrected before the first approval, then remains fixed across all successor versions.
- Type is explicitly selected from System, Software, Hardware, Safety, Usability, or Regulatory.
- Creation starts in Draft; content can be edited only in Draft.
- Ordinary draft edits do not create new versions.
- Requirements remain readable under retired Products, but creation and all changes are blocked.
- Stale writes are rejected without overwriting concurrent changes.
- Deletion and ownership transfer are not provided.

### REQ-REQ-002 — Navigate, Find and View Requirements

**Parent need:** UN-REQ-001

**Requirement:** The web application shall provide a Requirements entry under each Product, with a searchable, filterable list of independently expandable parent–child branches, a creation action, and access to individual Requirement details.

**Acceptance criteria:**
- Individual Requirements do not appear as navigation-tree entries.
- The list includes all statuses and displays one row per Requirement entity, with ID, title, type, and status from its working version when present, otherwise its latest Approved version. When both exist, clearly distinguish them, for example v3 Draft · v2 Approved.
- Users can filter by type and status and search by ID or title.
- Search is case-insensitive; active filters and search apply together, including Product area filtering specified by REQ-AREA-003 in [Product Areas](Product%20Areas.md).
- With no filters or search, initially show root Requirements with child branches collapsed. A parent row has an accessible expand/collapse control; expanding reveals its immediate children indented beneath it. Nested parents can be expanded independently to any existing depth. Collapsing a branch hides all its descendants without removing them or changing relationships. Leaf rows have no expansion control.
- Preserve one row per Requirement entity and the working-otherwise-latest-Approved selection above. Determine each displayed child's parent from the link owned by that selected child version, grouping under the parent's stable Requirement identity. Do not combine parent links from historical child versions into the displayed hierarchy.
- Grouping by identity does not retarget an exact-version link. If a child's parent link names an earlier version than the parent's displayed row, clearly identify the linked parent ID and version on the child row and provide navigation to that exact parent version. The parent row still shows its selected version and its ordinary details link uses the existing default selection. Do not claim this current list is an exact-version or Released-revision baseline tree.
- When search or any filter is active, show matching Requirements and their ancestor paths, automatically revealing matching descendants even inside previously collapsed branches. Clearly distinguish nonmatching ancestors as context, not matches; omit unrelated rows. An empty match set displays the no-match message rather than unrelated context. Clearing search and filters restores the user's unfiltered expansion choices for the current page; filter-driven expansion must not overwrite those choices.
- Keep existing ID, title, type, version/status information and details navigation for roots and children. Ordering within each sibling group is deterministic by manual ID. Expansion controls work with keyboard and pointer, identify the affected Requirement and expose their expanded/collapsed state to assistive technology; indentation is not the only indication of hierarchy.
- Keep hierarchy browsing Product-scoped and subject to existing read authorization. Requirements in retired Products remain browsable, including expansion/collapse; no write action is introduced. Do not silently omit descendants or present a partially loaded branch as complete when retrieval fails or is bounded; show the failure or incomplete state explicitly.
- Default details open the working version when present, otherwise the latest Approved version. Users can explicitly open any earlier version; an exact-version reference always opens that version without silently switching to a newer one.
- Details show the responsible Product, ID, version number, title, statement, type, rationale, status, review history, and successor-creation reason where applicable.
- Statements render as Markdown under REQ-CONTENT-001; rationale renders as plain text.

### REQ-REQ-003 — Review a Requirement

**Parent need:** UN-REQ-001

**Requirement:** The web application shall support Requirement review and retain the decisions and exact content to which they applied.

**Acceptance criteria:**
- A working version permits Draft → In Review, In Review → Approved, and In Review → Draft. Submission freezes editing; rejection returns the same version to Draft without changing its version number or losing review history.
- An Approved version may explicitly reopen to Draft only if it is the latest version, no newer version exists, and no Released revision references it directly or indirectly under REQ-LINK-009. Reopening is web-only, requires approval permission and a nonblank reason, retains version GUID/number and earlier history, and invalidates its current approval. No direct Approved → In Review transition is allowed; submission and approval must be repeated.
- A successor Draft under REQ-REQ-006 remains available instead of reopening and is mandatory for changes to released versions. Any newer version blocks reopening earlier ones.
- The same user may submit and approve.
- Review comments are optional plain text.
- Each transition retains actor, time, decision, optional comment (mandatory reason for reopening), immutable entity/version identities, version number, and the exact ID, title, statement, type, rationale, and owned links where applicable at that decision.
- Later edits and transitions do not alter earlier history.
- Draft edits do not erase history or create new versions.
- Approval is distinct from release. Reopening a linked target does not revoke this Requirement's approval, but release requires every directly or indirectly outgoing referenced version to be Approved (REQ-LINK-009). Product-retirement and stale-write protections apply to reopening; concurrent release or successor creation must not permit an ineligible reopen. Revision/baseline selection remains outside this increment.

### REQ-REQ-004 — Access Requirements through MCP

**Parent need:** UN-REQ-001

**Requirement:** The system shall provide authenticated local MCP access to list, read, create, and edit Requirements.

**Acceptance criteria:**
- Clients can list and read Requirements of every status and read exact versions without latest-version substitution. Reads without an exact-version selection use the same working-otherwise-latest-Approved default as the web application.
- Creation and Draft editing follow the same validation, Product eligibility, access, and concurrency rules as the web application.
- MCP may create a new Requirement starting at v1 and edit its existing Draft. It cannot create a successor version, change status, make review decisions, or edit In Review or Approved versions. Exact-version access does not bypass these restrictions.
- Anonymous, password-restricted, and revoked-session access is denied, consistent with REQ-SYS-AUTH-002.
- Local clients can connect when the application runs in a container; remote MCP access is outside scope.

The future multi-file import workflow is specified by REQ-AI-IMPORT-001–006 in [AI Imports](AI%20Imports.md), under [PBI #245](https://github.com/andlil2023/Retravion/issues/245). It reuses this MCP contract without adding server operations or changing lifecycle, authorization, concurrency or audit rules. The initial import scope creates new Drafts; it does not edit existing record content. Planning and execution remain separately scoped under #246 and #247.

### REQ-REQ-005 — Audit Requirement Changes

**Parent needs:** UN-REQ-001, UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall audit Requirement changes and denied mutation attempts through web and MCP.

**Acceptance criteria:**
- Successful entity creation, actual Draft edits, review transitions, and successor Draft creation record actor, time, distinct action, immutable Requirement entity/version identities, version number, success outcome, and initial or old/new values.
- Values cover responsible Product identity, manual ID, title, statement, type, rationale, and status. Review events also retain the decision and optional comment; reopening is a distinct action retaining its mandatory reason, old/new status, exact content/owned links, and invalidated approval without deleting earlier history. Successor creation is a distinct operation: history and audit retain the source and new version identities, required immutable change reason, and initial copied content and owned links. Inherited links are initial creation values, not duplicate per-link success events (REQ-LINK-006).
- The business change, any review-history entry, and required audit event commit together.
- Audit-write failure prevents a successful change; success is reported only after confirmed commit.
- An uncertain commit outcome is reported as uncertain, with advice to retrieve before retrying; no automatic retry occurs.
- Unauthorized mutation attempts remain denied and submit failure events with actor if known, time, action, and unknown target, without submitted identifiers, values, or comments.
- Audit-submission failures for denied attempts are safely logged without submitted values, credentials, or provider exception details. Logging does not replace an audit event.
- Reads, no-op saves, ordinary validation failures, duplicate-ID conflicts, attempts to create a second working version, and stale-write rejections produce no change events. Authorized released-version/older-version reopening rejections and missing-reason validation produce no change event or successful history. Unauthorized-attempt auditing still applies, including denied successor creation and reopening. The same safe logging, transaction atomicity, and uncertain-outcome protections cover the new operation.

### REQ-REQ-006 — Maintain Stable Requirement Versions

**Parent need:** UN-REQ-001

**Requirement:** The system shall maintain a linear sequence of separately addressable Requirement versions, allowing eligible latest-version reopening while preserving earlier and released versions.

**Acceptance criteria:**
- A new Requirement starts as Draft v1. Version numbers increase sequentially to v2, v3, and so on only when creating a successor from the latest Approved version. Draft edits, submission, rejection, and eligible reopening retain the same version GUID and number.
- At most one working version, either Draft or In Review, exists per entity. If one exists, users open it rather than create another. Concurrent creation requests must not produce multiple working versions.
- Only the latest Approved version can be the source of a successor; older versions remain readable but cannot be used to branch the version sequence.
- Successor creation is web-only and requires the same permission as content editing. Existing Product-retirement, access, and stale-write protections apply.
- The new Draft has its own immutable version GUID, retains the entity GUID and fixed manual ID, and initially copies the source title, statement, type, rationale, and owned links to the same exact target versions. Relationship behavior applies when separately implemented under REQ-LINK-003; versioning alone does not introduce relationships.
- A nonblank change reason is required at successor creation and is fixed once created, separate from later edits and optional review comments. Retain actor, time, source/new version identities, reason, initial values, history, and audit under REQ-REQ-005.
- Earlier Approved versions retain their content, status, and owned links. Approving a newer version does not change older statuses; latest Approved is a designation, not another lifecycle state. Creating or approving a version never automatically retargets incoming links.
- Existing records become v1 with their existing immutable entity identity, Product, content, current status, and history preserved. Allocate an immutable version GUID without reconstructing earlier versions from review snapshots. Preserve earlier snapshot evidence as recorded; do not present inferred version assignments as historical fact.

### REQ-REQ-007 — Derive an Independent Requirement Across Products

**Parent need:** UN-REQ-001

**Parent requirements:** REQ-LINK-007, REQ-SYS-AUDIT-003

**Requirement:** The web application shall create an independent Requirement in a different Product from a selected readable source Requirement version, retaining immutable copy-time provenance.

**Acceptance criteria:**
- From the source's exact-version details, outside the Links page, a user with source-read and destination-create permission may select a different active destination Product and copy any readable source version, including Draft, In Review, or Approved. Check both permissions again on submission. Existing restricted-session and retired-destination protections apply.
- Require a new destination manual ID under REQ-REQ-001. Pre-fill title, statement, type, and rationale from the selected source; permit editing before creation. Apply existing field and Product-scoped uniqueness validation. A duplicate ID is rejected without creation.
- Create a separate entity and Draft v1 with independent immutable entity/version identities, Product ownership, content and lifecycle, without ordinary links. Do not alter source content, lifecycle, or links; source changes never synchronize to the copy.
- Preserve immutable provenance independently of the editable destination values: source Product and Requirement identities, exact version GUID and number, status at copy time, and the source ID, title, statement, type and rationale as copied. Preserve this evidence even if the source Draft later changes or the source is deleted. No provenance edit or removal is provided.
- Show a read-only Derived from section on the copy's details with the stored identifiers, version, copy-time status and snapshot to users who can read the copy, even if they cannot access the source. Link to the exact source only when it exists and is accessible; otherwise retain the stored details as text. This does not grant access to other source Product records. Reverse Source for navigation is deferred to #143.
- Revalidate source version and destination state at submission. Reject stale source or unsafe concurrent state without creating anything, and ask the user to reload and review. Do not automatically retry. Authorization is checked even when submitted input is invalid.
- Creation and provenance commit together with the required audit event under REQ-REQ-005. A distinct success action records actor, time, outcome, immutable destination Product/entity/version identities, initial destination values, and source identity/version and copy-time snapshot. Failure of business, provenance or audit writes prevents partial commit or success. On uncertain commit outcome report uncertainty, advise retrieval before user-directed retry, and do not retry automatically. Unauthorized attempts submit sanitized failure events; audit-submission failures are safely logged. Reads, no-ops, ordinary validation failures, duplicate conflicts and stale rejections create no change event; unauthorized attempts remain audited even with invalid input.
- Provenance is evidence, not an ordinary outgoing relationship: releasing the destination uses the retained snapshot and does not traverse, freeze, or require continued access to the live source. Other ordinary references still follow REQ-LINK-009. This specification does not implement release integration.

Specified by [#174](https://github.com/andlil2023/Retravion/issues/174); implementation remains separately scoped under [#141](https://github.com/andlil2023/Retravion/issues/141). The snapshot and release rule are approved future behavior, not evidence of implementation.

## Boundaries and Open Details

PBI #187 separately specifies future version-specific Product area classification and filtering in [Product Areas](Product%20Areas.md), including successor inheritance and released evidence. It changes no runtime functionality or existing MCP contract. The following first-increment boundaries remain historical scope, not a claim that later increments were never implemented.

The first increment excludes relationships, automatic ID suggestions, Test Cases, separate verification criteria, and Product revision integration. The test specifications linked above are repository verification plans, not an application Test Case feature. Its flat Product-scoped list is extended by the expandable hierarchy specified in REQ-REQ-002 above; that extension does not implement runtime hierarchy browsing.

The approved [Relationship Management](Relationships.md) specification defines a separate follow-up for the shared graphical relationship page, Requirement–User Need links, and parent–child Requirement links. This versioning specification does not implement relationship storage or interfaces.

Release-baseline selection and implementation remain future revision work; the approved all-Approved outgoing closure and permanent exact-version protection are specified by REQ-LINK-009. Reopening must not bypass that rule when revision integration exists. Manual ID remains fixed after first approval, including after reopening. Review history remains distinct from the audit trail and release evidence. This specification does not decide field length limits, exact Unicode comparison semantics, text-search matching beyond case-insensitivity, MCP transport or authentication mechanism, or additional roles. Implementation planning must resolve relevant details without weakening the approved behavior.
