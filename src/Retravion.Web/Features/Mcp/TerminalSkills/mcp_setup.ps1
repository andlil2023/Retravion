<#
.SYNOPSIS
Explicit Retravion terminal install/update for a project directory. Rerun from a new ZIP to update, or when the HTTPS certificate is replaced.

.DESCRIPTION
Installs the Retravion skill and local bridge into the project, registers the bridge in the terminal's
project MCP configuration, and saves Retravion's HTTPS certificate for the bridge after you confirm its
fingerprint. Nothing is added to the Windows certificate store, no environment variable is set, and no
credential is created. Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp
#>
[CmdletBinding()]
param(
    [ValidateSet('pi', 'claude')] [string] $Client = '%CLIENT_ID%',
    [string] $Directory = (Get-Location).Path,
    [string] $Endpoint = 'https://localhost:8443/mcp',
    # Expected SHA-256 fingerprint from the AI Workflow page. When given, it must match and no prompt is shown.
    [string] $Fingerprint,
    # Use an exported certificate file instead of asking the running application for its certificate.
    [string] $CertificateFile,
    [switch] $Remove
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RetravionFingerprint([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Certificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join ':') }
    finally { $sha.Dispose() }
}

function Get-RetravionCertificate([uri] $Uri, [string] $File) {
    if ($File) { return [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $File).Path) }
    $tcp = [Net.Sockets.TcpClient]::new()
    try {
        $tcp.Connect($Uri.Host, $Uri.Port)
        # Capture only: the certificate is read so you can check it. Nothing is trusted until you confirm below.
        $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false, { $true })
        try {
            $ssl.AuthenticateAsClient($Uri.Host)
            return [Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate)
        } finally { $ssl.Dispose() }
    } catch {
        throw "Cannot read the HTTPS certificate from $($Uri.Host):$($Uri.Port). Start Retravion, check -Endpoint, or pass -CertificateFile."
    } finally { $tcp.Dispose() }
}

