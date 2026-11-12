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

### View the UI catalog in Development mode

From a checkout containing the UI catalog (currently branch `pbi/93-ui-catalog`), run:

```powershell
.\deploy.ps1 -Development
```

Sign in at **https://localhost:8443**, complete password replacement if required,
then open **https://localhost:8443/development/ui-catalog** or select **UI catalog**
under Development in the sidebar. Substitute `APP_HTTPS_PORT` from `.env` if changed.
The switch explicitly sets both `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT`
inside the application container; setting them in the calling shell alone does not
select Development mode. Use this mode only for trusted local development: it enables
Development-only behavior, including the catalog.

No database reset or credential changes are needed. Existing database data, `.env`
settings, and the HTTPS certificate are reused. Deployment still replaces the app
container and ends its active sessions. The option also applies to the app recreated
by `.\deploy.ps1 -RecoverAdministrator -Development`; recovery otherwise retains its
existing confirmation and password-replacement requirements.

Return to Production mode with:

```powershell
.\deploy.ps1
```

Without `-Development`, the app explicitly uses Production regardless of the shell's
environment settings; the catalog and its navigation link are unavailable.

### Deployment behavior

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

## Targeted collection discovery

Prefer bounded discovery for `list_products`, `list_user_needs`, `list_requirements`, `list_test_cases`, `list_features`, `list_revisions` and `list_product_areas`. Keep the required Product manual `productId` for Product-scoped lists.

- Supply `search` (up to 200 characters) for ordinal case-insensitive ID/title substring matching. Products use ID/name, areas code/label, and Revisions identifier/description.
- Optional `status` is an exact case-insensitive name: Active/Retired for Products/areas, Draft/Released for Revisions, Draft/InReview/Approved for versioned entities. Filters combine with AND and apply to the existing working-or-latest-Approved selection, not historical versions.
- Supplying any non-null discovery parameter opts into a page object: `items`, `offset`, `limit`, `total`, `remaining`, `nextOffset`, `complete`. Defaults: offset 0, limit 50; offset must be nonnegative and limit 1–200. Empty search also opts in.
- Ordering is ordinal ID then immutable identity. `total` counts authorized filtered matches; `remaining` counts matches after this page. Continue with `nextOffset` and identical filters until null. `complete` is true only when this response contains the entire filtered collection (offset 0 with no remaining matches). Never call a page the full Product collection, or treat a filtered absence as global absence.
- Pages are independent reads, not a frozen snapshot. Concurrent edits may move boundaries; restart discovery if a consistent collection is required.
- Omitting all discovery parameters (or passing all null) retains the legacy complete-array response for compatibility. Prefer paging in new terminal conversations. Bounds limit returned rows, not underlying store reads; no persistence strategy changes.
- Example: `list_requirements(productId="P", search="login", status="Draft", limit=20, offset=0)`. After discovery, use exact read tools for selected records. Update/reload older integrations before passing the new parameters.

## AI Workflow settings and local MCP (Claude Code and Pi)

Run the app with the local Podman setup above. Keep its published port bound to
`127.0.0.1`; this increment supports trusted clients on the same machine only,
not remote deployment or an OAuth authorization server. The `/mcp` endpoint uses
Streamable HTTP and accepts the terminal bridge's bearer credential, not browser cookies. Non-loopback
Host names and requests with an Origin header are rejected. Do not expose this
endpoint through a remote proxy or publish the container port on all interfaces.

MCP starts **disabled**, including when upgrading from the earlier always-available
MCP host. In **Settings → MCP settings** (`/settings/mcp`), only an administrator can
enable or disable it for all users. Administrators can also reach MCP settings from AI Workflow. The setting is stored in PostgreSQL and survives restarts. Disabling
ends all terminal connections; re-enabling requires connecting again. Requests
already executing when disabling begins may complete; requests starting after confirmed
disabling are denied. Browser access, setup instructions and downloads remain available.
Configuration failures deny MCP access and new connections rather than assuming enabled.
A settings save requires confirmation. On an uncertain settings save, reload the page
before deciding whether to retry manually; no automatic retry is performed.

Terminals connect through a small local bridge shipped in the terminal ZIP. There is
no credential to copy and no environment variable to set.

**Once per project directory**

1. Sign in, open **https://localhost:8443/settings/ai-workflow** (adjust the port if
   configured differently) and download the Pi or Claude Code ZIP. `/mcp-access` remains
   a compatibility route to the same page. Ask an administrator to enable MCP if it is disabled.
