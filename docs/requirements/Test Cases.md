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

### REQ-TC-007 — Access Test Cases through Local MCP

**Requirement:** The system shall provide authenticated local MCP list/read/create/Draft-edit access to Test Cases under the contracts below.

Approved documentation-only decisions for [PBI #158](https://github.com/andlil2023/Retravion/issues/158). These are future contracts, not implemented tools or authorization to implement them. No new permission model, transport, package, persistence strategy, or public runtime API is introduced by this specification.

**Acceptance criteria:**
- Reuse existing local MCP transport, bearer/session validation, container connectivity and server-side web authorization. Anonymous, password-restricted, revoked-session and otherwise unauthorized access is denied. Remote access and new roles are excluded. Enforce authorization before disclosing Product or Test Case lookup results.
- Authorized readers can inspect every status and retired Products. Creation requires an eligible active Product; editing requires an authorized editable Draft. Recheck authorization, Product retirement, exact version, concurrency revision, lifecycle and release protection at mutation time under REQ-TC-001 and REQ-LINK-009, including concurrent changes.
- MCP may create Draft v1 and edit an existing eligible Draft, including a successor created through the web. It cannot submit, approve, reject, reopen, create successors or change status; these actions remain web-only. PBI #224 separately permits explicitly approved ordinary Draft link add/remove/retarget under REQ-LINK-002/003/005/006/009; exact-version selection never grants mutation authority. Creation starts with no links; content replacement preserves all existing exact links.
- Reject unknown arguments rather than silently ignoring them, including injected actor, status, ownership, generated IDs, version number, reasons, or link changes. Nested step objects accept only `action` and `expectedResult`; never accept editable step numbers. Malformed requests do not mutate records.

#### Tool Requests

Argument/property names are case-sensitive camel case. GUIDs are immutable identities, not TC IDs or version numbers. Required arguments must be present with the stated type; omitted optional arguments use the defaults below.

| Tool | Arguments | Successful result |
|---|---|---|
| `list_test_cases` | Required `productId`: Product manual ID string. | Snapshot array, one entry per entity in that Product, using its working Draft/InReview version otherwise latest Approved. Include all statuses and latest-Approved designation when a working version also exists. No search, filter or pagination arguments in this increment; ordering is not guaranteed. |
| `read_test_case` | Required `internalId`: entity GUID; optional `versionId`: version GUID or null. | Object with `testCase`: selected snapshot, `versions`: all version summaries, and `history`: retained history for the entity. Omitted/null `versionId` follows the list default. Exact selection must belong to this entity; missing entity/version or mismatched ownership fails without substitution. |
| `create_draft_test_case` | Required `productId`: Product manual ID string. Optional `title`, `description`, `preconditions`: strings or null; optional `steps`: step array or null. | New Draft v1 snapshot with system-allocated TC ID and entity/version GUIDs. Missing/null text normalizes to empty strings; missing/null steps normalizes to an empty array. |
| `edit_draft_test_case` | Required `internalId`: entity GUID, `versionId`: exact version GUID, `expectedRevision`: positive integer concurrency token, and `title`, `description`, `preconditions`: strings, `steps`: step array. None may be null. | Updated snapshot of that exact Draft, or unchanged snapshot for an authorized current no-op. Full content replacement, not a patch; empty strings/array deliberately clear content. No default-version fallback. |

Each step is `{ "action": "Markdown text", "expectedResult": "Markdown text" }`. Both properties are required non-null strings; null elements, missing properties and wrong types fail. Empty/whitespace strings and an empty array are valid incomplete Drafts under REQ-TC-001. Array order determines displayed numbering and records reorder operations; step identities/numbers are not request fields. Title is plain text; description, preconditions, actions and expected results retain Markdown source, not rendered HTML.

Creation must not require review-complete content or Requirement links. Editing keeps entity/Product/version identities, TC ID, version number, successor metadata, status and links unchanged. A stale revision or wrong version fails even when submitted content matches current content; no-op detection cannot bypass concurrency or eligibility checks. `expectedRevision` is the snapshot's concurrency token, not its version number or a Product Revision identifier.

#### Response Shapes and Errors

Successful tool results follow the existing MCP convention: a text content block containing camel-case JSON. Operation failures use `isError: true` with a safe text message, not a success-shaped payload. Preserve existing transport/authentication rejection behavior, including request-size, browser-origin and host restrictions. Protocol-shape errors are rejected before dispatch; this contract does not introduce new machine-readable error codes.

- **Snapshot:** `internalId`, `productInternalId`, `id` (TC ID), `versionId`, `versionNumber`, `status` (`Draft`, `InReview`, `Approved`), `title`, `description`, `preconditions`, ordered `steps`, `links`, `revision` (positive integer concurrency token), `creationReason`, `sourceVersionId`, and nullable `latestApprovedVersionNumber`. Content strings and arrays are non-null. Creation reason/source are null for v1 and retain immutable successor metadata thereafter.
- **Link:** `{ requirementInternalId, requirementVersionId }`, both exact GUIDs. These are read-only endpoints; no latest substitution or inferred coverage for a newer Requirement version.
- **Version summary:** `internalId`, `versionId`, `versionNumber`, `status`, `revision`, `creationReason`, `sourceVersionId`. Return every version belonging to the entity, in ascending version-number order. `latestApprovedVersionNumber` describes current entity state, not a distinct lifecycle status.
- **History entry:** `actor` (identifier string or null for unknown), `occurredAt` (UTC timestamp), `action`, and `change`. `change` includes `internalId`, `versionId`, `versionNumber`, nullable `before`, `after`, nullable `decision`, `comment`, `sourceVersionId`, and `reason`. Before/after values retain event-time `productInternalId`, `id`, `title`, `description`, `preconditions`, `status`, ordered `steps` and exact `links`. Preserve unavailable historical values as null rather than reconstructing them from current state. `before` is null for creation. History spans the entity's versions, with immutable version identities distinguishing entries; reading an exact version never replaces its content with history/current values.
- Denied access reveals no protected record existence/content. Missing records, invalid input, stale revisions, non-Draft state, retirement and release protection remain failures with no successful mutation/history entry. Unexpected faults are not silently converted to ordinary validation failures or exposed with provider details.
- Report write success only after confirmed commit. If commit confirmation is lost, including cancellation after an attempted commit, report uncertainty, not success or confirmed rollback. Advise authorized read/list retrieval before any user-directed retry; never automatically retry create or edit. For creation with no returned identity, compare the Product list and read candidate records; this is not guaranteed request correlation or deduplication. If the result remains ambiguous, do not advise blind recreation. No idempotency key or exactly-once guarantee is introduced.

#### Future Mutation Audit Decisions and Verification

Current #158 runtime write paths: **N/A — documentation only**. Future tools must apply REQ-TC-006 and REQ-SYS-AUDIT-003 through the common audit submission contract, not infer policy from Product-specific REQ-SW-AUDIT-007–009.

| Proposed operation | Required event and retained values | Planned verification |
|---|---|---|
| `create_draft_test_case` | `test-case.create`: actor, occurrence time, success outcome, immutable entity/version target identities and version number; initial Product identity, generated TC ID, Draft status, normalized title/description/preconditions and ordered steps, initially empty exact links. | TC-TC-023, TC-TC-027–029; incomplete/complete creates, concurrent allocation, event values, atomic failures and uncertainty. |
| `edit_draft_test_case` | `test-case.edit` only for actual changes: actor, occurrence time, success outcome, immutable entity/version identities and version number; before/after Product/TC identity, status, all content and ordered steps, preserving exact links. Step addition/removal/edit/reorder is part of this one edit event, not duplicate per-step events. | TC-TC-024–029; field/step changes, preserved links, current no-op versus stale no-op, access, races, atomic failures and uncertainty. |

Commit business state, required owning-version history and required success audit together. Audit submission/write failure, business failure or history failure before commit prevents all parts from committing; failure to confirm an attempted commit follows the uncertainty rule above. Test both actual committed and uncommitted outcomes, including cancellation after attempt. No partial state/history/audit or automatic retries are permitted.

For each recognized create/edit call denied by authorization or the transport access gate, submit one failure event for the corresponding action with occurrence time, actor if known otherwise unknown, failure outcome and unknown target. Denial takes precedence over content validation when the mutation can be identified, including invalid submitted content. Do not retain submitted identifiers, revision tokens, fields, steps, endpoints or comments. Keep denial effective if audit submission fails/throws; log only a safe audit-failure message, without submitted data, credentials or provider exception details. Logging does not replace or guarantee an event. Unparseable, oversized or unidentifiable protocol requests have no identifiable Test Case mutation event obligation; existing protocol rejection remains effective. Reads, including denied reads, are not mutation attempts.

Authorized reads/list/history, unchanged current saves, ordinary validation/protocol failures, stale/wrong-version requests, lifecycle/retirement/release rejections produce no change event or successful history. Unauthorized recognized mutations still use the denied-attempt rule even if their arguments would otherwise be excluded. No MCP lifecycle writes exist. PBI #224's separate ordinary Draft link tool reuses the relationship history/audit contract; content tools still do not change links.

## Boundaries and Deferred Work

No application functionality is implemented by #144. Execution/results, Test Runs, CI integration, release-baseline selection and release machinery are excluded. REQ-LINK-009 specifies approved future release protection, not a completed revision design. Future release write operations still need their own audit decision and verification plan before implementation.

REQ-TC-007 specifies approved future MCP list/read/create/Draft-edit contracts from #158, including web-only lifecycle boundaries and per-write audit plans; #224 separately updates ordinary Draft link access as specified above. This documentation introduces no Test Case MCP operations. Public runtime contract implementation requires a separately approved PBI; preserve permissions, validation, retirement, concurrency and release protection.

PBI #187 specifies future broad Product areas and filtering in [Product Areas](Product%20Areas.md), independently for each exact Test Case and Requirement version. This is classification metadata, not a module or a change to TC IDs. Product-scoped module entities and module-qualified IDs such as TC-AUTH-001 remain deferred proposals, not approved implementation; current TC IDs remain simple and immutable.

RLC-Tracker's `docs/adr/0052-test-case-traceability-is-versioned-at-both-ends.md` and `TestCaseLifecycleHandler.cs` were reference material for exact-version links and copying structured steps/links into successors. Its architecture, permissions, and audit implementation are not adopted automatically; the approved interview governs this specification. Authored procedures and links are not evidence of execution, passing results, or verified coverage.

Field size limits, numbering format beyond the TC-001 convention and allocation gaps, detailed Markdown empty-content validation, exact search/status-filter semantics, permission provisioning, step persistence representation, and release/provenance integration require implementation planning without weakening the approved behavior. No new packages, persistence strategies, UI patterns, or public contracts are authorized here. Use the existing theme/shared components and UI catalog when UI implementation is approved.

## Proposed Implementation Increments — Not Created or Authorized

1. **Shared eligible reopening:** update User Need and Requirement lifecycle/history/audit with focused concurrency and failure tests; coordinate with #133/#134. Release eligibility cannot be bypassed while integration is pending.
2. **Test Case authoring and navigation:** Product ownership, simple numbering, incomplete Drafts, structured Markdown steps, list/details, concurrency, retirement, and creation/edit auditing.
3. **Test Case review and versioning:** review validation/history, permissions, reopening, successors, and transaction/audit coverage. Approval is unavailable until exact Requirement-link prerequisites exist.
4. **Test Case Requirement links:** storage and owner rules, submission/approval checks, inheritance, retargeting, history/audit; coordinate with #135–140. Review and links may need one coherent increment if independently testable boundaries cannot preserve the required gates.
5. **Shared Links-page Test Case integration:** graph/table navigation and equivalent authorized link actions.
6. **Revision release closure:** separately specify/implement baseline selection, all-Approved transitive validation, permanent protection, mutation/release concurrency, and release-specific audit decisions. This is a prerequisite for claiming release-based evidence protection.
7. **Future MCP Test Case access:** implement the contracts and prospective tests specified by #158 only under separately approved implementation scope. Shared modules/numbering remain a later proposal.

Each proposed implementation requires a separately approved PBI with its own file scope, applicable requirement IDs, write-path audit decisions, and focused verification. No follow-up issues are created by this specification.
