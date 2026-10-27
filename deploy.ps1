#requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $ResetDatabase,
    [switch] $UpdateImages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Podman {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    # Capture output so environment values are never printed by the wrapper.
    $output = & podman @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Podman '$($Arguments[0])' failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function New-ReviewSecret {
    return [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
}

function Read-ReviewEnvironment {
    param([string] $Root)
    $path = Join-Path $Root '.env'
    if (-not (Test-Path $path)) {
        $text = Get-Content (Join-Path $Root '.env.example') -Raw
        foreach ($key in @('POSTGRES_PASSWORD', 'INITIAL_ADMIN_PASSWORD', 'HTTPS_CERT_PASSWORD')) {
            $text = $text.Replace("$key=", "$key=$(New-ReviewSecret)")
        }
        # CreateNew prevents overwriting a file created by another invocation.
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
        Write-Host 'Created .env with random secrets. Keep this file private.'
    }
    $values = @{}
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -notmatch '^([A-Z_]+)=([A-Za-z0-9_.-]+)$') {
            throw '.env contains an invalid entry. Use unquoted letters, digits, dots, underscores or hyphens; see .env.example.'
        }
        if ($values.ContainsKey($Matches[1])) { throw "Duplicate .env key: $($Matches[1])" }
        $values[$Matches[1]] = $Matches[2]
    }
    foreach ($key in @('APP_HTTPS_PORT', 'POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD', 'INITIAL_ADMIN_PASSWORD', 'HTTPS_CERT_PASSWORD')) {
        if (-not $values.ContainsKey($key)) { throw "Missing .env key: $key" }
    }
    $port = 0
    if (-not [int]::TryParse($values.APP_HTTPS_PORT, [ref] $port) -or $port -lt 1024 -or $port -gt 65535) {
        throw 'APP_HTTPS_PORT must be a port between 1024 and 65535.'
    }
    foreach ($key in @('POSTGRES_DB', 'POSTGRES_USER')) {
        if ($values[$key] -notmatch '^[a-z][a-z0-9_]{0,62}$') { throw "$key must be a lowercase PostgreSQL identifier (maximum 63 characters)." }
    }
    return $values
}

function Start-ReviewMachine {
    $machines = @(Invoke-Podman @('machine', 'list', '--format', 'json') | ConvertFrom-Json)
    # Some Podman releases return an array as one pipeline item.
    $machines = @($machines | ForEach-Object { $_ })
    if ($machines.Count -eq 0) { throw 'No Podman machine exists. Run podman machine init, then rerun this script.' }
    if ($machines.Count -gt 1) { throw 'Multiple Podman machines exist. This local script requires a single machine to avoid targeting the wrong environment.' }
    if (-not $machines[0].Running) {
        Write-Host 'Starting Podman machine...'
        Invoke-Podman @('machine', 'start', $machines[0].Name) | Out-Null
    }
    Invoke-Podman @('info', '--format', 'json') | Out-Null
}

function Assert-ReviewResource {
    param([string] $Kind, [string] $Name)
    & podman $Kind exists $Name 2>$null
    if ($LASTEXITCODE -eq 1) { return $false }
    if ($LASTEXITCODE -ne 0) { throw "Cannot check $Kind '$Name'." }
    $item = @(Invoke-Podman @($Kind, 'inspect', $Name) | ConvertFrom-Json)[0]
    $labels = if ($Kind -eq 'container') { $item.Config.Labels } else { $item.Labels }
    if (-not $labels -or -not $labels.PSObject.Properties['com.retravion.review'] -or $labels.'com.retravion.review' -ne '1') {
        throw "Refusing to modify unowned $Kind '$Name'. Rename it or use a separate Podman machine."
    }
    return $true
}

