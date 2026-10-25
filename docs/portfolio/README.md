# Portfolio kit (Upwork)

Ready-to-paste text for an Upwork portfolio item, and the order of the media. Every number here is from
[`PROGRESS.md`](../../PROGRESS.md) → Measured results. Nothing describes a client, a deployment or an outcome that did
not happen: this is a portfolio project, with fictional data.

Field limits, as given by Upwork's help article "How to enhance your freelancer profile" and quoted in third-party
guides on 2026-10-25: title 70 characters, role 100, description 600, five skills per item. The help page itself
could not be read automatically, so check the counts in the form before you publish.

## Title (59 / 70)

```
Multi-Tenant SaaS Back-Office: .NET 10, Angular, PostgreSQL
```

## Your role (83 / 100)

```
Full-stack developer: specification, architecture, review; built with AI assistance
```

## Project description (529 / 600)

```
A multi-tenant back-office for service businesses, built on a SaaS foundation: organizations, invitations, server-enforced roles, plan limits and an append-only audit log. Tenant isolation is enforced in five layers and proven by 301 automated isolation tests that cover new code automatically. ASP.NET Core (.NET 10), EF Core, PostgreSQL, Angular 22, Docker, GitHub Actions. Jobs list p95 under 18 ms at 10,000 jobs; 0 axe issues on 20 screens. Built with AI assistance (Claude Code); I wrote the spec and reviewed every change.
```

## Skills and deliverables (5)

1. ASP.NET Core
2. Angular
3. PostgreSQL
4. SaaS Development
5. Docker

Keywords for the profile and proposals (not item tags): multi-tenant, tenant isolation, role-based access control,
audit log, .NET 10, C#, Entity Framework Core, TypeScript, NgRx SignalStore, Tailwind CSS, Playwright, xUnit,
Testcontainers, GitHub Actions, CI/CD, accessibility (WCAG), REST API.

## Project link

The GitHub repository, once it is public. There is no hosted demo (D035); the README's one-command run is the way to
try it.

## Media, in this order

Upwork shows the first image as the cover; all files are in [`docs/media/`](../media/).

1. `dashboard-desktop-light.png` (cover): the product at a glance.
2. `jobs-desktop-light.png`: real search, filters and sorting.
3. `job-desktop-light.png`: the workflow, limited to what the role allows, and the history.
4. `audit-desktop-light.png`: the append-only audit log, field by field.
5. `settings-plan-desktop-light.png`: plan limits enforced on the server.
6. `dashboard-mobile-dark.png` and `jobs-mobile-light.png`: responsive, both themes.
7. `walkthrough.webm`: the short walkthrough. Convert it to MP4 if the form does not accept WebM.

Add a text block or an image of the isolation section of the README ("How tenant isolation is enforced and
proven"); it is the strongest part of the project for a technical client.

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
| Making an existing multi-tenant app safe (isolation review, tests that prove it) | README → isolation; `docs/ARCHITECTURE.md`; the isolation suite and mutation checks |
| ASP.NET Core APIs with a clean architecture and real tests | the four projects, architecture tests, integration tests against PostgreSQL |
| Angular front ends that are accessible and responsive | `src/web`, `docs/AUDIT.md` |
| Performance work on PostgreSQL-backed lists | the load script `tools/load-jobs.mjs` and its results; the deadlock found and fixed under load (D033) |
| CI/CD and containerisation | `.github/workflows/ci.yml`, `Dockerfile`, `docker-compose.yml`, `docs/CI.md` |
