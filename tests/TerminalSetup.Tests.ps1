# Dependency-free script checks: pwsh -NoProfile -File tests/TerminalSetup.Tests.ps1
# Temporary directories and a throwaway in-memory certificate only: no network request,
# certificate-store change, Retravion credential or terminal client is used.
#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Throws([scriptblock] $Action, [string] $Expected) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught.Contains($Expected)) "Expected error containing '$Expected'; got '$caught'"
}

$source = Join-Path $PSScriptRoot '../src/Retravion.Web/Features/Mcp/TerminalSkills'
$temp = Join-Path ([IO.Path]::GetTempPath()) "retravion-setup-tests-$([guid]::NewGuid())"
try {
    # Lay the package out as the ZIP does, with the client placeholder resolved.
    $package = Join-Path $temp 'package'
    New-Item (Join-Path $package 'bridge') -ItemType Directory -Force | Out-Null
    New-Item (Join-Path $package 'skills/retravion') -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $source 'bridge/*') (Join-Path $package 'bridge')
    Copy-Item (Join-Path $source 'SKILL.md') (Join-Path $package 'skills/retravion')
    $setup = Join-Path $package 'mcp_setup.ps1'
    Set-Content $setup ((Get-Content (Join-Path $source 'mcp_setup.ps1') -Raw).Replace('%CLIENT_ID%', 'pi')) -NoNewline

    $key = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $key,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
    $cer = Join-Path $temp 'throwaway.cer'
    [IO.File]::WriteAllBytes($cer, $certificate.Export('Cert'))
    $fingerprint = ([Security.Cryptography.SHA256]::HashData($certificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join ':'

    $project = Join-Path $temp 'project'
    New-Item (Join-Path $project '.pi') -ItemType Directory -Force | Out-Null
    $existing = '{"mcpServers":{"other":{"url":"https://example.invalid/mcp","headers":{"X-Keep":"1"}}},"unrelated":[1,2]}'
    Set-Content (Join-Path $project '.pi/mcp.json') $existing -NoNewline

    # A wrong fingerprint, a remote endpoint and unreadable configuration change nothing.
    Assert-Throws { & $setup -Directory $project -CertificateFile $cer -Fingerprint ('00:' * 31 + '00') } 'does not match'
    Assert-Throws { & $setup -Directory $project -CertificateFile $cer -Fingerprint $fingerprint -Endpoint 'https://example.com/mcp' } 'Remote Retravion access is not supported'
    Assert-Throws { & $setup -Directory $project -CertificateFile $cer -Fingerprint $fingerprint -Endpoint 'http://localhost:8443/mcp' } 'Endpoint must be'
    Assert-True ((Get-Content (Join-Path $project '.pi/mcp.json') -Raw) -ceq $existing) 'Configuration untouched after refusal'
    Assert-True (-not (Test-Path (Join-Path $project '.retravion-terminal'))) 'Nothing trusted or installed after refusal'

    # Declining the interactive confirmation changes nothing either.
    function global:Read-Host { 'no' }
    try { Assert-Throws { & $setup -Directory $project -CertificateFile $cer 6>$null } 'Certificate not confirmed' }
    finally { Remove-Item function:global:Read-Host }
    Assert-True (-not (Test-Path (Join-Path $project '.retravion-terminal'))) 'Nothing trusted after declined confirmation'

    $broken = Join-Path $temp 'broken'
    New-Item (Join-Path $broken '.pi') -ItemType Directory -Force | Out-Null
    Set-Content (Join-Path $broken '.pi/mcp.json') '{not json'
    Assert-Throws { & $setup -Directory $broken -CertificateFile $cer -Fingerprint $fingerprint } 'Nothing was changed'
    Assert-True (-not (Test-Path (Join-Path $broken '.retravion-terminal'))) 'Nothing installed beside unreadable configuration'

    # Fresh install, then an identical rerun.
    foreach ($run in 1..2) {
        & $setup -Directory $project -CertificateFile $cer -Fingerprint $fingerprint -Endpoint 'https://localhost:9443/mcp' 6>$null
        $config = Get-Content (Join-Path $project '.pi/mcp.json') -Raw | ConvertFrom-Json
        Assert-True ($config.mcpServers.other.url -eq 'https://example.invalid/mcp' -and $config.mcpServers.other.headers.'X-Keep' -eq '1') 'Unrelated server preserved'
        Assert-True (($config.unrelated -join ',') -eq '1,2') 'Unrelated setting preserved'
        Assert-True ($config.mcpServers.retravion.command -eq 'node') 'Bridge registered as a stdio server'
        $server = $config.mcpServers.retravion.args[0]
        Assert-True ((Test-Path $server) -and $server.StartsWith((Resolve-Path $project).Path)) 'Bridge path points into the project'
        Assert-True ($null -eq $config.mcpServers.retravion.PSObject.Properties['headers'] -and $null -eq $config.mcpServers.retravion.PSObject.Properties['env']) 'No credential header or environment variable'
    }
    $runtime = Join-Path $project '.retravion-terminal'
    Assert-True ((Get-Content (Join-Path $runtime 'endpoint.json') -Raw | ConvertFrom-Json).endpoint -eq 'https://localhost:9443/mcp') 'Endpoint saved'
    $saved = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content (Join-Path $runtime 'retravion.pem') -Raw))
    Assert-True ($saved.Thumbprint -eq $certificate.Thumbprint) 'Confirmed certificate saved as PEM'
    Assert-True (Test-Path (Join-Path $runtime 'bridge/tools.json')) 'Tool list installed'
    Assert-True (Test-Path (Join-Path $project '.pi/skills/retravion/SKILL.md')) 'Skill installed'
    Assert-True (-not (Test-Path (Join-Path $project '.mcp.json'))) 'Other client untouched'
    Assert-True (-not (Get-ChildItem $project -Recurse -Force -File | Select-String -SimpleMatch 'RETRAVION_MCP_TOKEN')) 'No token variable anywhere'

    # The installed bridge starts with only what setup wrote and lists the connection tools.
    $lines = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}', '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | node $server
    $names = ($lines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object id -eq 2).result.tools.name
    Assert-True ($names -contains 'rtv_connect' -and $names -contains 'list_products') 'Installed bridge lists its tools'

    # Real TLS on loopback with throwaway certificates: the bridge accepts exactly the confirmed
    # certificate and fails closed for any other one. No Retravion instance or credential is involved.
    function New-Throwaway {
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $names.AddDnsName('localhost'); $req.CertificateExtensions.Add($names.Build())
        $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        return @{ Cert = $cert.ExportCertificatePem(); Key = $rsa.ExportPkcs8PrivateKeyPem() }
    }
    $served = New-Throwaway; $other = New-Throwaway
    Set-Content (Join-Path $temp 'served.pem') $served.Cert; Set-Content (Join-Path $temp 'served.key') $served.Key
    Set-Content (Join-Path $temp 'other.pem') $other.Cert
    $probe = Join-Path $temp 'probe.mjs'
    Set-Content $probe @'
