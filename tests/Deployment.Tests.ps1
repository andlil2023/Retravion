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
    Assert-True ($config.INITIAL_ADMIN_USERNAME -ceq '') 'No default administrator username'
    Assert-True ($config.INITIAL_ADMIN_PASSWORD -ceq '') 'No generated or default administrator password'
    Assert-True ($config.HTTPS_CERT_PASSWORD -ne $config.POSTGRES_PASSWORD) 'Independent random secrets'
    $again = Read-ReviewEnvironment $temp
    Assert-True ($again.HTTPS_CERT_PASSWORD -eq $config.HTTPS_CERT_PASSWORD) 'Secrets preserved on rerun'
    Assert-True ((Get-Content (Join-Path $temp '.env') -Raw) -ceq $original) '.env never rewritten'
    $adminText = $original.Replace('INITIAL_ADMIN_USERNAME=', 'INITIAL_ADMIN_USERNAME= chosen user ').Replace('INITIAL_ADMIN_PASSWORD=', 'INITIAL_ADMIN_PASSWORD= Abc123!💡 =# ')
    Set-Content (Join-Path $temp '.env') $adminText
    $config = Read-ReviewEnvironment $temp
    Assert-True ($config.INITIAL_ADMIN_USERNAME -ceq ' chosen user ') 'Username preserved verbatim'
    Assert-True ($config.INITIAL_ADMIN_PASSWORD -ceq ' Abc123!💡 =# ') 'Unicode, spaces, punctuation and equals preserved verbatim'
    Set-Content (Join-Path $temp '.env') ($original.Replace('INITIAL_ADMIN_USERNAME=', '#INITIAL_ADMIN_USERNAME='))
    Assert-Throws { Read-ReviewEnvironment $temp } 'INITIAL_ADMIN_USERNAME'
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
                return "[{`"Labels`":{`"com.retravion.review`":`"$label`"},`"Config`":{`"Labels`":{`"com.retravion.review`":`"$label`"}},`"State`":{`"Running`":false}}]"
            }
        }
        'image' { $global:LASTEXITCODE = if ($script:ImagesCached) { 0 } else { 1 } }
        'build' { if ($script:FailBuild) { $global:LASTEXITCODE = 125; return 'mock build failure' } }
        'run' { if ($script:FailRecovery -and $arguments -contains '--recover-initial-administrator') { $global:LASTEXITCODE = 1; return 'sensitive provider output' } }
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
    $script:FailRecovery = $false
    $script:FailDatabase = $false
    $script:ResetDatabase = $false
    $script:UpdateImages = $false
    $script:Development = $false
}
function Count-Calls([string] $Pattern) { return @($script:Calls | Where-Object { $_ -like $Pattern }).Count }

Reset-Fixture
$script:ResourcesExist = $true
$script:Answer = 'RECOVER'
Invoke-ReviewRecovery
Assert-True ((Count-Calls 'build|*') -eq 1) 'Recovery builds the command image'
Assert-True ($script:Calls.FindIndex([Predicate[string]] { param($call) $call -like 'build|*' }) -lt $script:Calls.FindIndex([Predicate[string]] { param($call) $call -eq 'stop|retravion-review-app' })) 'Recovery builds before stopping the application'
Assert-True ((Count-Calls 'stop|retravion-review-app') -eq 1) 'Recovery stops the application'
Assert-True ((Count-Calls 'run|*--recover-initial-administrator') -eq 1) 'Recovery invokes one-off command'
Assert-True ((Count-Calls 'start|retravion-review-app') -eq 0) 'Old image container is never restarted'
Assert-True ((Count-Calls 'rm|retravion-review-app') -eq 1) 'Recovery removes the old image container'
Assert-True ((Count-Calls 'run|-d|*--name|retravion-review-app|*localhost/retravion-review:local') -eq 1) 'Recovery recreates the application from the newly built image'
Assert-True ($script:Calls.FindIndex([Predicate[string]] { param($call) $call -like 'run|*--recover-initial-administrator' }) -lt $script:Calls.FindIndex([Predicate[string]] { param($call) $call -eq 'rm|retravion-review-app' })) 'Recovery succeeds before replacing the old container'
Assert-True ((Count-Calls "*$($config.INITIAL_ADMIN_PASSWORD)*") -eq 0) 'Recovery password absent from arguments'

