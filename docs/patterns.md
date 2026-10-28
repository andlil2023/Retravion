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

Other candidates: MudBlazor list/detail pages, create/update commands, query handlers, and domain tests.

When none applies, propose the smallest architecture-compliant approach for approval. Do not treat it as established before review and acceptance.

## Promotion rule
Identify a real repeated need and review the canonical implementation. Add one entry recording status, canonical path, shared component/contract, applicability, and exception rule. Do not create an ADR for ordinary promotion.
