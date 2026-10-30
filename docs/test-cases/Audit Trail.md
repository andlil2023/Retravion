# Audit Trail — Test Specifications

This specification describes verification intent, not evidence of implemented tests or successful verification results.

**Requirements:** [Audit Trail](../requirements/Audit%20Trail.md)

## Verification Method and Automation Status

- TC-AUDIT-006–024 are planned automated verification candidates, not evidence of implementation or passing tests. A manual review of the audit view's clarity is also recommended.
- TC-AUDIT-001–005 describe the original contract increment; their original verification intent and boundaries are retained below. Their current automation status is not established by this specification.
- Manual inspection has automation status Not applicable.
- Local database-backed verification uses a disposable PostgreSQL Podman container as described in `README.md`. Atomicity requires database-backed verification; an uncertain acknowledgement may require controlled fault injection.

## Test Cases

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result | Automation candidate |
|---|---|---|---|---|---|---|
| TC-AUDIT-001 | REQ-AUDIT-01 | Unit/contract | Audit information types available | Represent an identified actor, occurrence time, action, identified target, and action outcome; exercise successful and failed action outcomes | All five categories are representable and their supplied information can be retrieved | Yes |
| TC-AUDIT-002 | REQ-AUDIT-01 | Unit/contract | Audit information types available | Represent an activity whose actor is unknown | The activity can be represented without inventing an actor identity | Yes |
| TC-AUDIT-003 | REQ-AUDIT-01 | Unit/contract | Audit information types available | Represent separately an identified target, an unidentified target, and an activity with no applicable target | All three cases are representable; an unidentified target is distinguishable from no applicable target | Yes |
| TC-AUDIT-004 | REQ-AUDIT-01 | Unit/contract | Submission interface and information types available; test double available | Through the interface, exercise submission success and failure for both successful and failed audited actions | The caller can distinguish submission success from failure independently of the audited action's outcome | Yes |
| TC-AUDIT-005 | REQ-AUDIT-01 | Inspection | Contract implementation diff available | Inspect the submission interface, information types, and their location | A common contract exists in `src/Retravion.Application/AuditTrail/`; no separate project, working submission implementation, database storage, or operation integration is introduced | No—manual review |

## Planned Audit Inspection and Account-Administration Cases