Reset-Fixture
$script:ResourcesExist = $true
$script:Answer = 'RECOVER'
$script:FailBuild = $true
Assert-Throws { Invoke-ReviewRecovery } 'build'
Assert-True ((Count-Calls 'stop|*') -eq 0) 'Failed recovery image build leaves application running'
Assert-True ((Count-Calls 'run|*') -eq 0) 'Failed build cannot run an outdated image'

Reset-Fixture
$script:ResourcesExist = $true
$script:Answer = 'RECOVER'
$script:FailRecovery = $true
Assert-Throws { Invoke-ReviewRecovery } 'Administrator recovery failed'
Assert-True ((Count-Calls 'start|retravion-review-app') -eq 0) 'Failed recovery leaves application stopped'
Assert-True ((Count-Calls 'rm|retravion-review-app') -eq 0) 'Failed recovery preserves the stopped container'
Assert-True ((Count-Calls 'run|-d|*--name|retravion-review-app|*') -eq 0) 'Failed recovery does not create a new application'

Reset-Fixture
$script:ResourcesExist = $true
$script:Answer = 'no'
Assert-Throws { Invoke-ReviewRecovery } 'Recovery cancelled'
Assert-True ((Count-Calls 'stop|*') -eq 0) 'Declined recovery leaves application running'

# Both deployment and recovery must explicitly choose the environment, ignoring shell defaults.
$oldAspNetEnvironment = $env:ASPNETCORE_ENVIRONMENT
$oldDotNetEnvironment = $env:DOTNET_ENVIRONMENT
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    foreach ($developmentMode in @($false, $true)) {
        foreach ($recoveryMode in @($false, $true)) {
            Reset-Fixture
            $script:ResourcesExist = $true
            $script:Development = $developmentMode
            if ($recoveryMode) {
                $script:Answer = 'RECOVER'
                Invoke-ReviewRecovery
            } else { Invoke-ReviewDeployment }
            $expectedEnvironment = if ($developmentMode) { 'Development' } else { 'Production' }
            $appCall = @($script:Calls | Where-Object { $_ -like 'run|-d|*--name|retravion-review-app|*' })
            Assert-True ($appCall.Count -eq 1) 'One application host recreated'
            Assert-True ($appCall[0].Contains("|-e|ASPNETCORE_ENVIRONMENT=$expectedEnvironment|")) 'Explicit ASP.NET hosting environment'
            Assert-True ($appCall[0].Contains("|-e|DOTNET_ENVIRONMENT=$expectedEnvironment|")) 'Explicit .NET hosting environment'
            Assert-True ($appCall[0].Contains('|-e|ConnectionStrings__DefaultConnection|')) 'Existing database connection configuration retained'
            Assert-True ($appCall[0].Contains('|-e|InitialAdmin__Username|-e|InitialAdmin__Password|')) 'Existing provisioning configuration retained'
            Assert-True ($appCall[0].Contains('127.0.0.1:8443:8443')) 'Existing loopback HTTPS binding retained'
            Assert-True ($appCall[0].Contains('localhost.pfx:ro')) 'Existing certificate mount retained'
            Assert-True ((Count-Calls 'volume|rm|*') -eq 0) 'Mode selection never removes the database volume'
            Assert-True ((Count-Calls 'volume|create|*') -eq 0) 'Existing database volume is reused'
            Assert-True ($env:ASPNETCORE_ENVIRONMENT -eq 'Development' -and $env:DOTNET_ENVIRONMENT -eq 'Development') 'Caller environment unchanged'
        }
    }
} finally {
    $env:ASPNETCORE_ENVIRONMENT = $oldAspNetEnvironment
    $env:DOTNET_ENVIRONMENT = $oldDotNetEnvironment
}

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
    Assert-True ((Count-Calls 'run|*InitialAdmin__Username*InitialAdmin__Password*') -eq 1) 'App receives both provisioning variable names'
    Assert-True ((Count-Calls "*$($config.INITIAL_ADMIN_PASSWORD)*") -eq 0) 'Administrator password absent from arguments'
    Assert-True ((Count-Calls "*$($config.INITIAL_ADMIN_USERNAME)*") -eq 0) 'Administrator username absent from arguments'
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
