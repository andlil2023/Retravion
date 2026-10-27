# Dependency-free script checks: pwsh -NoProfile -File tests/Deployment.Tests.ps1
# No real Podman calls, certificate changes, network requests, or repository .env writes.
#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deploy.ps1')

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Throws([scriptblock] $Action, [string] $Expected) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught.Contains($Expected)) "Expected error containing '$Expected'; got '$caught'"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) "retravion-deploy-tests-$([guid]::NewGuid())"
try {
    New-Item $temp -ItemType Directory | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '../.env.example') $temp
    $config = Read-ReviewEnvironment $temp
    $original = Get-Content (Join-Path $temp '.env') -Raw
    Assert-True ($config.POSTGRES_PASSWORD -match '^[a-f0-9]{48}$') 'Random database password'
    Assert-True ($config.INITIAL_ADMIN_PASSWORD -ne $config.POSTGRES_PASSWORD) 'Independent random secrets'
    $again = Read-ReviewEnvironment $temp
    Assert-True ($again.HTTPS_CERT_PASSWORD -eq $config.HTTPS_CERT_PASSWORD) 'Secrets preserved on rerun'
    Assert-True ((Get-Content (Join-Path $temp '.env') -Raw) -ceq $original) '.env never rewritten'
    Set-Content (Join-Path $temp '.env') ($original.Replace('APP_HTTPS_PORT=8443', 'APP_HTTPS_PORT=80'))
    Assert-Throws { Read-ReviewEnvironment $temp } 'APP_HTTPS_PORT'
    Set-Content (Join-Path $temp '.env') ($original + "`nPOSTGRES_USER=duplicate")
    Assert-Throws { Read-ReviewEnvironment $temp } 'Duplicate'
    Set-Content (Join-Path $temp '.env') ($original.Replace("POSTGRES_PASSWORD=$($config.POSTGRES_PASSWORD)", 'POSTGRES_PASSWORD=secret;unsafe'))
    Assert-Throws { Read-ReviewEnvironment $temp } 'invalid entry'
} finally { Remove-Item $temp -Recurse -Force }