All cases in this table are planned automation candidates. Each scenario requires the relevant future behavior and a suitable test fixture; no execution result is implied.

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result |
|---|---|---|---|---|---|
| TC-AUDIT-006 | REQ-SW-AUDIT-001 | Integration/UI | Administrator and recorded event available | Open the audit view | The event's recorded actor, occurrence time, action, target, and outcome are displayed |
| TC-AUDIT-007 | REQ-SW-AUDIT-001 | Integration/UI | Events with identified and unknown actors and identified, unknown, and not-applicable targets available | View the events as administrator | Each actor and target state is distinguishable |
| TC-AUDIT-008 | REQ-SW-AUDIT-002 | Integration/security | Signed-in non-administrator and recorded event available | Attempt to open the audit view and retrieve events via a direct request | Access is denied and no audit-event data is returned |
| TC-AUDIT-009 | REQ-SW-AUDIT-003 | Integration/UI | Events with differing times, actors, actions, outcomes, and targets available | Apply each date/time range, actor, action, outcome, and target filter individually | Only events matching the selected filter are shown |
| TC-AUDIT-010 | REQ-SW-AUDIT-003 | Integration/UI | Events matching different combinations available | Apply multiple filters together | Only events matching all active filters are shown |
| TC-AUDIT-011 | REQ-SW-AUDIT-003 | Integration/UI | Events exactly at both boundaries, inside, and outside a range available | Filter by the date/time range | Events at both boundaries and inside are shown; outside events are excluded |
| TC-AUDIT-012 | REQ-SW-AUDIT-003 | Integration/UI | Events with unknown and identified actors available | Filter for an unknown actor | Events with unknown actors are shown; identified actors are excluded |
| TC-AUDIT-013 | REQ-SW-AUDIT-003 | Integration/UI | Events with identified, unknown, and not-applicable targets available | Filter separately for unknown and not-applicable targets | Each selection returns only the corresponding target state |
| TC-AUDIT-014 | REQ-SW-AUDIT-004 | Integration | Administrator and suitable accounts available | Successfully create, inactivate, reactivate, or reset the password of a user account | Each action produces a success event with recorded actor, time, action, target, and outcome |
| TC-AUDIT-015 | REQ-SW-AUDIT-004 | Integration | Administrator and failure conditions for each account action available | Attempt each account action when it cannot be completed | Each failed action produces a failure event; no successful account change occurs |
| TC-AUDIT-016 | REQ-SW-AUDIT-004 | Integration/security | Signed-in non-administrator and suitable targets available | Attempt each account action through a direct request | Each action is denied and a failure event is recorded, with actor and target represented as available |
| TC-AUDIT-017 | REQ-SW-AUDIT-004 | Integration/security | Creation, reset, and representative failed-attempt events available | Inspect the audit records | Records contain no passwords, temporary passwords, password hashes, or other credential material |
| TC-AUDIT-018 | REQ-SW-AUDIT-005 | Database integration | Administrator and suitable accounts available | Complete each account change successfully | The account change and its corresponding success audit event are both committed |
| TC-AUDIT-019 | REQ-SW-AUDIT-005 | Database integration | Fault injection available for audit recording or shared commit | Cause recording or shared commit to fail during each account change | Neither the account change nor its success event is committed; the administrator receives a clear failure message |
| TC-AUDIT-020 | REQ-SW-AUDIT-005 | Database integration/fault injection | Controlled loss of shared commit confirmation available | Simulate loss of confirmation of the shared commit outcome | The application reports that the outcome could not be confirmed, rather than claiming success or rollback; the account change and success event remain atomic |
| TC-AUDIT-021 | REQ-SW-AUDIT-005 | Integration/fault injection | Reset commit failure and uncertain-outcome conditions available | Reset a password when the commit fails or its outcome cannot be confirmed | No usable replacement temporary password is displayed |
| TC-AUDIT-022 | REQ-SW-AUDIT-005 | Integration/security | Account-operation failure messages available | Inspect failure messages | Messages contain no passwords or sensitive implementation details |
| TC-AUDIT-023 | REQ-SW-AUDIT-006 | Integration/fault injection | Failed administrator and unauthorized attempts and audit-submission failure available | Cause audit submission to fail for both attempts | Each requested action remains unsuccessful and the audit failure is reported through operational logging |
| TC-AUDIT-024 | REQ-SW-AUDIT-006 | Integration/security | Operational logs from TC-AUDIT-023 available | Inspect the logs | Logs contain no passwords or other credential material |

## Product Audit Cases

The following specify the Product increment. Automated counterparts are in `AuditSubmissionContractTests`, `ProductAuditTransactionTests`, and `ProductAuditBehaviorTests`; execution evidence belongs in the PR, not this specification.

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result |
|---|---|---|---|---|---|
| TC-AUDIT-025 | REQ-AUDIT-01, REQ-AUDIT-02, REQ-SW-AUDIT-007, REQ-PRODUCT-006 | Contract/PostgreSQL/UI | Disposable database; identified actor and exact timestamp | Create, rename/edit, edit description, retire, and reactivate with edits; query using a new store and inspect the administrator audit view | Durable events retain actor, exact time, action, immutable target, success outcome, initial values on creation and old/new values thereafter; reactivation with edits is one event. The audit view displays the event-time Product ID, HTML-encoded, not the internal UUID; other event targets retain their existing display |
| TC-AUDIT-026 | REQ-SW-AUDIT-007, REQ-PRODUCT-006 | Application/PostgreSQL | Product exists | Read, save unchanged values, fail validation or uniqueness, attempt a stale edit | No additional Product change events; no failed write changes persisted Product values |
| TC-AUDIT-027 | REQ-SW-AUDIT-007, REQ-PRODUCT-006 | PostgreSQL | Product with history exists | Rename its Product ID; reuse old ID for another Product | History retains event-time IDs and distinguishes the Products by immutable UUID |
| TC-AUDIT-028 | REQ-SW-AUDIT-008, REQ-PRODUCT-006 | PostgreSQL/fault injection | Audit insert rejection available | Attempt creation, edit, description update, retirement, and reactivation with and without edits | Each fails and rolls back Product changes and required events together; successful control changes commit both |
| TC-AUDIT-029 | REQ-SW-AUDIT-008, REQ-PRODUCT-006 | PostgreSQL/fault injection | Commit acknowledgement injection available | Commit then simulate lost acknowledgement or cancellation; cancel a separate operation before commit | Post-commit cases report uncertainty without retry; both data and audit are durable. Pre-commit cancellation causes no change |
| TC-AUDIT-030 | REQ-SW-AUDIT-009, REQ-PRODUCT-006 | Integration/security | Anonymous, password-restricted, and revoked actors; audit recorder and operational logger | Attempt all mutation callbacks and reads; make audit submission return failure and throw | Mutation attempts remain denied and submit actor/time/action/failure with unknown target and no Product values; reads produce no change events; failed submissions log a safe error without submitted values or exception details |
| TC-AUDIT-031 | REQ-AUDIT-02, REQ-SW-AUDIT-007, REQ-SW-AUDIT-008 | PostgreSQL | Audit table predating Product details; concurrent edit fixture | Read legacy events, submit Product events, race edits from the same prior snapshot | Additive upgrade preserves legacy events with absent details; only one concurrent edit succeeds and has one matching audit event |

