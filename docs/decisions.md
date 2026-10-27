# Significant decision register

Record only choices that affect multiple features, establish long-lived constraints, or are expensive to reverse. Routine method, DTO, component, and file-layout choices do not belong here.

| ID | Decision | Status | Rationale | Revisit trigger |
|---|---|---|---|---|
| D-001 | Start with a .NET 10 / Blazor / PostgreSQL application | Accepted | Fits initial technical direction | Deployment or product constraints change |
| D-002 | Organize behavior as vertical slices with domain boundaries | Accepted | Localizes changes and supports incremental development | Frequent cross-slice coupling appears |
| D-003 | Use MudBlazor and establish application-level UI patterns | Accepted | Shared UI foundation without page-by-page redesign | Library constraints or UI needs change |
| D-004 | Use CSharpFunctionalExtensions for expected failures, legitimate absence, and clear railway-oriented composition | Accepted | Gives slices shared Result / Result<T> and Maybe<T> semantics without custom wrappers; required domain state and typed lifecycle invariants remain enforced | Library compatibility or concrete slice needs cannot be met without obscuring domain semantics |

A decision can be superseded. The agent must flag a revisit trigger rather than silently treating the choice as eternal.
