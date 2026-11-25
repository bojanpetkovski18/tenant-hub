# TenantHub: Specification

Status: v2.0.0 as built, 2026-11-25. Approved for v1 on 2026-10-21 and extended for v2 on 2026-11-16 (D037); the v2
specification was merged in here at M23 (D054). Changes are recorded in `DECISIONS.md` (D001–D054) and noted here with
the decision number. Sections 1–3 and 5.1–5.6 keep their v1 numbers; the v2 sections were added after them, and the
last section maps the old v2 numbers, which code comments still cite, to their places here.

## 1. Purpose

TenantHub is a multi-tenant back-office for small service businesses (cleaning companies, landscapers, maintenance contractors, small agencies). A business signs up as an **organization**, invites its team, keeps a list of **clients**, and plans and tracks **jobs** for them. Every change is recorded in an **audit log**, and what an organization can do is bounded by its **plan**.

The jobs-and-clients domain is deliberately ordinary. The real subject of the project is the foundation that every B2B SaaS product needs and that freelance clients repeatedly ask for:
- organization sign-up, invitations and membership in several organizations;
- role permissions enforced on the server, including rules that depend on the resource ("a member may update jobs assigned to them");
- tenant isolation that is enforced in depth and **proven by tests**;
- plan limits that hold under concurrent requests;
- an append-only audit trail written in the same transaction as the change.

v1 proved the organization boundary. v2 brings in the work a small service business does every day that v1 left outside the product: work repeats, planning is spatial, clients ask, work needs proof, and other tools need to know. Each v2 feature also shows a part of SaaS engineering that clients find hard to get right:

| What the customer gets | The mechanic it demonstrates |
|---|---|
| Recurring service agreements that generate the visits | Time-based generation in each organization's time zone, idempotent, with the schedule held in the database |
| A dispatch board: move a visit to a person and a day | Accessible drag and drop with an equivalent keyboard path; optimistic concurrency on every move |
| A client portal: clients see their visits, approve quotes, request work and confirm completed work | A second kind of identity with its own boundary inside the tenant (organization → client), proven by the isolation matrix |
| Completion proof: checklists and photos | Safe file uploads (content sniffing, re-encoding, metadata stripping), storage outside the database, a storage limit per plan |
| Notifications in the app and by email | A transactional outbox: messages are written in the same transaction as the change and delivered by a worker that is safe to run on several instances |
| An API and signed webhooks for other tools | Hashed API keys with fixed access levels; HMAC-signed webhooks with retries; protection against server-side request forgery |
| Features that grow with the plan | Plan entitlements (features on or off by plan) alongside numeric limits |
| None of the above weakens isolation | PostgreSQL row-level security as the sixth layer: the database itself refuses another organization's rows, and another client's rows in a portal session |

The one-line pitch: **your customers can sign in too, and the database proves they see only their own work.**

It is an independent portfolio project built with AI assistance. Within the owner's portfolio it complements Numera (analytics dashboard and data import) and Fitness Merchant (e-commerce): TenantHub is the B2B SaaS and business-rules project, and the one where Domain-Driven Design and CQRS are used because the rules justify them.

## 2. Users, roles and permissions

- **Visitor:** signs up (creating a user and an organization), signs in, opens an invitation link.
- **User:** an account (email and password). A user can belong to several organizations with a different role in each (a **membership**). The UI works inside one organization at a time and has an organization switcher.
- **Roles** (per membership): Owner, Admin, Member, Viewer.

Roles map to fixed permission sets defined in code (custom roles are later). The server checks permissions on every request; the UI only hides what the server would refuse.

| Permission | Owner | Admin | Member | Viewer |
|---|---|---|---|---|
| `org.read` (dashboard, organization details, member list, own notifications) | yes | yes | yes | yes |
| `org.manage` (name, time zone, daily capacity) | yes | yes | no | no |
| `org.delete` | yes | no | no | no |
| `billing.manage` (change plan) | yes | no | no | no |
| `members.manage` (invite, change role, remove) | yes | yes, see rules 5.2 | no | no |
| `clients.read` | yes | yes | yes | yes |
| `clients.write` (create, edit, archive, restore) | yes | yes | yes | no |
| `clients.delete` | yes | yes | no | no |
| `jobs.read` (jobs, the board, the jobs export) | yes | yes | yes | yes |
| `jobs.create` | yes | yes | yes | no |
| `jobs.manage` (edit, assign, transition or delete any job; move cards on the board) | yes | yes | no | no |
| `jobs.work` (edit and transition jobs assigned to me; their checklist and photos) | yes | yes | yes | no |
| `audit.read` (the audit log and its export) | yes | yes | no | no |
| `agreements.read` (agreements and their visits) | yes | yes | yes | yes |
| `agreements.manage` (create, edit, pause, resume, end; skip a visit) | yes | yes | no | no |
| `portal.manage` (add and remove contacts, send quotes for approval, handle work requests) | yes | yes | no | no |
| `integrations.manage` (API keys and webhooks) | yes | yes | no | no |

### 2.1 Identities
- **Staff user:** an account with memberships and roles, as above.
- **Client contact:** a person at one of an organization's clients, added by staff. Signs in to the portal with a one-time email link and has no password. A contact belongs to exactly one client of one organization; the same email at two clients is two contacts. Contacts never reach the staff app, and a staff account never gets a portal session.
- **API key:** a credential that an organization creates for another system. It acts in that organization with an access level chosen at creation (read-only or read-write) and is audited as "API key <name>".
- **System:** agreement generation and scheduled work, audited as "System".

### 2.2 Permissions in the v2 features
- The dispatch board uses the job permissions: everyone with `jobs.read` sees it; only `jobs.manage` moves cards, because only managers change the assignee or the scheduled date (section 7, `PUT /jobs/{id}`).
- Checklists and photos follow `jobs.work`: whoever may edit a job may tick its items and add photos.
- Audit export is part of `audit.read`; the jobs export is part of `jobs.read`.

### 2.3 What a client contact can do
Fixed, not role-based. A contact sees their own client's visits (upcoming, and the past 12 months), quotes waiting for their decision, and the checklist and photos of completed visits. They can approve or decline a quote, submit and follow work requests, and confirm a completed visit or report a problem with it. They see the business's name and the assignee's display name, and nothing else about the organization.

### 2.4 What an API key can do
- **Read-only:** `clients.read`, `jobs.read`, `agreements.read`.
- **Read-write:** those plus `clients.write`, `jobs.create`, `jobs.work`, `jobs.manage`.
- Never `members.manage`, `billing.manage`, `org.manage`, `org.delete`, `audit.read`, `portal.manage` or `integrations.manage`, whichever role its creator has.

## 3. Multi-tenancy model

### 3.1 Storage
One database and one schema shared by all organizations. Every tenant-owned table has an `organization_id` column. Alternatives (schema per tenant, database per tenant) and the reasons for this choice are in D005.

### 3.2 Resolving the tenant
- Tenant-scoped API routes carry the organization: `/api/orgs/{orgId}/...`. The SPA uses the organization's slug in its own URLs (`/o/{slug}/jobs`) and maps it to the id from the session (`GET /api/auth/session`).
- On every tenant-scoped request the server loads the caller's active membership for `{orgId}`. No active membership → `404` with code `organization-not-found`, identical for a foreign organization, an unknown id and a removed member (the organization's existence is not revealed). Membership found → a request-scoped `TenantContext` holds the organization id, user id, role and permissions. Missing permission → `403` with code `permission-denied` (D025).
- The organization is never taken from a request body, query string, header or cookie claim. Ids of other entities in a request (client id, assignee id) are always looked up inside the current tenant.

### 3.3 Enforcement in depth
1. **Membership check** per request (3.2); for the portal, the contact check (3.6); for API keys, the key looked up inside the route's organization (3.7).
2. **EF Core global query filters** on every tenant-owned entity: `organization_id = current tenant`. A tenant-owned entity is one that implements `ITenantOwned`.
3. **SaveChanges guard:** new tenant-owned entities get the current `organization_id` stamped; inserting, updating or deleting an entity of another organization throws, is logged as a security event and returns `500`. `organization_id` can never be modified. In a portal session the guard also refuses writes for another client.
4. **Composite foreign keys** in the database: a job references `(organization_id, client_id)` → `clients(organization_id, id)`, so a job can never point at another organization's client, even through a bug in the application.
5. **Allow-listed bypasses only:** `IgnoreQueryFilters()` is never called; the named bypass `AcrossTenants()` is allowed only in four files (the session, organization creation, invitation acceptance, tenant resolution), and an architecture test enforces the list (D024). v2 added none.
6. **PostgreSQL row-level security** (3.5): the database refuses another organization's rows even if layers 2–5 fail.