function Write-RetravionText([string] $Path, [string] $Text) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Read-RetravionConfig([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{} }
    try { $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { $config = $null }
    if ($config -isnot [pscustomobject]) { throw "Cannot read $Path as a JSON object. Fix or move it, then rerun. Nothing was changed." }
    return $config
}

$root = (Resolve-Path -LiteralPath $Directory).Path
# Guard against installing one level too deep: the project is the folder that contains .pi or .claude,
# and it is never the folder the ZIP was extracted into unless you say so.
if (@('.pi', '.claude', '.retravion-terminal') -contains (Split-Path -Leaf $root)) {
    throw "$root is a terminal configuration folder, not a project directory. Pass -Directory <your project directory>. Nothing was changed."
}
if (-not $PSBoundParameters.ContainsKey('Directory') -and $root -eq (Resolve-Path -LiteralPath $PSScriptRoot).Path) {
    throw 'Pass -Directory <your project directory>. Without it the extracted ZIP folder itself would be used. Nothing was changed.'
}
$skill = Join-Path $root $(if ($Client -eq 'pi') { '.pi/skills/retravion/SKILL.md' } else { '.claude/skills/retravion/SKILL.md' })
$configPath = Join-Path $root $(if ($Client -eq 'pi') { '.pi/mcp.json' } else { '.mcp.json' })
$runtime = Join-Path $root '.retravion-terminal'
$otherSkill = Join-Path $root $(if ($Client -eq 'pi') { '.claude/skills/retravion/SKILL.md' } else { '.pi/skills/retravion/SKILL.md' })

if ($Remove) {
    $config = Read-RetravionConfig $configPath
    if ($config.PSObject.Properties['mcpServers'] -and $config.mcpServers.PSObject.Properties['retravion']) {
        $config.mcpServers.PSObject.Properties.Remove('retravion')
        Write-RetravionText $configPath ($config | ConvertTo-Json -Depth 32)
    }
    if (Test-Path -LiteralPath $skill) { Remove-Item -LiteralPath (Split-Path -Parent $skill) -Recurse -Force }
    # The bridge folder is shared: keep it while the other terminal still uses it.
    if (-not (Test-Path -LiteralPath $otherSkill) -and (Test-Path -LiteralPath $runtime)) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    Write-Host "Removed the Retravion $Client integration from $root. Restart the terminal."
    return
}

$uri = [uri] $Endpoint
if ($uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/mcp' -or $uri.Query -or $uri.UserInfo -or
    @('localhost', '127.0.0.1', '[::1]') -notcontains $uri.Host) {
    throw 'Endpoint must be https://localhost:<port>/mcp. Remote Retravion access is not supported.'
}
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js 22 or later is required for the Retravion bridge. Install it, then rerun.' }
if ([int]((& $node.Source --version) -replace '^v(\d+).*', '$1') -lt 22) { throw 'Node.js 22 or later is required for the Retravion bridge.' }
foreach ($file in @('bridge/import-run.mjs', 'bridge/import-plan.mjs', 'bridge/server.mjs', 'bridge/tools.json', 'skills/retravion/SKILL.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file))) { throw "Package file $file is missing. Extract the complete ZIP and run the script from there." }
}
# Read existing configuration before anything is written, so a problem leaves the project untouched.
$config = Read-RetravionConfig $configPath

$certificate = Get-RetravionCertificate $uri $CertificateFile
$actual = Get-RetravionFingerprint $certificate
if ($Fingerprint) {
    if (($Fingerprint -replace '[^0-9A-Fa-f]', '') -ne ($actual -replace ':', '')) {
        throw "Certificate fingerprint does not match. Expected $Fingerprint, found $actual. Nothing was changed."
    }
} else {
    Write-Host "Retravion HTTPS certificate for $($uri.Host):$($uri.Port)"
    Write-Host "  Subject:  $($certificate.Subject)"
    Write-Host "  Expires:  $($certificate.NotAfter.ToString('u'))"
    Write-Host "  SHA-256:  $actual"
    Write-Host 'Compare the SHA-256 value with the one on the Retravion AI Workflow page.'
    if ((Read-Host 'Let the Retravion bridge in this project trust this certificate? Type TRUST') -cne 'TRUST') {
        throw 'Certificate not confirmed. Nothing was changed.'
    }
}

$pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($certificate.RawData, 'InsertLineBreaks').Replace("`r`n", "`n") + "`n-----END CERTIFICATE-----`n"
$bridge = Join-Path $runtime 'bridge'
New-Item -ItemType Directory -Path $bridge -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge/import-run.mjs') -Destination $bridge -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge/import-plan.mjs') -Destination $bridge -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge/server.mjs') -Destination $bridge -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge/tools.json') -Destination $bridge -Force
Write-RetravionText (Join-Path $runtime 'retravion.pem') $pem
Write-RetravionText (Join-Path $runtime 'endpoint.json') (@{ endpoint = $uri.AbsoluteUri } | ConvertTo-Json)
Write-RetravionText $skill (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'skills/retravion/SKILL.md') -Raw)

# Only the retravion entry is added or replaced; every other entry and setting is kept.
$entry = [pscustomobject]@{ command = 'node'; args = @((Join-Path $bridge 'server.mjs')) }
if (-not $config.PSObject.Properties['mcpServers']) { $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) }
$config.mcpServers | Add-Member -NotePropertyName retravion -NotePropertyValue $entry -Force
Write-RetravionText $configPath ($config | ConvertTo-Json -Depth 32)

Write-Host "Retravion $Client integration installed in $root. Restart the terminal to load the installed skill and bridge."
Write-Host "Start $Client there, accept its project and MCP prompts, then run $(if ($Client -eq 'pi') { '/skill:retravion' } else { '/retravion' }) and ask it to connect."
