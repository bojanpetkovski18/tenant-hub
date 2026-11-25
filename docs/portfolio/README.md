# Portfolio kit (Upwork)

Ready-to-paste text for an Upwork portfolio item, and the order of the media. Every number here is from
[`PROGRESS.md`](../../PROGRESS.md) → Measured results. Nothing describes a client, a deployment or an outcome that did
not happen: this is a portfolio project, with fictional data.

Field limits: title 70 characters, role 100, description 600, five skills per item, as Upwork's Help Center article
"How to enhance your freelancer profile" (support.upwork.com, article 360016144974) gives them, per a search on
2026-11-25 (the page itself could not be read automatically; some third-party guides quote shorter limits for other
fields). Check the counts in the form before you publish. The counts below were measured by a script.

## Title (66 / 70)

```
Multi-Tenant SaaS with a Client Portal: .NET 10, Angular, Postgres
```

## Your role (83 / 100)

```
Full-stack developer: specification, architecture, review; built with AI assistance
```

## Project description (580 / 600)

```
A multi-tenant back-office for service businesses whose own clients sign in too: roles, invitations, plans, an append-only audit log, recurring visits, a dispatch board, a client portal, photo uploads, notifications, API keys and signed webhooks. Isolation in six layers, including PostgreSQL row-level security, proven by 881 automated tests. ASP.NET Core (.NET 10), EF Core, PostgreSQL, Angular 22, Docker, GitHub Actions. Jobs list p95 26 ms at 10,000 jobs; 368/368 accessibility audit checks. Built with AI assistance (Claude Code); I wrote the spec and reviewed every change.
```

## Skills and deliverables (5)

1. ASP.NET Core
2. Angular
3. PostgreSQL
4. SaaS Development
5. Docker

Keywords for the profile and proposals (not item tags): multi-tenant, tenant isolation, row-level security,
role-based access control, client portal, audit log, transactional outbox, webhooks, HMAC, SSRF, file uploads,
.NET 10, C#, Entity Framework Core, TypeScript, NgRx SignalStore, Tailwind CSS, Playwright, xUnit, Testcontainers,
GitHub Actions, CI/CD, accessibility (WCAG), REST API.

## Project link

The GitHub repository. There is no hosted demo (D035, D053); the README's one-command run is the way to try it.

## Media, in this order

Upwork shows the first image as the cover; all files are in [`docs/media/`](../media/).

1. `board-desktop-light.png` (cover): the dispatch board, the week at a glance.
2. `portal-quotes-desktop-light.png`: the client portal, a quote waiting for the client.
3. `job-proof-desktop-light.png`: completion proof, a checklist and a photo.
4. `agreement-desktop-light.png`: recurring work that generates the visits.
5. `integrations-desktop-light.png`: API keys and signed webhooks.
6. `audit-desktop-light.png`: the append-only audit log, field by field.
7. `settings-plan-desktop-light.png`: limits and features by plan, enforced on the server.
8. `board-mobile-light.png` and `portal-visit-mobile-dark.png`: responsive, both themes.
9. `walkthrough.webm`: the 31-second walkthrough. Convert it to MP4 if the form does not accept WebM
   (`ffmpeg -i walkthrough.webm -c:v libx264 -pix_fmt yuv420p -movflags +faststart walkthrough.mp4`).

Add a text block or an image of the isolation section of the README ("How tenant isolation is enforced and
proven"); it is the strongest part of the project for a technical client.

## The v2 story, for a proposal or a case study

- **The client boundary.** A business's clients sign in to a portal with an emailed link. They are a second kind of
  identity with their own cookie and session check, and inside the organization the database adds a second boundary:
  in a portal session, PostgreSQL shows only that client's rows. A matrix test calls every portal endpoint with
  another client's and another organization's ids.
- **Row-level security.** Every tenant-owned table has a policy, and the API connects as a database user that cannot
  bypass it. With the application's tenant filter removed on purpose, the cross-tenant tests still pass, because the
  database refuses the rows; without the database session settings, 817 of 881 isolation tests fail.
- **The outbox.** Every side effect (emails, notifications, webhooks, file clean-up) is written in the same
  transaction as the change, so a failed command sends nothing, and two workers never deliver one message twice.
- **Webhooks.** Events are signed with HMAC-SHA256, retried over about a day, and sent only to public addresses,
  checked when the connection is made rather than when the address is saved.

## AI-assistance statement (for proposals and the profile)

```
TenantHub was built with AI assistance (Claude Code). I wrote the specification and the milestone plan, made the
architecture and product decisions, and reviewed and merged every change through pull requests with automated
checks. The repository shows the process: the specification, the decision log and a verification report for each
milestone.
```

## How the project maps to services

Edit this list to match the services you actually offer; the evidence column points to what a client can inspect.

| Service | Evidence in the repository |
|---|---|
| Building a B2B SaaS MVP (sign-up, organizations, invitations, roles, billing plans) | the whole product; `docs/SPEC.md` |
| Making an existing multi-tenant app safe (isolation review, row-level security, tests that prove it) | README → isolation; `docs/ARCHITECTURE.md`; the isolation suite and mutation checks |
| Customer-facing portals with their own sign-in | the portal, its matrix tests and D040 |
| Integrations: APIs for other systems, webhooks, exports | `docs/INTEGRATIONS.md`; the API-key matrix; the webhook and SSRF tests |
| Background work that is safe on several instances | the outbox and scheduler (D039) and their two-worker tests |
| ASP.NET Core APIs with a clean architecture and real tests | the four projects, architecture tests, integration tests against PostgreSQL |
| Angular front ends that are accessible and responsive | `src/web`, `docs/AUDIT.md` |
| Performance work on PostgreSQL-backed lists | the load script `tools/load-jobs.mjs` and its results; the deadlock found and fixed under load (D033) |
| CI/CD and containerisation | `.github/workflows/ci.yml`, `Dockerfile`, `docker-compose.yml`, `docs/CI.md` |
