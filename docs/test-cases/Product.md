# Product — Test Specifications

This design specifies verification intent, not implemented tests or successful verification results. The refined requirements include approved future behavior; the cases below do not assert that the current implementation satisfies it.

**Requirements:** [Product](../requirements/Product.md)

## Test Cases

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result | Automation candidate |
|---|---|---|---|---|---|---|
| TC-PRODUCT-001 | REQ-PRODUCT-001 | Integration | Product creation and retrieval are available. | Create and retrieve two Products with distinct entered IDs and valid kinds, one with a description and one without. | Each has a distinct generated internal UUID and its supplied Product ID, name and kind; both are active. The empty description is accepted. | Yes |
| TC-PRODUCT-002 | REQ-PRODUCT-001 | Integration | Product creation is available. | Try empty and whitespace-only names and Product IDs, and omit kind. | Each invalid creation is rejected; no Product is established. | Yes |
| TC-PRODUCT-003 | REQ-PRODUCT-002 | UI/end-to-end | An active Product exists. | Enter Markdown containing a heading and formatted text; observe the preview while editing. Switch between side-by-side and stacked arrangements, save and retrieve. Edit the saved description, save again, and retrieve. | The preview updates during editing and renders Markdown in both arrangements. The retrieved description renders the saved content initially and after the update. | Yes |
| TC-PRODUCT-004 | REQ-PRODUCT-003 | Integration | An active Product exists. | Retire it and retrieve it; then reactivate it and retrieve it again. | Its retrieved status is retired after retirement and active after reactivation; it remains retrievable throughout. | Yes |
| TC-PRODUCT-005 | REQ-PRODUCT-001, REQ-PRODUCT-005 | Integration | Creation and editing are available. | Save an ID with surrounding whitespace, mixed capitalization, internal spaces and non-ASCII characters; try creating another Product using the same ID with different capitalization. Retire the first Product and try again. | The ID is trimmed and its capitalization and internal characters are preserved. Both duplicate attempts are rejected, including while the first Product is retired. | Yes |
| TC-PRODUCT-006 | REQ-PRODUCT-001, REQ-PRODUCT-005 | Integration | An active Product exists. | Change its Product ID and retrieve it; create a second Product with the old ID. Attempt an edit to an ID already held by another Product. | Internal identity remains unchanged; the new ID is saved and the old ID can be reused. The conflicting edit is rejected. | Yes |
| TC-PRODUCT-007 | REQ-PRODUCT-001, REQ-PRODUCT-005 | Integration | Creation and editing are available. | Create Products with the same trimmed name and each of the four kinds; change an active Product's name and kind. | Duplicate names are accepted, surrounding name whitespace is removed, all four kinds can be selected, and changed values are retrieved. | Yes |
| TC-PRODUCT-008 | REQ-PRODUCT-004, REQ-PRODUCT-005 | UI/end-to-end | Active and retired Products with distinct IDs and names exist. | Open Product navigation; search using differently capitalized ID and name text; filter for each status; select a result. | The list initially shows both statuses and the specified columns. Search matches ID and name without regard to capitalization; filters show the selected status. Selection opens a read-only view with Edit; internal UUID is not displayed. | Yes |
| TC-PRODUCT-009 | REQ-PRODUCT-003, REQ-PRODUCT-005 | UI/end-to-end | An active Product exists. | Change a field and attempt to select Retire. Save or discard the edit, select Retire, then Save and cancel confirmation. Undo the pending retirement and edit. Repeat retirement and confirm Save. | Retirement cannot be selected while field edits are pending. Cancelling confirmation preserves pending state; retirement can be undone and fields edited. Confirming Save retires the Product, which remains visible and retrievable. | Yes |
| TC-PRODUCT-010 | REQ-PRODUCT-003, REQ-PRODUCT-005 | UI/end-to-end and integration | A retired Product exists. | Open its edit view and try changing fields. Select Reactivate, change fields, and Save; cancel confirmation and verify persisted state. Save again and confirm. | Only reactivation is available initially; after selection fields become editable. Cancellation retains pending edits and leaves persisted state retired. Confirmation saves reactivation and edits together. | Yes |
| TC-PRODUCT-011 | REQ-PRODUCT-003, REQ-PRODUCT-005 | Integration | A retired Product exists. | Attempt to modify Product ID, name, kind, and description without reactivation. | The persisted Product remains unchanged. | Yes |

## Coverage Gaps and Unresolved Ambiguities

- The requirements do not specify what happens if a Product cannot be retrieved, saved, or updated, or what feedback a user receives.
- The Markdown features to support and how promptly the preview must update are unresolved.
- Whether the editor/preview arrangement or status filter must be remembered between sessions is unresolved.
- Which authenticated users may create and maintain Products remains open for implementation planning.
- Exact case-insensitive comparison semantics for non-ASCII Product IDs and searches need to be selected consistently during implementation; the agreed requirements do not specify Unicode normalization.

## Deferred Tests

- Authorization and product-specific access await agreed access requirements.
- Regulatory information, linked entities, compatibility, Product Revisions, readiness checks, status-change reasons, and audit history are outside this increment.
- Configurable kinds, Product ID generation templates, and deletion are not specified in this increment.
