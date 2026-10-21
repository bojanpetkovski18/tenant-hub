# Decisions

Numbered, never renumbered. Superseded decisions stay with a note. "Pending" means the decision is proposed and gets confirmed (or changed) at the milestone named.

---

## D001: Scope: a B2B back-office whose real subject is the SaaS foundation
- **Date:** 2026-10-21
- **Context:** The portfolio already has an analytics dashboard (Numera) and an e-commerce store (Fitness Merchant). Freelance clients building B2B products repeatedly need the same foundation: organizations, invitations, roles, tenant isolation, plan limits and an audit trail. The owner's CV covers enterprise business applications, DDD and CQRS.
- **Decision:** A multi-tenant back-office for small service businesses (clients and jobs). The domain is deliberately ordinary; the multi-tenant foundation and its proofs are the product.
- **Why:** It shows reusable, sellable work ("I can add organizations, roles and isolation to your SaaS") and gives DDD a real reason to exist (job workflow, membership rules, plan policy).
- **Alternatives:** A generic task manager (crowded, shallow rules); an HR or workforce tool (too close to the owner's employer domain, see D002).
- **Trade-offs:** Clients and jobs alone are less eye-catching than a dashboard; the dashboard (M09) and the polished UI compensate.

## D002: Fictional data only; nothing from the owner's employers
- **Date:** 2026-10-21
- **Decision:** Organizations, people and data are invented. Emails use the reserved `.example` domain; phone numbers are obviously fake. No employer code, data, names or business rules. The domain (service jobs) is intentionally unlike the owner's professional workforce and ERP work.
- **Why:** Legal safety for a public repository, and the project's data rules.

## D003: Backend: .NET 10 LTS, Minimal APIs, four projects with enforced boundaries
- **Date:** 2026-10-21
- **Context:** Numera and Fitness Merchant use one API project with feature folders. TenantHub has more domain rules (job state machine, last-owner rule, role grants, plan policy) and is pitched at enterprise-minded clients.
- **Decision:**
  - `TenantHub.Domain`: aggregates, value objects, domain events and rules; no dependencies on EF Core, ASP.NET Core or other projects.
  - `TenantHub.Application`: one handler per command or query (light CQRS), ports such as `ITenantContext` and `IAppDbContext`. It references the EF Core abstractions package for `DbSet`/LINQ, but not Npgsql.
  - `TenantHub.Infrastructure`: `DbContext`, configurations, migrations, Identity, email, the SaveChanges guard and the audit writer.
  - `TenantHub.Api`: endpoints, authentication, the tenant middleware and the composition root. Endpoints only bind, authorize and call a handler.
  - No MediatR, AutoMapper or generic repository; built-in validation.
  - Architecture tests enforce the dependency direction and the `IgnoreQueryFilters` allow-list.
- **Why:** The boundaries are checked by the compiler and by tests rather than by convention. Domain rules become plain unit tests, and the structure is easy to explain in interviews.
- **Alternatives:** One project with feature folders (what the other two projects do; it would make this project look like them); full Clean Architecture with repositories per aggregate (more ceremony than the domain needs; EF Core already is the unit of work).
- **Trade-offs:** More projects and some mapping between domain and API contracts. Application depending on EF Core abstractions is a deliberate compromise to avoid a repository layer.
- **Verified:** .NET 10 is LTS, supported until 2028-11-14; latest runtime 10.0.13 and SDK 10.0.402, both released 2026-10-13 (Microsoft release metadata, checked 2026-10-21). The SDK and runtime are installed locally.
- **M01 (2026-10-21):** the four projects exist with exactly the references above; `global.json` pins SDK 10.0.402. Pinned packages are in D019.

## D004: PostgreSQL with EF Core (Npgsql)
- **Date:** 2026-10-21
- **Decision:** PostgreSQL 18.6 (`postgres:18.6-alpine`, pushed 2026-09-21, the newest 18.x on 2026-10-21; pinned at M01).
- **Why:** Composite foreign keys plus partial unique indexes model the tenant rules directly (SPEC 8.1); `xmin` is a free concurrency token; `SELECT … FOR UPDATE` gives the plan-limit lock; row-level security is available for V2; free and cheap hosting options exist; the owner already runs it in the other projects.
- **Alternatives:** SQL Server (closer to some enterprise clients, but a heavier container and fewer free hosts).
- **Trade-offs:** None significant for this project.

## D005: Shared database and schema with an `organization_id` column
- **Date:** 2026-10-21
- **Decision:** Every tenant-owned table carries `organization_id`; isolation is enforced by the layers in SPEC 3.3.
- **Why:** This is the most common model for small and mid-size SaaS products and the one clients usually ask freelancers to implement or fix. It is cheap to host, keeps migrations simple (one schema), and makes cross-tenant reporting possible later.
- **Alternatives:**
  - schema per tenant: stronger separation, but migrations run per schema and EF Core model caching per schema adds complexity;
  - database per tenant: strongest separation and per-tenant backup, but provisioning, connection management and cost do not fit a portfolio demo.
- **Trade-offs:** A missing filter leaks data; that risk is why isolation is enforced in depth and covered by coverage tests (SPEC 3.4).

## D006: The tenant comes from the route and an active membership, checked on every request
- **Date:** 2026-10-21
- **Decision:** Tenant-scoped endpoints are `/api/orgs/{orgId}/...`. Middleware loads the caller's active membership for that organization on every request; none → `404`.
- **Why:**
  - Several browser tabs can work in different organizations at the same time.
  - A removed member loses access on the next request, not when a cookie expires.
  - The tenant is explicit in every request, which makes the cross-tenant tests simple to write.
- **Alternatives:**
  - active organization as a cookie claim (breaks multiple tabs; stale after removal);
  - an `X-Organization` header (the same check is needed; less visible in logs and tests);
  - subdomain per tenant (needs wildcard DNS and certificates; not worth it for a demo).
- **Trade-offs:** One extra indexed lookup per request (primary-key read). Measured at M10.

## D007: Defence in depth; PostgreSQL row-level security deferred to V2
- **Date:** 2026-10-21
- **Decision:** Membership check, EF Core global query filters, a SaveChanges guard, composite foreign keys and an `IgnoreQueryFilters` allow-list (SPEC 3.3). Row-level security is V2.
- **Why:** Each layer catches a different mistake: a forgotten permission check, a forgotten `Where`, a write to another tenant's entity loaded by mistake, and a cross-tenant reference written by any code path. Row-level security would need a per-request session variable on pooled connections, which adds risk (a leaked setting on a reused connection) and is better done once the rest is stable.
- **Trade-offs:** The application user can still bypass the filters with raw SQL; the allow-list test and code review cover this until RLS is added.

## D008: Fixed roles mapped to permissions in code; resource rules in handlers
- **Date:** 2026-10-21
- **Decision:** Four roles (Owner, Admin, Member, Viewer) and a static role → permission map (SPEC 2). Endpoints declare a permission; rules that depend on the resource (assigned jobs, last Owner, who may grant which role) are checked in handlers or domain services. `GET /api/me` returns each membership's permissions so the UI does not duplicate the map.
- **Why:** Permissions rather than role names in checks make custom roles (V2) a data change, not a code change. Resource rules belong next to the data they need.
- **Alternatives:** ASP.NET Core role checks (`[Authorize(Roles = …)]`): global roles only, so they do not fit per-organization roles.
- **Trade-offs:** The map is not editable without a deploy (accepted for the MVP).

## D009: Cookie authentication with ASP.NET Core Identity
- **Date:** 2026-10-21
- **Decision:** Identity with Guid keys; an `HttpOnly; Secure; SameSite=Strict` cookie; antiforgery token sent as `X-XSRF-TOKEN` using Angular's built-in XSRF support; thin custom endpoints instead of `MapIdentityApi`.
- **Why:** The SPA and API share one origin in production (the API serves the SPA), so a cookie is the simplest secure option and keeps tokens out of JavaScript.
- **Alternatives:** JWT in browser storage (exposed to XSS; needs refresh-token handling); an external identity provider (cost and setup for a demo).

## D010: A user can belong to several organizations
- **Date:** 2026-10-21
- **Decision:** Users and organizations are many-to-many through memberships, each with its own role.
- **Why:** This is how real B2B products work (consultants, accountants, agencies) and it makes the isolation story stronger: the same person has different rights in different tenants, and the tests prove it.
- **Trade-offs:** An organization switcher and slug routing in the SPA; a user-scoped area outside any organization.

## D011: Invitations with hashed single-use tokens; email through SMTP with a copy-link fallback (pending M05)
- **Date:** 2026-10-21
- **Decision:** 32-byte random tokens, stored as SHA-256 hashes, valid for 7 days and single use. Email goes through SMTP: Mailpit in development and Compose. When email is disabled (the public demo), the API returns the link and the UI shows a copy dialog.
- **Why:** A database leak does not expose working links; the demo works without sending email to real people.
- **To confirm at M05:** the Mailpit version and the email library (`System.Net.Mail` versus MailKit).

## D012: Audit entries from domain events, in the same transaction; append-only (pending M04)
- **Date:** 2026-10-21
- **Decision:** Aggregates raise domain events; a SaveChanges step turns them into audit entries before the transaction commits. Entries store an actor-name snapshot and changed fields as `jsonb`. A database trigger rejects `UPDATE` on `audit_entries`.
- **Why:** The audit trail cannot drift from the data (same transaction) and is meaningful ("Job JOB-0042 moved from Scheduled to In progress") rather than a generic row diff. A test checks that every state-changing endpoint produces an entry, so a forgotten event is caught.
- **Alternatives:** A generic change-tracking interceptor (complete but noisy, hard to read, and logs fields nobody cares about); writing entries explicitly in each handler (easy to forget).
- **To confirm at M04:** how changed fields are captured.

## D013: Plan limits checked in the command under a lock on the organization row
- **Date:** 2026-10-21
- **Decision:** Commands that consume a limit lock the organization row (`SELECT … FOR UPDATE`) inside their transaction, count the usage and fail with `403` and problem type `plan-limit-reached` when over the limit. Plan changes are simulated ("demo billing").
- **Why:** Counting without the lock lets two concurrent requests both pass; the lock serializes only the commands of one organization. `403` with a specific problem type is unambiguous for the client; `402 Payment Required` is reserved by the HTTP specification and poorly supported by clients.
- **Trade-offs:** Writes that consume a limit are serialized per organization; acceptable at this scale.

## D014: Frontend: Angular 22, signals, NgRx SignalStore, Tailwind CSS v4 and Angular CDK (pending M01)
- **Date:** 2026-10-21
- **Decision:** Standalone, zoneless Angular with signals. NgRx SignalStore (`@ngrx/signals`) for the session and organization context and for the list pages (filters, paging and sorting kept in sync with the URL). Tailwind CSS v4 with design tokens, and Angular CDK for overlays, menus, dialogs and focus management. No component library.
- **Why:** NgRx is on the owner's CV; SignalStore is its current, lighter form and fits zoneless Angular. Plain signals would also work for small pages, so SignalStore is used where state is shared or has several moving parts, not everywhere.
- **Verified:** `@angular/core` 22.2.3 and `@ngrx/signals` 22.0.1 (peer `@angular/core ^22.0.0`) are the latest on npm (checked 2026-10-21).
- **Confirmed at M01 (2026-10-21):** Angular 22.2.1 (2026-09-30), `@ngrx/signals` 22.0.1 (2026-09-10), Tailwind CSS 4.3.3, Vitest 5.0.3 as the Angular unit-test runner, TypeScript 6.0.3 (Angular 22.2 requires `>=6.0 <6.1`; TypeScript 7 is not supported yet). Exact versions and exceptions in D019. The first SignalStore is `ThemeStore`. Breakpoints use `matchMedia` directly rather than the CDK `BreakpointObserver`, whose media matcher inserts a `<style>` element that a strict CSP blocks (found in Fitness Merchant, K1 there).
- **Alternatives:** Classic `@ngrx/store` with effects (more boilerplate than these pages need); Angular Material (looks like a template; harder to give a distinct identity).

## D015: Test strategy with isolation tests as the headline
- **Date:** 2026-10-21
- **Decision:**
  - xUnit v3 unit tests for the domain (state machine, membership rules, plan policy) and the application handlers;
  - integration tests with `WebApplicationFactory` and Testcontainers PostgreSQL, including the isolation coverage tests and the cross-tenant matrix (SPEC 3.4);
  - architecture tests for project dependencies and the `IgnoreQueryFilters` allow-list;
  - Angular component tests with Vitest;
  - one Playwright end-to-end journey (SPEC 10, criterion 10).
- **Why:** Isolation and permissions are the claims this project makes, so they are tested systematically: enumerated from the model and the route table, not written by hand per endpoint.
- **Architecture tests, M01 (2026-10-21):** plain xUnit with reflection and project-file parsing, no library. Each layer rule is checked against the `.csproj` (catches a reference before any type is used) and the compiled assembly (catches framework references); a third test fails when a new project under `src/api` has no rule. Shown failing for Domain and Application with an ASP.NET Core reference and for an unruled project; Infrastructure → Api is already refused by MSBuild as a circular reference (MSB4006). NetArchTest was not chosen (last release 2021); ArchUnitNET (TngTech, 0.13.4, 2026-08-20) is reconsidered at M03 if the `IgnoreQueryFilters` allow-list needs call-level analysis.

## D016: Demo hosting (pending M12)
- **Date:** 2026-10-21
- **Decision:** Chosen at M12. Until then the stack runs with Docker Compose locally.
- **Options to compare then:** a free or low-cost container host with managed PostgreSQL; a GitHub Codespaces dev container (Fitness Merchant's choice); a small VPS. A public demo of a product with open sign-up needs the demo-mode protections in SPEC 4.1 (rate limits, no email, cleanup after 24 hours) whichever is chosen.
- **Why pending:** Free tiers and prices change; they are researched when the demo is ready.

## D017: CI tool versions pinned and checksum-verified
- **Date:** 2026-10-21
- **Decision:** GitHub Actions pinned by commit SHA; downloaded tools (gitleaks, actionlint, editorconfig-checker) pinned by version and checked against the SHA-256 from their releases. P0 runs a full-history secret scan and repository lint; M01 adds the build and test jobs.
- **Versions:** gitleaks 8.30.1, actionlint 1.7.12, editorconfig-checker 4.0.2; `actions/checkout` v7.0.1. These are the versions and checksums used in Fitness Merchant's CI (recorded 2026-10-13); the Linux checksums were re-checked against each release's checksum file on 2026-10-21 (see `PROGRESS.md` → P0).

## D018: Git flow: `dev` integration branch, one branch and one PR per milestone
- **Date:** 2026-10-21
- **Decision:** `main` holds releases; `dev` is the default and integration branch. Each milestone is a branch `#00nn-descriptive-name` from an up-to-date `dev`, pushed by the agent and merged by the owner through a GitHub pull request. `dev` is merged to `main` at release points (after M12).
- **Why:** Every milestone is reviewable as one PR with its verification report, which is also portfolio evidence of the working process.

## D019: Version pins and the security-patch exception (M01)
- **Date:** 2026-10-21
- **Rule:** every dependency is pinned to an exact version at least two weeks old (CLAUDE.md). **Exception:** a patch release that fixes a published security advisory is taken as soon as it exists, as Fitness Merchant did for .NET 10.0.13 (its D033, approved by the owner on 2026-10-16).
- **.NET:** SDK 10.0.402 (`global.json`); `Microsoft.AspNetCore.OpenApi` and `Microsoft.AspNetCore.Mvc.Testing` 10.0.13 (security release of 2026-10-13, exception); `Scalar.AspNetCore` 2.17.14 (2026-10-06); `xunit.v3` 4.0.1 (2026-09-12; 4.0.2 is too new).
- **Web:** `@angular/*` 22.2.1 (2026-09-30) except `@angular/cli` 22.2.3 (2026-10-15, exception: 22.2.1 bundles `@modelcontextprotocol/server` 2.2.0 with GHSA-jm63-cq47-j5jp, GHSA-46jr-4wx8-v453 and GHSA-rvq5-wwqv-78pq; 22.2.3 ships 2.3.1). `@angular/build` pins `vite` 8.3.0, affected by the `launch-editor` advisories GHSA-698v-4r3x-3vcv, GHSA-r825-w8mm-5pxw and GHSA-587h-7wmj-h5pm; even `@angular/build` 22.2.3 still pins 8.3.0, so an npm `overrides` entry sets `vite` 8.3.3 (2026-10-06, meets the rule). Remove the override once `@angular/build` depends on a fixed `vite`. Other packages: see `src/web/package.json`.
- **Images:** `postgres:18.6-alpine`, `axllent/mailpit:v1.31.4` (2026-10-03; v1.31.5 and v1.31.6 are too new).
- **npm install scripts:** npm 11 runs only allow-listed scripts. Allowed: `lmdb` and `msgpackr-extract` (native build cache), as in Fitness Merchant. `esbuild` and `@parcel/watcher` are not allowed; they work from their prebuilt platform packages (build, tests and dev server verified).
- **Verified:** `npm audit`: 0 vulnerabilities after the changes (4 before: 3 high, 1 moderate); `dotnet list package --vulnerable --include-transitive`: none in any project.

## D020: The merge gate: one required check over every CI job
- **Date:** 2026-10-21
- **Context:** The owner asked that every merge into `dev` be guaranteed clean: building, tested, linted and safe.
- **Decision:** `.github/workflows/ci.yml` runs these jobs on every PR and every push to `dev` and `main`:
  - API: locked restore, `dotnet format --verify-no-changes`, warnings-as-errors build, all tests;
  - web: `npm ci`, ESLint, Prettier, Vitest, production build with size budgets;
  - dependency audit (npm moderate and above; NuGet including transitive);
  - secret scan of the full history;
  - repository lint (actionlint, editorconfig-checker);
  - PR conventions: branch name, PR target, no `fixup!`/`WIP` commits.

  A final **CI result** job depends on all of them and fails if any failed or was cancelled. Branch protection on `dev` and `main` requires only that check, plus "up to date with the base branch" (`docs/CI.md`).
- **Also:** NuGet lock files (`RestorePackagesWithLockFile`) so CI restores exactly the reviewed dependency graph (`--locked-mode` fails on drift, shown with NU1004); a PR template with the sections every PR description uses.
- **Why one aggregate check:** required checks are configured by name in the repository settings. With a single aggregate, adding a gate (E2E in M11) changes only the workflow, and a renamed job cannot silently stop being required.
- **Trade-offs:** The dependency audit can block a merge because of a newly published advisory, without any code change. This is intended; such a finding is fixed or explained in this file. Branch protection itself is a repository setting the owner applies once; the agent cannot set it.
