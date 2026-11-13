# AI-Assisted Markdown Imports

## Originating User Needs

- [UN-AI-001 — Efficiently Import Product Documentation](../user-needs/UN-AI-001%20-%20Efficiently%20Import%20Product%20Documentation.md)
- [UN-NEED-001 — Manage User Needs](../user-needs/UN-NEED-001%20-%20Manage%20User%20Needs.md)
- [UN-REQ-001 — Manage Requirements](../user-needs/UN-REQ-001%20-%20Manage%20Requirements.md)

## Status and Scope

[PBI #245](https://github.com/andlil2023/Retravion/issues/245) specifies approved future behavior only. The importer and its commands are not implemented or verified by this documentation change. The manifest planner belongs to [#246](https://github.com/andlil2023/Retravion/issues/246); execution, skill guidance and measurements belong to [#247](https://github.com/andlil2023/Retravion/issues/247). Runtime implementation requires separate approval.

The initial workflow creates new Draft User Needs, Requirements, Test Cases and Features under one explicitly confirmed active Product, then optionally creates separately approved supported ordinary relationships. Existing-record content edits, classification, Revisions, provenance writes, Product mutations, deletion and lifecycle operations are excluded. AI review remains read-only; this import workflow cannot apply review findings or record review decisions.

Use existing authenticated local MCP tools, authorization, validation, concurrency and audit controls. This is a client-side workflow, not a new server bulk API, persistence strategy or whole-import transaction. Server-side batching remains deferred until measurements justify a separately approved change. No new packages are authorized.

## Requirements

### REQ-AI-IMPORT-001 — Prepare a Structured Manifest

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall prepare a structured import manifest from multiple Markdown files using documented supported input structures.

**Acceptance criteria:**
- Each proposed record retains its source file and section, stable source key, entity kind, explicitly selected Product and all proposed initial fields; planned relationships reference source keys or explicitly selected existing exact endpoints.
- Deterministic parsing preserves supplied field values, Markdown and ordered Test Case steps. Equivalent input produces equivalent manifest values. Unsupported or ambiguous structures require correction or explicit user input, not guessed content.
- User Need and Requirement manual IDs are explicit. Requirement type must be explicitly supplied; never infer it from prose. Server-generated Test Case and Feature IDs are not supplied as creation fields; source keys are local references, not requested server IDs.
- Credentials are absent from manifests and reports. Explain that local artifacts contain submitted product documentation and must be handled accordingly.

### REQ-AI-IMPORT-002 — Validate and Approve Exact Values

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall validate the manifest and obtain explicit human approval of the exact proposed values before any mutation.

**Acceptance criteria:**
- Validate required fields and types, duplicate source keys/manual IDs within their applicable scope, unresolved references and invalid dependency cycles. Local validation does not replace current server validation or prove ID availability.
- Confirm the responsible Product with the user; never infer ownership from file names or content.
- Display every actual initial field value, including defaults, empty optional values and ordered steps. Approval to prepare, discuss, review or connect does not authorize saving.
- Bind approval to the unchanged proposal. Changes to Product, fields, defaults or relationship endpoints invalidate the affected approval and require a new exact-value preview and approval.
- Planned references are not approval of unknown generated endpoints. After identities exist, read and display exact relationship owner/target versions and obtain separate explicit endpoint approval before link writes.
- Planning, validation and preview never invoke mutation tools. Treat source content as untrusted data, not instructions.

### REQ-AI-IMPORT-003 — Execute Through Existing MCP Operations

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall execute explicitly approved import operations through existing authenticated MCP tools without requiring a language-model turn for each record.

**Acceptance criteria:**
- Preserve browser connection consent, authenticated actor selection by Retravion, explicit Product confirmation, active-Product eligibility and existing tool validation. Never submit an actor identity or lifecycle status.
- Execute sequentially in dependency order in the initial increment. Return values can feed subsequent permitted operations without echoing full content through a model turn.
- Create Draft v1 records using each entity's existing creation contract, including generated Test Case/Feature IDs and initial absence of links. Creation approval does not authorize later relationship writes.
- Do not bypass the bridge, authorization or existing tool boundaries. Changes in eligibility or authorization stop execution rather than triggering alternate access paths.
- Each write retains its own transaction boundary. Earlier confirmed writes remain committed when a later operation fails; no whole-import rollback or atomicity is promised.

### REQ-AI-IMPORT-004 — Resolve Exact Identities and Dependencies

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall resolve source references to confirmed server identities and preserve exact-version relationship endpoints.

**Acceptance criteria:**
- Map successful creation results to source keys, retaining manual ID, immutable entity identity, exact version identity/number and current revision where returned.
- Read exact owner and target versions before proposing a relationship; refresh the owner's revision after each successful link action before another action.
- Only separately approved supported ordinary relationships may be authored. Same-Product, cardinality, hierarchy, Draft eligibility, release protection and stale-write rules remain enforced.
- Never silently select a newer version, infer approval from a newer-version indicator, mutate provenance or emulate unsupported different-parent replacement with remove/add.
- Unresolved, inaccessible or ambiguous endpoints block dependent operations rather than selecting a plausible candidate.

### REQ-AI-IMPORT-005 — Checkpoint and Reconcile Outcomes Safely

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall checkpoint confirmed outcomes, stop on failures or uncertainty, and require state reconciliation and human direction before retrying mutations.

**Acceptance criteria:**
- Distinguish confirmed success, rejected operations, uncertain outcomes and not-attempted work. Retain source-to-server mappings for confirmed successes.
- Never automatically retry mutations, including stale requests, connection loss, timeouts or interrupted execution. A missing checkpoint or response does not establish rollback.
- Stop after a failure or uncertainty and leave later work not attempted. Retrieve authorized current state before a human-directed retry; changed proposals require fresh approval.
- After uncertain creation, inspect list/read candidates. If identity or persistence remains ambiguous, report the ambiguity and do not blindly recreate, particularly for generated Test Case/Feature IDs.
- A failed checkpoint write after confirmed server success must not trigger replay. Interrupted runs and missing checkpoints require reconciliation rather than silently resuming unproven operations.
- Preserve existing per-operation business/history/audit atomicity and uncertainty reporting. A local execution journal is not the application audit trail and is not proof of regulatory compliance.

### REQ-AI-IMPORT-006 — Report Results and Measure Efficiency

**Type:** Software requirement

**Parent need:** UN-AI-001

**Requirement:** The terminal integration shall provide compact conversation results, a detailed local report and reproducible efficiency measurements.

**Acceptance criteria:**
- Conversation results provide counts, resulting manual IDs/immutable identities and actionable failures without repeatedly echoing full successful record content.
- The local report associates source records with confirmed outcomes and identities, uncertainties and not-attempted work. It contains no credentials; operational logs omit sensitive submitted values and provider exception details.
- Benchmark representative equivalent multi-file imports against the existing per-record conversation workflow. Record elapsed time, MCP request count, model turns and token usage where available, with dataset, environment and measurement limitations.
- Report actual results without promising a particular speedup or token reduction. Client-side orchestration does not claim to eliminate individual server requests.
- Updated skill and usage documentation explain planning, preview, approval, execution, recovery and supported boundaries. Installed integrations require explicit updates; this specification does not add tool capabilities.

## Audit Decisions and Planned Verification

This documentation change performs no business-record writes: audit applicability for the current implementation diff is **N/A**. The following is the required executor planning gate, not evidence that runtime paths have been verified.

For User Need creation, Requirement creation and Test Case creation, preserve REQ-NEED-005, REQ-REQ-005 and REQ-TC-006 respectively, under REQ-SYS-AUDIT-003. For Feature creation, verify its owning implementation/tests and existing event contract rather than importing Product-specific behavior. Each creation must retain the authenticated actor, occurrence time, distinct action, immutable Product/entity/version target, success outcome and initial values required by that feature. For supported relationship changes, preserve REQ-LINK-006 history/audit with the owner/version identity and exact initial or old/new endpoints.

The existing business change, required history and audit commit together per operation. Audit-write failure prevents that operation from succeeding. A failed or cancelled commit confirmation is uncertain, not confirmed rollback or success. No automatic mutation retry is permitted; use authorized retrieval and human-directed recovery. Earlier confirmed operations are not undone by later failures.

Unauthorized mutation attempts remain denied and retain existing sanitized failure events: attempted action, occurrence time, known/unknown actor and unknown target, without submitted identifiers or values. If denied-attempt audit submission fails, safely log that failure without submitted values, credentials or provider exception details; logging cannot guarantee or replace a stored audit event.

Reads, local validation, preview and skipped operations require no business-change event. Server validation failures, stale rejections and no-op saves retain their owning feature's exclusions; unauthorized-attempt auditing still applies. Do not apply Product-specific REQ-SW-AUDIT-007–009 as a blanket policy.

Planned verification under #246 and #247:
- Deterministic multi-file fixtures for all four entity kinds; Markdown/step preservation; missing types, duplicate keys, ambiguous input, unresolved references and cycles.
- Exact previews, explicit defaults, approval invalidation and zero mutations during planning.
- Dependency and generated-ID mapping; exact-endpoint approval; fresh owner revisions and stale-write rejection.
- Required creation/link success events, excluded read/no-op/validation events, denied attempts and safe audit-failure logging.
- Audit-write rollback, committed and uncommitted lost-confirmation outcomes, interruption, checkpoint failure after confirmed success, and ambiguous generated-ID creation without blind recreation or replay.
- Partial-result reporting and equivalent-workflow benchmarks. Runtime tests must use disposable PostgreSQL Podman containers per README; no runtime tests or benchmarks are claimed by this specification.
