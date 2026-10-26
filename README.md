# Retravion

Retravion is a product-lifecycle documentation application for software teams, intended to help define, link, review, and trace user needs, requirements, and tests.

## Current status

The repository currently contains a .NET 10 solution with a minimal Blazor landing page, application/domain/infrastructure project boundaries, smoke tests, and GitHub Actions CI. Product features, persistence, and authentication are not implemented yet.

## Getting started

Install the .NET SDK version specified in [`global.json`](global.json) (currently `10.0.401`). No database or external services are required for the bootstrap.

From the repository root:

```sh
dotnet restore Retravion.slnx
dotnet build Retravion.slnx --configuration Release --no-restore
dotnet test Retravion.slnx --configuration Release --no-build --no-restore
```

Run the web application:

```sh
dotnet run --project src/Retravion.Web -- --urls http://localhost:5080
```

Open <http://localhost:5080> to view the landing page.

## Repository structure

```text
Retravion.slnx
src/
  Retravion.Web/              Blazor host and UI
  Retravion.Application/      Application use cases (currently empty)
  Retravion.Domain/           Domain model (currently empty)
  Retravion.Infrastructure/   External integrations (currently empty)
tests/
  Retravion.UnitTests/        Domain assembly smoke test
  Retravion.IntegrationTests/ Web host smoke test
```

Shared build settings are in `Directory.Build.props`; package versions are managed centrally in `Directory.Packages.props`. GitHub Actions restores, builds, and tests the solution on pushes and pull requests.

## Documentation

- [Architecture](docs/architecture.md)
- [Domain vocabulary and open questions](docs/domain.md)
- [Significant decisions](docs/decisions.md)