## Individual Event Inspection Cases

These cases specify the individual-event details increment. Execution evidence belongs in the PR. UI automation inspects the native disclosure markup and its rendered contents; browser checks additionally verify native keyboard interaction and visual wrapping.

| Test ID | Verifies | Level | Preconditions | Steps/stimulus | Expected result |
|---|---|---|---|---|---|
| TC-AUDIT-032 | REQ-SW-AUDIT-001, REQ-SW-AUDIT-010 | UI/integration | Authorized administrator; creation and change events with historical values | Open an event's View details disclosure in the list | Recorded metadata and event-time Product ID appear; creation has all initial values; later events have only differing fields with exact old/new values; no internal UUID is displayed |
| TC-AUDIT-033 | REQ-SW-AUDIT-010 | UI | Legacy, denied, and identical-snapshot events; unknown and not-applicable metadata | Inspect each event's details | Each remains viewable; metadata states remain distinct; no recorded field changes is explicit; an empty value is distinguishable from no prior value |
| TC-AUDIT-034 | REQ-SW-AUDIT-002, REQ-SW-AUDIT-010 | Integration/security | Sensitive event and query spy; anonymous, ordinary, password-restricted, and revoked-session actors | Request audit page and invoke the audit operation directly | No event details are returned; unauthorized calls do not reach the query |
| TC-AUDIT-035 | REQ-SW-AUDIT-010 | UI/security | Long multiline Markdown and HTML/script/link-like values in metadata and old/new fields | Inspect rendered details; review wrapping rules | Exact values are encoded plain text, not executable HTML or active links; no truncation; wrapping and preserved whitespace rules apply |
| TC-AUDIT-036 | REQ-SW-AUDIT-010 | Manual/browser | Filtered list in a browser | Open and close details with mouse and keyboard; inspect long values at narrow width in light/dark themes | Native disclosure works; list filters remain intact; values remain readable without widening the page; no mutation occurs |

## Coverage and Limits

TC-AUDIT-001–TC-AUDIT-004 cover the original contract information and result representations. TC-AUDIT-005 checks the architectural direction and boundary of that original increment; its expectation that no working submission, storage, or operation integration is introduced applies to that increment only, not to later work.

TC-AUDIT-006–013 plan verification of REQ-SW-AUDIT-001–003 and contribute to REQ-SYS-AUDIT-001. TC-AUDIT-014–024 plan verification of REQ-SW-AUDIT-004–006 and contribute to REQ-SYS-AUDIT-002. This is a coverage plan, not evidence that verification has passed or that regulatory compliance is established. The Product cases additionally exercise durable submission and compatible storage evolution for REQ-AUDIT-02. Dedicated specifications for REQ-AUDIT-03 are not added here. REQ-SYS-AUDIT-003 remains an expectation for future non-Product business records, not a claim of implemented coverage.
