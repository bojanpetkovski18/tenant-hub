# M02: Accounts and organizations

Branch: `#0002-accounts-sign-up-and-organizations`

## Goal
People can sign up with an organization, sign in and out, see their memberships, create further organizations and switch between them. Cookie authentication with antiforgery and rate limits is in place.

## Context
SPEC sections 2, 4.1 (accounts and organizations), 5.1, 7 (public and user-scoped endpoints), 8.1 (Identity, organizations, memberships) and 9; D006, D009, D010. User stories US-01 to US-04.

## Scope
1. Database: `AppDbContext` (Identity with Guid keys, snake_case names), `organizations` and `memberships` (including `removed_at`), first migration, `GET /health` with database readiness. A test fails when the model and migrations differ.
2. Domain: `Organization` (name rules, slug generation with reserved words and collision suffixes, job counter field), `Membership` and `Role`; unit tests for the rules in 5.1.
3. Cookie authentication: `HttpOnly`, `Secure` (relaxed only in Development over http), `SameSite=Strict`, sliding expiry; the API returns `401`/`403` problem details instead of redirects; security-stamp validation so a password change signs out other sessions.
4. Antiforgery: the `XSRF-TOKEN` cookie is issued; unsafe methods on cookie-authenticated endpoints require `X-XSRF-TOKEN`; Angular `withXsrfConfiguration`.
5. Endpoints: `POST /api/auth/sign-up` (user, organization and Owner membership in one transaction), `sign-in`, `sign-out`, `GET /api/me`, `PATCH /api/me`, `POST /api/me/password`, `POST /api/orgs` (owner limit of 5).
   - Fallback authorization policy = authenticated; public endpoints explicitly anonymous.
   - Rate limits per IP and per account on sign-up and sign-in; record the numbers in DECISIONS.
   - `GET /api/me` returns memberships with `id`, `slug`, `name` and `role`; `permissions` arrive in M03.
6. Angular:
   - `SessionStore` (NgRx SignalStore): user, memberships, `isSignedIn`; loaded on startup from `/api/me`;
   - sign-up and sign-in pages: labelled fields, inline and server errors, show/hide password, return URL;
   - organization picker (`/orgs`) with "create organization"; switcher in the sidebar; routes `/o/:slug/...` resolve the organization from the store; unknown slug → not-found page; last organization remembered in `localStorage`;
   - account page (display name, password);
   - `authGuard`; a `401` interceptor clears the session.
7. Dev seed: two fictional organizations and one demo user per role, plus one user in both organizations, created from `DEMO_*` settings in `.env` (skipped with a warning when not set). Full demo data comes in M09.

## Out of scope
Tenant-scoped endpoints and permissions (M03), invitations (M05), password reset by email (V2).

## Tests required
- Integration: sign-up creates the three rows in one transaction (a forced failure leaves none); duplicate email; sign-in, me, sign-out, then me returns `401`; generic error for a wrong password; lockout after 5 failures; rate limit returns `429`; an unsafe request without an antiforgery token is rejected; the fallback policy protects an unannotated endpoint; the sixth owned organization is refused; password change invalidates another session.
- Domain: slug generation, reserved words, collision suffix, name length.
- Web: sign-up and sign-in validation and server errors; guard redirects with the return URL; the switcher navigates between organizations.

## Acceptance criteria
- All tests above pass.
- In the browser: sign up, sign out, sign in, create a second organization, switch between them, and use two tabs with different organizations; the session survives a reload; cookie flags are correct (DevTools → Application); nothing auth-related is in `localStorage` except the last-organization slug.
- Forms are usable by keyboard only, errors are announced, and the layout works at the three widths.

## Verification required
Run the tests; do a browser pass including the cookie check; `curl` a sign-in without an XSRF token to show the rejection; review the migration as SQL.
