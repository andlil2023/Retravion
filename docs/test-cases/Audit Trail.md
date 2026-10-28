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

## Coverage and Limits

TC-AUDIT-001–TC-AUDIT-004 cover the original contract information and result representations. TC-AUDIT-005 checks the architectural direction and boundary of that original increment; its expectation that no working submission, storage, or operation integration is introduced applies to that increment only, not to later work.

TC-AUDIT-006–013 plan verification of REQ-SW-AUDIT-001–003 and contribute to REQ-SYS-AUDIT-001. TC-AUDIT-014–024 plan verification of REQ-SW-AUDIT-004–006 and contribute to REQ-SYS-AUDIT-002. This is a coverage plan, not evidence that verification has passed or that regulatory compliance is established. Dedicated specifications for REQ-AUDIT-02 and REQ-AUDIT-03 are not added here; existing automated test coverage has not been assessed by this documentation change.
