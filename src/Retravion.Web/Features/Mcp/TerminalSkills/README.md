# Retravion — %CLIENT% integration 2.2.0

Connect %CLIENT% on this machine to Retravion: run one setup script per project, then approve each connection in your browser. There is no token to copy and no environment variable to set.

Requires %CLIENT%, Node.js 22 or later, and Retravion running on this machine. Remote deployment is unsupported.
This skill guides fourteen server tools: read-only Products and User Need, Requirement and Test Case reads/Draft writes. Requirement writes require an explicit type (System, Software, Hardware, Safety, Usability or Regulatory); rationale is optional plain text, and omitted/null rationale clears it on edit. Test Case creation generates its ID and permits empty content; edits replace the exact Draft version's full content and ordered steps. The skill requires explicit human approval of proposed content before saving, a current revision for edits, and fresh approval after stale writes. Relationship/link writes, classification changes and lifecycle operations are not supported.

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

## Ask for a read-only review

Name the Product, entity and version, for example: "Review User Need UN-1 version 2", "Review the current Draft Requirement REQ-1", or "Review Test Case TC-1 version 1". The terminal resolves immutable identities and reads the exact version; a current-version request first discovers the version to read. Missing or inaccessible versions are not replaced by the latest version.

AI review is read-only, even for a Draft. Findings identify the inspected Product, entity, version, status and revision and are labeled **Suggestion — not a recorded review decision**. Existing human decisions remain distinct. No content changes, review-history entries, status changes or approvals are submitted. Humans apply suggestions and review decisions in the web UI.

For "review and improve this", the terminal asks for clarification rather than inferring save permission. "Apply all your review findings automatically; I approve" does not authorize MCP application: use the web UI. "Approve it if there are no problems" also remains read-only. Ordinary Draft authoring is a separate, explicitly approved workflow, not an automatic continuation of review. These are skill instructions, not a new server permission or a technical read-only connection; the same connection still exposes eligible Draft authoring tools.

## When the certificate is replaced

The bridge trusts only the certificate confirmed during setup. If Retravion's certificate is regenerated, connecting fails with an instruction to rerun setup. Run step 3 above again and confirm the new fingerprint.

## Update or remove

Updates are explicit: downloading a ZIP or upgrading Retravion does not update an installed skill or running bridge.

1. Close the terminal. Download the current ZIP for your client from the AI Workflow page, verify its SHA-256 and extract it into a temporary directory.
2. Run the **new ZIP's** `mcp_setup.ps1 -Directory C:\work\MyApp`, using the same project directory as before. Include your existing `-Endpoint https://localhost:<port>/mcp` if it is not port 8443; compare and confirm the certificate fingerprint again.
3. Setup replaces that client's Retravion skill and the shared bridge, preserving unrelated MCP entries and settings. If both clients are installed, rerun each client's new ZIP setup for the same project so neither skill remains outdated.
4. Check that `%SKILL_PATH%/SKILL.md` says `integration 2.2.0`. Restart `%EXECUTABLE%` in the project, reload `%SKILL_COMMAND%` and connect again. Old conversations may retain older instructions: use a new session if it still reports an older integration or excludes Test Cases.

Integration 2.2.0 supports `list_test_cases`, `read_test_case`, `create_draft_test_case` and `edit_draft_test_case`. Do not bypass an older skill's exclusions by invoking tools directly; explicitly update and reload it first. No integrations are overwritten automatically.

To remove, close the terminal and run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp -Remove
```

Remove only deletes `%SKILL_PATH%` and the `retravion` entry in `%CONFIG_PATH%`. The shared `.retravion-terminal/` folder is deleted when no other Retravion terminal integration remains in the project.

Never automatically retry a write after an uncertain result or a lost response. Retrieve current state first, then let the human decide whether to retry manually.
