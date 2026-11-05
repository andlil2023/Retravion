# Revision Management

## Status and Scope

Approved specification from the operator interview for [PBI #157](https://github.com/andlil2023/Retravion/issues/157). This documentation changes no runtime behavior and is not evidence of implemented releases or executed runtime verification. Planned cases are in [Revision Management — Test Specifications](../test-cases/Revisions.md).

This specification completes baseline-selection and release-workflow decisions around REQ-LINK-009 in [Relationship Management](Relationships.md). No originating Revision User Need has been established here; do not invent one. Audit traceability uses UN-AUDIT-01, UN-AUDIT-02 and REQ-SYS-AUDIT-003 in [Audit Trail](Audit%20Trail.md).

## Requirements

### REQ-REV-001 — Identify and Prepare a Product Revision

**Requirement:** The system shall maintain a revision owned by one Product, with an immutable internal GUID, a required identifier unique within that Product, an optional description, and Draft or Released state.

**Acceptance criteria:**
- Creation starts in Draft. The identifier and description may change in Draft; both freeze on release. Responsible Product and internal GUID do not change.
- An editor may create an empty Draft or copy a Released revision's explicit selections into a new Draft under the same Product. The new revision has its own identity and valid destination identifier.
- Copy exact selected versions, without upgrading to newer versions or modifying the source revision. Resolve the new Draft's closure from those selections; do not treat the source's release evidence as a new release.
- An empty Draft is valid, but release requires at least one explicitly selected version.
- Reject stale edits, deletion and release without overwriting newer state or appending success history. Enforce identifier uniqueness under concurrent creation/editing.

### REQ-REV-002 — Explicitly Select Exact Baseline Versions

**Requirement:** A Draft revision shall permit explicit selection and removal of exact User Need, Requirement and Test Case versions belonging to its Product.

**Acceptance criteria:**
- Draft selections may reference Draft, In Review or Approved versions. Each reference retains entity type, immutable entity/version identities and version number; never silently substitute the current/latest version.
- Changing a selection is explicit. Creating or approving a newer version never advances a revision selection automatically.
- The effective baseline includes selected roots and their recursive outgoing ordinary-reference closure: Requirement–User Need, child–parent Requirement and Test Case–Requirement links. Incoming neighbors do not join merely because they reference a member.
- Multiple exact versions of one entity may coexist in selections or the closure. Preserve and clearly distinguish them, rather than resolving them to one version or rejecting release merely for that reason.
- Traverse repeated/shared references to completion without unbounded recursion or omission. A partial UI graph is not the baseline.
- Selection changes affect revision membership only; they do not edit the selected records or their owned links.

### REQ-REV-003 — Authorize Revision Operations

**Requirement:** Revision operations shall use existing Product-access, editing and approval capabilities without granting authority through UI visibility.

**Acceptance criteria:**
- Readers may view accessible revisions and their baseline versions. Editors may create/copy Drafts, edit their metadata and selections, and delete Drafts. Approvers may release revisions.
- Check authority and Product access on server-side reads and every mutation, including direct callbacks. Do not disclose inaccessible revisions, versions or blocking-record details.
- Retired Products remain readable to authorized users, but creation, copying, editing, selection changes, deletion and release are blocked. Coordinate mutations with concurrent retirement and authorization changes under existing controls.
- These are capability mappings, not approval for new roles, a permission-provisioning model or an independent-review/separation-of-duties policy.

### REQ-REV-004 — Release and Permanently Protect the Complete Baseline

**Parent requirement:** REQ-LINK-009

**Requirement:** Release shall be an explicit, atomic Draft → Released operation that validates and permanently protects the complete exact-version outgoing baseline.

**Acceptance criteria:**
- There is no separate revision review state. Before confirmation, show the exact baseline and any blocking versions, subject to access controls. Cancelling confirmation makes no change.
- Revalidate the current revision, nonempty selections, permissions, active Product and entire outgoing closure at commit time. Every version in that closure must be Approved; any Draft or In Review member blocks release, including members not loaded in the preview.
- Coordinate release with concurrent content/owned-link changes, selection/metadata changes, deletion, reopening, review rejection, successor creation and Product retirement. No stale validation, partial baseline or partially protected release may commit.
- Commit Released state, immutable baseline evidence, protection of every closure member, associated history and required audit together. Failure of any part prevents the entire operation. No persistence/synchronization strategy is selected by this specification.
- Preserve release actor and occurrence time, explicit selections and the complete resolved closure of exact version identities. Released-baseline reads use frozen evidence, not a fresh traversal of current links.
- A Released revision cannot reopen, change metadata/selections or be deleted. Later baselines require new revisions.
- Every directly or indirectly protected version permanently retains its Approved status, content and owned links. Changes require successors with new version identities. Creating or approving successors never changes a released baseline.
- Protection applies if any Released revision references the version. Sharing versions with another revision cannot remove protection. Incoming editable Draft owners remain free to change their own references without changing protected targets.

### REQ-REV-005 — Delete Drafts without Erasing Evidence

**Requirement:** Editors shall be able to delete Draft revisions without deleting referenced records or previously retained audit/history.

**Acceptance criteria:**
- Only Drafts in active Products may be deleted, subject to authorization and stale-write checks. Deletion racing release cannot delete a Released revision.
- Preserve the deleted revision's immutable identity and prior values in audit/history; deletion never erases that evidence or changes referenced entity versions.
- After an uncertain deletion outcome, authorized retrieval must distinguish deletion from a still-existing Draft before any user-directed retry. The concrete retained-deletion representation remains implementation planning, not authorization for a new persistence strategy.

### REQ-REV-006 — Retain Revision History and Audit

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** Revision mutations shall retain attributable history and required audit consistently with their business changes.

**Common event and failure rules:**
- Each successful operation below records actor, occurrence time, distinct action, immutable Product/revision identity, success outcome and initial or old/new values. Preserve event-time identifier/description, state and exact selection endpoints as applicable; later edits or deletion cannot rewrite evidence.
- Commit business state, selection/protection/release evidence, associated history and required audit together. Audit submission/write failure or any other precommit write failure prevents the change. Success is reported only after confirmed commit.
- If commit is attempted but confirmation is lost, including cancellation after the attempt, report uncertainty rather than success or confirmed rollback. Advise authorized retrieval before a user-directed retry; never retry automatically. Retrieval must establish whether the attempted creation, deletion or release committed, using immutable operation/record identity without leaking inaccessible records. Public retrieval contracts and concrete correlation mechanisms require implementation approval.
- Deny unauthorized mutation attempts and submit a failure event with attempted action, occurrence time, actor if known (otherwise unknown), failure outcome and unknown target. Do not retain submitted identifiers, selections, values or comments. This applies even if the submitted input would also fail validation.
- If denied-attempt audit submission fails or throws, keep the operation denied and log the audit failure without submitted values, credentials or provider exception details. An operational log is not proof of a persisted audit event.
- Reads, previews, navigation, unchanged saves, cancellation before mutation, ordinary validation/uniqueness failures, unapproved/empty-baseline release rejections, invalid references, lifecycle conflicts and stale/concurrency rejections produce no change event or successful history. Unauthorized attempts still follow the denial rule.

| Write operation | Required successful history/audit values in addition to common fields | Verification |
|---|---|---|
| Create empty Draft | Initial identifier, description, Draft state and empty selections | TC-REV-001, TC-REV-011–015 |
| Copy Released revision to new Draft | Source revision immutable identity, destination identity and initial metadata/state/exact selections; no duplicate per-selection creation events | TC-REV-002, TC-REV-011–015 |
| Edit Draft identifier/description | Old/new metadata, retaining immutable revision identity | TC-REV-001, TC-REV-011–015 |
| Add/remove/change explicit selections | Old/new exact selections, entity types and immutable entity/version endpoints; one replacement is one atomic old/new operation | TC-REV-003, TC-REV-011–015 |
| Delete Draft | Prior metadata, state, exact selections and deletion action against the retained immutable identity | TC-REV-009, TC-REV-011–015 |
| Release | Draft → Released, final metadata, explicit selections, complete exact closure, release actor/time and resulting permanent protection; no duplicate entity-edit or per-member success events merely for protection | TC-REV-005–008, TC-REV-011–015 |

These are revision-specific audit decisions, not automatic adoption of Product REQ-SW-AUDIT-007–009 or an auditing framework. Reuse the common audit submission contract; any required public-contract extension needs separate approval.

## Reopening and Runtime Integration Boundaries

[User Need Management](User%20Needs.md) REQ-NEED-003, [Requirement Management](Requirements.md) REQ-REQ-003 and [Test Case Management](Test%20Cases.md) REQ-TC-003 already specify eligible reopening: only the latest Approved version, no newer version, no direct/indirect Released-revision protection, approval authority and a mandatory reason. Reopening retains identity/history and invalidates current approval. Referencing owners keep their approvals, but release waits until the entire closure is Approved again. Their feature-specific audit rules remain applicable; revision release must not replace them with duplicate entity-edit events.

Future implementation must connect all three entity types' content, owned-link, lifecycle and successor paths to permanent release protection and transactionally coordinated current-state checks. Existing requirements or closed PBIs alone are not evidence that runtime release integration exists. Preserve REQ-LINK-009 and TC-LINK-025–029; add the revision workflow verification below rather than weakening those cases.

Cross-Product copy/provenance remains outside this increment. REQ-REQ-007 explicitly treats Requirement derivation provenance as retained snapshot evidence, not a live-source release-closure reference: releasing the copy does not traverse or protect the source or require access to it. This does not exempt the copy's ordinary references, implement provenance integration, or decide the rule for other provenance types; those still require an explicit decision (#141–143).

Implementation PBIs may be proposed separately after specification approval; none are created or authorized here. Persistence representation, locking/transaction mechanics, identifier comparison/normalization and length limits, permission provisioning, concrete UI layout/navigation and public contracts require bounded implementation planning/approval. No new packages, persistence strategies, roles, APIs, Test Runs/results or CI integration are authorized. Follow established architecture, shared components and UI catalog when UI implementation is approved.
