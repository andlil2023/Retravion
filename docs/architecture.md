# Architecture — Retravion

## Scope

Retravion documents and traces the lifecycle of software products. Only features approved for the current iteration are in scope. Future workflows are intentionally undecided.

## Established Starting Choices

- .NET 10 and Blazor for the web application.
- PostgreSQL for persistence.
- Domain-driven design and Vertical Slice Architecture, with explicit domain boundaries.
- Prefer modern C#, records, and immutable values where appropriate.
- Railway-oriented result handling where it improves clarity; avoid unnecessary abstraction.
- MudBlazor as the UI component library; application-level UI patterns will be established through reviewed implementations.

## Structural Constraints

- Domain logic must not depend on UI, database, or infrastructure details.
- A feature owns its implementation; crossing feature boundaries requires an explicit contract.
- Public contracts and schema changes require impact analysis and tests.
- Avoid speculative generalized frameworks or premature microservices.
- Persistence and authentication details are not yet fully designed; choose them when the first slice requires them.

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
- Invalid persisted data must result in an explicit