function Initialize-ReviewCertificate {
    param([string] $Root, [string] $Password)
    $directory = Join-Path $Root '.local/review'
    New-Item $directory -ItemType Directory -Force | Out-Null
    $pfx = Join-Path $directory 'localhost.pfx'
    $securePassword = ConvertTo-SecureString $Password -AsPlainText -Force
    if (Test-Path $pfx) {
        try {
            $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $Password)
            if ($cert.NotAfter -le (Get-Date).AddDays(1)) { throw 'Certificate is expired or expires tomorrow.' }
        } catch { throw 'Cannot use the local HTTPS certificate. Check HTTPS_CERT_PASSWORD, or remove .local/review/localhost.pfx to regenerate it.' }
    } else {
        Import-Module PKI
        $cert = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\CurrentUser\My' `
            -FriendlyName 'Retravion local review' -KeyAlgorithm RSA -KeyLength 2048 `
            -KeyExportPolicy Exportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $securePassword | Out-Null
    }
    if (-not (Test-Path "Cert:\CurrentUser\Root\$($cert.Thumbprint)")) {
        if ((Read-Host 'Trust the Retravion localhost HTTPS certificate for your Windows account? Type TRUST') -cne 'TRUST') {
            throw 'Certificate trust declined. Nothing was deployed. Rerun when ready to trust the certificate.'
        }
        Import-Module PKI
        $cer = Join-Path $directory 'localhost.cer'
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    }
    return $pfx
}

function Invoke-ReviewDeployment {
    if (-not $IsWindows) { throw 'This script supports Windows only.' }
    if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
        throw 'Install Podman for Windows (https://podman.io/docs/installation), initialize a Podman machine, then rerun with PowerShell 7.'
    }
    $root = $PSScriptRoot
    $config = Read-ReviewEnvironment $root
    Start-ReviewMachine

    $app = 'retravion-review-app'
    $database = 'retravion-review-db'
    $network = 'retravion-review-network'
    $volume = 'retravion-review-postgres'
    $appExists = Assert-ReviewResource 'container' $app
    $dbExists = Assert-ReviewResource 'container' $database
    $networkExists = Assert-ReviewResource 'network' $network
    $volumeExists = Assert-ReviewResource 'volume' $volume
    if ($ResetDatabase -and (Read-Host "DELETE all local review database data in '$volume'? Type RESET") -cne 'RESET') {
        throw 'Database reset cancelled. Nothing was deleted.'
    }
    $pfx = Initialize-ReviewCertificate $root $config.HTTPS_CERT_PASSWORD
    $images = @('docker.io/library/postgres:17', 'mcr.microsoft.com/dotnet/sdk:10.0.403', 'mcr.microsoft.com/dotnet/aspnet:10.0')
    foreach ($image in $images) {
        & podman image exists $image 2>$null
        $existsCode = $LASTEXITCODE
        if ($existsCode -notin @(0, 1)) { throw "Cannot inspect cached image '$image'." }
        if ($UpdateImages -or $existsCode -eq 1) {
            Write-Host "Pulling $image..."
            Invoke-Podman @('pull', $image) | Out-Null
        }
    }
    Write-Host 'Building application image...'
    Invoke-Podman @('build', '--pull=never', '-f', (Join-Path $root 'Containerfile'), '-t', 'localhost/retravion-review:local', $root) | Out-Null

    # Do not disrupt existing containers until the build and certificate setup succeed.
    if ($appExists) { Invoke-Podman @('rm', '-f', $app) | Out-Null }
    if ($dbExists) { Invoke-Podman @('rm', '-f', $database) | Out-Null }
    if ($ResetDatabase -and $volumeExists) {
        Invoke-Podman @('volume', 'rm', $volume) | Out-Null
        $volumeExists = $false
    }
    if (-not $networkExists) { Invoke-Podman @('network', 'create', '--label', 'com.retravion.review=1', $network) | Out-Null }
    if (-not $volumeExists) { Invoke-Podman @('volume', 'create', '--label', 'com.retravion.review=1', $volume) | Out-Null }

    # Pass secrets through process environment, not command-line argument values.
    $environment = @{
        POSTGRES_DB = $config.POSTGRES_DB
        POSTGRES_USER = $config.POSTGRES_USER
        POSTGRES_PASSWORD = $config.POSTGRES_PASSWORD
        ConnectionStrings__DefaultConnection = "Host=$database;Port=5432;Database=$($config.POSTGRES_DB);Username=$($config.POSTGRES_USER);Password=$($config.POSTGRES_PASSWORD)"
        InitialAdmin__Password = $config.INITIAL_ADMIN_PASSWORD
        ASPNETCORE_Kestrel__Certificates__Default__Password = $config.HTTPS_CERT_PASSWORD
    }
    $previous = @{}
    try {
        foreach ($key in $environment.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
        }
        Write-Host 'Starting PostgreSQL...'
        Invoke-Podman @('run', '-d', '--name', $database, '--label', 'com.retravion.review=1', '--network', $network,
            '--pull=never', '-v', "${volume}:/var/lib/postgresql/data", '-e', 'POSTGRES_DB', '-e', 'POSTGRES_USER', '-e', 'POSTGRES_PASSWORD', $images[0]) | Out-Null
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & podman exec $database sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT 1"' *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Seconds 2
        }
        if (-not $ready) { throw "PostgreSQL did not become ready within 120 seconds. Inspect: podman logs $database. If .env database credentials changed, restore them or explicitly use -ResetDatabase." }
        Write-Host 'Starting application...'
        # Forward slashes keep Windows drive paths compatible with Podman's volume parser.
        $certificatePath = $pfx.Replace('\', '/')
        Invoke-Podman @('run', '-d', '--name', $app, '--label', 'com.retravion.review=1', '--network', $network,
            '--pull=never', '-p', "127.0.0.1:$($config.APP_HTTPS_PORT):8443", '-v', "${certificatePath}:/https/localhost.pfx:ro",
            '-e', 'ASPNETCORE_URLS=https://+:8443', '-e', 'ASPNETCORE_Kestrel__Certificates__Default__Path=/https/localhost.pfx',
            '-e', 'ASPNETCORE_Kestrel__Certificates__Default__Password', '-e', 'ConnectionStrings__DefaultConnection',
            '-e', 'InitialAdmin__Password', 'localhost/retravion-review:local') | Out-Null
    } finally {
        foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    }
    $url = "https://localhost:$($config.APP_HTTPS_PORT)"
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest $url -TimeoutSec 3
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $ready) { throw "Application HTTPS check failed. Inspect: podman logs $app" }
    Write-Host "Ready: $url"
    Write-Host 'Database and initial-admin settings are reserved; this app does not consume them yet.'
}

# Dot-sourcing exposes helpers for tests without running deployment.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-ReviewDeployment } catch { Write-Error $_ -ErrorAction Continue; exit 1 }
}
