# Product Areas

## Status and scope

Approved direction from the operator interview for [PBI #187](https://github.com/andlil2023/Retravion/issues/187), with bounded implementation decisions approved in [PBI #189](https://github.com/andlil2023/Retravion/issues/189). This specification is not runtime verification evidence; actual results belong in the implementation PR. Cases are in [Product Areas — Test Specifications](../test-cases/Product%20Areas.md).

**Parent need:** [UN-AREA-001 — Organize Product Information by Area](../user-needs/UN-AREA-001%20-%20Organize%20Product%20Information%20by%20Area.md). Existing lifecycle and release rules include REQ-REQ-003/006, REQ-TC-003/005, REQ-LINK-009 and REQ-REV-004. Audit traceability includes REQ-SYS-AUDIT-003; Product-specific audit rules are not automatically generalized.

## Requirements

### REQ-AREA-001 — Manage Product Areas

**Requirement:** The system shall allow authorized Product managers to maintain broad Product-specific areas for classifying Requirements and Test Cases.

- An area has a stable code, a changeable label and active or retired status. Codes are unique within a Product; separate Products may use the same code. A code cannot change or be reused for a different meaning, including after retirement.
- Users with existing Product-management authorization may create areas, rename labels and retire areas. Enforce authorization and Product access server-side and recheck eligibility at mutation time. Authorized readers can inspect existing areas; Product retirement blocks changes, not authorized reads.
- Retired areas remain readable and filterable but cannot be newly selected for classification. Existing classifications remain intact; unrelated edits do not require reclassification.
- Prevent stale writes and concurrent duplicate creation or unsafe mutation/retirement races from overwriting state or violating these rules. Use existing Product-management authorization; there is no area deletion or reactivation. Codes contain 1–100 ASCII letters, digits or hyphens, are trimmed and displayed uppercase, and are compared case-insensitively within the Product. Labels are required and trimmed, with no application-level length limit. These implementation bounds were explicitly approved for PBI #189.

### REQ-AREA-002 — Classify Exact Versions

**Requirement:** A Requirement or Test Case version shall have at most one optional primary area from its own Product, independently of any linked record's area.

- An authorized eligible Draft may be assigned an active area, reassigned to another active area or cleared. An unclassified version remains valid. Cross-Product selection and new selection of a retired area are rejected.
- Apply existing version, authorization, Product retirement, stale-write, review and release-protection rules. An In Review or Approved version is not directly editable; eligible reopening or a successor follows existing rules. A Released-protected version cannot be changed in place.
- A successor Draft initially inherits the exact source version's area, including a retired one. Retaining that inherited area during unrelated edits is not a new selection; after changing or clearing it, selecting that retired area again is prohibited. Earlier versions and history remain unchanged.
- Area classification is not an ordinary record relationship and does not imply test coverage, completeness of impact analysis or a Feature relationship. It is metadata, not part of a permanent Requirement or Test Case identifier.

### REQ-AREA-003 — Find Classified Information

**Requirement:** The web application shall let authorized users filter the Product's Requirement and Test Case lists by area.

- Each list offers all records, a specific Product area (including retired areas), and unclassified records. Each row is filtered using the area of the version it displays: working Draft/In Review when present, otherwise latest Approved, consistent with REQ-REQ-002 and REQ-TC-002.
- Area filtering combines with existing search and filters and remains scoped to accessible Product records. An area code need not match free-text search. Exact-version details show that version's classification, not a silently substituted version.

### REQ-AREA-004 — Preserve Release-time Area Evidence

**Parent requirements:** REQ-LINK-009, REQ-REV-004.

**Requirement:** Release shall retain the area code and label at release for each classified Requirement and Test Case version in the complete frozen baseline.

- Retain an explicit unclassified state where applicable. Resolve the label current at release, including for an inherited retired area; later renaming or retirement cannot rewrite an earlier released view. Current work can display the current label, while released revision evidence shows the retained release-time label and code.
- Preserve multiple exact versions separately and include both explicitly selected and indirectly included members of the complete outgoing closure; a partial graph is never sufficient. A released baseline read uses frozen area evidence, not a current-label lookup as a substitute.
- Coordinate area rename/retirement, classification changes and release so the committed evidence reflects a consistent release-time state. Release, full area evidence, protection, history and audit commit atomically under REQ-REV-004/006; failed or uncertain commits follow those requirements. No new persistence strategy is selected.

### REQ-AREA-005 — Retain Area Change History and Audit

**Parent requirement:** REQ-SYS-AUDIT-003.

**Requirement:** Area-management and classification mutations shall retain attributable history and required audit atomically with business changes.

All successful operations below retain actor, occurrence time, distinct action, immutable Product and target identity, success outcome, and the stated initial or old/new values. Retain event-time values despite later changes. Authorized reads, navigation, filtering, previews, cancellation, current no-op saves, ordinary validation and uniqueness failures, invalid/cross-Product/retired selections, lifecycle/retirement conflicts and stale/concurrency rejections create no change event or successful history. Unauthorized mutation attempts still require denied-attempt events, even when input is invalid.

| Write path | Required successful event/history values | Planned verification |
|---|---|---|
| Create Product area | Product and immutable area identity; initial code, label and active status. | TC-AREA-001, 007–010 |
| Rename Product area | Product and immutable area identity, stable code, old/new label and unchanged status. | TC-AREA-001, 007–010 |
| Retire Product area | Product and immutable area identity, stable code/label and old/new status. | TC-AREA-001, 007–010 |
| Create Requirement or Test Case with area | Existing creation event retains immutable entity/version identity and initial area or explicit absence, in addition to existing initial values. | TC-AREA-002, 007–010 |
| Change or clear Draft classification | Existing owning-version edit event retains entity/version identity and old/new area identities and event-time codes/labels or absence. | TC-AREA-002, 007–010 |
| Create successor | Existing successor creation event retains source/new version identities and initial inherited area or absence; no duplicate classification event merely for inheritance. | TC-AREA-003, 007–010 |
| Release revision | Existing release event/history and frozen evidence retain complete exact-version closure and release-time code/label or absence; no per-member classification change event merely for snapshotting. | TC-AREA-005–010 |

Commit each business change, applicable history and required audit together. Business, history, evidence or audit submission/write failures before commit prevent the whole change and success. Report success only after confirmed commit. If an attempted commit's outcome is uncertain, report uncertainty rather than success or rollback, require authorized outcome retrieval before any user-directed retry, and never automatically retry. Retrieval must distinguish committed from uncommitted outcomes without exposing inaccessible records; public retrieval contracts require separate approval.

Unauthorized attempts produce a failure event with attempted action, time, actor if known otherwise unknown, failure outcome and unknown target, without submitted identifiers, labels, codes or other submitted values. Denial remains effective if audit submission fails or throws; operational logs omit submitted values, credentials and provider exception details, and are not evidence of a persisted event. Reads and ordinary validation failures do not require change events; unauthorized attempts remain auditable regardless of invalid input.

## Approved implementation boundaries — PBI #189

Existing current versions migrate as unclassified, without invented area-change history. Pre-existing released evidence without an area snapshot displays **Not recorded**, not Unclassified and not a current-label lookup. New releases store explicit absence or area identity/code/label for every Requirement and Test Case exact version in the complete closure.

Cross-Product Requirement copies start unclassified: no Product-local area identity or code match transfers to the destination. Existing MCP creation, editing and response contracts remain unchanged; MCP edits preserve the owning version's classification, and area management is web-only. No new external APIs or packages are introduced. Internal web outcome retrieval uses immutable operation identities and atomic owning-feature history to distinguish committed and uncommitted writes before user-directed retry; it is authorization checked and Product scoped.

The bounded persistence change uses the existing PostgreSQL transaction and lazy-schema conventions: Product area/current history, version-local nullable area identities, owning-feature write history/operation receipts, and frozen release JSON. It does not introduce a new persistence strategy. All participating writes and release resolution share the existing entity advisory-lock order; area changes join that lock boundary. A changed release-time label invalidates the baseline confirmation token.

Any new public contract or its behavior requires separate approval. Test Case and Requirement identifier formats remain unchanged by this specification. Feature/Module entities, Test Execution, integrations and automated impact analysis are outside scope.

These rules are not a claim of regulatory compliance. Runtime verification requires focused security/audit tests, disposable PostgreSQL execution and independent review against the current implementation diff.