# Mock only external boundaries. The orchestration and ownership checks remain real.
$script:FixtureConfig = $config
function Read-ReviewEnvironment { param($Root) return $script:FixtureConfig }
function Initialize-ReviewCertificate { param($Root, $Password) return 'C:\local review\localhost.pfx' }
function Invoke-WebRequest { param($Uri, $TimeoutSec) return @{ StatusCode = 200 } }
function Start-Sleep { param($Seconds) }
function Read-Host { param($Prompt) return $script:Answer }
function podman {
    $arguments = @($args)
    $script:Calls.Add(($arguments -join '|'))
    $global:LASTEXITCODE = 0
    switch ($arguments[0]) {
        'machine' {
            if ($arguments[1] -eq 'list') { return $script:Machines }
        }
        { $_ -in @('container', 'network', 'volume') } {
            if ($arguments[1] -eq 'exists') {
                $global:LASTEXITCODE = if ($script:ResourcesExist) { 0 } else { 1 }
            } elseif ($arguments[1] -eq 'inspect') {
                $label = if ($script:Owned) { '1' } else { '0' }
                return "[{`"Labels`":{`"com.retravion.review`":`"$label`"},`"Config`":{`"Labels`":{`"com.retravion.review`":`"$label`"}}}]"
            }
        }
        'image' { $global:LASTEXITCODE = if ($script:ImagesCached) { 0 } else { 1 } }
        'build' { if ($script:FailBuild) { $global:LASTEXITCODE = 125; return 'mock build failure' } }
        'exec' { if ($script:FailDatabase) { $global:LASTEXITCODE = 1 } }
    }
}
function Reset-Fixture {
    $script:Calls = [Collections.Generic.List[string]]::new()
    $script:Answer = 'RESET'
    $script:Machines = '[{"Name":"podman-machine-default","Running":false}]'
    $script:ResourcesExist = $false
    $script:Owned = $true
    $script:ImagesCached = $true
    $script:FailBuild = $false
    $script:FailDatabase = $false
    $script:ResetDatabase = $false
    $script:UpdateImages = $false
}
function Count-Calls([string] $Pattern) { return @($script:Calls | Where-Object { $_ -like $Pattern }).Count }

Reset-Fixture
$oldPassword = [Environment]::GetEnvironmentVariable('POSTGRES_PASSWORD', 'Process')
try {
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD', 'prior-process-value', 'Process')
    Invoke-ReviewDeployment
    Assert-True ((Count-Calls 'machine|start|*') -eq 1) 'Stopped machine started'
    Assert-True ((Count-Calls 'pull|*') -eq 0) 'Cached images reused without pull'
    Assert-True ((Count-Calls 'run|*') -eq 2) 'Exactly two containers'
    Assert-True ((Count-Calls 'run|*127.0.0.1:8443:8443*') -eq 1) 'Only app port exposed, bound to loopback'
    Assert-True ((Count-Calls 'run|*POSTGRES_PASSWORD*') -eq 1) 'Database receives environment variable name'
    Assert-True ((Count-Calls "*$($config.POSTGRES_PASSWORD)*") -eq 0) 'Secrets absent from argument values'
    Assert-True ([Environment]::GetEnvironmentVariable('POSTGRES_PASSWORD', 'Process') -eq 'prior-process-value') 'Process environment restored'
} finally { [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD', $oldPassword, 'Process') }

Reset-Fixture
$script:ResourcesExist = $true
Invoke-ReviewDeployment
Assert-True ((Count-Calls 'volume|rm|*') -eq 0) 'Normal rerun preserves database volume'
Assert-True ((Count-Calls 'rm|-f|*') -eq 2) 'Normal rerun replaces owned containers'

Reset-Fixture
$script:ResourcesExist = $true
$script:ResetDatabase = $true
Invoke-ReviewDeployment
Assert-True ((Count-Calls 'volume|rm|retravion-review-postgres') -eq 1) 'Explicit reset removes only deployment volume'
Assert-True ((Count-Calls 'volume|create|*') -eq 1) 'Reset recreates volume'

Reset-Fixture
$script:ResourcesExist = $true
$script:ResetDatabase = $true
$script:Answer = 'no'
Assert-Throws { Invoke-ReviewDeployment } 'reset cancelled'
Assert-True ((Count-Calls '*rm|*') -eq 0) 'Declined reset changes no resources'

Reset-Fixture
$script:ResourcesExist = $true
$script:Owned = $false
Assert-Throws { Invoke-ReviewDeployment } 'Refusing to modify unowned'
Assert-True ((Count-Calls '*rm|*') -eq 0) 'Unowned resources protected'

Reset-Fixture
$script:ResourcesExist = $true
$script:FailBuild = $true
Assert-Throws { Invoke-ReviewDeployment } 'mock build failure'
Assert-True ((Count-Calls '*rm|*') -eq 0) 'Build failure preserves existing containers and data'

Reset-Fixture
$script:UpdateImages = $true
Invoke-ReviewDeployment
Assert-True ((Count-Calls 'pull|*') -eq 3) 'Update pulls all three pinned images'

Reset-Fixture
$script:ImagesCached = $false
Invoke-ReviewDeployment
Assert-True ((Count-Calls 'pull|*') -eq 3) 'Missing images downloaded'

Reset-Fixture
$script:FailDatabase = $true
Assert-Throws { Invoke-ReviewDeployment } 'PostgreSQL did not become ready'
Assert-True ((Count-Calls 'run|*retravion-review:local') -eq 0) 'App not started before database readiness'

Reset-Fixture
$script:Machines = '[]'
Assert-Throws { Invoke-ReviewDeployment } 'podman machine init'
Reset-Fixture
$script:Machines = '[{"Name":"one","Running":true},{"Name":"two","Running":false}]'
Assert-Throws { Invoke-ReviewDeployment } 'Multiple Podman machines'

Write-Host 'PASS: deployment environment, lifecycle, safety, caching, and failure checks.'
