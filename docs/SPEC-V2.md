# TenantHub v2: Specification

Status: proposed on 2026-11-16 (M13) for the owner's approval; implementation starts at M14 once it is approved. This document extends `SPEC.md`, which describes v1.0.0 as built. Where v2 changes a v1 rule, it says so and names the `SPEC.md` section. At v2.0.0 (M23) the two are merged into one `SPEC.md` describing the product as built. Decisions are D037–D045 in `DECISIONS.md`.

## 1. Purpose and positioning

v1 proved the organization boundary: the staff of one business never see another business's data, and the tests show it. Most multi-tenant starters stop at that point. A small service business, though, spends much of its day on work that v1 leaves outside the product:
- **Work repeats.** The same client is cleaned every Tuesday or mowed every other Friday, so the office types every visit by hand.
- **Planning is spatial.** The morning question is "who goes where this week", and a filtered list answers it badly.
- **Clients ask.** They phone to ask when someone is coming, to approve a price, or to request extra work.
- **Work needs proof.** After a visit, the business wants a ticked checklist, photos and the client's acknowledgement.
- **Other tools need to know.** Accounting, a website form or a team chat should hear about jobs without anyone copying data.

v2 brings that work into the product. Each feature is chosen so that it also shows a part of SaaS engineering that clients find hard to get right:

| What the customer gets | The mechanic it demonstrates |
|---|---|
| Recurring service agreements that generate the visits | Time-based generation in each organization's time zone, idempotent, with the schedule held in the database |
| A dispatch board: move a visit to a person and a day | Accessible drag and drop with an equivalent keyboard path; optimistic concurrency on every move |
| A client portal: clients see their visits, approve quotes, request work and confirm completed work | A second kind of identity with its own boundary inside the tenant (organization → client), proven by the isolation matrix |
| Completion proof: checklists and photos | Safe file uploads (content sniffing, re-encoding, metadata stripping), storage outside the database, a storage limit per plan |
| Notifications in the app and by email | A transactional outbox: messages are written in the same transaction as the change and delivered by a worker that is safe to run on several instances |
| An API and signed webhooks for other tools | Hashed, scoped API keys; HMAC-signed webhooks with retries; protection against server-side request forgery |
| Features that grow with the plan | Plan entitlements (features on or off by plan) alongside v1's numeric limits |
| None of the above weakens isolation | PostgreSQL row-level security as the sixth layer (D007): the database itself refuses another organization's rows, and another client's rows in a portal session |

The one-line pitch for the portfolio: **your customers can sign in too, and the database proves they see only their own work.**

The job and client domain stays as it is; v2 adds to it. Every v1 acceptance criterion (`SPEC.md` section 10) still holds.

## 2. Identities and permissions

### 2.1 Identities
- **Staff user** (v1): an account with memberships and roles.
- **Client contact** (new): a person at one of an organization's clients, added by staff. Signs in to the portal with a one-time email link and has no password. A contact belongs to exactly one client of one organization; the same email at two clients is two contacts. Contacts never reach the staff app, and a staff account never gets a portal session.
- **API key** (new): a credential that an organization creates for another system. It acts in that organization with an access level chosen at creation (read-only or read-write) and is audited as "API key <name>".
- **System** (new): agreement generation and scheduled work, audited as "System".

### 2.2 New staff permissions
Added to the role map in `SPEC.md` section 2; v1 permissions are unchanged.

| Permission | Owner | Admin | Member | Viewer |
|---|---|---|---|---|
| `agreements.read` (agreements and their visits) | yes | yes | yes | yes |
| `agreements.manage` (create, edit, pause, resume, end; skip a visit) | yes | yes | no | no |
| `portal.manage` (add and remove contacts, send quotes for approval, handle work requests) | yes | yes | no | no |
| `integrations.manage` (API keys and webhooks) | yes | yes | no | no |

- The dispatch board uses the v1 job permissions: everyone with `jobs.read` sees it; only `jobs.manage` moves cards, because only managers change the assignee or the scheduled date (`SPEC.md` 7, `PUT /jobs/{id}`).
- Checklists and photos follow `jobs.work`: whoever may edit a job may tick its items and add photos.
- Audit export is part of `audit.read`; the jobs export is part of `jobs.read`.

### 2.3 What a client contact can do
Fixed, not role-based. A contact sees their own client's visits (upcoming, and the past 12 months), quotes waiting for their decision, and the checklist and photos of completed visits. They can approve or decline a quote, submit and follow work requests, and confirm a completed visit or report a problem with it. They see the business's name and the assignee's display name, and nothing else about the organization.

