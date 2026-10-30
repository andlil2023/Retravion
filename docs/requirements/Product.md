# Product

## Originating User Need

- [UN-PRODUCT-001 — Organize Product Information](../user-needs/UN-PRODUCT-001%20-%20Organize%20Product%20Information.md)

## Implementation Status

The requirements below describe the agreed Product behavior. The identity, kind, list, view, edit, and lifecycle refinements documented here are **approved for future implementation**; their presence is not evidence that the current software implements or verifies them. Existing Product behavior remains in place until the relevant implementation work is completed.

## Requirements

### REQ-PRODUCT-001 — Establish and Retrieve a Product

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to establish and subsequently retrieve a Product with an internal identity, Product ID, name, kind, description, and status.

**Acceptance criteria:**
- Each Product has a generated, immutable internal UUID that is not displayed to or editable by users. Internal relationships use this identity rather than the user-visible Product ID.
- A user-entered Product ID is required. Leading and trailing whitespace is trimmed; a value empty after trimming is rejected. Other characters, including internal spaces and non-ASCII characters, are allowed.
- Product IDs are unique case-insensitively, including IDs held by retired Products. Entered capitalization is preserved for display.
- A name is required and trimmed at both ends; a value empty after trimming is rejected. Different Products may have the same name.
- A kind is required, chosen from System, Software application, Hardware device, and Firmware.
- The description is optional and may be empty.
- A new Product defaults to active.
- The saved Product ID, name, kind, description, and status are available when the Product is retrieved.

### REQ-PRODUCT-002 — Edit and Preview the Description

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to write and update a Product description in Markdown and preview the rendered description while editing.

**Acceptance criteria:**
- The preview updates as the description is edited.
- The user can arrange the editor and preview side by side or one above the other.
- The saved description can be displayed as rendered Markdown.
- Editing is subject to the retired-Product rules in REQ-PRODUCT-005 and REQ-PRODUCT-003.

### REQ-PRODUCT-003 — Change Product Status

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to retire an active Product and reactivate a retired Product.

**Acceptance criteria:**
- Retired Products remain retrievable and visible in the Product list; retirement does not delete a Product.
- A retired Product's fields are read-only until the user selects reactivation in the edit view.
- Both retirement and reactivation are initiated from the edit view and take effect only after Save and confirmation. Cancelling confirmation leaves the pending edit state available for further editing or another save attempt.
- Before selecting retirement, the user must save or discard pending field changes. The user can undo the pending retirement selection and continue editing instead.
- After selecting reactivation, the user may edit fields before saving; confirmed Save applies reactivation and any field edits together. Until then the persisted Product remains retired.
- A reason is not required in this increment.

### REQ-PRODUCT-004 — List and Find Products

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall show a Product list from Product navigation and allow a product team member to find Products by ID or name.

**Acceptance criteria:**
- The list displays Product ID, name, kind, and active/retired status, and shows both statuses by default.
- The user can filter by active or retired status.
- Search matches Product ID and name case-insensitively.
- Selecting a Product opens its read-only view.

### REQ-PRODUCT-005 — View and Edit Product Details

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall provide a read-only Product view and a separate edit view for maintaining Product details.

**Acceptance criteria:**
- The view offers an Edit action; field editing and status actions are not performed on the read-only view.
- In the edit view, a user may change the Product ID, name, kind, and description of an active Product, subject to REQ-PRODUCT-001 and REQ-PRODUCT-002. The internal ID cannot be changed.
- An edited Product ID remains subject to required, trimming, and case-insensitive uniqueness rules; after a successful change the previous ID may be reused.
- Retired Product fields remain read-only until reactivation is selected, as specified in REQ-PRODUCT-003.
- Field changes take effect on Save; reactivation with edits is saved together as specified in REQ-PRODUCT-003.

### REQ-PRODUCT-006 — Trace Product Changes

**Parent needs:** UN-PRODUCT-001, UN-AUDIT-01, UN-AUDIT-02

**Requirement:** The system shall audit Product creation, actual edits (including description), retirement, reactivation, and unauthorized Product-change attempts as specified in REQ-SW-AUDIT-007–009.

**Acceptance criteria:**
- Successful changes retain immutable identity and initial or old/new values, including the Product ID at the event.
- Reactivation with edits commits as one Product change and one audit event.
- Product changes and their audit events commit together; unconfirmed commit outcomes are reported as uncertain.
- Reads, no-op saves, and ordinary validation failures produce no Product change event.
- Unauthorized changes remain denied; their audit records and audit-failure operational logs omit submitted field values.

## Scope

The description is free-form Markdown; users may include intended use and intended users, but these are not required or validated as separate fields. Product deletion, Product Revisions, compatibility matrices and entity linking, regulatory information objects, configurable kinds, Product ID generation templates, readiness checks, and new roles or permissions are outside this increment. Status-change reasons and a Product-specific history UI remain deferred. Product audit recording is covered by REQ-PRODUCT-006; earlier historical gaps remain as-is and are not filled retrospectively. The effect of retirement on future linking is undecided. No migration is required for this increment: development uses a new empty database.
