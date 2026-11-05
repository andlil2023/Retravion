# Relationship Management

## Originating User Needs

- [UN-REQ-001 — Manage Requirements](../user-needs/UN-REQ-001%20-%20Manage%20Requirements.md)
- [UN-NEED-001 — Manage User Needs](../user-needs/UN-NEED-001%20-%20Manage%20User%20Needs.md)
- [UN-AUDIT-01 — Traceability for Regulatory Compliance](../user-needs/UN-AUDIT-01%20-%20Traceability%20for%20Regulatory%20Compliance.md)
- [UN-AUDIT-02 — Inspect Application Activity](../user-needs/UN-AUDIT-02%20-%20Inspect%20Application%20Activity.md)

## Status and Scope

Approved future behavior from the interview for [PBI #128](https://github.com/andlil2023/Retravion/issues/128), not implemented functionality or verification evidence. Planned verification is in [Relationship Management — Test Specifications](../test-cases/Relationships.md).

The initial scope covers User Needs, Requirements, Requirement–User Need links, and parent–child Requirement links. Implementation is split across [#135](https://github.com/andlil2023/Retravion/issues/135), [#136](https://github.com/andlil2023/Retravion/issues/136), [#137](https://github.com/andlil2023/Retravion/issues/137), [#138](https://github.com/andlil2023/Retravion/issues/138), [#139](https://github.com/andlil2023/Retravion/issues/139), and [#140](https://github.com/andlil2023/Retravion/issues/140). Issue creation is not authorization to begin implementation.

Exact-version links require separately addressable versions. [#132](https://github.com/andlil2023/Retravion/issues/132) specifies their identity/lifecycle in REQ-REQ-006 and REQ-NEED-006 in [Requirement Management](Requirements.md) and [User Need Management](User%20Needs.md): each entity has a stable GUID, each version has a distinct immutable GUID and sequential number, and only one Draft or In Review working version may exist. The approved scope extension in [#144](https://github.com/andlil2023/Retravion/issues/144) revises #132: the latest Approved version may explicitly reopen with approval permission and a mandatory reason only if no newer version exists and no Released revision references it directly or indirectly. A successor Draft with a required immutable change reason remains available and is mandatory for changes to released versions. Earlier versions retain their Approved status and owned links. REQ-LINK-009 defines the common future release-protection rule. Implementing this behavior in [#133](https://github.com/andlil2023/Retravion/issues/133) and [#134](https://github.com/andlil2023/Retravion/issues/134) remains a prerequisite for exact-version relationships.

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
- Approval of an owner requires every ordinary target version to be Approved. Draft and In Review targets block approval; absence of optional links does not. A target's later eligible reopening does not revoke an already Approved owner's approval, but release is blocked until the whole outgoing reference closure is Approved (REQ-LINK-009).
- Approved owners must explicitly reopen under their lifecycle rules before editing owned links. Any owner version protected by a Released revision remains immutable; editable Draft incoming references to it are not frozen merely because their target is protected.

### REQ-LINK-003 — Preserve Exact-Version Traceability

**Parent needs:** UN-REQ-001, UN-NEED-001

**Requirement:** The system shall preserve exact relationship endpoints and require deliberate changes to linked versions.

**Acceptance criteria:**
- Creating or approving a newer target version never silently retargets an existing link.
- A successor Draft version inherits the latest Approved source version's owned ordinary links to the same exact target version GUIDs; earlier versions and links remain unchanged. Successor creation follows REQ-REQ-006 and REQ-NEED-006, including one working version, required immutable change reason, and web-only creation.
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

This direction is deferred to [#141](https://github.com/andlil2023/Retravion/issues/141) and [#142](https://github.com/andlil2023/Retravion/issues/142). Requirement-specific copy-time evidence and audit values are specified by REQ-REQ-007 in [Requirement Management](Requirements.md); User Need provenance remains subject to separately approved specification. Audit destination creation and provenance creation together, with actor/time/action, immutable destination identity, outcome, initial destination values, source identity/version, and preserved copy-time provenance. Apply the same consistency, excluded-event, denied-attempt, failure, and uncertain-outcome decisions as REQ-LINK-006. These future writes are not authorized by #128.

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

## Approved Future Release Protection — PBI #144

### REQ-LINK-009 — Preserve the Released Revision Reference Closure

**Requirement:** A revision shall enter Released state only when every directly or indirectly referenced entity version is Approved, and those exact versions shall thereafter remain immutable.

**Acceptance criteria:**
- Apply this common rule to User Needs, Requirements, and Test Cases. Resolve direct revision references to exact version identities and recursively follow outgoing references, including owned Requirement–User Need, child–parent Requirement, and Test Case–Requirement links. Check the entire closure, not just the directly listed versions or currently visible graph. Repeated references must terminate traversal without omitting reachable versions.
- A Draft or In Review anywhere in that closure blocks release, even if all directly listed versions are Approved. Reopening a target does not automatically revoke referencing owners' approvals; reapproval of the target restores release eligibility only when the entire closure is again Approved.
- Once any Released revision directly or indirectly references a version, preserve its Approved status, content, and owned links permanently. It cannot reopen or edit in place; changes require a successor with its own version identity. Creating or approving successors never changes the released closure or silently retargets its references.
- Only the latest Approved version with no newer version and no Released-revision reference may reopen to Draft, requiring approval permission and a nonblank reason. Preserve decision evidence, invalidate its current approval, and repeat submission/review. Product retirement, authorization, stale-write, history, and feature-specific audit rules apply. No direct Approved → In Review transition is provided.
- Protection follows outgoing references only. Releasing a Test Case can protect its Requirements and their User Needs/parents; releasing a Requirement does not by itself protect every Test Case or child that references it. Eligible Draft owners can change incoming references to protected versions without modifying those protected versions.
- Release validation/protection and concurrent mutations, reopening, or successor creation must be coordinated so no released closure can contain unapproved or subsequently changed versions. Checks must not use stale state or a partial UI graph as evidence of eligibility.
- Requirement cross-Product derivation provenance under REQ-REQ-007 is retained snapshot evidence, not an outgoing ordinary reference in the release closure. Releasing the copy does not traverse or protect the live source or require continued access to it. This exception does not weaken protection of the destination or its ordinary outgoing references, or decide the rule for other provenance types.

This is approved future behavior, not implemented revision machinery. [Revision Management](Revisions.md), specified by [#157](https://github.com/andlil2023/Retravion/issues/157), defines Product-owned revision identity, explicit exact-version selection, Draft → Released behavior, capabilities, retained release evidence, deletion boundaries and release-specific history/audit/failure rules. Its [verification specification](../test-cases/Revisions.md) complements TC-LINK-025–029. Persistence/synchronization and runtime integration require separately approved implementation planning. The explicit Requirement-provenance snapshot exception is specified above and by REQ-REQ-007; it does not implement provenance or revision integration. Other provenance types still require an explicit release-protection decision. Future reference types must honor the release evidence rule; this does not silently exempt indirectly referenced entities. Reopening audit is specified by REQ-REQ-005, REQ-NEED-005, and REQ-TC-006; #144 changes no runtime write paths.

## Boundaries and Follow-up

- Test Cases remain unimplemented. [Test Case Management](Test%20Cases.md) now specifies their fields, cardinality, lifecycle, owned exact Requirement links, and shared-page integration under [#144](https://github.com/andlil2023/Retravion/issues/144). These are separately approved future implementation work, not additions to #128's initial implementation scope.
- No entity-creation dialogs, Test Case implementation, execution/results, Product hierarchy, shared applicability records, automatic synchronization, or release-baseline behavior is introduced here.
- No new MCP relationship operation is specified by this document. New public APIs require separate approval.
- Exact layout, rendering bounds and overflow behavior, target-picker search/pagination, and detailed persistence/provenance representation require implementation planning or the named prerequisite specifications. Version identity and lifecycle are defined by REQ-REQ-006 and REQ-NEED-006. Implementation choices must not silently truncate a view in a way that implies hidden relationships are absent or change these approved behaviors.
- RLC-Tracker's Explore connections and Test Case work were reference material only; this specification neither adopts its architecture nor copies its UI or permission model.
- Documentation-only #128 changes no application business-record writes. All cases in the companion test specification are planned future evidence, not claims of executed runtime verification.