### 2.4 What an API key can do
- **Read-only:** `clients.read`, `jobs.read`, `agreements.read`.
- **Read-write:** those plus `clients.write`, `jobs.create`, `jobs.manage`.
- Never `members.manage`, `billing.manage`, `org.manage`, `org.delete`, `audit.read`, `portal.manage` or `integrations.manage`, whichever role its creator has.

## 3. Tenancy model additions

### 3.1 Layer 6: row-level security (D038, built in M14)
- Every tenant-owned table has `ENABLE ROW LEVEL SECURITY` and the policy `tenant_isolation`: rows are visible and writable only when `organization_id` equals the session setting `app.organization_id`. **Changed in M14:** no `FORCE ROW LEVEL SECURITY`. The table owner is the migration role and the owner of the one definer function below, and it must stay exempt; the API's user is checked never to own a table, so `FORCE` would protect nothing more (D038).
- **Two database users.** The API connects as its own user (`ConnectionStrings:Database`, `tenant_hub_app` by default), which owns nothing and is not a superuser; migrations run as the owner (`ConnectionStrings:DatabaseAdmin`), which also creates the API's user at startup. In v1 the app connected as the image's `POSTGRES_USER`, a superuser that would bypass every policy. The integration tests use the same split, because tests run as a superuser would prove nothing. Startup refuses an API user that is a superuser, has `BYPASSRLS` or owns a table.
- **No leaks between requests.** **Changed in M14:** the settings are session-level, set by an interceptor before any command whose scope differs from what the connection last received, rather than per transaction, because EF Core reads outside transactions and a scope enters its tenant part-way through. The first command of every scope overwrites all values, and Npgsql also resets the session when a connection returns to its pool. A test runs several scopes on one physical connection to prove it. Without a tenant, a command reads nothing tenant-owned and cannot write: fail closed.
- **Allow-listed bypasses** (D024). The query-filter bypass (`AcrossTenants()`) stays allow-listed per file. In the database, the same files widen reading in two narrow ways, as permissive `SELECT` policies: a user's own memberships in every organization (`app.user_id`: the session, tenant resolution, the organization-creation count, the "already a member" check) and the one invitation whose token the request presents (`app.invitation_token_hash`). The demo reset uses one `SECURITY DEFINER` function that answers, per user, whether anything in any organization refers to the account. Writes are never widened.
- **Coverage.** A test enumerates the tenant-owned tables from the EF Core model and fails if any lacks row-level security or the `tenant_isolation` policy; another pins the list of `SECURITY DEFINER` functions. A new tenant-owned entity is therefore covered by its migration or the build fails.
- **Mutation checks.** With one EF Core query filter removed, the cross-tenant matrix still passes, because the database refuses the rows. With the policy removed as well, it fails. Recorded at M14 and repeated at M22.

### 3.2 The client boundary (portal)
- Portal routes are `/api/portal/...`. A portal request resolves its session to (organization, client, contact). The organization and the client come only from the session, never from the route, body, query string or a header.
- On every portal request the contact must still exist and be active, and the client must be active; otherwise the session ends (`401 portal-session-ended`). This mirrors the v1 membership check (D006): a removed contact loses access on the next request.
- Row-level security adds `app.client_id` in portal sessions. When it is set, client-owned tables (jobs, agreements, work requests, checklist items, attachments, contacts) also require `client_id = app.client_id`.
- Endpoint classification (D025) gains a fourth class, `.PortalScoped()`; an unclassified endpoint still fails the build.
- **Portal matrix:** two organizations, two clients in each, one contact per client. For every portal endpoint, a contact using another client's ids (same organization) or another organization's ids gets `404`; the other data is unchanged (row counts and hashes, as in v1); no foreign data appears in any body.

### 3.3 Machine access
- API keys use the same tenant routes as staff (`/api/orgs/{orgId}/...`) with `Authorization: Bearer <key>`. The key's organization must equal `{orgId}`; otherwise `404`, exactly as for a non-member.
- Endpoints opt in to key access explicitly. Any other endpoint refuses a key with `403 api-key-not-allowed`.
- Bearer requests carry no cookies and skip antiforgery. A request that carries both a cookie session and a key is refused (`400`).
- The cross-tenant matrix gets a key row for every key-enabled endpoint.

## 4. Features

