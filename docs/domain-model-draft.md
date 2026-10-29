# Domain Model — Working Draft

Interview checkpoints: Products, versioned traceability, and Documents. This is not the complete domain. Rules below were confirmed during the interview; aggregate boundaries remain proposals. This document does not establish implementation invariants or replace [domain.md](domain.md). Feature design must confirm applicable rules before implementation.

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

- Product IDs are stable.
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

Products sharing that hardware advance its revision together. Their purpose, labeling, and firmware can differ. A shared Hardware Design entity is proposed to represent this relationship.

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
- Product release approval is represented by status only for now.

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

## 10. Open questions and scope limits

- Coordination of shared hardware revision changes across Products.
- Exact matrix approval lifecycle; detailed Document lifecycle and storage model.
- Aggregate boundaries and approval lifecycles for User Needs, Features, Requirements, Test Cases, and Documents.
- Test Execution remains unexplored and is later scope; a Test Case is not evidence that a test has been executed.
- Exceptional correction of previously approved compatibility; removal is not part of the current workflow.
- Evidence reuse across approvals and matrices.
- Nested systems and Product reuse across systems are not current requirements.
- Installed customer systems and third-party product lifecycle management are out of scope for now.
