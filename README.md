# Retravion

Retravion is a product-lifecycle documentation application for software teams, intended to help define, link, review, and trace user needs, requirements, and tests.

## Current status

The repository currently contains a .NET 10 solution with a minimal Blazor landing page, application/domain/infrastructure project boundaries, smoke tests, and GitHub Actions CI. Product features, persistence, and authentication are not implemented yet.

## Getting started

Install the .NET SDK version specified in [`global.json`](global.json) (currently `10.0.403`). No database or external services are required for the bootstrap.

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

## Local review with Podman (Windows)

This is a disposable internal-review environment, not a production installer. It runs
exactly two Linux containers: the application and PostgreSQL 17. The app currently
has no database access or admin-account implementation; their settings are reserved
for future features.

### Prerequisites and first start

- Install [Podman for Windows](https://podman.io/docs/installation) (Podman Desktop
  is optional) and [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows).
- Initialize one Podman machine with `podman machine init` if none exists. The script
  starts it when stopped. Multiple machines are rejected to avoid ambiguous targeting.
- Run from a PowerShell 7 terminal, as your normal Windows user:

```powershell
.\deploy.ps1
```

No host .NET SDK or Compose provider is required. Initial image downloads and NuGet
restore during the container build need internet access. The app is built from your
checkout on every run. Images remain in Podman's cache and are never committed.

On first run, the script creates `.env` from `.env.example` and generates independent
random secrets. You can instead copy the template and fill in all blank secrets
before running. Values must be unquoted letters, digits, dots, underscores or hyphens;
variable expansion is not supported. Do not share or commit `.env`.

The script creates a password-protected localhost certificate under `.local/review/`.
Type `TRUST` when asked to trust it for your Windows account; Windows may also prompt.
No administrator terminal is required. Keep the private certificate and `.env` private.
If the certificate expires, delete `.local/review/localhost.pfx` and rerun to generate
and trust a replacement. This does not reset the database. To remove trust later,
remove the matching **Retravion local review** certificate from your current-user
Trusted Root Certification Authorities store using Windows certificate management.

Open **https://localhost:8443** (or the `APP_HTTPS_PORT` from `.env`). Only the app port
is published, bound to IPv4 loopback. PostgreSQL is accessible only on the container
network. Both services are checked for readiness before the script reports success.

### Rerun, reset, and image updates

```powershell
.\deploy.ps1                    # Rebuild app, preserve .env and database volume
.\deploy.ps1 -ResetDatabase     # Type RESET to delete/recreate review database
.\deploy.ps1 -UpdateImages      # Pull supported image versions and rebuild
.\deploy.ps1 -ResetDatabase -UpdateImages
```

A normal rerun replaces this setup's containers but retains the named database volume.
No automatic schema upgrades are performed. Early schema changes may require an
explicit reset; future database features must supply current-schema initialization.
Changing database credentials/names in `.env` does **not** modify an existing database:
restore the previous values or reset explicitly. Never discard `.env` unless you also
intend to recreate the database and certificate with new secrets.

Images are `postgres:17`, .NET SDK `10.0.403` (matching `global.json`), and ASP.NET runtime
`10.0`. `-UpdateImages` refreshes these tags, never switches PostgreSQL major versions.
Normal runs download only missing images; package restore may still require a network
connection. Image cache availability is not a guarantee of a fully offline build.

Configuration passed to the app uses `ConnectionStrings__DefaultConnection` and
`InitialAdmin__Password`. The latter does not create a user, change a password, or
implement authentication. No credentials are baked into the app image. Podman users
can inspect container environment values: this setup is for a trusted local machine.

### Stop and troubleshoot

```powershell
podman stop retravion-review-app retravion-review-db
podman logs retravion-review-app
podman logs retravion-review-db
```

Containers do not automatically restart after Windows/Podman restarts; rerun the script.
Use `podman machine list` for machine problems. For port conflicts, change
`APP_HTTPS_PORT` in `.env`. Setup failures exit nonzero and may leave owned resources
available for inspection; fix the reported issue and rerun. Avoid simultaneous runs.
The script refuses to replace resources with its names unless they carry its ownership
label, and never performs a global prune. Treat logs as potentially sensitive.

Run the dependency-free deployment checks without starting containers or trusting
certificates:

```powershell
pwsh -NoProfile -File tests/Deployment.Tests.ps1
```

These checks mock external operations. A real Windows/Podman smoke test must also
verify certificate trust, container builds/startup, a second run preserving secrets and
data, an explicit reset, and `-UpdateImages` on the target machine.

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