import https from "node:https";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
const [bridge, cert, key, other] = process.argv.slice(2);
const { createRequest } = await import(pathToFileURL(bridge).href);
const server = https.createServer({ cert: readFileSync(cert), key: readFileSync(key) }, (request, response) => response.end("ok"));
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const url = new URL(`https://localhost:${server.address().port}/mcp`);
const trusted = await createRequest(readFileSync(cert, "utf8"))(url, { method: "POST" });
let refused = false;
try { await createRequest(readFileSync(other, "utf8"))(url, { method: "POST" }); } catch { refused = true; }
server.close();
console.log(JSON.stringify({ status: trusted.status, text: trusted.text, refused }));
'@
    $tls = node $probe $server (Join-Path $temp 'served.pem') (Join-Path $temp 'served.key') (Join-Path $temp 'other.pem') | ConvertFrom-Json
    Assert-True ($tls.status -eq 200 -and $tls.text -eq 'ok') 'Bridge reaches the server presenting the confirmed certificate'
    Assert-True ($tls.refused -eq $true) 'Bridge refuses a server whose certificate was not confirmed'

    # Claude uses its own paths and shares the bridge; removing one client keeps the other working.
    & $setup -Client claude -Directory $project -CertificateFile $cer -Fingerprint $fingerprint 6>$null
    Assert-True ((Get-Content (Join-Path $project '.mcp.json') -Raw | ConvertFrom-Json).mcpServers.retravion.command -eq 'node') 'Claude entry added'
    Assert-True (Test-Path (Join-Path $project '.claude/skills/retravion/SKILL.md')) 'Claude skill installed'
    & $setup -Directory $project -Remove 6>$null
    $config = Get-Content (Join-Path $project '.pi/mcp.json') -Raw | ConvertFrom-Json
    Assert-True ($null -eq $config.mcpServers.PSObject.Properties['retravion'] -and $config.mcpServers.other.url -eq 'https://example.invalid/mcp') 'Only the Retravion entry removed'
    Assert-True (-not (Test-Path (Join-Path $project '.pi/skills/retravion'))) 'Pi skill removed'
    Assert-True (Test-Path (Join-Path $runtime 'bridge/server.mjs')) 'Shared bridge kept while Claude uses it'
    & $setup -Client claude -Directory $project -Remove 6>$null
    Assert-True (-not (Test-Path $runtime)) 'Bridge removed with the last client'
    Write-Host 'Terminal setup script checks passed.'
} finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
