# Test Case Management

## Status and Scope

Approved future behavior from the operator interview for [PBI #144](https://github.com/andlil2023/Retravion/issues/144), not implemented functionality or verification evidence. Verification intent is in [Test Case Management — Test Specifications](../test-cases/Test%20Cases.md). No originating Test Case User Need has been established by this task; do not invent one. Audit traceability uses UN-AUDIT-01, UN-AUDIT-02, and REQ-SYS-AUDIT-003 in [Audit Trail](Audit%20Trail.md).

The first implementation is web-only. Existing [relationship rules](Relationships.md) and the release-based protection rule REQ-LINK-009 apply. #144 explicitly revises #132's unconditional Approved-version immutability for User Needs and Requirements: approval freezes editing until explicit eligible reopening; release permanently protects the exact version and its owned links.

## Requirements

### REQ-TC-001 — Identify and Author a Test Case

**Requirement:** The system shall maintain a Product-owned Test Case with stable identity and ordered structured steps.

**Acceptance criteria:**
- Generate immutable entity and version GUIDs. Allocate a system-generated Product-scoped sequential ID such as TC-001 at creation; it is unique within that Product, never changed or reused, and retained across versions. Concurrent creation cannot allocate duplicate IDs.
- Fields are a plain-text title, Markdown description, Markdown preconditions, and ordered steps, each with a Markdown action and expected result. Apply REQ-CONTENT-001 from [Markdown Content](Markdown%20Content.md), including bullet lists and tables.
- Drafts may be incomplete. Submission requires a nonblank title, at least one step, and a nonblank action and expected result on every step, plus the links required by REQ-TC-004. Description and preconditions remain optional. Approval must still satisfy these validations.
- Only Draft content is editable. Authors can add, edit, remove, and reorder steps; displayed step numbers derive from order and renumber automatically, not from editable number fields.
- The responsible Product is immutable. Retiring it blocks creation and all changes, including content, steps, links, review, reopening, and successors, while retaining readable versions and history.
- Reject stale edits and lifecycle actions without overwriting newer state or appending successful history. Recheck permissions, status, Product eligibility, and release protection at mutation time, including changes concurrent with reopening, approval, or release.

### REQ-TC-002 — Navigate and Inspect Test Cases

**Requirement:** The web application shall provide a Product-scoped Test Case list and exact-version details.

**Acceptance criteria:**
- Show one row per Test Case with ID, title, version number, and status. Use its Draft or In Review working version when present, otherwise latest Approved; distinguish both when present, for example v2 Draft · v1 Approved.
- Provide ID/title search and status filtering, applicable together within the selected Product.
- Default details use the same working-otherwise-latest-Approved rule. Users can open every earlier version explicitly; an exact-version link never substitutes a later version.
- Details show Product, ID, version, status, all authoring fields and ordered steps, exact Requirement links, and successor reason where applicable.
- Show review and reopening history compactly by default, with expandable decision details retaining actor, time, decision, optional comment or mandatory reason, immutable entity/version identities, and exact content and owned Requirement links at the decision. Later changes never rewrite earlier decision evidence.

### REQ-TC-003 — Review and Reopen Test Cases

**Requirement:** The web application shall support explicit Test Case review and eligible reopening without losing decision evidence.

**Acceptance criteria:**
- Authors create/edit Drafts, manage steps and links, submit Draft → In Review, and create successors. Approvers perform In Review → Approved, In Review → Draft, and eligible Approved → Draft reopening. The submitting user may also approve when authorized.
- Submission freezes editing. Rejection returns the same version to Draft; optional review comments are plain text. Draft edits, submission, rejection, and reopening retain version GUID and number.
- Reopening is web-only, requires approval permission and a nonblank reason, and is permitted only for the latest version with no successor and no direct or indirect Released-revision reference under REQ-LINK-009. It invalidates the current approval while retaining earlier approval/reopening evidence. It does not transition directly to In Review; corrections must be submitted and approved again.
- A linked Requirement version can subsequently reopen without automatically revoking the Test Case's approval. Release remains blocked until the entire outgoing closure is Approved under REQ-LINK-009. Approval is not release.
- Product-retirement, access, stale-write, history, and audit protections apply to every transition and reopening. No direct editing of In Review or Approved content or owned links is allowed.

### REQ-TC-004 — Own Exact-Version Requirement Links

**Requirement:** Each Test Case version shall own its same-Product links to exact Requirement versions.

**Acceptance criteria:**
- A Test Case version may verify several distinct Requirement entities, but at most one exact version of each Requirement. Multiple Test Cases may verify the same Requirement version. Duplicate links, links to another version of an already-linked Requirement, and cross-Product links are rejected; an explicit atomic retarget replaces the existing endpoint.
- Drafts may have no Requirement links. Submission and approval require at least one. Drafts may link to Draft, In Review, or Approved targets; approval requires every linked Requirement version to be Approved.
- Only authorized editable Draft owners may add, remove, or retarget links. Apply REQ-LINK-002, REQ-LINK-003, REQ-LINK-005, and REQ-LINK-006, including atomic retargeting and explicit confirmation. No automatic retargeting or coverage claim for a newer Requirement version occurs.
- Extend the shared Links page and keyboard-accessible table/list alternative under REQ-LINK-004 to Test Cases, supporting exact-version incoming/outgoing navigation and the User Need → Requirement → Test Case chain. The arrows describe exploration/coverage, not ownership: outgoing ownership runs Test Case → Requirement → User Need (and child Requirement → parent Requirement).
- Incoming references to a protected target may still be changed by their own eligible Draft owners. Release protection follows outgoing references, not all incoming neighbors.

### REQ-TC-005 — Maintain Linear Test Case Versions

**Requirement:** The system shall maintain a linear sequence of Test Case versions with one working version at most.

**Acceptance criteria:**
- Create Draft v1; only deliberate successor creation increments the number sequentially. A successor is created from the latest Approved version, never an older version; no branching is provided.
- Allow at most one Draft or In Review per entity. If one exists, open it rather than create another. Concurrent successor/reopening operations cannot create multiple working versions or reopen an older version after a successor exists.
- Web successor creation requires author permission and a nonblank change reason, fixed once created. Retain source/new identities, actor, time, reason, initial copied content, and exact owned links in history and audit.
- A successor has a new immutable version GUID but retains entity GUID, Product, and TC ID. Copy title, description, preconditions, ordered actions/results, and links to the same exact Requirement versions. Author review and any retargeting are deliberate; inheritance does not assert suitability for newer target versions.
- Creating/approving successors leaves earlier versions' content, status, and links unchanged. Earlier versions cannot reopen once any newer version exists. Latest Approved is a designation, not a separate status. Released versions always require a successor for changes (REQ-LINK-009).

### REQ-TC-006 — Audit Test Case Mutations

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall audit Test Case changes and unauthorized mutation attempts through the shared audit submission contract.

**Acceptance criteria:**
- Cover entity creation; actual Draft field edits; step addition/edit/removal/reordering; review transitions; reopening; successor creation; and link add/removal/retargeting. Record actor, occurrence time, distinct action, immutable entity/version identities, version number, outcome, and initial or old/new values. Step edits may use an edit event with old/new ordered steps, not separate audit-interface methods.
- Values cover Product identity, TC ID, title, description, preconditions, ordered step actions/results, status, and exact owned Requirement endpoints as applicable. Review/reopening events retain decision and optional comment or mandatory reason; successor creation retains source/new version identities, required reason, and initial copied values/links. Retain event-time values despite later changes.
- Link changes retain owning-version history and exact old/new endpoints under REQ-LINK-006; a retarget is one old/new operation. Inherited links are initial successor values, not duplicate per-link success events.
- Business state, required history, and audit event commit together. Audit submission/write failure, business failure, or history failure prevents a successful change. Report success only after confirmed commit.
- If commit was attempted but its outcome cannot be confirmed, report uncertainty, not success or confirmed rollback; advise reloading before retry, with no automatic retry. Atomicity must hold for both committed and uncommitted outcomes.
- Unauthorized attempts on every mutation remain denied and submit a failure event with action, time, actor if known (otherwise unknown), failure outcome, and unknown target. Do not retain submitted IDs, fields, steps, endpoints, reasons, or comments. If submission fails or throws, log the audit failure without submitted values, credentials, or provider exception details. Logging neither replaces an event nor guarantees persistence.
- Reads, navigation, history expansion, no-op saves, ordinary validation failures, duplicate/invalid links, unapproved-target approval rejection, release-protection rejection, older-version reopening, second-working-version conflicts, and stale-write rejections produce no change event or successful history entry. Unauthorized-attempt auditing still applies even if input also fails validation.
- These are explicit Test Case decisions, not automatic adoption of Product-specific REQ-SW-AUDIT-007–009. Reuse the small common audit event contract; do not introduce entity-specific submission methods or a speculative auditing framework. Any necessary public contract extension requires separate approval.

## Boundaries and Deferred Work

No application functionality is implemented by #144. Execution/results, Test Runs, CI integration, release-baseline selection and release machinery are excluded. REQ-LINK-009 specifies approved future release protection, not a completed revision design. Future release write operations still need their own audit decision and verification plan before implementation.

Future MCP must support reading, creating, and updating Test Cases subject to the same permissions, validation, lifecycle, retirement, concurrency, and release protections. This first implementation provides no Test Case MCP operations; public APIs and detailed MCP lifecycle capabilities need separate approval.

Product-scoped shared module grouping (for example AUTH/AUDIT), filtering by module, and optional module-qualified IDs such as TC-AUTH-001 are deferred proposals, not approved implementation. Moving items, module renaming, and numbering rules require later decisions; current TC IDs remain simple and immutable.

RLC-Tracker's `docs/adr/0052-test-case-traceability-is-versioned-at-both-ends.md` and `TestCaseLifecycleHandler.cs` were reference material for exact-version links and copying structured steps/links into successors. Its architecture, permissions, and audit implementation are not adopted automatically; the approved interview governs this specification. Authored procedures and links are not evidence of execution, passing results, or verified coverage.

Field size limits, numbering format beyond the TC-001 convention and allocation gaps, detailed Markdown empty-content validation, exact search/status-filter semantics, permission provisioning, step persistence representation, and release/provenance integration require implementation planning without weakening the approved behavior. No new packages, persistence strategies, UI patterns, or public contracts are authorized here. Use the existing theme/shared components and UI catalog when UI implementation is approved.

## Proposed Implementation Increments — Not Created or Authorized

1. **Shared eligible reopening:** update User Need and Requirement lifecycle/history/audit with focused concurrency and failure tests; coordinate with #133/#134. Release eligibility cannot be bypassed while integration is pending.
2. **Test Case authoring and navigation:** Product ownership, simple numbering, incomplete Drafts, structured Markdown steps, list/details, concurrency, retirement, and creation/edit auditing.
3. **Test Case review and versioning:** review validation/history, permissions, reopening, successors, and transaction/audit coverage. Approval is unavailable until exact Requirement-link prerequisites exist.
4. **Test Case Requirement links:** storage and owner rules, submission/approval checks, inheritance, retargeting, history/audit; coordinate with #135–140. Review and links may need one coherent increment if independently testable boundaries cannot preserve the required gates.
5. **Shared Links-page Test Case integration:** graph/table navigation and equivalent authorized link actions.
6. **Revision release closure:** separately specify/implement baseline selection, all-Approved transitive validation, permanent protection, mutation/release concurrency, and release-specific audit decisions. This is a prerequisite for claiming release-based evidence protection.
7. **Future MCP Test Case access:** separately approve list/read/create/update contracts and tests. Shared modules/numbering remain a later proposal.

Each proposed implementation requires a separately approved PBI with its own file scope, applicable requirement IDs, write-path audit decisions, and focused verification. No follow-up issues are created by this specification.