2. Extract the ZIP and run its setup script, naming your project directory. It needs
   Node.js 22 or later:

   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp
   ```

3. The script shows the SHA-256 fingerprint of the application's HTTPS certificate.
   Compare it with the fingerprint on the AI Workflow page and type `TRUST` only if they
   match. The bridge then trusts exactly that certificate. Nothing is added to the Windows
   certificate store and certificate verification is never disabled. Rerun the script from
   a new ZIP to update the integration, or when the certificate is replaced.

The script installs the skill, the bridge under `.retravion-terminal/`, and a `retravion`
stdio entry in `.pi/mcp.json` or `.mcp.json`; unrelated entries are preserved.

**Every session**

1. Start `pi` or `claude` in the project directory and accept its project/MCP prompts.
2. Run `/skill:retravion` (Pi) or `/retravion` (Claude Code) and ask it to connect. It
   asks for your consent, then opens your browser at the AI Workflow page; if the browser
   cannot be opened it shows the link instead.
3. Sign in if needed, check that the code matches the one in the terminal, and select
   **Approve connection**. Signing in takes you on to the approval; only the connection
   code is carried through sign-in. The code can also be entered on the AI Workflow page.
4. Tell the terminal you have approved.

The bridge adds `rtv_connect`, `rtv_complete_connection` and `rtv_disconnect` and forwards
the tools below unchanged.

Available tools:

Use `list_revisions` with the Product manual `productId`, then `read_revision` with that `productId` and the Revision's immutable `internalId`. Both include retired Products. `revision.selections` are exact explicit roots; `members` is the complete outgoing baseline with `explicit` distinguishing roots from indirect members. Preserve each endpoint's kind, internalId, versionId and versionNumber—even multiple versions of one entity. `evidence=calculated-draft` is current Draft closure, not release evidence. `evidence=frozen-released` uses retained release-time members, identifiers, titles and area information, with release actor/time; never replace them with current versions or current area labels. Missing/inaccessible or incomplete baselines are errors, not permission to guess. Use `create_draft_revision` to create an empty Draft with approved `identifier` and `description`. Use `edit_draft_revision` to replace only that metadata, preserving exact selections. Use `change_draft_revision_selection` for `add`, `remove` or one atomic `replace`: `target` is the old endpoint for replacement; only replace accepts `replacement`. Endpoints require `kind` (`UserNeed`, `Requirement`, `TestCase`, `Feature`), `internalId`, `versionId` and `versionNumber`; never silently advance to newer versions. Each write requires explicit human approval of initial or old/new values and `confirmed=true`. Supply Product manual `productId`; edits/selections also require immutable Revision `internalId` and positive `expectedRevision` from a fresh read. Only Draft Revisions under active accessible Products may change. Referenced entities/links remain unchanged. Copy, delete, release and all entity lifecycle operations remain web-only. Review mode remains read-only. Never automatically retry; after uncertainty retrieve authorized list/read state before a human-directed retry. After uncertain creation without identity inspect candidates; if ambiguous, do not blindly recreate. Update and reload older integrations before using Revision writes.

`explore_relationships` is read-only: supply `kind` (user-need, requirement, test-case or feature), immutable `internalId` and required exact `versionId`. Direct incoming/outgoing edges retain exact endpoints, ownership, direction, Product, status and newer-version indicators. Optional `expandedVersionIds` expands only explicitly selected reachable versions (at most 20; repeats load once). `limit` defaults to 100 (1–200), separately capping edges and unavailable provenance snapshots. Report truncation, omitted counts and unexpanded IDs; never imply a complete transitive closure or absent hidden links. Unavailable origins are stored copy-time snapshots only, not live/expandable sources. Exploration never mutates. Integration 2.7.0 separately supports `change_draft_link` for explicitly human-approved add/remove/retarget of ordinary links owned by eligible Draft Requirements, Test Cases and Features. Read exact owner/target versions first; supply the owner's positive `expectedRevision` and `confirmed=true`. Relationships are `user-need`, `parent`, `verifies`, `addresses`; retarget names the old endpoint and a `replacementVersionId` of the same target entity. Different-parent replacement remains web-only. No provenance or lifecycle writes; review mode remains read-only. After uncertain outcomes retrieve authorized current state before any human-directed retry; never retry automatically. Update and reload older integrations before using link writes.


| Tool | Input / behavior |
| --- | --- |
| `list_products` | No arguments; read-only Products, including retired Products |
| `read_product` | `productId`; ID, name, kind, full Markdown description and active/retired status |
| `list_user_needs` | `productId` (the Product's manual ID); all statuses, including retired Products |
| `read_user_need` | `internalId` (need GUID); full content, revision token and review history |
| `create_draft_user_need` | `productId`, `id`, `title`, `statement` (Markdown); active Products only |
| `edit_draft_user_need` | `internalId`, `expectedRevision`, `id`, `title`, `statement`; Draft content under active Products only |
| `list_requirements` | `productId` (the Product's manual ID); all statuses, including retired Products |
| `read_requirement` | `internalId` (Requirement GUID); full content, revision token and exact review history |
| `create_draft_requirement` | `productId`, `id`, `title`, `statement` (Markdown), `type`, optional `rationale` (plain text); active Products only |
| `edit_draft_requirement` | `internalId`, `expectedRevision`, `id`, `title`, `statement`, `type`, optional `rationale`; Draft content under active Products only. Omitted/null rationale clears it. |

Requirement types are `System`, `Software`, `Hardware`, `Safety`, `Usability`, and
`Regulatory` (case-insensitive names). A type must be explicitly supplied. Manual
Requirement IDs are trimmed, limited to 100 Unicode characters, and unique
case-insensitively within the Product using the existing database collation.
The web Requirements list combines type/status filters with case-insensitive
substring search over ID/title.

Read before editing and supply the returned `revision` as `expectedRevision`.
There are no status, review, deletion or new-version tools. Extra arguments such as
`status` and `actor` are rejected. Product retirement and concurrent status/content
changes are checked at the same transactional write boundary as web changes. Writes
use the authenticated Retravion user and the same atomic audit rules as web writes.

Terminal access renews each time the terminal uses Retravion and ends after 30 idle
minutes. It cannot outlive the browser session that approved it. Logout, session expiry,
account-session revocation, application restart, a newer connection approved from the same
browser session, globally disabling MCP and **End terminal connection** end its access. Ending it
on the page requires confirmation and does not log out the browser. Afterwards, ask the
terminal to connect again; restarting it is not needed. The credential exists only in the
bridge process and as a digest in the application.

Never put credentials into prompts```

