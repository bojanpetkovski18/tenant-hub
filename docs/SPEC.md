# TenantHub: Specification

Status: approved for implementation on 2026-10-21. Changes after that date are recorded in `DECISIONS.md` and noted here with the decision number.

## 1. Purpose

TenantHub is a multi-tenant back-office for small service businesses (cleaning companies, landscapers, maintenance contractors, small agencies). A business signs up as an **organization**, invites its team, keeps a list of **clients**, and plans and tracks **jobs** for them. Every change is recorded in an **audit log**, and what an organization can do is bounded by its **plan**.

The jobs-and-clients domain is deliberately ordinary. The real subject of the project is the foundation that every B2B SaaS product needs and that freelance clients repeatedly ask for:
- organization sign-up, invitations and membership in several organizations;
- role permissions enforced on the server, including rules that depend on the resource ("a member may update jobs assigned to them");
- tenant isolation that is enforced in depth and **proven by tests**;
- plan limits that hold under concurrent requests;
- an append-only audit trail written in the same transaction as the change.

It is an independent portfolio project built with AI assistance. Within the owner's portfolio it complements Numera (analytics dashboard and data import) and Fitness Merchant (e-commerce): TenantHub is the B2B SaaS and business-rules project, and the one where Domain-Driven Design and CQRS are used because the rules justify them.

## 2. Users, roles and permissions

- **Visitor:** signs up (creating a user and an organization), signs in, opens an invitation link.
- **User:** an account (email and password). A user can belong to several organizations with a different role in each (a **membership**). The UI works inside one organization at a time and has an organization switcher.
- **Roles** (per membership): Owner, Admin, Member, Viewer.

Roles map to fixed permission sets defined in code (custom roles are V2). The server checks permissions on every request; the UI only hides what the server would refuse.

| Permission | Owner | Admin | Member | Viewer |
|---|---|---|---|---|
| `org.read` (dashboard, organization details, member list) | yes | yes | yes | yes |
| `org.manage` (name, time zone) | yes | yes | no | no |
| `org.delete` | yes | no | no | no |
| `billing.manage` (change plan) | yes | no | no | no |
| `members.manage` (invite, change role, remove) | yes | yes, see rules 5.2 | no | no |
| `clients.read` | yes | yes | yes | yes |
| `clients.write` (create, edit, archive, restore) | yes | yes | yes | no |
| `clients.delete` | yes | yes | no | no |
| `jobs.read` | yes | yes | yes | yes |
| `jobs.create` | yes | yes | yes | no |
| `jobs.manage` (edit, assign, transition or delete any job) | yes | yes | no | no |
| `jobs.work` (edit and transition jobs assigned to me) | yes | yes | yes | no |
| `audit.read` | yes | yes | no | no |

## 3. Multi-tenancy model

### 3.1 Storage
One database and one schema shared by all organizations. Every tenant-owned table has an `organization_id` column. Alternatives (schema per tenant, database per tenant) and the reasons for this choice are in D005.

### 3.2 Resolving the tenant
- Tenant-scoped API routes carry the organization: `/api/orgs/{orgId}/...`. The SPA uses the organization's slug in its own URLs (`/o/{slug}/jobs`) and maps it to the id from the session (`GET /api/auth/session`).
- On every tenant-scoped request the server loads the caller's active membership for `{orgId}`. No active membership → `404` with code `organization-not-found`, identical for a foreign organization, an unknown id and a removed member (the organization's existence is not revealed). Membership found → a request-scoped `TenantContext` holds the organization id, user id, role and permissions. Missing permission → `403` with code `permission-denied` (D025).
- The organization is never taken from a request body, query string, header or cookie claim. Ids of other entities in a request (client id, assignee id) are always looked up inside the current tenant.

### 3.3 Enforcement in depth
1. **Membership check** per request (3.2).
2. **EF Core global query filters** on every tenant-owned entity: `organization_id = current tenant`. A tenant-owned entity is one that implements `ITenantOwned`.
3. **SaveChanges guard:** new tenant-owned entities get the current `organization_id` stamped; inserting, updating or deleting an entity of another organization throws, is logged as a security event and returns `500`. `organization_id` can never be modified.
4. **Composite foreign keys** in the database: a job references `(organization_id, client_id)` → `clients(organization_id, id)`, so a job can never point at another organization's client, even through a bug in the application.
5. **Allow-listed bypasses only:** `IgnoreQueryFilters()` is allowed only in a short, named list of places (sign-in membership lookup, invitation acceptance, demo seed and reset). An architecture test enforces the list.
6. PostgreSQL row-level security as a further layer is V2 (D007).

