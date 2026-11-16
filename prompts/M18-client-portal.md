# M18: Client portal, quotes, work requests and plan entitlements

Branch: `#0018-client-portal-quotes-and-requests`

## Goal
A business's clients sign in with an email link, see only their own visits, approve quotes and request work, and the isolation suite proves the organization → client boundary as firmly as v1 proves the organization boundary. Plans start to differ by feature.

## Context
SPEC-V2 2.1, 2.3, 3.2, 4.4, 4.8, 5.3–5.5, 5.9, 6 (portal), 7 (portal-scoped, contacts, quotes, requests), 8, criteria 13 and 19; D006, D011, D021, D022, D025, D028 (token in the URL fragment), D038, D040, D042. Stories US-52, US-80–US-84.

This is the most security-sensitive milestone of v2: a new identity, a new cookie scheme and a new endpoint class. Ask the owner to switch to the strongest model for it if not already.

The owner approves the portal sign-in bypass by approving SPEC-V2 section 8. Add exactly that one.

## Scope
1. **Entitlements first.** `PlanPolicy` features (portal on Team and Business; API and webhooks on Business, used in M21); `403 plan-feature-unavailable`; `GET /api/orgs/{orgId}` exposes `features`; the downgrade confirmation (`confirmFeatureLoss`, D042); Settings → Plan shows features.
2. **Contacts.** `client_contacts` with the rules in 5.3; endpoints; the client page Contacts tab; an invitation email to the contact through the outbox.
3. **Portal authentication.**
   - `portal_sign_in_tokens`; request and confirm endpoints, rate-limited by address and by email;
   - the link carries the token in the fragment (D028);
   - a separate cookie authentication scheme (own cookie name, 7 days, `HttpOnly`, `Secure`, `SameSite=Strict`) and antiforgery on unsafe portal requests;
   - a portal resolution filter that loads the contact, client and organization on every request and ends the session when any is gone, archived or no longer entitled.
4. **The client boundary.**
   - a `PortalContext` (organization, client, contact) in Application;
   - a fourth endpoint class `.PortalScoped()` in the coverage tests;
   - row-level security on client-owned tables also checks `app.client_id` when it is set (D038);
   - handlers read the client only from `PortalContext`.
5. **Quotes** (5.4): "Send for approval" on the job page (`portal.manage`); the approval state on the job; the new In progress precondition in the job workflow (domain, with tests for every v1 transition still behaving the same for jobs never sent); approve and decline in the portal.
6. **Work requests** (5.5): portal list and form; staff Requests inbox with a count in the sidebar; convert (prefilled job form, limit-checked) and decline.
7. **Audit:** contacts as actors (`actor_kind = contact`); every portal write audited.
8. **Portal SPA area:**
   - `/portal/:slug/...`, lazy-loaded, its own small layout (organization name and initials, both themes, skip link);
   - sign in, check your email, visits, visit detail, quotes, requests;
   - every page has loading, empty and error states.
9. **Demo:** the seed adds contacts to a few Brightline clients with open quotes and requests. Decide how a demo visitor tries the portal without email (for example, a "Try as a client" button in demo mode alongside the v1 role buttons) and log it (D040).

## Out of scope
Completion proof and sign-off (M19); portal notifications (M20); custom branding or logos; contacts in more than one client.

## Tests required
- **Portal matrix (criterion 13):** two organizations, two clients each, one contact per client; every portal endpoint with foreign ids (same organization and other organization) gives `404`, no change (counts and hashes), no foreign data in bodies; a staff cookie on a portal route and a portal cookie on a staff route are both refused.
- **Session:** removal of the contact, archiving the client and downgrading to Free each end the session on the next request.
- **Sign-in:** a token works once, expires after 15 minutes, never appears in logs; the request answers identically for unknown emails; rate limits.
- **Row-level security:** with `app.client_id` set, raw SQL sees only that client's rows in each client-owned table; the coverage test also checks the client policy on those tables.
- **Quotes:** the In progress precondition, approval reset on amount change, one decision only, concurrency between two contacts deciding at once.
- **Entitlements:** every portal and contact endpoint refused on Free; the downgrade confirmation lists the portal; data kept and restored on upgrade.
- **Web:** portal pages' states; staff contacts tab and requests inbox; permission and plan visibility.

## Acceptance criteria
- SPEC-V2 criteria 13 and 19 (portal part) met.
- In the browser with Mailpit: add a contact, sign in as them from the emailed link in a second browser profile, approve a quote, request work, convert the request as staff, remove the contact and see the portal session end.

## Verification required
Full test suite; the matrix count recorded; mutation checks: remove the portal resolution filter (fails), remove `app.client_id` from the policies with the handlers' client filter removed too (fails), recorded; browser pass at three widths, keyboard, both themes, for staff and portal pages; production build under the CSP; the existing end-to-end journey.
