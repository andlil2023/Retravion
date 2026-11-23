# Development Specification Entity

**Status:** Approved specification; implementation and detailed integration design remain future work.

## Purpose

A Development Specification describes a bounded change to a Product. It records what is to be implemented, its approval state, relevant traceability, and final implementation evidence. The display name may be **Specification**.

Retravion determines whether a specification is approved for implementation. It does not decide when implementation starts.

## Scope

The first release supports creating, listing, viewing, editing, reviewing, approving and deleting specifications, recording implementation evidence and marking a specification Implemented. Factory scheduling, queue ordering, project-management integration and the detailed factory activity dashboard are separate follow-up work.

## Properties

| Property | Rule |
| --- | --- |
| Product | Required; exactly one Product per specification. |
| ID | Required user-entered string, unique within the Product. Editable in Draft, including after reopening. |
| Title | Required from creation. |
| Description | Markdown; may be empty in Draft but is required before submission for review. May include acceptance criteria and other information. |
| Status | Draft, In Review, Approved or Implemented. |
| Assignee | Optional Retravion user; indicates responsibility, not access rights or factory ownership. |
| Links | Optional links to exact versions of User Needs, Requirements and Test Cases belonging to the same Product. |
| Current AI review | Optional linked review covering impact, risks, feasibility and gaps where applicable. |
| Final evidence report | One linked Markdown report, with screenshots stored in Retravion; required for Implemented. |
| PR URL | Clickable link to the implementation PR; required for Implemented. |

Configurable description templates are deferred.

### Exact-version persistence and coordination

The User Need, Requirement and Test Case stores each expose a minimal Product-scoped projection of their exact version rows. The projection returns the owning record identity for navigation and the version identity, label, version number and approval status; it never follows the default-version projection. An uninitialized owner has no available versions. Specification reads apply existing Product view access and emit no business-change audit.

The Specification owns its link rows, keyed by specification, target kind and exact version UUID. UUIDs are unique within each owning version table. Draft replacement validates the complete set against the owning stores in the caller's transaction; owner version rows are retained when successors are created. The cross-slice check is application-owned, not a cross-schema foreign key. A link replacement locks the Specification row before reading the old set. Its success event (`specification.edit-links`) records the actor, occurrence time, immutable Specification identity, outcome and old/new sets of target kinds and exact version UUIDs in the same transaction (REQ-SYS-AUDIT-003, REQ-AUDIT-01 and REQ-AUDIT-02). Validation failures and no-ops emit no success event. Denials and failed/uncertain writes use the common Specification path, without submitted values in operational logs or automatic retry.

For approval and owner reopening, share the owner projection's `FOR NO KEY UPDATE` lock on the exact version row, through commit in the caller's transaction. Approval takes the Specification row first, then exact rows in kind/UUID order, and checks Product and status after obtaining each lock. Reopening takes that same exact row lock **before** checking for Approved or Implemented references, using a new statement under Read Committed; it must not lock Specification rows or read through the default-version projection. Neither side acquires another slice's schema/owner-wide locks for this check. This per-version boundary leaves unrelated versions and successor creation outside the reopening guard. The approval and reopening commands are separate work; Draft link editing imposes no approval requirement or reopening guard.

## Lifecycle

### Draft

- Users can create and edit Draft specifications. ID and title are required; description may be empty.
- A user may request an optional AI review.
- Submission for review requires a non-empty description.

### In Review

- An authorized user can approve the specification or return it to Draft; a reason for returning is optional.
- An optional AI review may be performed.
- Approval requires every linked User Need, Requirement and Test Case version to be Approved and belong to the same Product.
- Missing link types produce a non-blocking reminder; no explanation is required.

### Approved

- Approved means eligible for implementation; it does not queue or start work.
- An authorized user can return it to Draft with a required reason, provided any active factory run has been confirmed stopped. Returning to Draft invalidates approval; the specification must be reviewed and approved again.
- Assignment may change without returning to Draft.
- Linked approved versions cannot be reopened while referenced by an Approved specification. New successor versions may still be created.