### 4.1 Account recovery and verification (M15)
- **Password reset by email.** The request always answers "if an account exists, we sent a link". The link is single use and valid for 30 minutes; a new password ends every session of the account.
- **Email verification.** New accounts get a verification link. Until they verify, they see a banner and cannot send invitations or portal invitations, so that an unverified account cannot use the product to send mail. Existing accounts and demo accounts are treated as verified. Verification is asked only while email is on: without email no link can be sent and the product sends no mail (D039). Accounts made from an invitation link start verified.
- In demo mode email is off: the reset page explains that the demo has no email.

### 4.2 Recurring service agreements (M16)
- An agreement belongs to a client and holds a visit template (title, description, assignee, priority, quoted amount per visit, estimated duration, checklist from M19) and a recurrence (5.1).
- Visits are ordinary jobs linked to the agreement and its occurrence date, so everything in v1 (workflow, list, audit, limits) applies to them unchanged.
- Visits are generated for a rolling horizon ahead of the organization's today: daily by the scheduler, and at once when an agreement is created or changed.
- Agreements can be paused, resumed and ended. A single visit can be skipped.
- The client page gains an Agreements tab; the jobs list gains an "agreement" filter.

### 4.3 Dispatch board (M17)
- A week view in the organization's time zone, Monday to Sunday. Rows are the active members plus "Unassigned"; cells are days. A side column lists open jobs with no scheduled date.
- Cards show number, client, title, status, priority and estimated duration.
- Managers move a card by dragging it, or by keyboard: select the card, then "Move to…" (person and day) or arrow keys. Each move goes through the same domain rules as the job form.
- Each cell shows the person's planned hours against the organization's daily capacity; an over-capacity cell is marked with an icon and text, never colour alone.
- Below 768 px the board becomes a day-by-day list per person, with the "Move to…" action and no dragging.

### 4.4 Client portal (M18)
- Staff add contacts (name, email) on the client page; the contact receives an invitation email. Contacts sign in at `/portal/{orgSlug}` by entering their email and following the one-time link.
- **Visits:** upcoming and past, with date, status and assignee; a visit page with its details and, once completed, its proof (M19).
- **Quotes:** staff send a job's quote for approval; the contact approves or declines it with an optional comment.
- **Work requests:** the contact describes the work and a preferred date. Staff see a Requests inbox with a count, and convert a request into a job (prefilled) or decline it with a reason; the contact sees the outcome.
- **Plan entitlements** start here (4.8): the portal is a Team and Business feature.

### 4.5 Completion proof (M19)
- **Checklists:** items on a job, copied from the agreement's template or added by hand; each item may be required. Completing a job requires every required item to be done.
- **Photos:** added to a job by whoever may work on it, re-encoded and stripped of metadata on upload, stored outside the database and served only through authorized endpoints.
- **Client sign-off:** in the portal, a completed visit shows its checklist and photos; the contact confirms it ("Looks good") or reports a problem, which notifies staff (M20) and does not reopen the job by itself.

### 4.6 Notifications (M20)
- **In the app:** a bell in the shell with an unread count, a list, mark as read and mark all as read.
- **By email:** each user chooses, per notification type, whether to also receive it by email.
- **Daily digest** of overdue jobs at 07:00 in the organization's time zone.
- Delivered through the outbox (4.9). Nobody is notified about their own action.

### 4.7 API keys, webhooks and exports (M21)
- **API keys** for the key-enabled endpoints (3.3), with the OpenAPI document describing the bearer scheme.
- **Webhooks** for chosen events, signed, retried and logged, with redelivery.
- **Exports:** the audit log as CSV for a date range, and the jobs list as CSV for the current filters.
- API keys and webhooks are Business features (4.8).

### 4.8 Plan entitlements (M18, extended in M19 and M21)
v1's plans gain features and two limits. The v1 limits (`SPEC.md` 5.5) are unchanged.

| | Free | Team | Business |
|---|---|---|---|
| Client portal | no | yes | yes |
| API keys and webhooks | no | no | yes |
| Active agreements | 3 | 200 | unlimited |
| File storage | 100 MB | 5 GB | 50 GB |

- An endpoint for a feature the plan lacks answers `403 plan-feature-unavailable` with `feature` and the cheapest plan that has it. The UI hides such actions from non-Owners, and shows Owners which plan includes them.
- **Downgrade** (changes `SPEC.md` 5.5): still refused while numeric usage exceeds the target plan. Features that would turn off are listed in the response; the Owner repeats the request with `confirmFeatureLoss: true`. Afterwards portal sign-in and webhook deliveries stop; the data is kept, and upgrading again restores them.
- `GET /api/orgs/{orgId}` adds `features` and the new limits, for this plan and every plan.

