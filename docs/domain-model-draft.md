# Domain Model — Working Draft

Interview checkpoints: Products, versioned traceability, and Documents. This is not the complete domain. The original rules were confirmed during the interview; additions labeled **Review proposal** and aggregate boundaries remain proposals, except for the direction explicitly approved under [PBI #108](https://github.com/andlil2023/Retravion/issues/108) in sections 7.1 and 8.1. That approval covers domain design only, not application implementation or the unresolved lifecycle decisions in section 10. This document does not establish implementation invariants or replace [domain.md](domain.md). Feature design must confirm applicable rules before implementation.

## 1. Products and revisions

```mermaid
classDiagram
    class Product {
        ProductId
        Name
        Kind
        RequiresCompatibilityMatrix
    }
    class ProductRevision {
        RevisionId
        DisplayLabel
        Status
    }
    Product "1" --> "0..*" ProductRevision : has
    ProductRevision "1" --> "0..*" ProductRevision : includes exact component revisions
```

**Product kinds:** System/platform, Software application, Hardware device, Firmware.

- Product internal identities are stable. In the implemented Product model, `Product.InternalId` is the immutable identity used for relationships; the user-visible `Product.Id` is editable and must not be used as a relationship key.
- Revision labels need not be unique; references use revision IDs.
- A system revision references constituent revisions; it does not own their Products.

## 2. c4D example

```mermaid
flowchart TD
    R["c4D revision"]
    R --> A["Catalyst Client revision"]
    R --> B["Sentinel Client revision"]
    R --> C["Office Client revision"]
    R --> D["Server revision"]
    R --> M["Approved compatibility matrix revision"]
    M --> S["Supported combinations:<br/>applications, hardware, firmware,<br/>drivers and external systems"]
```

**Composition** says what is included. **Compatibility** says what works together.

## 3. Shared hardware, separate products

```mermaid
flowchart TD
    H["Shared hardware design"]
    H --> HR["Hardware revision .02"]
    P1["HZ-002 revision<br/>Label: HZ-002.02"] --> HR
    P2["HZ-002-E revision<br/>Label: HZ-002.02-E"] --> HR
    P1 --- F1["Own firmware product<br/>and revision history"]
    P2 --- F2["Own firmware product<br/>and revision history"]
```

Products sharing that hardware are intended to adopt hardware revisions together. Their purpose, labeling, and firmware can differ. A shared Hardware Design entity is proposed to represent this relationship.

**Review proposal:** A Hardware Design revision identifies the shared physical design; a Hardware Product Revision identifies a particular Product baseline that references that design revision. They are distinct identities. A design change creates a new Hardware Design revision; it never updates references in released Product Revisions. Coordinated adoption means creating new Product Revisions that reference the new design, not advancing existing released baselines. The coordination workflow remains open.

## 4. Revision lifecycle

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Released
    Released --> Unsupported
```

- Draft composition may reference Draft components.
- All components must be Released before the system revision is Released.
- If the Product requires a matrix, an approved matrix revision is mandatory.
- Released contents are immutable; Unsupported status preserves the released definition.

**Review proposals:**

- Component release eligibility is checked at the time of system release. A component later becoming Unsupported does not rewrite or automatically invalidate an existing parent baseline. An Unsupported component is not eligible for a new system release under the Released-component rule above.
- Release approval records the approver, timestamp, decision, and comment against the exact baseline being approved; status alone is not the approval record. One approver is sufficient for this proposal. Detailed approval workflow remains open.
- The release baseline preserves the matrix policy applied at release and the original approved matrix reference, when required. Product policy changes apply to future releases; any obligation imposed on existing releases requires a separate explicit decision, not reinterpretation of their release history.

## 5. Compatibility and traceability

```mermaid
flowchart LR
    P["Top-level Product<br/>One logical matrix"] --> M["Matrix revisions"]
    R["Product revision"] -->|original| M1["Matrix revision 1"]
    R -->|current| M2["Matrix revision 2"]
    M2 -->|supersedes| M1
    M2 --> A["Approval<br/>Approver · timestamp<br/>decision · comment"]
    M2 --> E["Verification evidence<br/>Retravion documents / external links"]
```

- A responsible user, such as a product owner, configures the matrix requirement per Product.
- One logical matrix belongs to the top-level Product; constituent Products do not automatically need separate matrices.
- Each matrix revision applies to **exactly one Product Revision**.
- Approved matrix revisions are immutable.
- A new Product Revision needs a new matrix revision when a matrix is required.
- Later compatibility approval creates a new matrix revision, not new product contents. Applying it requires approval, a comment, and verification evidence links.
- Original/current references and change history are preserved; current means explicitly approved for that release, not automatically newest.
- One approver, timestamp, decision, and comment are recorded; multiple approvers are not currently needed.
- Compatibility uses explicitly supported versions, including hardware revisions and external-system supplier/model/software versions. External dependencies can also have qualified requirements such as a Windows-provided driver.

**Review proposals:**

- The original matrix reference belongs to the immutable release baseline. Later compatibility assessments and their application records are separate from product contents. For later updates, one approval decision both approves the exact matrix revision and applies it to its one target Product Revision; a separate application approval is not proposed.
- Application history is append-only and identifies the target Product Revision, exact matrix revision, approver, timestamp, decision, comment, and evidence references. The current matrix is derived from this history, not from the newest matrix revision or a mutable field inside the frozen baseline.
- Supported entries describe combinations, not independent lists whose cross-product is implicitly supported. An entry may use exact versions or explicit qualified constraints; support is claimed only when the entire assessed configuration matches one entry. Each entry identifies its assessed dependency scope. An omitted dependency is unassessed, not unrestricted or implicitly not applicable; not-applicable dependencies must be explicit. An unlisted combination is unassessed, not necessarily known to be incompatible, and must not be presented as supported.
- Supersession and withdrawal are append-only dispositions; neither edits nor deletes an approved matrix, its evidence references, or its approval history. A withdrawal identifies the matrix, actor, timestamp, and reason. A withdrawn matrix cannot be current; withdrawal does not automatically restore an older matrix. If no replacement is explicitly approved, there is no current approved matrix, while the original release reference remains preserved. The detailed exceptional-correction workflow remains open.

## 6. Proposed DDD classification

An **entity** has stable identity. An **aggregate** is a consistency boundary with a root controlling its changes. A **value object** is defined by its values rather than independent identity.

| Concept | Proposed classification |
|---|---|
| Product | Aggregate root — identity, kind, matrix policy |
| Product Revision | Aggregate root — lifecycle and exact composition |
| Hardware Design | Aggregate root candidate — shared hardware revision history |
| Compatibility Matrix Revision | Aggregate root candidate — supported combinations and approval |
| Composition entry | Value object — component revision reference |
| Compatibility combination | Value object — supported component/dependency constraints |
| Approval record | Immutable value object |
| IDs, revision label, product kind, status | Value types |
| User Need, Feature, Requirement, Test Case, Document | Independently versioned entities; aggregate boundaries still open |
| Baseline selection and traceability link | Value object candidates — exact version references and relationships |
| Document type | Value type candidate — classification, not a separate entity per type |

References between proposed aggregates use IDs. A system is not one enormous aggregate containing all Products.

## 7. Needs, Features, Requirements, and Test Cases

```mermaid
flowchart LR
    N["User Need version<br/>Why a change is needed"] --> R["Requirement version<br/>Verifiable obligation"]
    N --> F["Feature version<br/>Concrete capability · optional"]
    F --> R
    I["Other design input /<br/>risk mitigation"] --> R
    R --> T["Test Case version"]
```

- A User Need describes a desired change, missing functionality, or similar high-level need.
- A Feature is a concrete capability driven by a User Need. A need may require several Features.
- Valid paths: **Need → Requirement → Test Case**, **Requirement → Test Case**, and **Need → Feature → Requirement → Test Case**.
- Requirements must be verifiable; neither a User Need nor a Feature is mandatory for a Requirement.
- A Feature is verified through its linked Requirements and their Test Cases.
- Need–Feature, Need–Requirement, Feature–Requirement, and Requirement–Test Case links can be many-to-many. A Test Case usually verifies one Requirement; a Requirement may need several Test Cases.
- User Needs, Features, Requirements, and Test Cases can be shared across Products and have independent version histories.

### 7.1 Approved design direction — shared User Needs (#108)

**Design approval only:** The following conceptual contracts distinguish documentation ownership, system composition, and need applicability. They are not approved public APIs, persistence entities, aggregate boundaries, or permission to implement application behavior.

| Concept | Responsibility |
|---|---|
| User Need | Stable `UserNeedId`, one responsible Product identified by its immutable internal identity, and an independent version history. |
| User Need Version | Immutable `UserNeedVersionId`, parent `UserNeedId`, and versioned title and need statement. Editing content creates a new version; earlier versions remain retrievable and unchanged. |
| Requirement Version | An exact immutable version of a verifiable requirement; its detailed lifecycle remains separate design work. |
| Applicability entry | An exact User Need version and exact target Product Revision, scoped to the baseline recording the applicability decision. |
| Addresses link | An exact Requirement version addressing an exact User Need version, scoped to a baseline. |
| Revision baseline | Exact composition, selected documentation versions, applicability entries, and traceability relationships. |

- **Ownership** assigns responsibility for maintaining a need. For a shared c4D need, the owner is the c4D system Product.
- **Composition** identifies exact constituent Product Revisions. The system does not own the constituent Products.
- **Applicability** explicitly identifies which selected need versions apply to which exact Product Revisions. Ownership does not imply applicability, and composition does not automatically apply every system need to every component.
- Sharing a need neither duplicates its identity nor transfers ownership. c4D is a Product of kind System; no separate bundle entity is introduced.
- Addresses links are many-to-many: a need version can be addressed by several requirement versions, and a requirement version can address several need versions. A Requirement can exist without a User Need.
- References use immutable identities, not editable Product IDs, revision labels, or a pointer to the latest version.

**Comparison with the current model:** `src/Retravion.Domain/Product/Product.cs` implements Product identity and active/retired lifecycle, not Product Revisions. User Need, Requirement, Product Revision, and baseline models are not implemented. Sections 7 and 8 provide the existing draft direction for independently versioned documentation and exact-version baselines. REQ-PRODUCT-001 and REQ-PRODUCT-005 constrain Product identity and editing, but do not specify this new capability; no new requirement IDs are assigned by this design.

## 8. Product Revision baselines

```mermaid
flowchart LR
    A["Product Revision A"] --> R1["Requirement V1"]
    B["Product Revision B"] --> R2["Requirement V2"]
    R1 -->|verified by in A| T["Test Case V1"]
    R2 -->|verified by in B| T
```

- Each Product Revision selects exact User Need, Feature, Requirement, and Test Case versions as applicable, together with their traceability links.
- Released baselines preserve both selected versions and relationships; later changes do not alter earlier baselines.
- Updating an item does not automatically update related items. Requirement V2 can still be verified by Test Case V1 if the procedure remains suitable.
- Verification mappings belong to the Product Revision baseline: adding the V2 → V1 mapping does not modify Test Case V1 or the earlier baseline.

**Review proposals:**

- Within one Product Revision baseline, select at most one version of each logical User Need, Feature, Requirement, or Test Case. Every baseline traceability link references exact endpoint versions selected in that same baseline; selecting Requirement V2 cannot satisfy a link to Requirement V1.
- Referencing a component Product Revision does not automatically import or flatten its selected items and traceability links into the parent baseline. Component baselines retain their own context. Items needed for parent-level traceability must be explicitly selected, with links satisfying the same endpoint rule. Different component baselines may retain different versions of a shared item; this does not silently resolve them into one parent selection.
- These consistency rules do not require every Requirement to have a User Need or Feature, and do not make optional Documents mandatory.

### 8.1 Approved design direction — applicability and historical traceability (#108)

For User Needs and Requirements, the following consistency rules are approved as domain design. The corresponding rules above for Features and Test Cases, and other unrelated Review proposals, remain proposals.

- Within one baseline, select at most one version of each logical User Need or Requirement. Every baseline-local addresses link has both exact endpoint versions selected in that baseline.
- Constituent baselines retain their own selections and traceability. Referencing a constituent revision does not automatically import, flatten, or reconcile its documentation. Different constituents can retain different versions of a shared need; eligibility of such divergence for a new system release remains undecided.
- Parent-level traceability explicitly selects the relevant need and requirement versions and preserves the exact constituent baseline context from which a requirement was selected. It does not substitute for or modify constituent traceability.
- Applicability changes are baseline changes, not edits to previously approved applicability. Approved/released baseline contents and relationships cannot change in place.
- Approval identifies the exact baseline being approved, not merely a status. It preserves constituent revision identities, selected documentation versions, applicability decisions, addresses links, and approval identity, actor, time, and decision.
- The approved baseline preserves release-time Product labels or equivalent historical display information, so later Product renaming does not misrepresent the approved configuration.
- Later compatibility assessments remain separate from frozen Product contents, as described in section 5. This approval does not resolve the detailed release or compatibility approval workflows.

#### Worked example: one need, two client Products

The following labels and behaviors are illustrative, not allocated requirement IDs or approved application requirements. Notification details and thresholds would need separate approval.

**Shared need N, version 1:** Operators need to recognize a loss of connection to the system. Its responsible Product is c4D.

| Constituent baseline | Selected need | Addressing requirement |
|---|---|---|
| Catalyst Client revision C7 | N/v1 | Catalyst requirement A/v1: display a persistent connection-loss banner. |
| Sentinel Client revision S4 | N/v1 | Sentinel requirement B/v2: display a connection-loss indicator and audible notification. |

Each client baseline selects its exact requirement and N/v1, records applicability to that client revision, and retains its addresses link. Both requirements address the same need version without duplicating the need.

An approved **c4D revision D3** preserves:

- Exact constituent references to C7 and S4, together with its other selected component revisions.
- Explicit parent-level selections of N/v1, A/v1, and B/v2.
- Applicability of N/v1 to C7 and S4.
- Parent-level addresses links A/v1 → N/v1 and B/v2 → N/v1, with C7 and S4 respectively retained as their constituent baseline contexts.
- Its exact-baseline approval record and historical Product display information.

These parent selections are explicit, not an automatic import of component documentation. Omission of another component from this illustrative applicability set does not decide whether it is inapplicable or whether release coverage is sufficient; coverage policy remains open.

If N/v2 is later created and Catalyst C8 adopts it, D3 still references C7, S4, and N/v1, with its original applicability and links. A subsequent c4D revision makes new selections explicitly. New need versions, constituent revisions, or applicability decisions never advance an earlier approved baseline automatically.

## 9. Optional, versioned Documents

```mermaid
flowchart LR
    F["Feature version"] -.->|optional rationale| A["Document version<br/>Type: ADR"]
    P["Product Revision"] -.->|optional| R["Document version<br/>Type: Release notes"]
```

- One basic **Document entity** with a **Document type**, not separate entities for ADRs, release notes, or verification reports.
- Products, User Needs, Features, Requirements, and Test Cases may all link to Documents; these links are optional.
- Documents have version history. Each link selects an exact Document version rather than following the latest version.
- Verification evidence may be stored in Retravion or linked externally, as described in section 5.

**Review proposals:**

- Document relationships included in a released baseline are frozen with their exact Document versions. Adding a new version or changing a live Product-level document link does not change a historical baseline. Later compatibility evidence belongs to its separate matrix approval history, not to an amendment of the release baseline.
- An external URL alone is a convenience link, not an immutable identification of approval evidence. External approval evidence must identify the reviewed artifact and its exact external revision, with a retained copy or content hash where necessary to distinguish the reviewed content from later changes. A hash identifies content but does not ensure it remains retrievable; evidence retention and storage details remain open.

## 10. Open questions and scope limits

- Confirmation of the remaining Review proposals during feature design; the #108 design approval is limited to sections 7.1 and 8.1 and is not application implementation approval.
- User Need ownership transfer: one responsible Product is the approved direction, but transfer behavior remains undecided.
- Whether User Need versions require separate approval before baseline selection.
- Release coverage: whether every applicable need must have an addressing requirement and how justified gaps are represented.
- Whether a new system release may retain different need versions across constituents, and what assessment is required.
- Retirement, withdrawal, deletion, and exceptional correction of needs or their versions without losing historical references.
- Implementation boundaries for this design: aggregate ownership, authorization, retention, atomic approval, and concurrency validation. These must be resolved by the affected implementation slices rather than inferred here.
- Audit decision for #108: N/A — this documentation-only task introduces no business-record write operations. Future write operations require explicit audit decisions and verification plans; Product-specific audit policy is not automatically a policy for User Needs or revision baselines.
- Change / ControlledChange is outside #108's scope.
- Coordination workflow for shared hardware revision adoption across new Product Revisions.
- Detailed matrix and release approval workflows within the proposed historical boundaries; detailed Document lifecycle, evidence retention, and storage model.
- Aggregate boundaries and approval lifecycles for User Needs, Features, Requirements, Test Cases, and Documents.
- Test Execution remains unexplored and is later scope; a Test Case is not evidence that a test has been executed.
- Detailed exceptional-correction workflow for previously approved compatibility, including the proposed withdrawal and replacement behavior; deleting approval history is not proposed.
- Evidence reuse across approvals and matrices.
- Nested systems and Product reuse across systems are not current requirements.
- Installed customer systems and third-party product lifecycle management are out of scope for now.
