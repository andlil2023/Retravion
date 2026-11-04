# Relationship Management

## Originating User Needs

- [UN-REQ-001 — Manage Requirements](../user-needs/UN-REQ-001%20-%20Manage%20Requirements.md)
- [UN-NEED-001 — Manage User Needs](../user-needs/UN-NEED-001%20-%20Manage%20User%20Needs.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

## Status and Scope

Approved future behavior from the interview for [PBI #128](https://github.com/andlil2023/Retravion/issues/128), not implemented functionality or verification evidence. Planned verification is in [Relationship Management — Test Specifications](../test-cases/Relationships.md).

The initial scope covers User Needs, Requirements, Requirement–User Need links, and parent–child Requirement links. Implementation is split across [#135](https://github.com/andlil2023/Retravion/issues/135), [#136](https://github.com/andlil2023/Retravion/issues/136), [#137](https://github.com/andlil2023/Retravion/issues/137), [#138](https://github.com/andlil2023/Retravion/issues/138), [#139](https://github.com/andlil2023/Retravion/issues/139), and [#140](https://github.com/andlil2023/Retravion/issues/140). Issue creation is not authorization to begin implementation.

Exact-version links require separately addressable versions. The interview approved creating a new Draft when changing an Approved Requirement or User Need, preserving the Approved version and its links. [#132](https://github.com/andlil2023/Retravion/issues/132) must reconcile the existing reopening specifications in [Requirement Management](Requirements.md) and [User Need Management](User%20Needs.md) and settle version identity/lifecycle details before implementation in [#133](https://github.com/andlil2023/Retravion/issues/133) and [#134](https://github.com/andlil2023/Retravion/issues/134). This document does not silently replace their existing lifecycle requirements.

## Requirements

### REQ-LINK-001 — Own Ordinary Relationships

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The system shall maintain ordinary relationships between exact entity versions within one responsible Product, with an explicit link owner.

**Acceptance criteria:**
- A Requirement version owns its links to exact User Need versions. A Requirement can address multiple User Needs; a User Need can be addressed by multiple Requirements.
- User Need links are optional, including when approving a Requirement.
- A child Requirement version owns its link to an exact parent Requirement version and may have zero or one parent.
- Parent hierarchies prohibit self-parenting and circular ancestry, including cycles involving different versions of the same Requirement identities. Different version identities must not provide a way to bypass the cycle rule.
- Ordinary links do not cross Product boundaries. Link ownership is distinct from which endpoint is currently central or expanded in the view.

### REQ-LINK-002 — Control Link Changes by the Owning Version

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The system shall permit ordinary link mutations only when the owning version is Draft and the caller can edit that owner.

**Acceptance criteria:**
- The same permissions as content editing apply; being visible in a graph does not grant mutation permission.
- A Draft owner may link to Draft, In Review, or Approved target versions.
- In Review and Approved owners cannot change their links. An Approved target does not prevent an editable Draft owner from adding or removing its incoming reference.
- These rules apply to central and expanded entities alike, including incoming links owned by connected Draft entities.
- Existing Product-retirement and stale-write protections apply to relationship mutations. Retired-Product records remain readable, but their links cannot be changed.
- Replacing a parent is atomic: either the new parent replaces the old one, or the old link remains intact. Removing a parent is allowed and preserves the child's own children and other links.
- Approval of an owner requires every ordinary target version to be Approved. Draft and In Review targets block approval; absence of optional links does not.

### REQ-LINK-003 — Preserve Exact-Version Traceability

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The system shall preserve exact relationship endpoints and require deliberate changes to linked versions.

**Acceptance criteria:**
- Creating or approving a newer target version never silently retargets an existing link.
- A new Draft version inherits its predecessor's owned ordinary links to the same exact target versions; earlier versions and links remain unchanged.
- The view displays the exact linked version and flags when a newer target version is available. Newer does not imply that the target is suitable or Approved.
- Users can inspect the proposed target version and explicitly confirm a change showing old and new targets, only while authorized to edit the Draft owner.
- Retargeting is atomic, obeys Product and hierarchy rules, and preserves the old endpoint on failure or stale rejection.
- A later parent version does not automatically detach children or otherwise change their links.

### REQ-LINK-004 — Explore Relationships from an Exact Entity Version

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The web application shall provide one shared graphical relationship page accessible from User Need and Requirement details.

**Acceptance criteria:**
- A button such as Links opens the exact selected version as the central entity, including historical or Approved versions; the page never silently substitutes the latest version.
- Initially show direct incoming and outgoing relationships, with relationship labels, direction, entity identity, exact version, status, and owning Product.
- Clicking a connected entity makes it the new center. A separate expand/collapse action reveals or hides indirect branches without changing the center.
- Repeated expansion can reveal a complete supported chain; repeated entities must not cause unbounded expansion. Test Case chains become available only after their separate feature and integration exist.
- Collapsing a branch changes presentation only, never deletes a link.
- Read-only and editable states are explained with status/text and, where useful, lock icons; color alone is insufficient. Indicators reflect the link owner, not a misleading blanket lock on all references to an Approved entity.
- A keyboard-accessible table/list alternative provides equivalent exploration and link-management actions, permissions, and exact-version information.

### REQ-LINK-005 — Deliberately Add, Remove, and Retarget Links

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The shared page shall enable users to manage permitted ordinary links on central and expanded entities through explicit, individually saved actions.

**Acceptance criteria:**
- Adding a link selects an existing entity and exact target version, followed by an explicit Add link action. Entity creation is outside this page.
- Removing a link requires confirmation naming the relationship and both endpoints.
- Changing a linked version shows the old and new exact versions and requires explicit confirmation.
- Each operation saves immediately; there is no accumulated page-wide pending-change Save workflow.
- Actions are available equivalently in graph and table/list workflows and use the same authorization and validation rules as the underlying mutations.
- Report confirmed success or failure clearly. Never imply a failed or uncertain operation succeeded; uncertain outcomes follow REQ-LINK-006.

### REQ-LINK-006 — Record Relationship History and Audit

**Parent needs:** UN-AUDIT-01, UN-AUDIT-02, UN-REQ-001, UN-NEED-001

**Parent requirement:** REQ-SYS-AUDIT-003

**Requirement:** The system shall retain successful ordinary relationship changes in the owning entity's history and audit attributable changes and denied mutation attempts.

**Acceptance criteria:**
- Cover add, removal, parent replacement, target-version change, and link inheritance during new Draft creation. A replacement/retarget is one old/new change, not a committed removal followed by a separate addition. Inherited links are initial values of the new-version creation operation; do not add duplicate per-link success events for that creation.
- History and audit retain actor, occurrence time, distinct action, immutable owning entity and version identity, success outcome, relationship type, and initial or old/new exact endpoints. Later record changes do not rewrite event-time values.
- Approval paths changed by relationship checks retain the applicable existing review-history/audit requirements, including REQ-REQ-005 and, for future User Need lifecycle changes, REQ-NEED-005. This document adds no ordinary outgoing User Need relationship.
- Business changes, associated history, and required audit events commit together. Audit submission/write failure or a business/history write failure prevents successful commit of the operation.
- Report success only after confirmed commit. If commit is attempted but its outcome cannot be confirmed, report uncertainty rather than success or confirmed rollback, advise retrieval before retry, and do not retry automatically.
- Unauthorized mutation attempts remain denied and submit failure events containing attempted action, time, actor if known (otherwise unknown), failure outcome, and unknown target, without submitted identifiers, field values, or comments.
- If denied-attempt audit submission fails or throws, safely log the audit failure without submitted values, credentials, or provider exception details. Logging neither substitutes for an event nor proves its persistence.
- Reads, navigation, expansion/collapse, unchanged saves, ordinary validation failures, duplicate-link conflicts, hierarchy conflicts, unapproved-target approval rejections, and stale-write rejections produce no change event or successful history entry. Unauthorized-attempt auditing still applies even when submitted input would also fail validation.

These are explicit relationship-specific decisions, not an automatic extension of Product-specific REQ-SW-AUDIT-007–009 or a claim that existing features have been audited.

## Approved Future Provenance Direction — Not Initial Implementation Scope

### REQ-LINK-007 — Independently Derive Across Products

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** Future cross-Product reuse shall create an independent Requirement or User Need with immutable provenance rather than a shared record or automatic version synchronization.

**Acceptance criteria:**
- Creation occurs outside the Links page, using a selected source version. The destination has independent identity, Product ownership, lifecycle, and content.
- Preserve a read-only Derived from reference to the exact source identity/version and source content as it stood when copied, even if that source version was Draft and later changes.
- Cross-Product copies start without ordinary links. Origin references are provenance, not parent–child Requirement links.
- Source changes never automatically update copies. An origin reference is exempt from the rule requiring ordinary targets to be Approved before owner approval.
- Source-read and destination-create permissions apply; copying does not grant access to other Product records.

This direction is deferred to [#141](https://github.com/andlil2023/Retravion/issues/141) and [#142](https://github.com/andlil2023/Retravion/issues/142). Before implementation, their provenance specifications must settle copy-time representation and audit values without discarding content. Audit destination creation and provenance creation together, with actor/time/action, immutable destination identity, outcome, initial destination values, source identity/version, and preserved copy-time provenance. Apply the same consistency, excluded-event, denied-attempt, failure, and uncertain-outcome decisions as REQ-LINK-006. These future writes are not authorized by #128.

### REQ-LINK-008 — Explore Provenance in Both Directions

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The future relationship view shall expose accessible provenance in both directions without permitting origin-link mutation.

**Acceptance criteria:**
- Show Derived from and Source for, distinguishable from ordinary relationships, with owning Product and exact source/copy version context.
- Origin references are the explicit cross-Product exception; ordinary links remain same-Product.
- Display the accessible origin reference, but require explicit expansion before loading the other Product's relationship neighborhood. Clicking an accessible referenced entity may make it the center.
- Reveal only records the caller may access; expansion or refocusing does not bypass permission checks or disclose inaccessible record details.
- Graph and table/list provide equivalent provenance navigation. Origin links remain read-only.

Deferred to [#143](https://github.com/andlil2023/Retravion/issues/143). Provenance viewing performs no business-record writes and requires no change event.

## Boundaries and Follow-up

- Test Cases do not exist in this increment. The agreed future direction is Test Case-owned links to exact Requirement versions, making User Need → Requirement → Test Case chains explorable. Detailed Test Case fields, cardinality, lifecycle, and integration require [#144](https://github.com/andlil2023/Retravion/issues/144) and separately approved implementation work.
- No entity-creation dialogs, Test Case implementation, execution/results, Product hierarchy, shared applicability records, automatic synchronization, or release-baseline behavior is introduced here.
- No new MCP relationship operation is specified by this document. New public APIs require separate approval.
- Exact layout, rendering bounds and overflow behavior, target-picker search/pagination, and detailed versioning/provenance representation require implementation planning or the named prerequisite specifications. They must not silently truncate a view in a way that implies hidden relationships are absent or change these approved behaviors.
- RLC-Tracker's Explore connections and Test Case work were reference material only; this specification neither adopts its architecture nor copies its UI or permission model.
- Documentation-only #128 changes no application business-record writes. All cases in the companion test specification are planned future evidence, not claims of executed runtime verification.