### 3.4 Proof
Isolation is a product feature, so it has its own tests (section 10, criteria 1–3, 12, 13):
- **Coverage tests:** every entity implementing `ITenantOwned` has a query filter and an index starting with `organization_id`; every entity that does not is on an explicit allow-list. Every tenant-owned table has row-level security and the tenant policy, and the list of `SECURITY DEFINER` functions is pinned. Every API endpoint is classified as public, user-scoped, tenant-scoped or portal-scoped; an unclassified endpoint fails the build, and so does a tenant endpoint without a matrix case. New entities and endpoints are therefore covered automatically, not by remembering to add a test.
- **Cross-tenant matrix:** two seeded organizations. For every tenant-scoped endpoint, a user of organization A calls it with organization B's route and with B's entity ids under A's route. Expected: `404` (or `403` where documented), no change in B's data (checked by row counts and a hash of B's rows), and no B data in any response body. The permission matrix checks every role against every endpoint; the API-key matrix (3.7) and the portal matrix (3.6) do the same for keys and contacts.
- **Database guarantees:** inserting a job that references another organization's client fails on the composite foreign key, with the application layer bypassed; a pooled connection reused across two tenants never shows the first tenant's rows.
- **Mutation checks:** the suite is broken on purpose and must fail. Recorded in `PROGRESS.md` at M03 (repeated on the full application in M10), M14, M18 and M22.

### 3.5 Row-level security (D038, built in M14)
- Every tenant-owned table has `ENABLE ROW LEVEL SECURITY` and the policy `tenant_isolation`: rows are visible and writable only when `organization_id` equals the session setting `app.organization_id`. There is no `FORCE ROW LEVEL SECURITY`: the table owner is the migration role and the owner of the one definer function below, and it must stay exempt; the API's user is checked never to own a table, so `FORCE` would protect nothing more (D038).
- **Two database users.** The API connects as its own user (`ConnectionStrings:Database`, `tenant_hub_app` by default), which owns nothing and is not a superuser; migrations run as the owner (`ConnectionStrings:DatabaseAdmin`), which also creates the API's user at startup. The integration tests use the same split, because tests run as a superuser would prove nothing. Startup refuses an API user that is a superuser, has `BYPASSRLS` or owns a table.
- **No leaks between requests.** The settings are session-level, set by an interceptor before any command whose scope differs from what the connection last received, because EF Core reads outside transactions and a scope enters its tenant part-way through. The first command of every scope overwrites all values, and Npgsql also resets the session when a connection returns to its pool. A test runs several scopes on one physical connection to prove it. Without a tenant, a command reads nothing tenant-owned and cannot write: fail closed.
- **Widened reading, allow-listed** (D024). The files allowed to bypass the query filter widen reading in the database in two narrow ways, as permissive `SELECT` policies: a user's own memberships in every organization (`app.user_id`) and the one invitation whose token the request presents (`app.invitation_token_hash`). The demo reset uses one `SECURITY DEFINER` function that answers, per user, whether anything in any organization refers to the account. Writes are never widened.
- **Mutation checks** (M14, repeated in M22): with one EF Core query filter removed, the cross-tenant matrix still passes, because the database refuses the rows; with the policy removed as well, it fails.

### 3.6 The client boundary (portal)
- Portal routes are `/api/portal/...`. A portal request resolves its session to (organization, client, contact). The organization and the client come only from the session, never from the route, body, query string or a header.
- On every portal request the contact must still exist and be active, the client must be active and the plan must include the portal; otherwise the session ends (`401 portal-session-ended`). A removed contact loses access on the next request.
- Row-level security adds `app.client_id` in portal sessions: a restrictive `client_isolation` policy on clients, jobs, agreements, work requests and contacts, and on checklist items and attachments through their job, requires the client to match (D040, D048). Staff sessions leave it unset and are unaffected.
- Endpoints in the portal group (`MapPortalGroup()`) are the fourth endpoint class; an unclassified endpoint still fails the build.
- **Portal matrix:** two organizations, two clients in each, one contact per client. For every portal endpoint, a contact using another client's ids (same organization) or another organization's ids gets `404`; the other data is unchanged (row counts and hashes); no foreign data appears in any body.

### 3.7 Machine access
- API keys use the same tenant routes as staff (`/api/orgs/{orgId}/...`) with `Authorization: Bearer <key>`. The key is looked up inside the route's organization, through the tenant filter and the policies; a key of another organization is therefore `404`, exactly as for a non-member (D044).
- Endpoints opt in to key access explicitly (`.AllowApiKey()`). Any other endpoint refuses a key with `403 api-key-not-allowed`.
- Bearer requests carry no cookies and skip antiforgery. A request that carries both a cookie session and a key is refused (`400 credentials-conflict`).
- The API-key matrix covers every key-enabled endpoint (access levels, revoked and expired keys, another organization, endpoints that refuse keys).

## 4. Features

### 4.1 Core features (v1)

**Accounts and organizations**
- Sign up with display name, email, password and organization name. This creates the user, the organization (Free plan, time zone from the browser, editable) and an Owner membership, signs the user in and opens the new organization.
- Sign in, sign out, "me" (profile and memberships), change display name and password.
- Create another organization (a user may own at most 5).
- Organization switcher; the last organization used is remembered per browser.

**Members and invitations**
- Admins and Owners invite by email with a role. Admins may invite Admin, Member or Viewer; only Owners may make someone an Owner, and only by changing the role of an existing member.
- Invitation link: single use, valid for 7 days, the token is stored only as a hash. Pending invitations can be listed, resent (a new token; the old link stops working) and revoked.
- Accepting:
  - signed in with the invited email → accept, the membership is created, the organization opens;
  - no account → a sign-up form with the email fixed → account and membership are created together;
  - signed in with a different email → explained, with an option to sign out;
  - expired, revoked or used link → a clear message; already a member → opens the organization.
- Invitation emails are sent through SMTP (Mailpit in development) during the request. When email is disabled (demo mode), the inviter gets a "copy link" dialog instead (D011).
- Member list: name, email, role, joined date. Change role, remove a member, leave the organization. Removing a member unassigns their open jobs (recorded in the audit log).

**Clients**
- Create, edit, archive, restore and delete. Fields: name (required), contact person, email, phone, address, notes.
- List with search (name, contact, email), status filter (active, archived, all), sorting and server-side paging.
- Detail page: client information, the client's jobs, agreements (4.3) and portal contacts (4.5).

**Jobs**
- Fields: per-organization number (`JOB-0042`), title, client, description, assignee, status, priority, scheduled date, due date, quoted amount, estimated duration (5.8).
- Status workflow (5.4): New → Scheduled → In progress → Completed, Cancelled from any open status, and Reopen from Completed.
- List with search (number, title), filters (status, assignee including "me" and "unassigned", client, agreement, priority, overdue, due date range), sorting, server-side paging, filters reflected in the URL, and a CSV export of the current filters (4.8).
- Detail page: fields, the allowed status actions for this user, the checklist, photos, the quote panel and client feedback (4.6, 4.5), and the job's history from the audit log.
- Concurrent edits are detected (optimistic concurrency); the second save gets `409` and a "reload" prompt.

**Audit log**
- Every state-changing command writes one or more audit entries in the same database transaction: who (a user, a contact, an API key or the system; 5.15), when, what action, which entity, a human-readable summary, and the changed fields with old and new values.
- Viewer (Admin and Owner): filters by actor, action, entity type and date range; paging; links to the entity; a CSV export for a date range (4.8).
- Append-only: there is no API to change or delete entries, and a database trigger rejects `UPDATE` on the table.

**Plans and limits**
- Plans: Free, Team and Business (limits in 5.5, features in 4.9). Settings → Plan shows usage meters for each limit and the features of each plan.
- Limits are checked on the server inside the same transaction as the change, holding a lock on the organization row, so concurrent requests cannot exceed them.
- Changing plan is simulated ("demo billing", Owner only, no payment). A downgrade is refused while usage exceeds the target plan, and the response lists the limits that block it; features that would turn off must be confirmed (4.9).

**Dashboard** (per organization)
- Tiles: open jobs, overdue jobs, jobs completed this month, active clients.
- Jobs by status, my next 7 days of scheduled jobs, plan usage, and recent activity (Admin and Owner).

**Organization settings**
- Name, time zone and daily capacity (5.8). "Overdue" and "this month" use the organization's time zone.
- Delete organization (Owner): confirmed by typing the organization name; deletes all its data, its photos included.

**Demo mode** (`DEMO_MODE=true`)
- Seeded fictional organizations and users (8.3), with "Try as Owner / Admin / Member / Viewer" buttons on the sign-in page, and "Try as a client" on the portal's sign-in page for the seeded contacts (D040).
- Scheduled reset of the seeded data every `DEMO_RESET_INTERVAL_HOURS`, timed from when the seeded organizations were created, so restarts neither reset early nor skip one. Organizations created by visitors are deleted at the first reset after they are 24 hours old, and visitor accounts over a day old that nothing refers to are deleted too. The demo accounts' email preferences go back to the defaults. Sign-up, sign-in and invitations have lower rate limits, and email sending is off.
- The demo accounts keep their password and their roles in the seeded organizations, and the seeded organizations cannot be deleted (`403 demo-protected`).

### 4.2 Account recovery and verification (M15)
- **Password reset by email.** The request always answers "if an account exists, we sent a link". The link is single use and valid for 30 minutes; a new password ends every session of the account.
- **Email verification.** New accounts get a verification link. Until they verify, they see a banner and cannot send invitations or portal invitations, so that an unverified account cannot use the product to send mail. Existing accounts and demo accounts are treated as verified. Verification is asked only while email is on: without email no link can be sent and the product sends no mail (D039). Accounts made from an invitation link start verified.
- In demo mode email is off: the reset page explains that the demo has no email.

### 4.3 Recurring service agreements (M16)
- An agreement belongs to a client and holds a visit template (title, description, assignee, priority, quoted amount per visit, estimated duration, checklist) and a recurrence (5.7).
- Visits are ordinary jobs linked to the agreement and its occurrence date, so everything about jobs (workflow, list, audit, limits) applies to them unchanged.
- Visits are generated for a rolling horizon ahead of the organization's today: daily by the scheduler, and at once when an agreement is created or changed.
- Agreements can be paused, resumed and ended. A single visit can be skipped.
- The client page has an Agreements tab; the jobs list has an "agreement" filter.

### 4.4 Dispatch board (M17)
- A week view in the organization's time zone, Monday to Sunday. Rows are the active members who can work on jobs plus "Unassigned" (and a former member who still has work that week); cells are days. A "Not scheduled" area lists open jobs with no scheduled date, the nearest due first (the 50 nearest, with the total).
- Cards show number, client, title, status, priority and estimated duration.
- Managers move a card by dragging it (into a cell, or onto "Not scheduled"), or by keyboard: each card's "Move to…" asks for a person and a day (an empty day is "Not scheduled"). Arrow-key moves were left out (D046). Each move goes through the same domain rules as the job form, and is announced to screen readers.
- Each cell shows the person's planned hours against the organization's daily capacity; an over-capacity cell is marked with an icon and text, never colour alone.
- Below 1024 px the board is a day-by-day list per person, with the "Move to…" action and no dragging (the seven-day grid needs about 900 px; D046). Only the layout for the screen is drawn (K6, M23).

### 4.5 Client portal (M18)
- Staff add contacts (name, email) on the client page; the contact receives an invitation email. Contacts sign in at `/portal/{orgSlug}` by entering their email and following the one-time link.
- **Visits:** upcoming and past, with date, status and assignee; a visit page with its details and, once completed, its proof (4.6).
- **Quotes:** staff send a job's quote for approval; the contact approves or declines it with an optional comment.
- **Work requests:** the contact describes the work and a preferred date. Staff see a Requests inbox with a count, and convert a request into a job (prefilled) or decline it with a reason; the contact sees the outcome.
- The portal is a Team and Business feature (4.9).

### 4.6 Completion proof (M19)
As built: D043 (photos, SkiaSharp, the file store), D048 (checklists and sign-off).

- **Checklists:** items on a job, copied from the agreement's template or added by hand; each item may be required. Completing a job requires every required item to be done.
- **Photos:** added to a job by whoever may work on it, re-encoded and stripped of metadata on upload, stored outside the database and served only through authorized endpoints.
- **Client sign-off:** in the portal, a completed visit shows its checklist and photos; the contact confirms it ("Looks good") or reports a problem, which notifies staff (4.7) and does not reopen the job by itself.

### 4.7 Notifications (M20)
As built: D050 (fan-out from audited changes through the outbox, the digest task, retention in the same run).

- **In the app:** a bell in the shell with an unread count, a list, mark as read and mark all as read.
- **By email:** each user chooses, per notification type, whether to also receive it by email.
- **Daily digest** of overdue jobs at 07:00 in the organization's time zone.
- Delivered through the outbox (4.10). Nobody is notified about their own action.

### 4.8 API keys, webhooks and exports (M21)
As built: D044 (keys found inside the route's organization, events from audited changes through the outbox, the connection-time guard) and D051 (CSV format and streaming). Developer guide: `docs/INTEGRATIONS.md`.

- **API keys** for the key-enabled endpoints (3.7), with the OpenAPI document describing the bearer scheme.
- **Webhooks** for chosen events, signed, retried and logged, with redelivery and a test event.
- **Exports:** the audit log as CSV for a date range, and the jobs list as CSV for the current filters.
- API keys and webhooks are Business features (4.9).

### 4.9 Plan entitlements (M18, extended in M19 and M21)
Plans have features and two more limits, alongside the limits in 5.5.

| | Free | Team | Business |
|---|---|---|---|
| Client portal | no | yes | yes |
| API keys and webhooks | no | no | yes |
| Active agreements | 3 | 200 | unlimited |
| File storage | 100 MB | 5 GB | 50 GB |

- An endpoint for a feature the plan lacks answers `403 plan-feature-unavailable` with `feature` and the cheapest plan that has it. The UI hides such actions from non-Owners, shows Owners which plan includes them, and does not ask the API for a feature the plan lacks.
- **Downgrade:** still refused while numeric usage exceeds the target plan. Features that would turn off are listed in the response (`plan-feature-loss`); the Owner repeats the request with `confirmFeatureLoss: true`. Afterwards portal sign-in and webhook deliveries stop; the data is kept, and upgrading again restores them.
- `GET /api/orgs/{orgId}` includes `features` and the new limits, for this plan and every plan.

### 4.10 Background work: the outbox and the scheduler (M15)
- **Outbox.** Messages (emails, notifications, webhook events, file clean-up) are written in the same `SaveChanges` as the change and its audit entries, so a rolled-back command sends nothing.
- **Dispatcher.** A worker claims batches with `FOR UPDATE SKIP LOCKED`, so several API instances can run it without sending a message twice. Failures retry with backoff (a message type may have its own schedule, as webhooks do); a message that keeps failing is marked dead and logged. Handlers are idempotent by message id.
- **Secrets in messages.** Messages that carry a secret (a reset or sign-in link) are encrypted with ASP.NET Core Data Protection and deleted once sent.
- **Invitations** keep their v1 contract: they are sent during the request, so the inviter sees `sent`, `link` or `failed` at once (D011). Everything else uses the outbox.
- **Scheduler.** Recurring work (agreement generation, the daily digest, retention clean-up, the weekly file sweep, the demo reset) is held in a `scheduled_tasks` table with a due time and a lease, so a restart neither skips nor repeats a run, two instances never run the same task at once, and a host that sleeps catches up when it wakes (D039).

### 4.11 Later
Custom roles; real billing with Stripe; a platform-admin console; SSO; a Kanban board (the dispatch board covers planning); per-member capacity; route planning and maps; SMS; native apps; a hosted public demo (D035, D053).

### 4.12 Out of scope
Real payments; emails to real recipients from the public demo; invoicing and accounting; native mobile apps; schema-per-tenant and database-per-tenant (D005).

## 5. Business rules

### 5.1 Accounts and organizations
- Email is unique, compared case-insensitively. Passwords: at least 10 characters with upper case, lower case, a digit and a symbol. Lockout for 15 minutes after 5 failed sign-ins.
- Organization name: 2–80 characters. The slug is generated from the name at creation: lowercase `a-z`, `0-9` and `-`, 3–40 characters, unique, not a reserved word (`api`, `app`, `admin`, `new`, `settings`, `invite`, `orgs`, `o`). On a collision `-2`, `-3`, … is appended. The slug does not change when the name changes.
- A user may own at most 5 organizations.
- Password reset and verification links carry a token made by ASP.NET Core Identity and protected with Data Protection, bound to the account's security stamp, so it is single use and an older one stops working (D039). A reset link works for 30 minutes, a verification link for 7 days; changing the password voids both. The token travels in the link's fragment (D028).

### 5.2 Memberships
- An organization always has at least one Owner. The last Owner cannot leave, be removed or be demoted; transfer ownership first by making another member an Owner.
- Admins cannot change, remove or create Owners. Nobody changes their own role.
- Removing a member, or a member leaving, unassigns their jobs that are not Completed or Cancelled. Closed jobs keep the former member as assignee, for history. An agreement template naming them loses its assignee (5.7).
- Removal is soft (`removed_at` is set): the membership no longer grants access, but jobs and audit entries can still name the person. Inviting a removed person again reactivates the membership with the new role.
- Only active members can be assigned to jobs.

### 5.3 Invitations
- At most one pending invitation per email per organization; inviting again replaces it.
- Pending invitations count toward the seat limit.
- The email must match the accepting account's email (case-insensitive).

### 5.4 Jobs
- The number is per organization, sequential and never reused (a counter on the organization row).
- Allowed transitions:

  | From | To | Requires | Who |
  |---|---|---|---|
  | New | Scheduled | assignee and scheduled date | `jobs.manage` |
  | New, Scheduled | In progress | assignee; a quote sent for approval must be approved (5.10) | `jobs.manage`, or `jobs.work` if assigned to me |
  | In progress | Completed | every required checklist item done (5.12) | `jobs.manage`, or `jobs.work` if assigned to me |
  | New, Scheduled, In progress | Cancelled | a reason (1–500 characters) | `jobs.manage` |
  | Completed | In progress (reopen) | a reason; clears the client's feedback | `jobs.manage` |

- Any other transition is rejected with `409` and the allowed transitions for the current state; a missing condition is `409 quote-not-approved` or `409 checklist-incomplete` with the open items.
- The client must be active when a job is created or moved to a different client. Archiving a client is allowed while it has open jobs; a warning shows how many.
- Due date ≥ scheduled date when both are set. Quoted amount: 0 to 1,000,000.00, two decimals, stored as `numeric(12,2)`. Every amount is in the organization's currency, which is EUR.
- Overdue: due date earlier than today in the organization's time zone, and status not Completed or Cancelled.
- A job may be deleted by `jobs.manage` only while it is New or Cancelled.

### 5.5 Plans

| Limit | Free | Team | Business |
|---|---|---|---|
| Seats (members plus pending invitations) | 3 | 10 | 50 |
| Active clients | 25 | 500 | unlimited |
| Open jobs (not Completed or Cancelled) | 50 | 2,000 | unlimited |
| Active agreements | 3 | 200 | unlimited |
| File storage (after re-encoding) | 100 MB | 5 GB | 50 GB |

- Reaching a limit returns `403` with code `plan-limit-reached`, `plan`, `limit`, `allowed` and `usage` (D013). Seats are active members plus pending (not expired) invitations; renewing an expired invitation takes a seat again, and accepting one turns its seat into a membership.
- Archived clients and closed jobs do not count, so restoring a client or reopening a job is also limit-checked. Generated visits count as open jobs (5.7).
- Features by plan, and the downgrade with features that turn off: 4.9.

### 5.6 Clients
- Name 2–120 characters, unique among the organization's active clients (case-insensitive). Email optional but validated; phone free text up to 40 characters.
- Delete only when the client has no jobs; otherwise archive. Deleting a client deletes its contacts; archiving it pauses its agreements and ends its contacts' portal sessions.

### 5.7 Agreements and visits
- **Recurrence:** weekly on chosen weekdays every N weeks (N 1–8); monthly on day D (1–28, or "last day") every N months (N 1–12); monthly on the nth weekday (first to fourth, or last) every N months. Dates only, in the organization's time zone.
- **Start and end:** a start date (not before the organization's today when created); no end, an end date, or an end after a number of visits (1–500).
- **Horizon:** visits exist for occurrences up to 60 days after the organization's today. Generation is idempotent: `(agreement_id, occurrence_on)` is unique among the agreement's jobs.
- **A generated visit** is created with the template's fields, `scheduled_on` and `due_on` equal to the occurrence date, and status Scheduled when the template has an assignee, otherwise New. It gets its job number when it is generated.
- **Editing an agreement** applies to future visits that are New or Scheduled and have not been changed individually; those are updated or regenerated. Visits changed by hand are kept, and the agreement page lists them.
- **Skipping** a visit cancels it with the reason "Skipped"; the cancelled job keeps its occurrence date, so it is not generated again.
- **Pause** stops generation; **resume** generates from the organization's today onward, never in the past. **End** cancels future visits that are New or Scheduled (reason "Agreement ended") and keeps the rest.
- The client must be active to create or resume an agreement. Archiving a client pauses its agreements.
- **Limits:** generated visits count as open jobs (5.5). Generation stops at the limit and the agreement shows "generation paused: plan limit reached" (with a notification, 5.13). Active agreements are limited per plan (5.5).
- The template's assignee must be an active member; if they are removed, future visits become unassigned (5.2) and the template's assignee is cleared.

### 5.8 Dispatch
- Moving a card sets the assignee and `scheduled_on` with the job's concurrency token. A newer version gives `409 concurrency-conflict`: the card returns to its place and the board reloads.
- Moving a job to a day before its due date is allowed; to a day after its due date asks for confirmation and moves the due date too (the rule `due_on ≥ scheduled_on` stays): the API answers `409 after-due-date` until the move says `moveDueDate`.
- A New job moved to a person and a day becomes Scheduled (5.4). A move to "Unassigned" or "Not scheduled" follows the job form's rules: where the form would refuse, the board refuses with the same reason. The form checks an assignee and a day when a job changes status, not afterwards, so it allows a Scheduled job to lose either; the board allows the same moves, and a job taken off the plan keeps its status (D046). Completed and Cancelled jobs are shown but cannot be moved (`409 job-closed`).
- Jobs have an optional `estimated_minutes` (15–1440). Organizations have a daily capacity (60–1440 minutes, default 480), edited in Settings → General.

### 5.9 Contacts and portal sessions
- A client has at most 5 active contacts. Email is unique among a client's active contacts (case-insensitive). Contacts do not take seats.
- **Sign-in link:** 32 random bytes, stored as a SHA-256 hash, valid for 15 minutes, single use. Requesting a link always gives the same answer, whether or not the email is a contact. Rate-limited per address (5 in 10 minutes) and to one link a minute per contact.
- **Session:** a cookie of its own (separate name and scheme from the staff cookie, `HttpOnly`, `Secure`, `SameSite=Strict`, scoped to `/api/portal`), valid for 7 days, with antiforgery on unsafe requests. Removing the contact, archiving or deleting the client, or losing the portal entitlement ends it on the next request.
- Deleting a client deletes its contacts. A client with jobs cannot be deleted (5.6), so contacts of such clients are removed one by one.

### 5.10 Quotes
- Staff with `portal.manage` may send a quote for a job that is New or Scheduled, has a quoted amount, and whose client has at least one active contact. The job's approval becomes Pending.
- **While Pending or Declined, the job cannot move to In progress** (`409 quote-not-approved`; 5.4).
- The contact approves or declines once, with an optional comment (up to 1,000 characters). The approval records the amount approved, who and when.
- Changing the quoted amount of an approved job resets the approval to Not requested; the audit log shows both events. So does changing the amount or the client of a pending quote, or the client of an approved one (D047). Jobs that were never sent behave exactly as without the portal.

### 5.11 Work requests
- A contact may have at most 10 open requests. Title 3–120 characters, description up to 2,000, preferred date optional (not in the past).
- Statuses: Open → Converted (to a job; the request links to it) or Declined (with a reason, 1–500 characters, shown to the contact). No other transitions.
- Converting is limit-checked like creating a job.

### 5.12 Checklists and photos
- **Checklists:** at most 30 items per job, text 1–200 characters. Ticking or unticking records who and when. A job moves to Completed only when every required item is done (`409 checklist-incomplete` with the open items; the UI disables Complete and names them). Items can be edited while the job is open; a completed job's checklist is read-only until it is reopened.
- **Photos:** at most 10 per job and 10 MB per file. Accepted by content (JPEG, PNG or WebP magic bytes), whatever the file name or declared type. Re-encoded, with every metadata block (EXIF, GPS, XMP, ICC beyond sRGB) removed, the long edge limited to 2,560 px, plus a thumbnail. Stored under a random key; served with `Content-Disposition`, `X-Content-Type-Options: nosniff` and a private cache.
- **Storage limit:** the stored bytes (after re-encoding, photo and thumbnail) per organization, checked under the organization lock like the other limits.
- Files of a deleted job or organization are removed after the commit, through the outbox; a weekly sweep removes orphans.
- **Client feedback:** once per completed visit, either "confirmed" or "problem reported" (text 1–2,000 characters). Reopening the job clears it.

### 5.13 Notifications
| Type | Who receives it | Email by default |
|---|---|---|
| Assigned to you | the new assignee | on |
| Your visit was rescheduled or cancelled | the assignee | off |
| Quote approved or declined | managers, and whoever sent the quote | on |
| New work request | managers | on |
| Client reported a problem | managers and the assignee | on |
| Agreement generation paused by a plan limit | Owners and Admins | on |
| A webhook endpoint was disabled after three days of failures | Owners | on |
| Daily digest of overdue jobs (07:00, organization time zone; skipped when there are none) | each assignee (their jobs) and managers (a count) | on |

- Notifications are tenant-owned and addressed to one user in one organization; email preferences are per user. Kept for 90 days.
- The unread count refreshes on navigation and every 60 seconds while the tab is visible; there are no websockets.

### 5.14 API keys and webhooks
- **Keys:** the full key is shown once; only a SHA-256 hash and a short visible prefix are stored. Optional expiry (up to one year). Revocation is immediate. Rate limit per key (120 requests a minute). The last-used time is recorded at most once a minute.
- **Webhook endpoints:** at most 5 per organization; `https` only; chosen events from a fixed catalog (`job.created`, `job.status_changed`, `job.assigned`, `quote.approved`, `quote.declined`, `request.created`, `visit.confirmed`, `visit.problem_reported`).
- **Signing:** `TenantHub-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<body>")>`. The secret is shown once, stored encrypted (it must be readable to sign), and can be rotated with both secrets valid for 24 hours.
- **Payload:** `{ id, type, occurredAt, organizationId, data }`; `id` is stable across retries, so receivers can deduplicate.
- **Delivery:** a 10-second timeout and no redirects. Any 2xx is success. Retries back off over about a day (8 attempts: after 1 m, 5 m, 30 m, 2 h, 4 h, 8 h and 10 h). After 3 days of failures the endpoint is disabled and Owners are notified. The delivery log keeps status code, duration and error class for 30 days, but no response bodies. Managers can redeliver.
- **Server-side request forgery:** the host is resolved and refused if any address is private, loopback, link-local, unique-local, multicast or otherwise non-public (IPv4 inside IPv6 judged as IPv4); the connection goes to the address that was checked (no second lookup). A setting honoured only in Development and in the E2E environment of the second journey (D052) allows one named local receiver.
- **Exports:** UTF-8 with a byte-order mark; cells starting with `=`, `+`, `-`, `@`, a tab or a carriage return are neutralized with an apostrophe. Exports stream from the database and never load the whole result (D051).

### 5.15 Audit
- Entries have `actor_kind` (`user`, `contact`, `api_key`, `system`) and `actor_ref` (the contact or key id); `actor_user_id` is nullable. Entries from before v2 are `user`.
- Every state-changing endpoint, portal ones included, writes an entry in the same transaction (criterion 8). Generated visits are audited as "System (agreement <title>)". Marking notifications read is not audited (D050).

## 6. Screens

**Public:** landing page (`/`, short product pitch and "Try the demo"), sign in (with demo buttons in demo mode), sign up, accept invitation (`/invite#<token>`: the token is in the fragment, D028), forgot password, reset password, verify email.

**Signed in, outside an organization:** organization picker (`/orgs`) with "create organization", account settings (`/account`), and Account → Notifications for email preferences.

**Inside an organization** (`/o/:slug/...`), in an app shell with a sidebar (organization switcher at the top, then navigation filtered by permission and plan) that becomes a drawer below 1024 px, and the notification bell with its panel:
- Dashboard
- Jobs: list (with Export), new, detail (checklist, photos, quote panel, client feedback) and edit
- Board
- Agreements: list, detail (upcoming visits, visits changed by hand, generation state) and form, with a live preview of the next occurrence dates
- Requests inbox (`portal.manage`)
- Clients: list, new, detail (jobs, agreements, portal contacts) and edit
- Members: members tab and pending-invitations tab
- Audit log (with Export)
- Settings: General (with daily capacity), Plan and usage (with features), Danger zone; Settings → Integrations (API keys, webhooks and their delivery log)

**Portal** (`/portal/{orgSlug}/...`, its own small layout with the organization's name and initials, the same design tokens and both themes): sign in, check your email, visits, visit detail, quotes, requests (list and new), and "signed out" or "link expired".

**System:** not found, access denied, "you were removed from this organization", global error banner.

**Design direction:** a calm, dense work tool rather than a marketing template. Neutral slate surfaces, one teal accent, compact tables with a 14 px base size, light and dark themes. Status and priority use an icon and text as well as colour. A visual identity distinct from Numera and Fitness Merchant.

## 7. API

All endpoints are under `/api` and return JSON. Errors are RFC 7807 problem details with `traceId` and, for business-rule errors, a stable `code` (for example `plan-limit-reached`, `invalid-transition`, `last-owner`). Lists use `page` (1-based) and `pageSize` (default 25, maximum 100) and return `items`, `page`, `pageSize` and `total`. Sort fields come from a fixed allow-list per endpoint. API documentation is generated from the code (OpenAPI document plus a Scalar UI in development); the 105 routes below are the OpenAPI document's, checked at M23.

**Public**

| Method | Path | Notes |
|---|---|---|
| GET | `/api/auth/session` | `{ user, demoMode }`: profile and memberships, or `user: null`; `demoMode` says whether to show the demo buttons and banner (D032); issues the XSRF cookie (D021) |
| POST | `/api/auth/sign-up` | user, organization and Owner membership; signs in |
| POST | `/api/auth/sign-in` | rate-limited per IP and per account |
| POST | `/api/auth/password-reset/request` | `{ email }`; always `202`; rate-limited |
| POST | `/api/auth/password-reset/confirm` | `{ token, password }`; ends every session |
| POST | `/api/auth/verify-email` | `{ token }` |
| POST | `/api/invitations/preview` | body `{ token }`: organization name, inviter, role, masked email, state, whether an account exists, and for a signed-in caller whether the email matches and whether they already belong (D028) |
| POST | `/api/invitations/sign-up` | body `{ token, displayName, password }`: new account and membership in one transaction; signs in |
| GET | `/api/demo/accounts` | demo mode only (404 otherwise): the account for each role, the seeded portal contacts, the reset interval and the next reset |
| POST | `/api/demo/sign-in` | demo mode only (404 otherwise): `{ role }`, no password; rate-limited |
| POST | `/api/portal/sign-in/request` | `{ orgSlug, email }`; always `202`; rate-limited |
| POST | `/api/portal/sign-in/confirm` | `{ orgSlug, token }` (the slug from the link's URL; D040); starts a portal session |
| POST | `/api/portal/demo/sign-in` | `{ email }`: demo mode only (404 otherwise), the seeded contacts only (D040) |
| GET | `/api/portal/session` | contact, client and organization names, features; no session → `contact: null` |
| GET | `/health`, `/health/live` | readiness (includes the database) and liveness |

**User-scoped** (signed in, no organization)

| Method | Path | Notes |
|---|---|---|
| POST | `/api/auth/sign-out` | |
| PATCH | `/api/me` | display name; returns the session |
| POST | `/api/me/password` | current and new password |
| POST | `/api/me/verification-email` | sends the link again; rate-limited |
| GET, PUT | `/api/me/notification-preferences` | email on or off per type |
| POST | `/api/orgs` | create an organization (Owner) |
| POST | `/api/invitations/accept` | body `{ token }`: signed-in acceptance; already a member → `alreadyMember: true` |

**Tenant-scoped** (`/api/orgs/{orgId}`, membership or an API key required; "key" marks the routes that accept a key, 3.7)

| Method | Path | Permission |
|---|---|---|
| GET | `/` | `org.read`: id, name, slug, plan, time zone, daily capacity, created, `usage`, `limits` and `features` of the plan, and every plan's limits and features |
| PATCH | `/` | `org.manage`: `{ name, timeZone, dailyCapacityMinutes? }` (IANA zone); errors reported together; audited |
| DELETE | `/` | `org.delete`; body `{ confirmName }` (whitespace collapsed, case-sensitive); cascades everything the organization owns, its audit entries included, so the deletion is logged instead (D031) |
| PUT | `/plan` | `billing.manage`: `{ plan, confirmFeatureLoss? }`; a downgrade that does not fit → `409 plan-downgrade-blocked` with `blocking`; features that would turn off → `409 plan-feature-loss` until confirmed; audited |
| GET | `/dashboard` | `org.read` |
| GET | `/members` | `org.read`: active members with name, email, role, joined, and what the caller may do to each (`isYou`, `canChangeRole`, `canRemove`, `canLeave`; D028) |
| PATCH | `/members/{userId}` | `members.manage`: role; `204` |
| DELETE | `/members/{userId}` | `members.manage`: remove someone else |
| DELETE | `/members/me` | `org.read`: leave (D028) |
| GET | `/invitations` | `members.manage`: open invitations (pending and expired) |
| POST | `/invitations` | `members.manage`: email and role; `delivery` is `sent`, `link` (email disabled) or `failed`, with the link in the last two |
| POST | `/invitations/{id}/resend` | `members.manage` |
| DELETE | `/invitations/{id}` | `members.manage` |
| GET | `/clients` | `clients.read`, key: `q` (name, contact, email), `status` (`active` default, `archived`, `all`), `sort` (`name` default, `-name`, `updated`, `-updated`, `created`, `-created`), paging |
| POST | `/clients` | `clients.write`, key |
| GET | `/clients/{id}` | `clients.read`, key |
| PUT | `/clients/{id}` | `clients.write`, key; body includes the `version` read earlier; a newer one → `409 concurrency-conflict` |
| POST | `/clients/{id}/archive`, `/clients/{id}/restore` | `clients.write`; idempotent; restoring onto a taken active name → `409 client-name-taken` |
| DELETE | `/clients/{id}` | `clients.delete`; `409` if the client has jobs |
| GET, POST | `/clients/{id}/contacts` | `clients.read` / `portal.manage` |
| DELETE | `/contacts/{id}` | `portal.manage` (one id: a contact belongs to one client; D040) |
| GET | `/jobs` | `jobs.read`, key: `q`, `status[]`, `assignee` (id, `me`, `none`), `clientId`, `agreementId`, `priority[]`, `overdue`, `dueFrom`, `dueTo`, `completedFrom` (a day in the organization's zone), `sort`, paging |
| POST | `/jobs` | `jobs.create`, key |
| GET | `/jobs/export` | `jobs.read`, key: the list filters; CSV (5.14) |
| GET | `/jobs/{id}` | `jobs.read`, key; includes the transitions allowed for the caller (each with what is still missing), and whether the caller may edit, assign and delete it (D029) |
| GET | `/jobs/{id}/history` | `jobs.read`: the job's audit entries, newest first |
| PUT | `/jobs/{id}` | route permission `jobs.work`, key; the domain allows `jobs.manage` on any job, `jobs.work` only on the caller's own job, and only managers change the assignee or the scheduled date; concurrency token |
| POST | `/jobs/{id}/transitions` | `jobs.work` at the route, key; rules in 5.4: `{ to, reason? }`; a move outside the table → `409 invalid-transition` with `allowed`; someone else's job → `403 job-not-yours`; a managers-only move → `403 manager-only`; a missing field or reason → `400` |
| DELETE | `/jobs/{id}` | `jobs.manage`; New or Cancelled only |
| POST | `/jobs/{id}/schedule` | `jobs.manage`: a board move, `{ assigneeUserId, scheduledOn (null: not scheduled), version, moveDueDate }` (5.8, D046) |
| POST | `/jobs/{id}/quote` | `portal.manage`: send for approval |
| POST | `/jobs/{id}/skip` | `agreements.manage`: generated visits only |
| GET, POST | `/jobs/{id}/checklist` | `jobs.read` / `jobs.work` (domain rules as for edits) |
| PUT, DELETE | `/jobs/{id}/checklist/{itemId}` | `jobs.work` |
| GET, POST | `/jobs/{id}/photos` | `jobs.read` / `jobs.work`; upload is `multipart/form-data` |
| GET | `/jobs/{id}/photos/{photoId}`, `/thumbnail` | `jobs.read` |
| DELETE | `/jobs/{id}/photos/{photoId}` | `jobs.work` |
| GET | `/board` | `jobs.read`: `week` (any day of the week, in the organization's zone; this week when left out), `clientId`, `status[]` |
| GET | `/agreements` | `agreements.read`, key: client, status, search, paging |
| POST | `/agreements` | `agreements.manage` |
| POST | `/agreements/preview` | `agreements.read`: the next occurrences of a recurrence, without saving |
| GET | `/agreements/{id}` | `agreements.read`, key: with the next occurrences and visits changed by hand |
| PUT | `/agreements/{id}` | `agreements.manage`; concurrency token |
| POST | `/agreements/{id}/pause`, `/resume`, `/end` | `agreements.manage` |
| GET | `/requests` | `portal.manage`: status, client, paging |
| GET | `/requests/open-count` | `portal.manage`: the sidebar's count (0 on a plan without the portal) |
| POST | `/requests/{id}/convert`, `/decline` | `portal.manage` (`jobs.create` also required to convert) |
| GET | `/notifications` | `org.read`: the caller's, newest first, with the unread count |
| POST | `/notifications/read` | `org.read`: `{ ids }` or `{ all: true }` |
| GET | `/audit` | `audit.read`: `actor` (user id), `action`, `entityType`, `entityId`, `from` and `to` (days in the organization's time zone), `before` (cursor), `pageSize` (50, at most 100). Newest first; returns `items` (each with `entityExists`) and `nextCursor`; no total (D030) |
| GET | `/audit/export` | `audit.read`: `from`, `to` (required, at most 366 days); CSV (5.14) |
| GET, POST | `/api-keys` | `integrations.manage` |
| DELETE | `/api-keys/{id}` | `integrations.manage` |
| GET, POST | `/webhooks` | `integrations.manage` |
| PUT, DELETE | `/webhooks/{id}` | `integrations.manage` (no version: edits wait for the endpoint's row lock; D044) |
| POST | `/webhooks/{id}/rotate-secret` | `integrations.manage` |
| GET | `/webhooks/{id}/deliveries` | `integrations.manage` |
| POST | `/webhooks/{id}/deliveries/{deliveryId}/redeliver` | `integrations.manage` |
| POST | `/webhooks/{id}/test` | `integrations.manage`: sends `webhook.ping` |

**Portal-scoped** (`/api/portal`, portal session required)

| Method | Path | Notes |
|---|---|---|
| POST | `/sign-out` | |
| GET | `/visits` | `when` (`upcoming` default, `past`), paging |
| GET | `/visits/{id}` | with checklist, photos and feedback once completed |
| GET | `/visits/{id}/photos/{photoId}`, `/thumbnail` | |
| POST | `/visits/{id}/confirm`, `/report-problem` | completed visits only, once |
| GET | `/quotes` | pending, and decided in the last 90 days |
| POST | `/quotes/{jobId}/approve`, `/decline` | `{ comment? }` |
| GET, POST | `/requests` | the contact's client's requests |

## 8. Data model (PostgreSQL)

### 8.1 Tables
Names are snake_case. Every timestamp is `timestamptz` in UTC. Tenant-owned tables have `organization_id`, a global query filter, an index starting with `organization_id`, row-level security (3.5), and composite foreign keys for references inside the tenant.

- **Identity tables** (ASP.NET Core Identity, Guid keys). `users` adds `display_name`, `created_at` and verification state. Not tenant-owned.
- **organizations:** `id`, `name`, `slug` (unique), `plan`, `time_zone` (IANA name), `daily_capacity_minutes`, `next_job_number`, `created_at`, `created_by_user_id`, `is_demo_seed`, `xmin` concurrency token. Not tenant-owned; it is the tenant, and access is by id after the membership check.
- **memberships** (tenant-owned): primary key `(organization_id, user_id)`, `role`, `joined_at`, `invited_by_user_id`, `removed_at` (soft removal, 5.2). Index on `user_id` for "my organizations". Only rows with `removed_at` null grant access.
- **invitations** (tenant-owned): `id`, `organization_id`, `email`, `normalized_email`, `role`, `token_hash` (unique), `expires_at`, `created_at`, `created_by_user_id`, `accepted_at`, `revoked_at`. Partial unique index on `(organization_id, normalized_email)` where the invitation is pending.
- **clients** (tenant-owned): `id`, `organization_id`, `name`, `contact_name`, `email`, `phone`, `address`, `notes`, `status`, `created_at`, `updated_at`, `xmin`. Unique `(organization_id, id)` as the target of composite foreign keys. Partial unique index on `(organization_id, lower(name))` where active.
- **jobs** (tenant-owned): `id`, `organization_id`, `number`, `title`, `description`, `client_id`, `assignee_user_id`, `status`, `priority`, `scheduled_on` (date), `due_on` (date), `quoted_amount` (`numeric(12,2)`), `estimated_minutes`, `created_at`, `created_by_user_id`, `updated_at`, `completed_at`, `xmin`; for visits `agreement_id`, `occurrence_on` and `changed_by_hand`; for quotes `approval_status`, `approval_sent_at`, `approval_sent_by_user_id`, `approved_amount`, `approval_decided_at`, `approval_decided_by_contact_id`, `approval_comment`; for sign-off `client_feedback`, `client_feedback_text`, `client_feedback_at`, `client_feedback_by_contact_id`.
  - Unique `(organization_id, number)`, and `(organization_id, agreement_id, occurrence_on)` where `agreement_id` is set.
  - Foreign key `(organization_id, client_id)` → `clients(organization_id, id)`.
  - Foreign key `(organization_id, assignee_user_id)` → `memberships(organization_id, user_id)`: an assignee is always a current or former member of the same organization (memberships are never hard-deleted while the organization exists).
  - Composite foreign keys to the agreement and to the deciding and signing-off contacts.
  - Indexes for the list filters, decided with `EXPLAIN` at M06; the board week is measured under load (M17, M22).
- **agreements** (tenant-owned): `id`, `organization_id`, `client_id` (composite foreign key), `title`, `description`, `assignee_user_id` (composite foreign key to memberships), `priority`, `quoted_amount`, `estimated_minutes`, `checklist_template` (`jsonb`), `recurrence` (`jsonb`, validated by the domain), `starts_on`, `ends_on`, `max_visits`, `status` (active, paused, ended), `paused_reason`, `generated_through`, `created_at`, `updated_at`, `xmin`.
- **client_contacts** (tenant-owned): `id`, `organization_id`, `client_id` (composite foreign key), `name`, `email`, `normalized_email`, `created_at`, `created_by_user_id`, `removed_at`, `last_sign_in_at`. Partial unique index on `(organization_id, client_id, normalized_email)` where active.
- **portal_sign_in_tokens** (tenant-owned): `token_hash` (unique), `organization_id`, `contact_id`, `expires_at`, `used_at`.
- **work_requests** (tenant-owned): `id`, `organization_id`, `client_id`, `contact_id`, `title`, `description`, `preferred_on`, `status`, `job_id`, `decline_reason`, `created_at`, `decided_at`, `decided_by_user_id`.
- **checklist_items** (tenant-owned): `id`, `organization_id`, `job_id` (composite foreign key), `position`, `text`, `required`, `done_at`, `done_by_user_id`.
- **attachments** (tenant-owned): `id`, `organization_id`, `job_id`, `storage_key`, `thumbnail_key`, `content_type`, `bytes` (photo and thumbnail together, as the storage limit counts them), `width`, `height`, `created_at`, `created_by_user_id`. The storage limit is a sum over this table under the organization lock (D043). With `checklist_items`, its client policy reads the job's (D048).
- **notifications** (tenant-owned): `id`, `organization_id`, `user_id`, `type`, `data` (`jsonb`: the text and the link), `source_id` (the outbox message or digest run, so a message notifies once), `created_at`, `read_at`. Index `(organization_id, user_id, created_at desc)`.
- **api_keys** (tenant-owned): `id`, `organization_id`, `name`, `prefix`, `key_hash` (unique), `access`, `created_at`, `created_by_user_id`, `expires_at`, `last_used_at`, `revoked_at`.
- **webhook_endpoints** (tenant-owned): `id`, `organization_id`, `url`, `events` (`text[]`), `secret_encrypted`, `previous_secret_encrypted`, `previous_secret_expires_at`, `created_at`, `disabled_at`, `failing_since`, `xmin` (not used as an edit version: edits take the row lock, D044).
- **webhook_deliveries** (tenant-owned): `id`, `organization_id`, `endpoint_id`, `event_id`, `event_type`, `body` (the bytes sent, so a redelivery sends them again), `attempt`, `status_code`, `duration_ms`, `error`, `created_at`, `next_attempt_at`, `succeeded_at`; no response bodies.
- **audit_entries** (tenant-owned): `id` (bigint identity), `organization_id`, `occurred_at`, `actor_user_id`, `actor_kind`, `actor_ref`, `actor_name` (snapshot), `action` (for example `job.status_changed`), `entity_type`, `entity_id`, `summary`, `changes` (`jsonb`), `trace_id`. Indexes `(organization_id, occurred_at desc, id desc)` (the timeline, keyset paging) and `(organization_id, entity_type, entity_id)`. A trigger rejects `UPDATE` (M07). `DELETE` happens only when the organization is deleted or during the demo reset.
- **Not tenant-owned, by the owner's approval** (M13): `notification_preferences` (`user_id`, `type`, `email`; per user), `outbox_messages` (`id`, `organization_id` nullable, `type`, `payload` encrypted when it carries a secret, `created_at`, `available_at`, `attempts`, `last_error`, `processed_at`, `dead_at`) and `scheduled_tasks` (`id`, `name`, `organization_id` nullable, `due_at`, `lease_until`, `last_run_at`, `last_error`; unique `(name, organization_id)` with nulls not distinct; D039), plus `data_protection_keys` (the key ring, D033). Only the dispatcher and the scheduler read the last three.

Organization deletion cascades to every tenant-owned table; its photo files go through the outbox.

### 8.2 Domain model
- **Organization** aggregate: name, slug, plan, time zone, daily capacity, job counter. Methods: `Rename`, `ChangeTimeZone`, `ChangePlan(target, usage, featuresInUse, confirmFeatureLoss)` (refuses the downgrade, asks to confirm feature loss), `NextJobNumber`.
- **Membership** rules (last Owner, who may grant which role) live in a domain service over the organization's memberships, because they need the whole set.
- **Invitation**: `Create`, `Resend` (rotates the token), `Revoke`, `Accept(email, now)`; knows if it is pending, expired, revoked or accepted.
- **Client**: `Update`, `Archive`, `Restore`. **ClientContact**: add and remove, with the per-client limit. **WorkRequest**: Open → Converted or Declined.
- **Job** aggregate: the state machine in 5.4 as a pure transition table with its quote and checklist conditions; `TransitionTo(target, actor, reason, today)`; validation of dates, amount and estimated duration; the checklist, photos (counted), the approval (send, approve, decline, reset) and the client's feedback.
- **Agreement** aggregate with a **Recurrence** value object (weekly, monthly by day, monthly by nth weekday) and the visit generation over the horizon.
- **ApiKey** (secret shown once, hash, access level, usable) and **WebhookEndpoint** (events, secrets with rotation, the failure window, disabling).
- **Plan limits and features**: a `PlanLimits` value object, `PlanFeatures`, and a `PlanPolicy` that checks a requested change against current usage.
- Aggregates raise **domain events** (`JobStatusChanged`, `MemberRemoved`, ...). The audit writer turns them into audit entries inside the same `SaveChanges` call, and from the same events writes the outbox messages for notifications and webhooks.

### 8.3 Seed data (fictional)
- **Brightline Cleaning Co.** (Team plan, Europe/Amsterdam): 6 members, 40 clients and 150 jobs spread over the past 4 months and the next month, in every status; three agreements (weekly office cleaning, fortnightly window cleaning, a monthly deep clean on the last Friday) with their visits; checklists and photos on recent completed work; three clients with a portal contact, a quote waiting, an approved one, open and declined requests, and two signed-off visits (one confirmed, one with a problem reported).
- **Harbor & Pine Landscaping** (Free plan, America/New_York): 3 members at 3 of 3 seats, usage near the limits, and an agreement held at the open-jobs limit, to show limit handling.
- Demo users for each role. One user belongs to both organizations, to show the switcher.
- Audit history generated from the same domain events as real use, so it is consistent with the data; contacts act as themselves in the portal history.
- People and companies are invented. Emails use the reserved `.example` domain and phone numbers are obviously fake. Photos are generated pictures, processed like uploads.

## 9. Non-functional requirements

- **Security:**
  - Authentication on every endpoint except the public ones in section 7; permissions and tenant checks on the server.
  - Cookie authentication (`HttpOnly`, `Secure`, `SameSite=Strict`) and antiforgery on unsafe requests, for staff and for the portal, with separate cookies and schemes.
  - Rate limits on sign-up, sign-in, invitation creation and acceptance, password reset, verification emails, portal links, and per API key.
  - Invitation and portal sign-in tokens: 32 random bytes, stored as SHA-256 hashes, compared in constant time; reset and verification tokens are protected and bound to the security stamp (5.1). No token is ever logged. API keys and webhook secrets are shown once.
  - Security headers: strict CSP with no inline scripts or styles, HSTS in production, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`.
  - Parameterized queries only; sort fields from allow-lists.
  - Uploads parsed as untrusted input, with size and dimension limits checked before the full image is decoded (5.12).
  - Outbound webhook requests behind the connection-time guard (5.14).
  - Non-root containers; secrets only in the environment.
  - A secret and personal-data scan of the files and the full history before each release.
- **Tenant isolation:** section 3. A failure of any isolation test blocks merging.
- **Background work:** idempotent and safe on several instances. A test runs two dispatchers and two schedulers against one database and checks that nothing is skipped or done twice.
- **Performance** (measured and recorded in `PROGRESS.md`):
  - jobs list API p95 < 200 ms locally with 10,000 jobs across 20 organizations, row-level security on;
  - board week p95 < 200 ms on the same data;
  - generating the 60-day horizon for 200 agreements in one organization: time recorded;
  - Lighthouse performance ≥ 90 on the landing page and the dashboard (mobile, production build);
  - initial JavaScript < 300 kB transferred; routes lazy-loaded, the portal its own area.
- **Accessibility:** WCAG 2.2 AA target, with no serious or critical axe violations on any screen, including 2.5.7 (Dragging Movements): every drag has a single-pointer and keyboard alternative, and moves are announced. Visible focus, skip link, labelled controls with associated errors, a table alternative for charts, and contrast checked in both themes.
- **Responsive:** every screen checked at 375, 768 and 1280 px (and, for v2 screens, 320 px and 200 % zoom) with no horizontal page scroll. Wide tables scroll inside their container or switch to cards.
- **Reliability:**
  - migrations apply on startup in containers;
  - commands are transactional, including their audit entries, outbox messages and plan-limit checks;
  - optimistic concurrency on clients, jobs, agreements and organizations.
- **Privacy:** contacts' personal data is deleted with the client or the organization; notification and delivery logs keep no personal data beyond names already in the organization.
- **Observability:** structured JSON logs with trace id, organization id and user id (or contact or key); no secrets, passwords, cookies, tokens, links or request bodies in logs. Cross-tenant attempts the guard catches, and refused webhook targets, are logged as security events.
- **Browsers:** current and previous versions of Chrome, Edge, Firefox and Safari.
- **Quality gates:** nullable reference types and strict TypeScript; warnings are errors; lint and format checks, the architecture tests, the isolation tests and both end-to-end journeys run in CI.

## 10. Acceptance criteria (product level)

1. The coverage tests in 3.4 pass, and fail when a tenant-owned entity without a filter or an unclassified endpoint is added (shown at M03).
2. The cross-tenant matrix passes for every tenant-scoped endpoint: no read, write or existence leak between two organizations.
3. Removing one global query filter or the membership check makes the test suite fail (mutation checks recorded at M03, M10, M14 and M22; since M14 the database's policies must be removed too, criterion 12).
4. A visitor signs up, invites a teammate, and the teammate accepts through the link (new account and existing account); both see the organization with the right role.
5. Every row of the permission table in section 2 is covered by an API test (allowed and refused), and the UI hides actions the user cannot perform.
6. Every allowed and refused job transition in 5.4 is covered by a domain test; the API returns `409` with the allowed transitions for refused ones.
7. Plan limits hold under concurrency: with one seat left, two simultaneous invitations produce exactly one success (integration test).
8. Every state-changing endpoint writes an audit entry in the same transaction; a forced failure after the change leaves neither the change nor the entry. `UPDATE` on audit entries is rejected by the database.
9. Every screen passes the 375/768/1280 px, keyboard, console-clean and axe checks in both themes.
10. `docker compose up` from a fresh clone runs the whole stack with demo data; the Playwright journey (sign up → invite → accept → create client → create and complete a job → see the audit entry) passes against it in CI.
11. The demo works with documented demo access, and the README shows it with screenshots and a short recording. There is no hosted demo, by the owner's choice (D035, D053): the demo is the one-command run.
12. **Row-level security:** every tenant-owned table has an enabled policy (coverage test). With one EF Core query filter removed, the cross-tenant matrix still passes; with the policy removed too, it fails. A pooled connection reused across two tenants never shows the first tenant's rows. Recorded at M14 and M22.
13. **Portal isolation:** the portal matrix passes for every portal endpoint; removing a contact ends their access on the next request.
14. **Agreements:** generation produces exactly the expected visits for each recurrence type over a year, including month ends, the last day of February in a leap year, and a non-UTC zone. Running generation twice, or on two workers at once, creates no duplicates. A plan limit stops generation at the limit.
15. **Board:** every move can be made by keyboard alone and is announced; a concurrent move gives a conflict and no lost update.
16. **Outbox:** a rolled-back command writes no message; a failed delivery is retried; two dispatchers deliver each message exactly once (integration test).
17. **Uploads:** a file whose content is not an allowed image is refused, whatever its name or declared type; stored images carry no EXIF or GPS data (the test reads the stored bytes); another organization's or another client's photo is `404`.
18. **Webhooks:** signatures verify with an independent implementation of the documented algorithm; private, loopback and link-local targets are refused, including hosts that resolve to them and redirects to them; retries follow the schedule.
19. **Entitlements:** each feature is refused by the API on plans without it (`403 plan-feature-unavailable`), hidden in the UI, and listed in the downgrade confirmation.
20. **Second journey:** a second Playwright journey passes against the production image in CI: create an agreement → its visits appear on the board → move one by keyboard → the client contact signs in through the emailed link and approves the quote → the assignee completes the checklist and adds a photo → the contact confirms the visit → a signed webhook reaches a test receiver (D052).
21. **Audit of new screens:** every v2 screen, staff and portal, passes the M10 audit (375/768/1280 px, keyboard, both themes, axe, clean console, every overlay opened; `docs/AUDIT.md`).

## 11. Delivery plan

Each milestone was one pull request. Briefs are in `prompts/`, status in `PROGRESS.md`, user stories in `docs/USER-STORIES.md`.

**v1** (released as v1.0.0 on 2026-10-25)

| # | Milestone | Depends on |
|---|---|---|
| M01 | Solution skeleton, architecture tests, CI, Angular shell | none |
| M02 | Accounts and organizations: sign-up, sign-in, me, create and switch organization | M01 |
| M03 | Tenancy core: tenant context, permissions, query filters, SaveChanges guard, isolation test harness | M02 |
| M04 | Clients (API and UI), composite keys, audit recording | M03 |
| M05 | Members and invitations | M03 |
| M06 | Jobs: aggregate, workflow, numbering, list, detail | M04, M05 |
| M07 | Audit log viewer and append-only guarantee | M04 |
| M08 | Plans and limits, organization settings, delete organization | M05, M06 |
| M09 | Dashboard, full seed data, demo mode | M06, M08 |
| M10 | Polish: states, responsive, accessibility, performance, known issues | all features |
| M11 | End-to-end test, production Docker image, Compose, E2E in CI | M10 |
| M12 | Demo hosting, README, architecture, portfolio kit, public-repo scan | M11 |

**v2** (D037; phases group the milestones)

| Phase | # | Milestone | Depends on |
|---|---|---|---|
| Plan | M13 | v2 specification, decisions and milestone briefs | v1.0.0 |
| A. Foundations | M14 | Row-level security: the database enforces the tenant | M13 |
| | M15 | Outbox, scheduler, password reset and email verification | M14 |
| B. Run the work | M16 | Recurring service agreements | M15 |
| | M17 | Dispatch board with keyboard moves | M16 |
| C. Face the client | M18 | Client portal, quotes, work requests, plan entitlements | M15 |
| | M19 | Checklists, photos and client sign-off | M16, M18 |
| | M20 | Notifications in the app and by email | M15, M18 |
| D. Connect | M21 | API keys, signed webhooks and exports | M15, M18 |
| E. Ship | M22 | v2 polish, audit and the second end-to-end journey | M14–M21 |
| | M23 | v2.0.0: documentation, portfolio and release | M22 |

## 12. Earlier section numbers

Code comments, decisions and briefs written before M23 cite the v2 specification (`SPEC-V2 x.y`, merged here and removed at M23, D054) and two v1 sections that moved. They map as follows; every other `SPEC x.y` reference is unchanged.

| Earlier reference | Here |
|---|---|
| SPEC-V2 1 | 1 |
| SPEC-V2 2.1–2.4 (2.2 "New staff permissions") | 2.1–2.4 (the permissions joined the table in 2) |
| SPEC-V2 3.1, 3.2, 3.3 | 3.5, 3.6, 3.7 |
| SPEC-V2 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 4.10 | 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 4.10, 4.11 |
| SPEC-V2 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8, 5.9 | 5.7, 5.8, 5.9, 5.10, 5.11, 5.12, 5.13, 5.14, 5.15 |
| SPEC-V2 6, 7, 9, 11 | 6, 7, 9, 11 |
| SPEC-V2 8 | 8.1 |
| SPEC-V2 10 (criteria 12–21) | 10 (the same numbers) |
| SPEC 4.1 "MVP" | 4.1 "Core features (v1)" |
| SPEC 4.2 "Later (V2)", 4.3 | 4.11, 4.12 |
