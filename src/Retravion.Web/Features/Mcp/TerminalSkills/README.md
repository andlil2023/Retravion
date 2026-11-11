# Retravion — %CLIENT% integration 2.1.0

Connect %CLIENT% on this machine to Retravion: run one setup script per project, then approve each connection in your browser. There is no token to copy and no environment variable to set.

Requires %CLIENT%, Node.js 22 or later, and Retravion running on this machine. Remote deployment is unsupported.
This skill guides ten server tools: read-only Products, User Need reads/Draft writes and Requirement reads/Draft writes. Requirement writes require an explicit type (System, Software, Hardware, Safety, Usability or Regulatory); rationale is optional plain text, and omitted/null rationale clears it on edit. The skill requires explicit human approval of proposed content before saving, a current revision for edits, and fresh approval after stale writes. The bridge also forwards the server's Test Case tools; Test Case workflows, relationship/link writes and lifecycle operations are not supported by this skill version.

## Set up once per project directory

1. Extract the ZIP into a temporary directory and review its files. Verify its SHA-256 against the value on the authenticated AI Workflow page; the hash checks integrity, not publisher identity.
2. Ask an administrator to enable MCP in Settings → AI Workflow, and make sure Retravion is running.
3. Run the setup script, naming your project directory:

   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp
   ```

   Add `-Endpoint https://localhost:<port>/mcp` if Retravion does not use port 8443.
4. The script shows the SHA-256 fingerprint of Retravion's HTTPS certificate. Compare it with the fingerprint on the AI Workflow page and type `TRUST` only if they match.

The script installs `%SKILL_PATH%/SKILL.md`, the bridge and the confirmed certificate in `.retravion-terminal/`, and adds only the `retravion` entry to `%CONFIG_PATH%`. Preserve unrelated server entries: the script keeps them, and every other setting, unchanged. Nothing is added to the Windows certificate store and TLS verification is never disabled. `.retravion-terminal/` contains no credential; do not commit it, because it holds a path and certificate specific to this machine.

## Connect in every session

1. Start `%EXECUTABLE%` in the project directory and accept its normal project-trust and MCP approval prompts.
2. Run `%SKILL_COMMAND%` and ask it to connect to Retravion. Answer yes when it asks for consent.
3. Your browser opens at Retravion. Sign in if needed, check that the code matches the one in the terminal, and select **Approve connection**. Signing in takes you on to the approval. If the page does not show the code, enter it on the AI Workflow page.
4. Tell the terminal you have approved. It is now connected.

Access renews while you work. It ends after 30 idle minutes, on browser logout or session expiry, when Retravion restarts, when you select **End terminal connection**, or when an administrator disables MCP. Ask the terminal to connect again; restarting it is not needed.

## When the certificate is replaced

The bridge trusts only the certificate confirmed during setup. If Retravion's certificate is regenerated, connecting fails with an instruction to rerun setup. Run step 3 above again and confirm the new fingerprint.

## Update or remove

To update, close the terminal, extract the new ZIP and run its `mcp_setup.ps1` again.

To remove, close the terminal and run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp -Remove
```

Remove only deletes `%SKILL_PATH%` and the `retravion` entry in `%CONFIG_PATH%`. The shared `.retravion-terminal/` folder is deleted when no other Retravion terminal integration remains in the project.

Never automatically retry a write after an uncertain result or a lost response. Retrieve current state first, then let the human decide whether to retry manually.
