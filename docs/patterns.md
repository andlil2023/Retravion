# Established implementation patterns

This is an index, not a tutorial. Only promote a pattern after a working implementation has been reviewed and accepted. `Provisional` means do not generalize it automatically.

## Page action placement and style

- **Status:** Established; approved convention, using the reviewed and accepted Create User page from #63.
- **Canonical implementation:** `src/Retravion.Web/Features/Identity/Users/CreateUser.razor`.
- **Shared component/contract:** `Components/PageHeader.razor` provides the title and navigation; existing MudBlazor buttons provide page actions. No additional action framework.
- **Rule:** Page headers contain the title and navigation. Actions that submit forms or change the displayed entity belong below the relevant card, not in the top bar. Keep destructive actions clearly labeled and require confirmation where appropriate.
- **Button style:** The main action below a card uses `Variant.Filled` and `Color.Primary`, consistently for Create User, Inactivate, and Reactivate. Confirmation remains required for availability changes; button styling does not replace it.
- **Applicability:** Form and entity-detail pages, including Create User and account inactivation/reactivation. Navigation actions such as Back or a link to Create User may remain in the header; application-shell controls are unaffected.
- **Exception rule:** Any different placement or main-action style requires explicit approval. Regression tests and independent review must check both placement and style.

## Compact versioned detail view (option B)

- **Status:** Established preferred template; the User Need implementation passed independent review and was accepted by the user after local preview. Use it for separately approved work on similar detail views; do not automatically redesign existing views.
- **Canonical implementation:** `src/Retravion.Web/Features/UserNeed/UserNeedDetails.razor` and its scoped `.razor.css`.
- **Shared component/contract:** Use the existing `PageHeader` in the owning feature's header slot, `MudPaper`, `MudChip`, `MudButton`, `MudTextField`, and the existing Markdown viewer. This is a layout reference, not a new shared component or cross-feature dependency.
- **Layout:** Quiet Product context above the record card; record identifier above the title; compact version and textual status chips beside the title on wide screens, wrapping below on narrow screens. Bound the title column's growth so metadata stays near the content rather than at the far edge; vertically center chips with the version disclosure's summary. Put version navigation in a labeled, keyboard-accessible `Versions` disclosure rather than repeating it as always-visible metadata. Keep exact-version destinations and mark the displayed version.
- **Actions:** Put primary Filled/Primary Edit first, alongside secondary record actions in one wrapping group below the content card. Follow the established page-action placement rule above. Keep lifecycle fields in a separate card with their existing actions below it; constrain this section's width for readability. Review history is last, after all lifecycle controls, so growing history cannot move those controls down.
- **Preservation:** Keep the owning feature's authorization, lifecycle eligibility, retired/read-only notices, provenance, version history, validation, busy states, and failure behavior. A read-only detail layout does not make lifecycle mutations available to otherwise ineligible records or users. Status must remain understandable without color.
- **Applicability:** Preferred starting point for similar versioned, read-only entity-detail views when separately approved. Use feature-owned content, routes, and lifecycle rules; do not copy User Need behavior or scoped CSS across feature boundaries as an implicit contract.
- **Exception rule:** Seek approval for a different action placement/style or a materially different layout. Verify primary styling, action order, narrow-screen wrapping, exact-version navigation, lifecycle eligibility, and history-last ordering when adapting this reference.

Other candidates: create/update commands and domain tests.

When none applies, propose the smallest architecture-compliant approach for approval. Do not treat it as established before review and acceptance.

## Promotion rule
Identify a real repeated need and review the canonical implementation. Add one entry recording status, canonical path, shared component/contract, applicability, and exception rule. Do not create an ADR for ordinary promotion.
