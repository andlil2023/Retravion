# Product

## Originating User Need

- [UN-PRODUCT-001 — Organize Product Information](../user-needs/UN-PRODUCT-001%20-%20Organize%20Product%20Information.md)

## Requirements

### REQ-PRODUCT-001 — Establish and Retrieve a Product

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to establish and subsequently retrieve a Product with an ID, name, description, and status.

**Acceptance criteria:**
- Each Product has an ID that distinguishes it from other Products.
- A name is required.
- The description may initially be empty.
- A new Product defaults to active.
- The saved name, description, and status are available when the Product is retrieved.

### REQ-PRODUCT-002 — Edit and Preview the Description

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to write and update a Product description in Markdown and preview the rendered description while editing.

**Acceptance criteria:**
- The preview updates as the description is edited.
- The user can arrange the editor and preview side by side or one above the other.
- The saved description can be displayed as rendered Markdown.

### REQ-PRODUCT-003 — Change Product Status

**Parent need:** UN-PRODUCT-001

**Requirement:** The system shall allow a product team member to retire an active Product and reactivate a retired Product.

## Scope

The description is free-form Markdown; users may include intended use and intended users, but these are not required or validated as separate fields. Regulatory-specific fields, description versioning, change reasons, and revision approval are outside this increment.
