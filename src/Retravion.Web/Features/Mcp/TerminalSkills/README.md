# Retravion — %CLIENT% integration 1.0.0

Requires the %CLIENT% terminal with native Streamable HTTP MCP and Agent Skills support.
No terminal client, installer, OAuth service or companion bridge is included. Use the terminal vendor's installation instructions.
This skill guides six tools: read-only Products and User Need reads/Draft writes. The server may also expose Requirement tools; Requirement workflows are not included in this skill version. The skill does not enable additional entities.

## Install into your project directory

1. Extract the ZIP into a temporary directory and review all files. Verify its SHA-256 against the value on the authenticated AI Workflow page; the hash checks integrity, not publisher identity.
2. Copy `skills/retravion/SKILL.md` to `%SKILL_PATH%/SKILL.md` in your project. If that path already exists, back it up and compare it before deliberately replacing it. Do not replace an entire existing skills directory.
3. Merge the `retravion` server entry from `mcp.example.json` into `%CONFIG_PATH%` in your project. Create that file only if absent. Preserve unrelated server entries and other configuration. If a `retravion` entry already exists, back it up and compare it first.
4. The example uses `https://localhost:8443/mcp`. Adjust it to the application's actual loopback endpoint/port. Remote deployment is unsupported. Trust the local HTTPS certificate in the client runtime; never disable TLS verification. Current Node clients can use `NODE_USE_SYSTEM_CA=1` with a trusted Windows certificate, or `NODE_EXTRA_CA_CERTS` pointing to a trusted PEM certificate.
5. Ask an administrator to enable MCP in Settings → AI Workflow. Sign in in your browser, finish required password replacement and create an MCP credential. In PowerShell 7, set it without placing it in shell history:

   ```powershell
   $env:RETRAVION_MCP_TOKEN = Read-Host 'MCP token' -MaskInput
   ```

   Never put the credential itself into the JSON file, source control, prompts, URLs or shared logs. The `${RETRAVION_MCP_TOKEN}` reference in the example is intentional.
6. Start `%EXECUTABLE%` from this project directory with that environment. Review the client's normal project-trust/MCP approval prompts. Check the connection using `%EXECUTABLE% mcp list`; in Pi `/reload` rescans skills/configuration, but restart the terminal when its inherited environment changes.
7. Discover/invoke `%SKILL_COMMAND%`, or ask “Use the retravion skill. Read my Products and help prepare User Need Drafts.” The skill requires explicit conversation approval before writes. Server permissions still apply.

## Reconnect

Credentials expire 30 minutes after issuance, or earlier when the browser session ends. Logout, account-session revocation, restart, credential replacement, revocation or global disabling ends access. Re-enabling global MCP does not restore old credentials. Obtain a fresh one in the browser and restart the terminal with the updated environment.

## Update manually

Close this terminal's MCP connection, download the new ZIP, review it and back up your existing Retravion skill/server entry.
Replace only `%SKILL_PATH%/SKILL.md`; merge any documented changes into only the `retravion` entry in `%CONFIG_PATH%`. Preserve endpoint customizations and every unrelated entry/file. Restart and verify skill/tool discovery. Keep the prior ZIP and backups; if verification fails, restore them before reconnecting. An update does not grant new server permissions.

## Remove manually

Close this terminal's MCP connection. Remove only `%SKILL_PATH%` and the `retravion` server entry from `%CONFIG_PATH%`; retain the config file if any unrelated settings/servers remain. Do not delete the parent skills directory. The other terminal's installation is independent and must be left untouched. Restart the client. Revoke your credential in the browser and clear its environment variable when finished:

```powershell
Remove-Item Env:RETRAVION_MCP_TOKEN -ErrorAction SilentlyContinue
```

Never automatically retry a write after an uncertain result or a lost response. Retrieve current state first, then let the human decide whether to retry manually.
