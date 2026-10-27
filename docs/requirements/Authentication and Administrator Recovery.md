# Authentication and Administrator Recovery

## Originating User Needs

- [UN-AUTH-001 — Controlled Access to the Application](../user-needs/UN-AUTH-001%20-%20Controlled%20Access%20to%20the%20Application.md)
- [UN-AUTH-002 — Recover Administrator Access](../user-needs/UN-AUTH-002%20-%20Recover%20Administrator%20Access.md)

## System Requirements

### REQ-SYS-AUTH-001 — Establish User Identity

**Type:** System requirement

**Parent need:** UN-AUTH-001

**Requirement:** The system shall verify users’ identities using application-managed usernames and passwords.

### REQ-SYS-AUTH-002 — Control Authenticated Access

**Type:** System requirement

**Parent need:** UN-AUTH-001

**Requirement:** The system shall restrict protected application access to authenticated users who have completed any mandatory password change, and end access when their session expires or is terminated.

### REQ-SYS-AUTH-003 — Recover Administrator Access

**Type:** System requirement

**Parent need:** UN-AUTH-002

**Requirement:** The system shall enable an authorized IT administrator to restore administrator access when the administrator cannot sign in.

### Verification Approach

These system requirements may be verified through their child software requirements, provided the combined verification covers each parent’s full obligation. A dedicated system-level test is not automatically required.

## Software Requirements

### REQ-SW-AUTH-001 — Initialize the Administrator Account

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-001

**Requirement:** On initial setup, the software shall initialize exactly one administrator account using a username and initial password supplied through `.env`, with no built-in default credentials.

**Acceptance criteria:**
- Missing or empty credentials, or an initial password that violates REQ-SW-AUTH-004, prevent initialization.
- Configuration errors identify the problem without exposing credentials.

### REQ-SW-AUTH-002 — Authenticate Using Username and Password

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-001

**Requirement:** The software shall authenticate a user only when the supplied username and password match that user’s credentials.

**Acceptance criteria:**
- Successful authentication establishes the identity of the specific user.
- Failed authentication establishes no authenticated identity and displays a generic failure message that does not reveal whether the username or password was incorrect.

### REQ-SW-AUTH-003 — Protect Stored Passwords

**Type:** Software requirement

**Parent needs:** UN-AUTH-001, UN-AUTH-002

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003

**Requirement:** The software shall store application-managed passwords only as salted, one-way hashes, not as plaintext or reversibly encrypted values.

**Acceptance criteria:**
- This applies to initial, changed, and recovery-reset passwords.
- The `.env` provisioning source is outside the application’s hashed credential store; importing its password does not permit storing a plaintext copy in that store.

### REQ-SW-AUTH-004 — Enforce Password Rules

**Type:** Software requirement

**Parent needs:** UN-AUTH-001, UN-AUTH-002

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003

**Requirement:** The software shall accept passwords only when they contain 10–30 Unicode scalar values inclusive and include at least three of these four categories: ASCII lowercase letters (`a–z`), ASCII uppercase letters (`A–Z`), ASCII digits (`0–9`), and Unicode punctuation or symbol characters.

**Acceptance criteria:**
- The supplied symbol examples are not an exhaustive allowlist.
- Whitespace and other characters contribute to length but not to a category.
- Validate the original input without trimming or normalization.
- Reject null, empty, out-of-range, and insufficient-category inputs.
- These rules apply to initial configuration, password changes, and recovery passwords.

### REQ-SW-AUTH-005 — Require Initial Password Replacement

**Type:** Software requirement

**Parent needs:** UN-AUTH-001, UN-AUTH-002

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003

**Requirement:** Following initial setup or administrator recovery, the software shall require the administrator to replace the initial password after successfully authenticating and before accessing protected functionality.

**Acceptance criteria:**
- Before authentication, only login is accessible.
- After authentication, access remains limited to completing the required password change.
- The replacement password satisfies REQ-SW-AUTH-004 and differs from the initial password.
- Successful completion routes the administrator to the home page with full administrator access.
- Subsequent sign-ins do not require this initial-password change again unless recovery resets the account.

### REQ-SW-AUTH-006 — Delay Repeated Failed Sign-ins

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-001

**Requirement:** After three consecutive failed sign-in attempts for a username, the software shall enforce a five-second delay before permitting each subsequent attempt until the account is locked.

**Acceptance criteria:**
- Failure counts and delays are enforced server-side per username, independently of browser sessions.
- Successful sign-in resets the consecutive failure count.
- Unknown usernames receive equivalent outward failure messages and retry restrictions.

### REQ-SW-AUTH-007 — Lock Accounts After Repeated Failures

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-001

**Requirement:** After ten consecutive failed sign-in attempts for a username, the software shall block further sign-ins for that account until authorized recovery.

**Acceptance criteria:**
- Lockout replaces the previously discussed ten-second delay.
- Existing authenticated sessions remain active and subject to their normal expiration rules.
- Outward responses do not reveal whether the username identifies an existing account.

### REQ-SW-AUTH-008 — Recover the Initial Administrator Account

**Type:** Software requirement

**Parent need:** UN-AUTH-002

**Parent requirement:** REQ-SYS-AUTH-003

**Requirement:** The software shall provide an explicit recovery operation, restricted to an authorized IT administrator with administrative access to the hosting environment, that unlocks the initial administrator account and restores its password from `.env`.

**Acceptance criteria:**
- Successful recovery clears the failed-attempt lockout, invalidates all existing sessions for the account, and requires password replacement under REQ-SW-AUTH-005.
- Recovery rejects a missing password or one that violates REQ-SW-AUTH-004.
- Rejected recovery leaves the account’s credentials and lockout state unchanged.
- Recovery reports configuration errors without displaying the password.

### REQ-SW-AUTH-009 — Enforce Protected Access

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-002

**Requirement:** The software shall deny access to application data and protected functionality unless the request belongs to a valid authenticated session whose mandatory password change has been completed.

**Acceptance criteria:**
- Restrictions apply regardless of how access is requested.
- Denied access does not disclose protected application data.
- The initial administrator has full application access once the mandatory password change is complete.

### REQ-SW-AUTH-010 — Warn About Inactivity and Expire Sessions

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-002

**Requirement:** The software shall request confirmation of presence after five minutes of inactivity and expire the authenticated session after thirty minutes of inactivity.

**Acceptance criteria:**
- Any user interaction within the application, including confirming presence, resets the inactivity timer.
- The warning does not itself reset the timer.
- Session expiration ends protected access and routes the user to the login page.

### REQ-SW-AUTH-011 — End the Session on Logout

**Type:** Software requirement

**Parent need:** UN-AUTH-001

**Parent requirement:** REQ-SYS-AUTH-002

**Requirement:** When a user logs out, the software shall terminate that authenticated session and route the user to the login page.

**Acceptance criteria:**
- The terminated session cannot subsequently access protected functionality without signing in again.

## Traceability Notes

REQ-SW-AUTH-003–REQ-SW-AUTH-005 support both initial authentication setup and administrator recovery. They remain single requirements with multiple traceability links rather than duplicated requirements.

REQ-SW-AUTH-007 protects authentication under UN-AUTH-001. Its reference to recovery connects it to REQ-SW-AUTH-008; that dependency does not make lockout itself a recovery requirement.

## Scope

Creating additional users, role-specific permissions, audit-trail recording, and recovery mechanisms for additional accounts remain outside this increment. The recovery command or script and password-hashing technology remain implementation decisions.