### 4.9 Background work: the outbox and the scheduler (M15)
- **Outbox.** Messages (emails, notifications, webhook events, file clean-up) are written in the same `SaveChanges` as the change and its audit entries, so a rolled-back command sends nothing.
- **Dispatcher.** A worker claims batches with `FOR UPDATE SKIP LOCKED`, so several API instances can run it without sending a message twice. Failures retry with backoff; a message that keeps failing is marked dead and logged. Handlers are idempotent by message id.
- **Secrets in messages.** Messages that carry a secret (a reset or sign-in link) are encrypted with ASP.NET Core Data Protection and deleted once sent.
- **Invitations** keep their v1 contract: they are sent during the request, so the inviter sees `sent`, `link` or `failed` at once (`SPEC.md` 4.1, D011). Everything new uses the outbox.
- **Scheduler.** Recurring work (agreement generation, the daily digest, retention clean-up) is held in a `scheduled_tasks` table with a due time and a lease, so a restart neither skips nor repeats a run, two instances never run the same task at once, and a host that sleeps catches up when it wakes. This is the lesson of the v1 demo reset (D032) generalized.

### 4.10 Still later
Custom roles; real billing with Stripe; a platform-admin console; SSO; a Kanban board (the dispatch board covers planning); per-member capacity; route planning and maps; SMS; native apps. Invoicing stays out of scope (`SPEC.md` 4.3).

## 5. Business rules