### Implemented

- An authorized user may mark an Approved specification Implemented after its implementation has been merged. A PR URL and final evidence report are required; these may already have been submitted by the factory control service or be supplied by the user.
- Moving to Implemented automatically clears the current assignee; assignment history remains visible.
- Implemented is final: the specification, retained review and evidence cannot be changed or deleted.
- Linked approved versions remain protected from reopening. Further app changes require a new specification.

## AI review

AI review is optional and is not a prerequisite for approval. It helps users identify affected modules, risks, ambiguity, feasibility concerns and gaps that might cause implementation to fail.

- A user initiates the review from an AI terminal and sees the result. The AI asks for explicit user approval before uploading it.
- Retravion retains one current review. Uploading a new review replaces the previous one.
- Returning an Approved specification to Draft marks its current review outdated. Changes to the specification also make a review outdated. It remains visible until replaced.
- An uploaded review is retained if present when the specification becomes Implemented.

Approval remains a human-controlled decision. An organization may authorize a different person to approve the specification from the person who conducted its AI review.

## Assignment and permissions

Assignment is optional and can change in Draft, In Review and Approved. It does not grant or restrict editing rights, and it does not control factory ownership or execution. Permissions govern access. Assignment changes are retained in history.

## Final implementation evidence

The final evidence report is a separate record linked to the specification:

- One final report per specification, written in Markdown.
- Screenshots are uploaded to and stored in Retravion and can be referenced by the report.
- A factory control service can submit the report through an authenticated integration; MCP is not mandated.
- The report should describe outcomes and limitations. It is supporting evidence, not a substitute for final release test approval.
- Earlier factory review iterations are not required to be stored in Retravion.

The initial workflow may use a human to mark the specification Implemented after merge. Automating that transition is future work.

## Deletion

- A specification may be deleted in any state except Implemented.
- Deletion is blocked while an associated run is active or its stopped state is uncertain.
- The user must confirm deletion after being shown the specification identity and warned that its current review, submitted evidence and associated factory activity history will also be removed.
- Late events for a deleted run must be rejected rather than recreating its history.

## Factory and project-management boundary

The intended direction is that a project-management system, such as GitHub or Azure DevOps, signals when work should start. Its PBI can link to a Retravion specification. The control service checks that the specification is Approved before implementation.

The control service owns its execution plan and operational queue. A user-facing run may retain activity, task status, attempts and timings in Retravion, with manual archiving and deletion of completed activity history. The detailed protocol, safe claim and recovery mechanism, dashboard and PBI integration remain separate design work.

If a specification needs changes during implementation, the active run must first be confirmed stopped. The specification can then return to Draft. Existing branch work need not be discarded, but the changed specification requires approval again and the factory must reassess that work before continuing.

## Acceptance criteria

1. Users can list, view and create specifications within a Product.
2. Creation requires an ID and title; duplicate IDs within the Product are rejected.
3. Users can edit Draft specifications, including their ID, subject to uniqueness.
4. A Draft with an empty description cannot be submitted for review.
5. Authorized users can move a specification through the stated lifecycle, including returning it to Draft under the stated conditions.
6. Approval is blocked if any linked exact record version is not Approved or belongs to another Product. Missing link types produce a non-blocking reminder.
7. Linked approved versions cannot be reopened while referenced by an Approved or Implemented specification; successor versions remain possible.
8. An optional current AI review can be uploaded with explicit user approval, replaced, and identified as outdated after relevant changes.
9. Assignment is optional, can be changed independently of content approval, and is cleared on implementation while retaining history.
10. Implemented requires a PR URL and final evidence report; the report supports stored screenshots.
11. Implemented specifications cannot be changed or deleted.
12. Deletion requires confirmation and is blocked for an active or uncertain run; associated review, evidence and run history are removed.
13. An authenticated factory control service can submit final evidence without requiring MCP as the integration protocol.

## Open for later design

Exact permissions, review and report formats and limits, screenshot upload mechanics, PR URL validation, and the factory run coordination and recovery protocol.
