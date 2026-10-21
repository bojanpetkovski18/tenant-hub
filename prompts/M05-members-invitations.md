# M05: Members and invitations

Branch: `#0005-members-and-invitations`

## Goal
Owners and Admins invite people by email, invitees accept by link (with or without an account), and members are managed within the Owner and Admin rules.

## Context
SPEC 4.1 (members and invitations), 5.2, 5.3, 7 (members, invitations, public invitation endpoints), 8.1 (memberships, invitations), 9 (tokens); D011. User stories US-10 to US-12. Confirm D011 (email library, Mailpit version) and record it.

## Scope
1. Domain:
   - `Invitation` (create, resend with a new token, revoke, accept; pending, expired, revoked and accepted states);
   - a membership domain service for the rules in 5.2 (last Owner, who may grant or change which role, no self role change, leave);
   - unit tests for every rule, written as tables.
2. Tokens: 32 random bytes, Base64Url in the link, SHA-256 hash stored, constant-time comparison, never logged (add a log-capture test).
3. Email: an `IEmailSender` with an SMTP implementation (Mailpit in development) and a disabled mode where the API returns the link. Plain, accessible HTML and text bodies.
4. Endpoints from SPEC 7: member list, role change, removal and leave; invitation list, create, resend and revoke; public preview, sign-up through invitation, and signed-in accept. Rate limits on create and accept.
5. Removing a member or leaving: soft removal, open jobs unassigned (the job part activates in M06), audit entries.
6. Isolation and permission matrices extended for all new endpoints; the invitation token lookup is the one new allow-listed `IgnoreQueryFilters` use, approved in this brief.
7. Angular: members page with members and pending-invitation tabs; invite dialog with a role picker limited to what the user may grant; copy-link dialog when email is disabled; role change and removal with confirmation; leave organization; the accept-invitation page for all states in US-11; "you no longer have access" handling when a request returns `404` for the current organization.

## Out of scope
The seat limit (M08; leave a clear hook), job unassignment (wired up in M06).

## Tests required
- Domain: invitation states and expiry with a fake `TimeProvider`; every membership rule.
- Integration: the full flows of US-10 to US-12; a used, revoked or expired token; email mismatch; already a member; a removed member's next request returns `404`; re-inviting a removed member reactivates the membership; the token never appears in logs.
- Web: invite dialog roles per user role; accept page states.

## Acceptance criteria
- All tests pass.
- In the browser, with Mailpit: invite, receive the email, accept as a new user in a private window, and as an existing user; resend makes the old link fail; revoke; change role; remove; leave; the last-Owner rule is visible and enforced.

## Verification required
Tests; browser pass including Mailpit; a database check that only token hashes are stored.
