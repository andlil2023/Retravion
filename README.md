# Retravion

Retravion is a product-lifecycle documentation application for software teams, intended to help define, link, review, and trace user needs, requirements, and tests.

## Current status

The repository contains a .NET 10 Blazor prototype, Identity boundaries, password validation/hashing, and create-only administrator provisioning in PostgreSQL. Login, sessions, and protected access are not implemented yet.

## Getting started

Install the .NET SDK version specified in [`global.json`](global.json) (currently `10.0.403`). Application startup requires PostgreSQL and a connection configured as `ConnectionStrings__DefaultConnection`. On an empty database, also supply `InitialAdmin__Username` and `InitialAdmin__Password`; there are no defaults. Passwords must contain 10–30 Unicode scalar values and at least three of ASCII lowercase, ASCII uppercase, ASCII digits, and Unicode punctuation/symbols. Initialization errors never echo credentials.

From the repository root:

```sh
dotnet restore Retravion.slnx
dotnet build Retravion.slnx --configuration Release --no-restore
```

For the test step, run the self-contained procedure in
[PostgreSQL integration tests](#postgresql-integration-tests). It starts a disposable
Podman container, runs the tests, and then removes that container.

Run the web application:

```sh
dotnet run --project src/Retravion.Web -- --urls http://localhost:5080
```

Open <http://localhost:5080> to view the landing page.

## Local review with Podman (Windows)

This is a disposable internal-review environment, not a production installer. It runs
exactly two Linux containers: the application and PostgreSQL 17. The app currently
initializes exactly one administrator in PostgreSQL before serving requests. Existing
credentials are never reset on startup, even if provisioning settings change or are blank.

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

Before first startup, copy `.env.example` to `.env` and fill in every blank value,
including `INITIAL_ADMIN_USERNAME` and `INITIAL_ADMIN_PASSWORD`. Alternatively, run the
script once to create `.env` with independent random database/certificate secrets,
then supply your chosen administrator credentials and rerun. Administrator credentials
are never generated. Startup fails until valid initial credentials are supplied.

Entries use `KEY=value`, one per line, with no expansion or quote processing. Administrator
values preserve spaces and Unicode exactly; quote characters are literal. Other values
allow only letters, digits, dots, underscores or hyphens. Do not share or commit `.env`.

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
Identity creates its schema/table when absent; no automatic upgrades of existing tables
are performed. Early schema changes may require an explicit reset. The singleton key
and atomic insert prevent duplicate administrators, including concurrent startup.
Changing database credentials/names in `.env` does **not** modify an existing database:
restore the previous values or reset explicitly. Never discard `.env` unless you also
intend to recreate the database and certificate with new secrets.

Images are `postgres:17`, .NET SDK `10.0.403` (matching `global.json`), and ASP.NET runtime
`10.0`. `-UpdateImages` refreshes these tags, never switches PostgreSQL major versions.
Normal runs download only missing images; package restore may still require a network
connection. Image cache availability is not a guarantee of a fully offline build.

Configuration passed to the app uses `ConnectionStrings__DefaultConnection`,
`InitialAdmin__Username`, and `InitialAdmin__Password`. Initial provisioning stores only
a salted one-way password hash. Once provisioned, absent/changed initial credentials
are ignored on ordinary startup; editing `.env` alone does not reset credentials. No credentials are baked into the app image. Podman users
can inspect container environment values: this setup is for a trusted local machine.

### Recover the initial administrator

Recovery is an offline operation for an authorized IT administrator with administrative
access to this hosting environment. This procedure supports the single-application-container
review deployment only. Before proceeding, stop every other application instance using this
database and prevent concurrent deployment or startup until recovery finishes. The script
cannot detect independently hosted instances; running any would leave their sessions active.
Do not use this procedure for a multi-instance deployment.

Set `INITIAL_ADMIN_PASSWORD` in the private `.env` to the intended recovery password, then run:

```powershell
.\deploy.ps1 -RecoverAdministrator
```

Confirm by typing `RECOVER`. The script first builds the recovery-capable image from this
checkout; a build failure leaves the application running. It then stops the managed application,
ending its sessions, runs recovery without exposing the password in command arguments, and recreates
the application from the newly built image on success. A recovery failure leaves the application
stopped; correct the configuration before retrying. If recreation or readiness fails after recovery,
inspect the container and rerun the normal deployment; recovery has already changed the password. Recovery clears the administrator lockout and requires authentication followed by a
password replacement. Keep `.env` private and replace its recovery password securely afterward.
The internal `--recover-initial-administrator` application command must only be run while all
application hosts using the database are stopped; running it directly does not stop them.

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

These checks mock external operations.

### PostgreSQL integration tests

Local development and agent database tests must use a disposable PostgreSQL Podman
container, not a locally installed PostgreSQL server. GitHub Actions may use its
PostgreSQL service container. Run the following from the repository root in PowerShell 7
with the Podman machine running. The container has an ephemeral test password, an
IPv4-loopback port selected by Podman, and a user with CREATE DATABASE permission.
Treat its connection details as sensitive; Podman users can inspect container environment
values. The test suite creates and drops uniquely named databases in this disposable server.

```powershell
$name = "retravion-tests-$([guid]::NewGuid().ToString('N'))"
$password = [guid]::NewGuid().ToString('N')
$previousConnection = $env:RETRAVION_TEST_POSTGRES
$created = $false
try {
    podman run -d --rm --name $name -e "POSTGRES_PASSWORD=$password" -p '127.0.0.1::5432' postgres:17 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the PostgreSQL test container.' }
    $created = $true
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        podman exec $name pg_isready -U postgres 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'PostgreSQL test container did not become ready.' }
    $binding = podman port $name 5432/tcp
    if ($LASTEXITCODE -ne 0 -or -not $binding) { throw 'Could not determine the test port.' }
    $port = ($binding | Select-Object -First 1).Split(':')[-1]
    $env:RETRAVION_TEST_POSTGRES = "Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Password=$password"
    dotnet test Retravion.slnx --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    if ($created) { podman rm -f $name | Out-Null }
    if ($null -eq $previousConnection) { Remove-Item Env:RETRAVION_TEST_POSTGRES -ErrorAction SilentlyContinue }
    else { $env:RETRAVION_TEST_POSTGRES = $previousConnection }
}
```

Cleanup removes only the container created by this run, not the review environment or
other containers. Do not point `RETRAVION_TEST_POSTGRES` at a non-disposable server;
tests create and drop databases. Report any skipped or failed tests rather than treating
an incomplete database run as verification.

A real Windows/Podman smoke test must also
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
