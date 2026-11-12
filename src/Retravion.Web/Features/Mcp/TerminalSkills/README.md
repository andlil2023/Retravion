# Retravion — %CLIENT% integration 2.8.0

Area reads use `list_product_areas` with manual `productId`, including active/retired areas and retired Products. Record reads expose exact-version `areaId` or null. `edit_draft_requirement_classification` and `edit_draft_test_case_classification` change only an eligible Draft's classification: read exact owner first, show old/new area identities/code/label or absence, obtain explicit human approval, and send immutable `internalId`, exact `versionId`, positive `expectedRevision`, required `areaId` (active same-Product identity or explicit null to clear), and `confirmed=true`. Content, links, earlier versions and released evidence are preserved. Inherited retired classifications may remain during unrelated edits but cannot be newly selected. Area administration and lifecycle writes remain web-only; review mode never authorizes a save. Never retry automatically; after stale/uncertain outcomes retrieve authorized current state and obtain fresh human approval. Explicitly update/reload older integrations before use.

`explore_relationships` is read-only: supply `kind` (user-need, requirement, test-case or feature), immutable `internalId` and required exact `versionId`. Direct incoming/outgoing edges retain exact endpoints, ownership, direction, Product, status and newer-version indicators. Optional `expandedVersionIds` expands only explicitly selected reachable versions (at most 20; repeats load once). `limit` defaults to 100 (1–200), separately capping edges and unavailable provenance snapshots. Report truncation, omitted counts and unexpanded IDs; never imply a complete transitive closure or absent hidden links. Unavailable origins are stored copy-time snapshots only, not live/expandable sources. Exploration never mutates. Integration 2.8.0 separately supports `change_draft_link` for explicitly human-approved add/remove/retarget of ordinary links owned by eligible Draft Requirements, Test Cases and Features. Read exact owner/target versions first; supply the owner's positive `expectedRevision` and `confirmed=true`. Relationships are `user-need`, `parent`, `verifies`, `addresses`; retarget names the old endpoint and a `replacementVersionId` of the same target entity. Different-parent replacement remains web-only. No provenance or lifecycle writes; review mode remains read-only. After uncertain outcomes retrieve authorized current state before any human-directed retry; never retry automatically. Update and reload older integrations before using link writes.


Connect %CLIENT% on this machine to Retravion: run one setup script per project, then approve each connection in your browser. There is no token to copy and no environment variable to set.

Requires %CLIENT%, Node.js 22 or later, and Retravion running on this machine. Remote deployment is unsupported.
This skill guides read-only Products, Revisions/baselines and relationship exploration, plus User Need, Requirement, Test Case and Feature reads/Draft writes. Requirement writes require an explicit type (System, Software, Hardware, Safety, Usability or Regulatory); rationale is optional plain text, and omitted/null rationale clears it on edit. Test Case creation generates its ID and permits empty content; edits replace the exact Draft version's full content and ordered steps. The skill requires explicit human approval of proposed content before saving, a current revision for edits, and fresh approval after stale writes. Only the explicitly approved ordinary Draft link authoring described above is supported; Only bounded Draft Revision preparation described below is supported; provenance writes, area administration and lifecycle operations are not supported.

Use `list_revisions` with the Product manual `productId`, then `read_revision` with that `productId` and the Revision's immutable `internalId`. Both include retired Products. `revision.selections` are exact explicit roots; `members` is the complete outgoing baseline with `explicit` distinguishing roots from indirect members. Preserve each endpoint's kind, internalId, versionId and versionNumber—even multiple versions of one entity. `evidence=calculated-draft` is current Draft closure, not release evidence. `evidence=frozen-released` uses retained release-time members, identifiers, titles and area information, with release actor/time; never replace them with current versions or current area labels. Missing/inaccessible or incomplete baselines are errors, not permission to guess. Use `create_draft_revision` to create an empty Draft with approved `identifier` and `description`. Use `edit_draft_revision` to replace only that metadata, preserving exact selections. Use `change_draft_revision_selection` for `add`, `remove` or one atomic `replace`: `target` is the old endpoint for replacement; only replace accepts `replacement`. Endpoints require `kind` (`UserNeed`, `Requirement`, `TestCase`, `Feature`), `internalId`, `versionId` and `versionNumber`; never silently advance to newer versions. Each write requires explicit human approval of initial or old/new values and `confirmed=true`. Supply Product manual `productId`; edits/selections also require immutable Revision `internalId` and positive `expectedRevision` from a fresh read. Only Draft Revisions under active accessible Products may change. Referenced entities/links remain unchanged. Copy, delete, release and all entity lifecycle operations remain web-only. Review mode remains read-only. Never automatically retry; after uncertainty retrieve authorized list/read state before a human-directed retry. After uncertain creation without identity inspect candidates; if ambiguous, do not blindly recreate. Update and reload older integrations before using Revision writes.

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
4. Check that `%SKILL_PATH%/SKILL.md` says `integration 2.8.0`. Restart `%EXECUTABLE%` in the project, reload `%SKILL_COMMAND%` and connect again. Old conversations may retain older instructions: use a new session if it still reports an older integration or excludes Features.

Integration 2.8.0 supports `list_test_cases`, `read_test_case`, `create_draft_test_case` and `edit_draft_test_case`. Do not bypass an older skill's exclusions by invoking tools directly; explicitly update and reload it first. Feature tools are `list_features`, `read_feature`, `create_draft_feature` and `edit_draft_feature`. Creation generates identities and Draft v1; edits require exact working version and revision and preserve links. Obtain explicit initial/old-new content approval before writing. After uncertain creation, inspect candidate records and never blindly recreate an ambiguous result. No integrations are overwritten automatically.

To remove, close the terminal and run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\mcp_setup.ps1 -Directory C:\work\MyApp -Remove
```

Remove only deletes `%SKILL_PATH%` and the `retravion` entry in `%CONFIG_PATH%`. The shared `.retravion-terminal/` folder is deleted when no other Retravion terminal integration remains in the project.

Never automatically retry a write after an uncertain result or a lost response. Retrieve current state first, then let the human decide whether to retry manually.