Never put credentials into prompts, source control, URLs or shared logs. Do not
retry a write automatically. If the result says the commit outcome could not be
confirmed, list/read the affected record and inspect its values before deciding whether to
retry manually. A lost response likewise does not prove the write rolled back.

### Download and install terminal skills

The AI Workflow page offers authenticated Pi and Claude Code ZIP downloads, with
integration version and SHA-256. These fixed packages contain only
`skills/retravion/SKILL.md`, a client-specific README, `mcp_setup.ps1` and the bridge
(`bridge/server.mjs`, `bridge/tools.json`); no credentials, user records or terminal client.
A hash checks file integrity, not publisher identity. Downloading grants no access.

Integration 2.4.0 guides Product, User Need, Requirement, Test Case and Feature reads and requires
explicit conversation approval before User Need, Requirement, Test Case or Feature Draft writes.
Test Case tools are `list_test_cases`, `read_test_case`, `create_draft_test_case` and
`edit_draft_test_case`. Creation generates the ID and permits empty content; edits replace
the exact working Draft version's full content and ordered steps. Revision checks and
no automatic retries (including stale or uncertain writes) remain required. Feature tools are
`list_features`, `read_feature`, `create_draft_feature` and `edit_draft_feature`. Feature creation
generates identities and Draft v1; edits replace title/description using the exact working version
and revision while preserving existing links. After uncertain creation, inspect candidate records;
never blindly recreate an ambiguous result. Relationship/link
writes, classification changes and lifecycle operations remain unsupported by the skill;
it does not confer lifecycle approval or new permissions. AI review is read-only and never
authorizes saving; humans apply suggestions and review decisions in the web UI.

Updates are explicit: upgrading the app or downloading a ZIP does not update installed
skills or a running bridge. Close the terminal, download and verify the current client ZIP,
extract it and run the **new ZIP's** `mcp_setup.ps1 -Directory C:\work\MyApp` for the same
project. Include the existing `-Endpoint` if not using port 8443 and confirm the certificate
fingerprint again. This replaces only the selected Retravion skill, shared bridge and
Retravion configuration; unrelated MCP entries and settings are preserved. For both clients,
run each client's setup so both skills are current. Check the installed `SKILL.md` heading
for `integration 2.4.0`, restart the terminal, reload the skill and connect again. Use a new
session if old instructions still report an older version or exclude Features; do not bypass the old
skill by calling tools directly.

To remove, run setup with `-Remove`:
it deletes only Retravion's skill and server entry, and the shared bridge folder once no
other Retravion terminal integration remains in the project.

Bridge and setup-script checks use fakes, temporary directories and throwaway
certificates only:

```powershell
node --test tests/TerminalBridge.Tests.mjs
pwsh -NoProfile -File tests/TerminalSetup.Tests.ps1
```
Future entity support requires an explicitly shipped new integration version.

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