### 3.4 Proof
Isolation is a product feature, so it has its own tests (section 10, criteria 1–3):
- **Coverage tests:** every entity implementing `ITenantOwned` has a query filter and an index starting with `organization_id`; every entity that does not is on an explicit allow-list. Every API endpoint is classified as public, user-scoped or tenant-scoped; an unclassified endpoint fails the build. New entities and endpoints are therefore covered automatically, not by remembering to add a test.
- **Cross-tenant matrix:** two seeded organizations. For every tenant-scoped endpoint, a user of organization A calls it with organization B's route and with B's entity ids under A's route. Expected: `404` (or `403` where documented), no change in B's data (checked by row counts and a hash of B's rows), and no B data in any response body.
- **Database guarantees:** inserting a job that references another organization's client fails on the composite foreign key, with the application layer bypassed.
- **Mutation checks:** removing one query filter, or the membership check, makes the suite fail. This is done by hand at M03 and M12 and recorded in `PROGRESS.md`.

## 4. Features

### 4.1 MVP

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
- Invitation emails are sent through SMTP (Mailpit in development). When email is disabled (public demo), the inviter gets a "copy link" dialog instead (D011).
- Member list: name, email, role, joined date. Change role, remove a member, leave the organization. Removing a member unassigns their open jobs (recorded in the audit log).

**Clients**
- Create, edit, archive, restore and delete. Fields: name (required), contact person, email, phone, address, notes.
- List with search (name, contact, email), status filter (active, archived, all), sorting and server-side paging.
- Detail page: client information and the client's jobs.

**Jobs**
- Fields: per-organization number (`JOB-0042`), title, client, description, assignee, status, priority, scheduled date, due date, quoted amount.
- Status workflow (5.4): New → Scheduled → In progress → Completed, Cancelled from any open status, and Reopen from Completed.
- List with search (number, title), filters (status, assignee including "me" and "unassigned", client, priority, overdue, due date range), sorting, server-side paging, and filters reflected in the URL.
- Detail page: fields, the allowed status actions for this user, and the job's history from the audit log.
- Concurrent edits are detected (optimistic concurrency); the second save gets `409` and a "reload" prompt.

**Audit log**
- Every state-changing command writes one or more audit entries in the same database transaction: who, when, what action, which entity, a human-readable summary, and the changed fields with old and new values.
- Viewer (Admin and Owner): filters by actor, action, entity type and date range; paging; links to the entity.
- Append-only: there is no API to change or delete entries, and a database trigger rejects `UPDATE` on the table.

**Plans and limits**
- Plans: Free, Team and Business (limits in 5.5). Settings → Plan shows usage meters for each limit.
- Limits are checked on the server inside the same transaction as the change, holding a lock on the organization row, so concurrent requests cannot exceed them.
- Changing plan is simulated ("demo billing", Owner only, no payment). A downgrade is refused while usage exceeds the target plan, and the response lists the limits that block it.

**Dashboard** (per organization)
- Tiles: open jobs, overdue jobs, jobs completed this month, active clients.
- Jobs by status, my next 7 days of scheduled jobs, plan usage, and recent activity (Admin and Owner).

**Organization settings**
- Name and time zone. "Overdue" and "this month" use the organization's time zone.
- Delete organization (Owner): confirmed by typing the organization name; deletes all its data.

**Demo mode** (`DEMO_MODE=true`)
- Seeded fictional organizations and users (section 8.3), with "Try as Owner / Admin / Member / Viewer" buttons on the sign-in page.
- Scheduled reset of the seeded data. Organizations created by visitors are deleted after 24 hours. Sign-up and invitations are rate-limited, and email sending is off.

### 4.2 Later (V2)
Kanban board for jobs; comments and attachments on jobs; email verification and password reset by email; audit CSV export; custom roles; PostgreSQL row-level security; real billing with Stripe; a platform-admin console; per-organization API keys and webhooks; SSO.

### 4.3 Out of scope
Real payments; emails to real recipients from the public demo; invoicing and accounting; native mobile apps; schema-per-tenant and database-per-tenant (D005).

## 5. Business rules

### 5.1 Accounts and organizations
- Email is unique, compared case-insensitively. Passwords: at least 10 characters with upper case, lower case, a digit and a symbol. Lockout for 15 minutes after 5 failed sign-ins.
- Organization name: 2–80 characters. The slug is generated from the name at creation: lowercase `a-z`, `0-9` and `-`, 3–40 characters, unique, not a reserved word (`api`, `app`, `admin`, `new`, `settings`, `invite`, `orgs`, `o`). On a collision `-2`, `-3`, … is appended. The slug does not change when the name changes (MVP).
- A user may own at most 5 organizations.

### 5.2 Memberships
- An organization always has at least one Owner. The last Owner cannot leave, be removed or be demoted; transfer ownership first by making another member an Owner.
- Admins cannot change, remove or create Owners. Nobody changes their own role.
- Removing a member, or a member leaving, unassigns their jobs that are not Completed or Cancelled. Closed jobs keep the former member as assignee, for history.
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
  | New, Scheduled | In progress | assignee | `jobs.manage`, or `jobs.work` if assigned to me |
  | In progress | Completed | none | `jobs.manage`, or `jobs.work` if assigned to me |
  | New, Scheduled, In progress | Cancelled | a reason (1–500 characters) | `jobs.manage` |
  | Completed | In progress (reopen) | a reason | `jobs.manage` |

- Any other transition is rejected with `409` and the allowed transitions for the current state.
- The client must be active when a job is created or moved to a different client. Archiving a client is allowed while it has open jobs; a warning shows how many.
- Due date ≥ scheduled date when both are set. Quoted amount: 0 to 1,000,000.00, two decimals, stored as `numeric(12,2)`. Every amount is in the organization's currency, which is EUR for the MVP.
- Overdue: due date earlier than today in the organization's time zone, and status not Completed or Cancelled.
- A job may be deleted by `jobs.manage` only while it is New or Cancelled.

### 5.5 Plans

| Limit | Free | Team | Business |
|---|---|---|---|
| Seats (members plus pending invitations) | 3 | 10 | 50 |
| Active clients | 25 | 500 | unlimited |
| Open jobs (not Completed or Cancelled) | 50 | 2,000 | unlimited |

- Reaching a limit returns `403` with code `plan-limit-reached`, `plan`, `limit` (`seats`, `activeClients`, `openJobs`), `allowed` and `usage` (D013). Seats are active members plus pending (not expired) invitations; renewing an expired invitation takes a seat again, and accepting one turns its seat into a membership.
- Archived clients and closed jobs do not count, so restoring a client or reopening a job is also limit-checked.

### 5.6 Clients
- Name 2–120 characters, unique among the organization's active clients (case-insensitive). Email optional but validated; phone free text up to 40 characters.
- Delete only when the client has no jobs; otherwise archive.

## 6. Screens

**Public:** landing page (`/`, short product pitch and "Try the demo"), sign in (with demo buttons in demo mode), sign up, accept invitation (`/invite#<token>`: the token is in the fragment, D028).

**Signed in, outside an organization:** organization picker (`/orgs`) with "create organization", account settings (`/account`).

**Inside an organization** (`/o/:slug/...`), in an app shell with a sidebar (organization switcher at the top, then navigation filtered by permission) that becomes a drawer below 1024 px:
- Dashboard
- Jobs: list, new, detail and edit
- Clients: list, new, detail and edit
- Members: members tab and pending-invitations tab
- Audit log
- Settings: General, Plan and usage, Danger zone

**System:** not found, access denied, "you were removed from this organization", global error banner.

**Design direction:** a calm, dense work tool rather than a marketing template. Neutral slate surfaces, one teal accent, compact tables with a 14 px base size, light and dark themes. Status and priority use an icon and text as well as colour. A visual identity distinct from Numera and Fitness Merchant.

## 7. API

All endpoints are under `/api` and return JSON. Errors are RFC 7807 problem details with `traceId` and, for business-rule errors, a stable `code` (for example `plan-limit-reached`, `invalid-transition`, `last-owner`). Lists use `page` (1-based) and `pageSize` (default 25, maximum 100) and return `items`, `page`, `pageSize` and `total`. Sort fields come from a fixed allow-list per endpoint.

**Public**

| Method | Path | Notes |
|---|---|---|
| GET | `/api/auth/session` | `{ user }` with profile and memberships, or `{ user: null }`; issues the XSRF cookie (D021) |
| POST | `/api/auth/sign-up` | user, organization and Owner membership; signs in |
| POST | `/api/auth/sign-in` | rate-limited per IP and per account |
| POST | `/api/invitations/preview` | body `{ token }`: organization name, inviter, role, masked email, state, whether an account exists, and for a signed-in caller whether the email matches and whether they already belong (D028) |
| POST | `/api/invitations/sign-up` | body `{ token, displayName, password }`: new account and membership in one transaction; signs in |
| GET | `/api/demo/accounts` | demo mode only |
| POST | `/api/demo/sign-in` | demo mode only: `{ role }` |
| GET | `/health`, `/health/live` | readiness (includes the database) and liveness |

**User-scoped** (signed in, no organization)

| Method | Path | Notes |
|---|---|---|
| POST | `/api/auth/sign-out` | |
| PATCH | `/api/me` | display name; returns the session |
| POST | `/api/me/password` | current and new password |
| POST | `/api/orgs` | create an organization (Owner) |
| POST | `/api/invitations/accept` | body `{ token }`: signed-in acceptance; already a member → `alreadyMember: true` |

**Tenant-scoped** (`/api/orgs/{orgId}`, membership required)

| Method | Path | Permission |
|---|---|---|
| GET | `/` | `org.read`: id, name, slug, plan, time zone, created, `usage` (seats, active clients, open jobs), `limits` of the plan (null = unlimited) and every plan's limits |
| PATCH | `/` | `org.manage`: `{ name, timeZone }` (IANA); both errors reported together; audited |
| DELETE | `/` | `org.delete`; body `{ confirmName }` (whitespace collapsed, case-sensitive); cascades everything the organization owns, its audit entries included, so the deletion is logged instead (D031) |
| PUT | `/plan` | `billing.manage`: `{ plan }`; a downgrade that does not fit → `409 plan-downgrade-blocked` with `blocking` (limit, allowed, usage per limit); audited |
| GET | `/dashboard` | `org.read` |
| GET | `/members` | `org.read`: active members with name, email, role, joined, and what the caller may do to each (`isYou`, `canChangeRole`, `canRemove`, `canLeave`; D028) |
| PATCH | `/members/{userId}` | `members.manage`: role; `204` |
| DELETE | `/members/{userId}` | `members.manage`: remove someone else |
| DELETE | `/members/me` | `org.read`: leave (D028) |
| GET | `/invitations` | `members.manage`: open invitations (pending and expired) |
| POST | `/invitations` | `members.manage`: email and role; `delivery` is `sent`, `link` (email disabled) or `failed`, with the link in the last two |
| POST | `/invitations/{id}/resend` | `members.manage` |
| DELETE | `/invitations/{id}` | `members.manage` |
| GET | `/clients` | `clients.read`: `q` (name, contact, email), `status` (`active` default, `archived`, `all`), `sort` (`name` default, `-name`, `updated`, `-updated`, `created`, `-created`), paging |
| POST | `/clients` | `clients.write` |
| GET | `/clients/{id}` | `clients.read` |
| PUT | `/clients/{id}` | `clients.write`; body includes the `version` read earlier; a newer one → `409 concurrency-conflict` |
| POST | `/clients/{id}/archive`, `/clients/{id}/restore` | `clients.write`; idempotent; restoring onto a taken active name → `409 client-name-taken` |
| DELETE | `/clients/{id}` | `clients.delete`; `409` if the client has jobs |
| GET | `/jobs` | `jobs.read`: `q`, `status[]`, `assignee` (id, `me`, `none`), `clientId`, `priority[]`, `overdue`, `dueFrom`, `dueTo`, `sort`, paging |
| POST | `/jobs` | `jobs.create` |
| GET | `/jobs/{id}` | `jobs.read`; includes the transitions allowed for the caller (each with what is still missing), and whether the caller may edit, assign and delete it (D029) |
| GET | `/jobs/{id}/history` | `jobs.read`: the job's audit entries, newest first (US-40: everyone who can read the job sees its history) |
| PUT | `/jobs/{id}` | route permission `jobs.work`; the domain allows `jobs.manage` on any job, `jobs.work` only on the caller's own job, and only managers change the assignee or the scheduled date; concurrency token |
| POST | `/jobs/{id}/transitions` | `jobs.work` at the route, rules in 5.4: `{ to, reason? }`; a move outside the table → `409 invalid-transition` with `allowed`; someone else's job → `403 job-not-yours`; a managers-only move → `403 manager-only`; a missing field or reason → `400` |
| DELETE | `/jobs/{id}` | `jobs.manage`; New or Cancelled only |
| GET | `/audit` | `audit.read`: `actor` (user id), `action`, `entityType`, `entityId`, `from` and `to` (days in the organization's time zone), `before` (cursor), `pageSize` (50, at most 100). Newest first; returns `items` (each with `entityExists`) and `nextCursor`; no total (D030) |

API documentation is generated from the code (OpenAPI document plus a Scalar UI in development).

## 8. Data model (PostgreSQL)

### 8.1 Tables
Names are snake_case. Every timestamp is `timestamptz` in UTC. Rows marked "tenant-owned" have `organization_id` and a global query filter.

- **Identity tables** (ASP.NET Core Identity, Guid keys). `users` adds `display_name` and `created_at`. Not tenant-owned.
- **organizations:** `id`, `name`, `slug` (unique), `plan`, `time_zone` (IANA name), `next_job_number`, `created_at`, `created_by_user_id`, `is_demo_seed`, `xmin` concurrency token. Not tenant-owned; it is the tenant, and access is by id after the membership check.
- **memberships** (tenant-owned): primary key `(organization_id, user_id)`, `role`, `joined_at`, `invited_by_user_id`, `removed_at` (soft removal, 5.2). Index on `user_id` for "my organizations". Only rows with `removed_at` null grant access.
- **invitations** (tenant-owned): `id`, `organization_id`, `email`, `normalized_email`, `role`, `token_hash` (unique), `expires_at`, `created_at`, `created_by_user_id`, `accepted_at`, `revoked_at`. Partial unique index on `(organization_id, normalized_email)` where the invitation is pending.
- **clients** (tenant-owned): `id`, `organization_id`, `name`, `contact_name`, `email`, `phone`, `address`, `notes`, `status`, `created_at`, `updated_at`, `xmin`. Unique `(organization_id, id)` as the target of composite foreign keys. Partial unique index on `(organization_id, lower(name))` where active.
- **jobs** (tenant-owned): `id`, `organization_id`, `number`, `title`, `description`, `client_id`, `assignee_user_id`, `status`, `priority`, `scheduled_on` (date), `due_on` (date), `quoted_amount` (`numeric(12,2)`), `created_at`, `created_by_user_id`, `updated_at`, `completed_at`, `xmin`.
  - Unique `(organization_id, number)`.
  - Foreign key `(organization_id, client_id)` → `clients(organization_id, id)`.
  - Foreign key `(organization_id, assignee_user_id)` → `memberships(organization_id, user_id)`: an assignee is always a current or former member of the same organization (memberships are never hard-deleted while the organization exists).
  - Indexes for the list filters, decided with `EXPLAIN` at M06.
- **audit_entries** (tenant-owned): `id` (bigint identity), `organization_id`, `occurred_at`, `actor_user_id`, `actor_name` (snapshot), `action` (for example `job.status_changed`), `entity_type`, `entity_id`, `summary`, `changes` (`jsonb`), `trace_id`. Indexes `(organization_id, occurred_at desc, id desc)` (the timeline, keyset paging) and `(organization_id, entity_type, entity_id)`. A trigger rejects `UPDATE` (M07). `DELETE` happens only when the organization is deleted or during the demo reset.

Organization deletion cascades to every tenant-owned table.

### 8.2 Domain model
- **Organization** aggregate: name, slug, plan, time zone, job counter. Methods: `Rename`, `ChangeTimeZone`, `ChangePlan(target, usage)` (refuses the downgrade), `NextJobNumber`.
- **Membership** rules (last Owner, who may grant which role) live in a domain service over the organization's memberships, because they need the whole set.
- **Invitation**: `Create`, `Resend` (rotates the token), `Revoke`, `Accept(email, now)`; knows if it is pending, expired, revoked or accepted.
- **Client**: `Update`, `Archive`, `Restore`.
- **Job** aggregate: the state machine in 5.4 as a pure transition table; `TransitionTo(target, actor, reason, today)`; validation of dates and amount.
- **Plan limits**: a `PlanLimits` value object and a `PlanPolicy` that checks a requested change against current usage.
- Aggregates raise **domain events** (`JobStatusChanged`, `MemberRemoved`, ...). The audit writer turns them into audit entries inside the same `SaveChanges` call.

### 8.3 Seed data (fictional)
- **Brightline Cleaning Co.** (Team plan): 6 members, about 40 clients and 150 jobs spread over the past 4 months and the next month, in every status.
- **Harbor & Pine Landscaping** (Free plan): 3 members and usage near the limits, to show limit handling.
- Demo users for each role. One user belongs to both organizations, to show the switcher.
- Audit history generated from the same domain events as real use, so it is consistent with the data.
- People and companies are invented. Emails use the reserved `.example` domain and phone numbers are obviously fake.

## 9. Non-functional requirements

- **Security:**
  - Authentication on every endpoint except the public ones in section 7; permissions and tenant checks on the server.
  - Cookie authentication (`HttpOnly`, `Secure`, `SameSite=Strict`) and antiforgery on unsafe requests.
  - Rate limits on sign-up, sign-in, invitation creation and invitation acceptance.
  - Invitation tokens: 32 random bytes, stored as SHA-256 hashes, compared in constant time, never logged.
  - Security headers: strict CSP with no inline scripts, HSTS in production, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`.
  - Parameterized queries only; sort fields from allow-lists.
  - Non-root containers; secrets only in the environment.
  - A secret and personal-data scan of the files and the full history before the repository goes public.
- **Tenant isolation:** section 3. A failure of any isolation test blocks merging.
- **Performance** (targets; measured in M10 and recorded in `PROGRESS.md`):
  - jobs list API p95 < 200 ms locally with 10,000 jobs across 20 organizations;
  - Lighthouse performance ≥ 90 on the landing page and the dashboard (mobile, production build);
  - initial JavaScript < 300 kB transferred; routes lazy-loaded.
- **Accessibility:** WCAG 2.2 AA target, with no serious or critical axe violations on any screen. Visible focus, skip link, labelled controls with associated errors, a table alternative for charts, and contrast checked in both themes.
- **Responsive:** every screen checked at 375, 768 and 1280 px with no horizontal page scroll. Wide tables scroll inside their container or switch to cards.
- **Reliability:**
  - migrations apply on startup in containers;
  - commands are transactional, including their audit entries and plan-limit checks;
  - optimistic concurrency on clients, jobs and organizations.
- **Observability:** structured JSON logs with trace id, organization id and user id; no secrets, passwords, cookies, tokens or request bodies in logs. Cross-tenant attempts the guard catches are logged as security events.
- **Browsers:** current and previous versions of Chrome, Edge, Firefox and Safari.
- **Quality gates:** nullable reference types and strict TypeScript; warnings are errors; lint and format checks, the architecture tests and the isolation tests run in CI.

## 10. Acceptance criteria (product level)

1. The coverage tests in 3.4 pass, and fail when a tenant-owned entity without a filter or an unclassified endpoint is added (shown at M03).
2. The cross-tenant matrix passes for every tenant-scoped endpoint: no read, write or existence leak between two organizations.
3. Removing one global query filter or the membership check makes the test suite fail (mutation check recorded at M03 and M12).
4. A visitor signs up, invites a teammate, and the teammate accepts through the link (new account and existing account); both see the organization with the right role.
5. Every row of the permission table in section 2 is covered by an API test (allowed and refused), and the UI hides actions the user cannot perform.
6. Every allowed and refused job transition in 5.4 is covered by a domain test; the API returns `409` with the allowed transitions for refused ones.
7. Plan limits hold under concurrency: with one seat left, two simultaneous invitations produce exactly one success (integration test).
8. Every state-changing endpoint writes an audit entry in the same transaction; a forced failure after the change leaves neither the change nor the entry. `UPDATE` on audit entries is rejected by the database.
9. Every screen passes the 375/768/1280 px, keyboard, console-clean and axe checks in both themes.
10. `docker compose up` from a fresh clone runs the whole stack with demo data; the Playwright journey (sign up → invite → accept → create client → create and complete a job → see the audit entry) passes against it in CI.
11. The demo works with documented demo access, and the README shows it with screenshots and a short recording. The hosting choice is made at M12 (D016).

## 11. Delivery plan

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

Details for each milestone are in `prompts/Mnn-*.md`; status is tracked in `PROGRESS.md`; user stories are in `docs/USER-STORIES.md`.