### 5.1 Agreements and visits
- **Recurrence:** weekly on chosen weekdays every N weeks (N 1–8); monthly on day D (1–28, or "last day") every N months (N 1–12); monthly on the nth weekday (first to fourth, or last) every N months. Dates only, in the organization's time zone.
- **Start and end:** a start date (not before the organization's today when created); no end, an end date, or an end after a number of visits (1–500).
- **Horizon:** visits exist for occurrences up to 60 days after the organization's today. Generation is idempotent: `(agreement_id, occurrence_on)` is unique among the agreement's jobs.
- **A generated visit** is created with the template's fields, `scheduled_on` and `due_on` equal to the occurrence date, and status Scheduled when the template has an assignee, otherwise New. It gets its job number when it is generated.
- **Editing an agreement** applies to future visits that are New or Scheduled and have not been changed individually; those are updated or regenerated. Visits changed by hand are kept, and the agreement page lists them.
- **Skipping** a visit cancels it with the reason "Skipped"; the cancelled job keeps its occurrence date, so it is not generated again.
- **Pause** stops generation; **resume** generates from the organization's today onward, never in the past. **End** cancels future visits that are New or Scheduled (reason "Agreement ended") and keeps the rest.
- The client must be active to create or resume an agreement. Archiving a client pauses its agreements.
- **Limits:** generated visits count as open jobs (`SPEC.md` 5.5). Generation stops at the limit and the agreement shows "generation paused: plan limit reached" (with a notification from M20). Active agreements are limited per plan (4.8).
- The template's assignee must be an active member; if they are removed, future visits become unassigned (as in v1 5.2) and the template's assignee is cleared.

### 5.2 Dispatch
- Moving a card sets the assignee and `scheduled_on` with the job's concurrency token. A newer version gives `409 concurrency-conflict`: the card returns to its place and the board reloads.
- Moving a job to a day before its due date is allowed; to a day after its due date asks for confirmation and moves the due date too (the v1 rule `due_on ≥ scheduled_on` stays).
- A New job moved to a person and a day becomes Scheduled (the v1 transition, `SPEC.md` 5.4). A move to "Unassigned" or "Unscheduled" follows the job form's rules: where the form would refuse (for example, a Scheduled job without an assignee), the board refuses with the same reason. Completed and Cancelled jobs are shown but cannot be moved.
- Jobs gain an optional `estimated_minutes` (15–1440). Organizations gain a daily capacity (60–1440 minutes, default 480), edited in Settings → General.

### 5.3 Contacts and portal sessions
- A client has at most 5 active contacts. Email is unique among a client's active contacts (case-insensitive). Contacts do not take seats.
- **Sign-in link:** 32 random bytes, stored as a SHA-256 hash, valid for 15 minutes, single use. Requesting a link always gives the same answer, whether or not the email is a contact. Rate-limited per address and per email.
- **Session:** a cookie of its own (separate name and scheme from the staff cookie, `HttpOnly`, `Secure`, `SameSite=Strict`), valid for 7 days, with antiforgery on unsafe requests. Removing the contact, archiving or deleting the client, or losing the portal entitlement ends it on the next request.
- Deleting a client deletes its contacts. A client with jobs cannot be deleted (v1 5.6), so contacts of such clients are removed one by one.

### 5.4 Quotes
- Staff with `portal.manage` may send a quote for a job that is New or Scheduled, has a quoted amount, and whose client has at least one active contact. The job's approval becomes Pending.
- **While Pending or Declined, the job cannot move to In progress** (`409 quote-not-approved`). This adds a condition to the v1 transition table (`SPEC.md` 5.4).
- The contact approves or declines once, with an optional comment (up to 1,000 characters). The approval records the amount approved, who and when.
- Changing the quoted amount of an approved job resets the approval to Not requested; the audit log shows both events. Jobs that were never sent behave exactly as in v1.

### 5.5 Work requests
- A contact may have at most 10 open requests. Title 3–120 characters, description up to 2,000, preferred date optional (not in the past).
- Statuses: Open → Converted (to a job; the request links to it) or Declined (with a reason, 1–500 characters, shown to the contact). No other transitions.
- Converting is limit-checked like creating a job.

### 5.6 Checklists and photos
- **Checklists:** at most 30 items per job, text 1–200 characters. Ticking or unticking records who and when. A job moves to Completed only when every required item is done (`409 checklist-incomplete` with the open items). Items can be edited while the job is open; a completed job's checklist is read-only until it is reopened.
- **Photos:** at most 10 per job and 10 MB per file. Accepted by content (JPEG, PNG or WebP magic bytes), whatever the file name or declared type. Re-encoded, with every metadata block (EXIF, GPS, XMP, ICC beyond sRGB) removed, the long edge limited to 2,560 px, plus a thumbnail. Stored under a random key; served with `Content-Disposition`, `X-Content-Type-Options: nosniff` and a private cache.
- **Storage limit:** the stored bytes (after re-encoding) per organization, checked under the organization lock like the other limits.
- Files of a deleted job or organization are removed after the commit, through the outbox; a weekly sweep removes orphans.
- **Client feedback:** once per completed visit, either "confirmed" or "problem reported" (text 1–2,000 characters). Reopening the job clears it.

### 5.7 Notifications
| Type | Who receives it | Email by default |
|---|---|---|
| Assigned to you | the new assignee | on |
| Your visit was rescheduled or cancelled | the assignee | off |
| Quote approved or declined | managers, and whoever sent the quote | on |
| New work request | managers | on |
| Client reported a problem | managers and the assignee | on |
| Agreement generation paused by a plan limit | Owners and Admins | on |
| Daily digest of overdue jobs (07:00, organization time zone; skipped when there are none) | each assignee (their jobs) and managers (a count) | on |

- Notifications are tenant-owned and addressed to one user in one organization; email preferences are per user. Kept for 90 days.
- The unread count refreshes on navigation and every 60 seconds while the tab is visible; there are no websockets.

### 5.8 API keys and webhooks
- **Keys:** the full key is shown once; only a SHA-256 hash and a short visible prefix are stored. Optional expiry (up to one year). Revocation is immediate. Rate limit per key. The last-used time is recorded at most once a minute.
- **Webhook endpoints:** at most 5 per organization; `https` only; chosen events from a fixed catalog (`job.created`, `job.status_changed`, `job.assigned`, `quote.approved`, `quote.declined`, `request.created`, `visit.confirmed`, `visit.problem_reported`).
- **Signing:** `TenantHub-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<body>")>`. The secret is shown once, stored encrypted (it must be readable to sign), and can be rotated with both secrets valid for 24 hours.
- **Payload:** `{ id, type, occurredAt, organizationId, data }`; `id` is stable across retries, so receivers can deduplicate.
- **Delivery:** a 10-second timeout and no redirects. Any 2xx is success. Retries back off over about a day (8 attempts). After 3 days of failures the endpoint is disabled and Owners are notified. The delivery log keeps status code, duration and error class for 30 days, but no response bodies. Managers can redeliver.
- **Server-side request forgery:** the host is resolved and refused if any address is private, loopback, link-local, unique-local, multicast or otherwise non-public; the connection goes to the address that was checked (no second lookup). A development-only setting allows a named local receiver.
- **Exports:** cells starting with `=`, `+`, `-`, `@`, a tab or a carriage return are neutralized. Exports stream from the database and never load the whole result.

### 5.9 Audit additions
- Audit entries gain `actor_kind` (`user`, `contact`, `api_key`, `system`) and `actor_ref` (the contact or key id). `actor_user_id` is already nullable; existing rows become `user`.
- Every new state-changing endpoint, portal ones included, writes an entry in the same transaction (v1 criterion 8). Generated visits are audited as "System (agreement <title>)".

## 6. Screens

**Public:** forgot password, reset password, verify email.

**Staff, inside an organization:**
- Agreements: list, detail (upcoming visits, visits changed by hand, generation state) and form, with a live preview of the next occurrence dates.
- Board.
- Requests inbox (`portal.manage`).
- Client detail gains Agreements and Contacts tabs.
- Job detail gains the checklist, photos, the quote panel and client feedback.
- Notifications: the bell and its panel; Account → Notifications for email preferences.
- Settings → General gains daily capacity; Settings → Plan shows features; Settings → Integrations has API keys and webhooks with their delivery log.
- Audit log and jobs list gain Export.

**Portal** (`/portal/{orgSlug}/...`, its own small layout with the organization's name and initials, the same design tokens and both themes): sign in, check your email, visits, visit detail, quotes, requests (list and new), and "signed out" or "link expired".

## 7. API additions
Conventions as in `SPEC.md` 7: problem details with `traceId` and stable codes; lists with `page`, `pageSize`, `items`, `total`.

**Public**

| Method | Path | Notes |
|---|---|---|
| POST | `/api/auth/password-reset/request` | `{ email }`; always `202`; rate-limited |
| POST | `/api/auth/password-reset/confirm` | `{ token, password }`; ends every session |
| POST | `/api/auth/verify-email` | `{ token }` |
| POST | `/api/portal/sign-in/request` | `{ orgSlug, email }`; always `202`; rate-limited |
| POST | `/api/portal/sign-in/confirm` | `{ token }`; starts a portal session |

**User-scoped**

| Method | Path | Notes |
|---|---|---|
| POST | `/api/me/verification-email` | sends the link again; rate-limited |
| GET, PUT | `/api/me/notification-preferences` | email on or off per type |

**Tenant-scoped** (`/api/orgs/{orgId}`; "key" marks API-key access)

| Method | Path | Permission |
|---|---|---|
| GET | `/agreements` | `agreements.read`, key: client, status, search, paging |
| POST | `/agreements` | `agreements.manage` |
| GET | `/agreements/{id}` | `agreements.read`, key: with the next occurrences and visits changed by hand |
| PUT | `/agreements/{id}` | `agreements.manage`; concurrency token |
| POST | `/agreements/{id}/pause`, `/resume`, `/end` | `agreements.manage` |
| POST | `/agreements/preview` | `agreements.read`: the next occurrences of a recurrence, without saving |
| POST | `/jobs/{id}/skip` | `agreements.manage`: generated visits only |
| GET | `/board` | `jobs.read`: `week` (a Monday in the organization's zone), `clientId`, `status[]` |
| POST | `/jobs/{id}/quote` | `portal.manage`: send for approval |
| GET, POST | `/jobs/{id}/checklist` | `jobs.read` / `jobs.work` (domain rules as for edits) |
| PUT, DELETE | `/jobs/{id}/checklist/{itemId}` | `jobs.work` |
| GET, POST | `/jobs/{id}/photos` | `jobs.read` / `jobs.work`; upload is `multipart/form-data` |
| GET | `/jobs/{id}/photos/{photoId}`, `/thumbnail` | `jobs.read` |
| DELETE | `/jobs/{id}/photos/{photoId}` | `jobs.work` |
| GET, POST | `/clients/{id}/contacts` | `clients.read` / `portal.manage` |
| DELETE | `/clients/{id}/contacts/{contactId}` | `portal.manage` |
| GET | `/requests` | `portal.manage`: status, client, paging |
| POST | `/requests/{id}/convert`, `/decline` | `portal.manage` (`jobs.create` also required to convert) |
| GET | `/notifications` | `org.read`: the caller's, newest first, with the unread count |
| POST | `/notifications/read` | `org.read`: `{ ids }` or `{ all: true }` |
| GET, POST | `/api-keys` | `integrations.manage` |
| DELETE | `/api-keys/{id}` | `integrations.manage` |
| GET, POST | `/webhooks` | `integrations.manage` |
| PUT, DELETE | `/webhooks/{id}` | `integrations.manage` |
| POST | `/webhooks/{id}/rotate-secret` | `integrations.manage` |
| GET | `/webhooks/{id}/deliveries` | `integrations.manage` |
| POST | `/webhooks/{id}/deliveries/{deliveryId}/redeliver` | `integrations.manage` |
| GET | `/audit/export` | `audit.read`: `from`, `to` (required, at most 366 days) |
| GET | `/jobs/export` | `jobs.read`, key: the list filters |

Key access is also enabled on the v1 endpoints `GET /clients`, `GET /clients/{id}`, `POST /clients`, `PUT /clients/{id}`, `GET /jobs`, `GET /jobs/{id}`, `POST /jobs`, `PUT /jobs/{id}` and `POST /jobs/{id}/transitions`.

**Portal-scoped** (`/api/portal`, portal session required)

| Method | Path | Notes |
|---|---|---|
| GET | `/session` | contact, client and organization names, features; public like the staff session (no session → `contact: null`) |
| POST | `/sign-out` | |
| GET | `/visits` | `when` (`upcoming` default, `past`), paging |
| GET | `/visits/{id}` | with checklist, photos and feedback once completed |
| GET | `/visits/{id}/photos/{photoId}`, `/thumbnail` | |
| POST | `/visits/{id}/confirm`, `/report-problem` | completed visits only, once |
| GET | `/quotes` | pending, and decided in the last 90 days |
| POST | `/quotes/{jobId}/approve`, `/decline` | `{ comment? }` |
| GET, POST | `/requests` | the contact's client's requests |

## 8. Data model additions
Tenant-owned tables get a query filter, an index starting with `organization_id`, a row-level security policy (3.1), and composite foreign keys for references inside the tenant, as in v1.

- **agreements** (tenant-owned): `id`, `organization_id`, `client_id` (composite foreign key), `title`, `description`, `assignee_user_id` (composite foreign key to memberships), `priority`, `quoted_amount` (`numeric(12,2)`), `estimated_minutes`, `checklist_template` (`jsonb`), `recurrence` (`jsonb`, validated by the domain), `starts_on`, `ends_on`, `max_visits`, `status` (active, paused, ended), `paused_reason`, `generated_through`, `created_at`, `updated_at`, `xmin`.
- **jobs** gain: `agreement_id` and `occurrence_on` (composite foreign key; unique `(organization_id, agreement_id, occurrence_on)` where `agreement_id` is set), `changed_by_hand`, `estimated_minutes`, `approval_status`, `approval_sent_at`, `approval_sent_by_user_id`, `approved_amount`, `approval_decided_at`, `approval_decided_by_contact_id`, `approval_comment`, `client_feedback`, `client_feedback_text`, `client_feedback_at`.
- **organizations** gain `daily_capacity_minutes` and `storage_bytes_used`.
- **client_contacts** (tenant-owned): `id`, `organization_id`, `client_id` (composite foreign key), `name`, `email`, `normalized_email`, `created_at`, `created_by_user_id`, `removed_at`, `last_sign_in_at`. Partial unique index on `(organization_id, client_id, normalized_email)` where active.
- **portal_sign_in_tokens** (tenant-owned): `token_hash` (unique), `organization_id`, `contact_id`, `expires_at`, `used_at`.
- **work_requests** (tenant-owned): `id`, `organization_id`, `client_id`, `contact_id`, `title`, `description`, `preferred_on`, `status`, `job_id`, `decline_reason`, `created_at`, `decided_at`, `decided_by_user_id`.
- **checklist_items** (tenant-owned): `id`, `organization_id`, `job_id` (composite foreign key), `position`, `text`, `required`, `done_at`, `done_by_user_id`.
- **attachments** (tenant-owned): `id`, `organization_id`, `job_id`, `storage_key`, `content_type`, `bytes`, `width`, `height`, `thumbnail_key`, `created_at`, `created_by_user_id`.
- **notifications** (tenant-owned): `id`, `organization_id`, `user_id`, `type`, `data` (`jsonb`), `created_at`, `read_at`. Index `(organization_id, user_id, created_at desc)`.
- **notification_preferences** (not tenant-owned, per user): `user_id`, `type`, `email`.
- **api_keys** (tenant-owned): `id`, `organization_id`, `name`, `prefix`, `key_hash` (unique), `access`, `created_at`, `created_by_user_id`, `expires_at`, `last_used_at`, `revoked_at`.
- **webhook_endpoints** (tenant-owned): `id`, `organization_id`, `url`, `events` (`text[]`), `secret_encrypted`, `previous_secret_encrypted`, `previous_secret_expires_at`, `created_at`, `disabled_at`, `failing_since`, `xmin`.
- **webhook_deliveries** (tenant-owned): `id`, `organization_id`, `endpoint_id`, `event_id`, `event_type`, `attempt`, `status_code`, `duration_ms`, `error`, `created_at`, `next_attempt_at`, `succeeded_at`.
- **outbox_messages** (not tenant-owned, infrastructure): `id`, `organization_id` (nullable: account emails have none), `type`, `payload` (`jsonb`, encrypted when it carries a secret), `created_at`, `available_at`, `attempts`, `last_error`, `processed_at`, `dead_at`.
- **scheduled_tasks** (not tenant-owned, infrastructure): `name`, `organization_id` (nullable), `due_at`, `lease_until`, `last_run_at`, `last_error`; unique `(name, organization_id)` with nulls not distinct (a primary key cannot hold the null of an application-wide task, so the key is a surrogate `id`; D039).
- **audit_entries** gain `actor_kind` and `actor_ref` (5.9).

**Allow-list entries this specification asks the owner to approve** (CLAUDE.md: never added without approval):
- `outbox_messages`, `scheduled_tasks` and `notification_preferences` as not tenant-owned. The first two are infrastructure that only the dispatcher and scheduler read; the third belongs to a user, not an organization.
- Tenant bypasses for the outbox dispatcher and the scheduler (they work across organizations by design, then act inside one tenant context per message or task), for portal sign-in (it finds the contact by organization slug and email before any session exists), and for API-key authentication (it finds the key by hash before the tenant is known).

## 9. Non-functional additions
- **Isolation:** sections 3.1–3.3; any failure blocks merging, as in v1.
- **Background work:** idempotent and safe on several instances. A test runs two dispatchers and two schedulers against one database and checks that nothing is skipped or done twice.
- **Uploads:** 5.6; parsing runs on untrusted input with size and dimension limits checked before decoding the full image.
- **Performance** (measured, recorded in `PROGRESS.md`):
  - jobs list p95 with row-level security on, compared with v1's 17.2–17.9 ms on the same machine and tool; target under 200 ms, with the overhead reported;
  - board week p95 under 200 ms with 10,000 jobs across 20 organizations;
  - generating the 60-day horizon for 200 agreements in one organization: time recorded;
  - initial JavaScript stays under 300 kB transferred; the portal is its own lazy-loaded area.
- **Accessibility:** WCAG 2.2 AA as in v1, including 2.5.7 (Dragging Movements): every drag has a single-pointer and keyboard alternative, and moves are announced to screen readers.
- **Privacy:** contacts' personal data is deleted with the client or the organization; notification and delivery logs keep no personal data beyond names already in the organization.

## 10. Acceptance criteria (v2)
v1 criteria 1–11 (`SPEC.md` 10) continue to hold. In addition:

12. **Row-level security:** every tenant-owned table has an enabled, forced policy (coverage test). With one EF Core query filter removed, the cross-tenant matrix still passes; with the policy removed too, it fails. A pooled connection reused across two tenants never shows the first tenant's rows. Recorded at M14 and M22.
13. **Portal isolation:** the portal matrix passes for every portal endpoint; removing a contact ends their access on the next request.
14. **Agreements:** generation produces exactly the expected visits for each recurrence type over a year, including month ends, the last day of February in a leap year, and a non-UTC zone. Running generation twice, or on two workers at once, creates no duplicates. A plan limit stops generation at the limit.
15. **Board:** every move can be made by keyboard alone and is announced; a concurrent move gives a conflict and no lost update.
16. **Outbox:** a rolled-back command writes no message; a failed delivery is retried; two dispatchers deliver each message exactly once (integration test).
17. **Uploads:** a file whose content is not an allowed image is refused, whatever its name or declared type; stored images carry no EXIF or GPS data (the test reads the stored bytes); another organization's or another client's photo is `404`.
18. **Webhooks:** signatures verify with an independent implementation of the documented algorithm; private, loopback and link-local targets are refused, including hosts that resolve to them and redirects to them; retries follow the schedule.
19. **Entitlements:** each feature is refused by the API on plans without it (`403 plan-feature-unavailable`), hidden in the UI, and listed in the downgrade confirmation.
20. **Second journey:** a second Playwright journey passes against the production image in CI: create an agreement → its visits appear on the board → move one by keyboard → the client contact signs in through the emailed link and approves the quote → the assignee completes the checklist and adds a photo → the contact confirms the visit → a signed webhook reaches a test receiver.
21. **Audit of new screens:** every new screen, staff and portal, passes the M10 audit (375/768/1280 px, keyboard, both themes, axe, clean console, every overlay opened).

## 11. Delivery plan
Phases group milestones; each milestone is one pull request. Briefs are in `prompts/M14-*.md` to `prompts/M23-*.md`.

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
