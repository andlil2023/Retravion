# Authentication and Administrator Recovery

## Originating User Needs

- [UN-AUTH-001 — Controlled Access to the Application](../user-needs/UN-AUTH-001%20-%20Controlled%20Access%20to%20the%20Application.md)
- [UN-AUTH-002 — Recover Administrator Access](../user-needs/UN-AUTH-002%20-%20Recover%20Administrator%20Access.md)
- [UN-AUTH-003 — Create User Accounts](../user-needs/UN-AUTH-003%20-%20Create%20User%20Accounts.md)
- [UN-AUTH-004 — Control Account Availability](../user-needs/UN-AUTH-004%20-%20Control%20Account%20Availability.md)

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

### REQ-SYS-AUTH-004 — Create User Accounts

**Type:** System requirement

**Parent need:** UN-AUTH-003

**Requirement:** The system shall allow administrators to create user accounts so that additional authorized people can sign in to Retravion.

**Acceptance criteria:**
- Non-administrators cannot create accounts.
- A newly created user must replace their temporary password before accessing protected functionality.

### REQ-SYS-AUTH-005 — Control Account Availability

**Type:** System requirement

**Parent need:** UN-AUTH-004

**Requirement:** The system shall allow administrators to inactivate and reactivate user accounts so that access can be withdrawn or restored.

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

**Parent needs:** UN-AUTH-001, UN-AUTH-002, UN-AUTH-003

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003, REQ-SYS-AUTH-004

**Requirement:** The software shall store application-managed passwords only as salted, one-way hashes, not as plaintext or reversibly encrypted values.

**Acceptance criteria:**
- This applies to initial passwords (including generated temporary passwords for newly created accounts), changed passwords, and recovery-reset passwords.
- The `.env` provisioning source is outside the application’s hashed credential store; importing its password does not permit storing a plaintext copy in that store.

### REQ-SW-AUTH-004 — Enforce Password Rules

**Type:** Software requirement

**Parent needs:** UN-AUTH-001, UN-AUTH-002, UN-AUTH-003

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003, REQ-SYS-AUTH-004

**Requirement:** The software shall accept passwords only when they contain 10–30 Unicode scalar values inclusive and include at least three of these four categories: ASCII lowercase letters (`a–z`), ASCII uppercase letters (`A–Z`), ASCII digits (`0–9`), and Unicode punctuation or symbol characters.

**Acceptance criteria:**
- The supplied symbol examples are not an exhaustive allowlist.
- Whitespace and other characters contribute to length but not to a category.
- Validate the original input without trimming or normalization.
- Reject null, empty, out-of-range, and insufficient-category inputs.
- These rules apply to initial configuration, generated temporary passwords for newly created accounts, password changes, and recovery passwords.

### REQ-SW-AUTH-005 — Require Initial Password Replacement

**Type:** Software requirement

**Parent needs:** UN-AUTH-001, UN-AUTH-002, UN-AUTH-003

**Parent requirements:** REQ-SYS-AUTH-001, REQ-SYS-AUTH-003, REQ-SYS-AUTH-004

**Requirement:** Following initial setup, administrator recovery, or creation of an additional account, the software shall require the user to replace the initial password after successfully authenticating and before accessing protected functionality.

**Acceptance criteria:**
- Before authentication, only login is accessible.
- After authentication, access remains limited to completing the required password change.
- The replacement password satisfies REQ-SW-AUTH-004 and differs from the initial password.
- The replacement form retains the current-password field and includes labeled, masked new-password and confirm-new-password fields that support new-password autofill.
- The new password and confirmation are both required and must match exactly, case-sensitively, without trimming or Unicode normalization. This validation is enforced server-side, including direct submissions that bypass the form.
- Missing, empty, or mismatched new-password/confirmation values prevent replacement and display a clear, accessible validation message. The stored password remains unchanged and access remains limited to completing the required password change.
- Password values are not echoed in validation responses or URLs.
- Confirmation is required after initial setup, administrator recovery, and creation of an additional account.
- Successful completion routes the user to the home page with the access assigned to their account; administrator accounts have full administrator access.
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

### REQ-SW-AUTH-012 — Create User Accounts

**Type:** Software requirement

**Parent need:** UN-AUTH-003

**Parent requirement:** REQ-SYS-AUTH-004

**Requirement:** The software shall allow administrators to create ordinary or administrator accounts by supplying a username and display name.

**Acceptance criteria:**
- Only administrators can create accounts.
- Usernames contain 3–50 characters drawn from ASCII letters, digits, `.`, `_`, and `-`.
- Usernames are unique and matched at sign-in case-insensitively; an existing username cannot be reused with different capitalization.
- Display names support Unicode.
- Invalid input or a duplicate username prevents account creation.

### REQ-SW-AUTH-013 — Issue an Initial Temporary Password

**Type:** Software requirement

**Parent need:** UN-AUTH-003

**Parent requirement:** REQ-SYS-AUTH-004

**Requirement:** On account creation, the software shall generate a temporary password satisfying REQ-SW-AUTH-004 and display it to the creating administrator once.

**Acceptance criteria:**
- The password is generated using cryptographically secure randomness.
- It is stored according to REQ-SW-AUTH-003 and cannot subsequently be retrieved.
- The user must replace it before accessing protected functionality, following REQ-SW-AUTH-005.

### REQ-SW-AUTH-014 — Inactivate and Reactivate Accounts

**Type:** Software requirement

**Parent need:** UN-AUTH-004

**Parent requirement:** REQ-SYS-AUTH-005

**Requirement:** The software shall allow only administrators to inactivate and reactivate user accounts, and shall prevent inactive accounts from signing in.

**Acceptance criteria:**
- Inactivation prevents subsequent sign-in but does not require immediate termination of existing sessions.
- Reactivation removes the inactivity restriction; other authentication restrictions still apply.
- Non-administrators cannot change account availability.
- An administrator cannot inactivate their own account.

## Traceability Notes

REQ-SW-AUTH-003–REQ-SW-AUTH-005 support initial authentication setup, administrator recovery, and creation of additional accounts. They remain single requirements with multiple traceability links rather than duplicated requirements.

REQ-SW-AUTH-007 protects authentication under UN-AUTH-001. Its reference to recovery connects it to REQ-SW-AUTH-008; that dependency does not make lockout itself a recovery requirement.

REQ-SW-AUTH-014 implements account availability under UN-AUTH-004 and REQ-SYS-AUTH-005. Reactivation removes only the inactivity restriction; it does not bypass the failed-sign-in lockout in REQ-SW-AUTH-007 or other authentication restrictions.

## Scope

Creating additional ordinary and administrator accounts, and administrator-controlled account inactivation and reactivation, are included. Role-specific permissions beyond the ordinary/administrator account distinction, audit-trail recording, and recovery mechanisms for additional accounts remain outside this increment. The recovery command or script and password-hashing technology remain implementation decisions.
