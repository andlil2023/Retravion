# Architecture — Retravion

## Scope

Retravion documents and traces the lifecycle of software products. Only features approved for the current iteration are in scope. Future workflows are intentionally undecided.

## Established Starting Choices

- Stack: .NET 10, Blazor, PostgreSQL, and MudBlazor (D-001/D-003 in `docs/decisions.md`).
- Domain-driven design and Vertical Slice Architecture, with explicit domain boundaries (D-002).
- Prefer modern C#, records, and immutable values where appropriate.
- Prefer functional-first implementation: pure decisions and immutable data, with explicit effects and result-based expected failures; avoid unnecessary abstraction.
- Establish application-level UI patterns through reviewed implementations.

## Functional-First Implementation

- Prefer deterministic functions with explicit inputs and outputs for decisions and transformations. Pass values such as the current time into the logic rather than reading external state within it.
- Prefer immutable data and new values over shared mutable state. Keep domain behavior and invariants in the domain model.
- Isolate effects such as persistence, cryptography, clocks, and UI interactions in explicit orchestration or infrastructure boundaries. The application necessarily performs effects; make their ordering and failure handling visible.
- Return expected failures as values using the conventions below. Preserve cancellation and unexpected faults rather than silently converting every exception to a normal failure.
- Prefer composition when it improves readability and testability; do not sacrifice clarity for compactness. Imperative flow, local mutation, synchronization, and exceptions are appropriate when required by framework contracts, security, resource management, or demonstrable clarity.

## Expected Failures and Legitimate Absence

Use CSharpFunctionalExtensions (D-004) rather than custom Result/Maybe wrappers. Its version is managed in `Directory.Packages.props`; Identity Domain and Application reference it directly. Add references elsewhere only when an approved slice needs them.

- Use `Result` for an operation that can fail as expected without returning a value, and `Result<T>` when success produces a value. Validation failures and invalid lifecycle transitions are explicit failures, not exceptions used for normal control flow. Unexpected faults remain exceptions; do not silently turn every exception into an expected failure.
- Use `Maybe<T>` when absence is a legitimate outcome, such as an optional lookup result. If a lookup can also fail, `Result<Maybe<T>>` distinguishes a failed operation from a successful lookup with no match. Convert absence to failure only where the use case requires a value.
- Prefer fluent `Ensure`, `Bind`, `Map`, and `Tap` composition when it makes validation, sequencing, transformation, and success-only effects clearer. A failure must short-circuit later success steps; keep explicit branching when it is clearer.
- Do not use `Maybe<T>` (or nullable properties) to make required domain state optional for convenience. Validated construction, state-specific domain types, and typed lifecycle transitions still enforce required values. Return a failure rather than constructing invalid state.

This is usage guidance, not an established application implementation pattern. Do not introduce a parallel Result/Maybe framework or speculative adapters.

## Structural Constraints

- Domain logic must not depend on UI, database, or infrastructure details.
- A feature owns its implementation; crossing feature boundaries requires an explicit contract.
- Public contracts and schema changes require impact analysis and tests.
- Avoid speculative generalized frameworks or premature microservices.
- Persistence and authentication details are not yet fully designed; choose them when the first slice requires them.

## Identity and Access Ownership

- `src/Retravion.Identity.Domain/` owns account invariants and password rules; it has no UI, infrastructure, or unrelated-feature dependencies.
- `src/Retravion.Identity.Application/` owns Identity use cases and contracts and references only `Retravion.Identity.Domain`.
- `src/Retravion.Identity.Infrastructure/` owns external dependency implementations and references only `Retravion.Identity.Application`.
- `src/Retravion.Web/Features/Identity/` owns authentication UI, grouped by use case (currently `Login/`). UI consumes Application contracts, not Infrastructure implementation types.
- Web references Identity Application and Identity Infrastructure; Infrastructure access is restricted to composition in `Program.cs`.
- Identity projects must not reference unrelated application/domain projects. Keep implementation types `internal` where practical and expose only necessary contracts.

These projects establish ownership only. Authentication functionality and contracts are introduced by subsequent approved slices, not this foundation.

## Domain Modeling Principles

### Single Responsibility

- Each domain type must represent one cohesive domain concept or responsibility.
- Domain behavior and invariants belong in the domain model, not in UI components, persistence entities, or application handlers.
- Application handlers coordinate use cases and external dependencies.
- Avoid introducing abstractions solely to reduce class size or method count.

### State-Specific Domain Types

- Domain objects must not represent multiple lifecycle states through a status property and state-dependent nullable fields.
- Model lifecycle states with distinct domain types when their invariants, required data, or permitted operations differ. Simple status values are acceptable when they do not introduce invalid combinations of state and data.
- Required state-specific data must be represented by required properties and validated construction.
- Lifecycle transitions must be explicit operations with typed inputs and outputs.
- Invalid transitions must return explicit failures.
- Domain objects must not expose unrestricted setters that bypass invariants.
- Domain types must enforce their invariants at construction and during transitions; immutability alone is not sufficient.

### Persistence Boundary

- Persistence models may represent multiple lifecycle states in one database table or persistence entity.
- Persistence models must not be exposed as domain objects.
- Mapping from persistence to domain must validate state-specific invariants.
- Invalid persisted data must result in an explicit failure, never an invalid domain object.
