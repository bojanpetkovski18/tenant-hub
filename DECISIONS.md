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

## D011: Invitations with hashed single-use tokens; email through SMTP with a copy-link fallback (confirmed M05)
- **Date:** 2026-10-21
- **Decision:** 32-byte random tokens, stored as SHA-256 hashes, valid for 7 days and single use. Email goes through SMTP: Mailpit in development and Compose. When email is disabled (the public demo), the API returns the link and the UI shows a copy dialog.
- **Why:** A database leak does not expose working links; the demo works without sending email to real people.
- **To confirm at M05:** the Mailpit version and the email library (`System.Net.Mail` versus MailKit).
- **Confirmed at M05 (2026-10-22):**
  - `System.Net.Mail` (in the framework, no dependency). It is enough for one relay with plain text and HTML alternatives; MailKit's advantages (modern auth, rich MIME, connection reuse) are not needed for invitation emails. Revisit if a provider requires OAuth or the volume grows.
  - Mailpit stays pinned at `v1.31.4` (published 2026-10-03, per the GitHub releases API on 2026-10-22). The latest, `v1.31.6` (2026-10-15), is under the two-week minimum, so it is not adopted yet.
  - `Email__Mode` is `Smtp` or `Disabled`, and unset means disabled, so nothing is sent by accident. A failed send still keeps the invitation and gives the inviter the link (`delivery: failed`).

## D012: Audit entries from domain events, in the same transaction; append-only (pending M04)
- **Date:** 2026-10-21
- **Decision:** Aggregates raise domain events; a SaveChanges step turns them into audit entries before the transaction commits. Entries store an actor-name snapshot and changed fields as `jsonb`. A database trigger rejects `UPDATE` on `audit_entries`.
- **Why:** The audit trail cannot drift from the data (same transaction) and is meaningful ("Job JOB-0042 moved from Scheduled to In progress") rather than a generic row diff. A test checks that every state-changing endpoint produces an entry, so a forgotten event is caught.
- **Alternatives:** A generic change-tracking interceptor (complete but noisy, hard to read, and logs fields nobody cares about); writing entries explicitly in each handler (easy to forget).
- **Confirmed at M04 (D027):** changed fields are captured by the domain method that makes the change (it knows the old and new values and how to name them), not by a change-tracker diff.

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

## D016: Demo hosting (decided in M12: see D035)
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
- **`minimatch` override (2026-11-16, `#0013a`):**
  - **The advisory:** GHSA-g3hv-75wg-m249 (published 2026-10-30, medium) is a denial of service in `minimatch` below 11.0.1. Every copy in the web app (seven, all 10.2.6) comes from the lint tool chain (`eslint`, `@eslint/config-array`, `typescript-eslint`), so the shipped app was not affected; the dependency-audit gate failed all the same, as intended.
  - **Why an override:** the upstream fixes, `eslint` 10.15.0 (2026-11-13) and `typescript-eslint` 8.73.0 (2026-11-16), are too new for the rule. `minimatch` 11.0.1, the first patched version, dates from 2026-10-27, so an `overrides` entry sets `minimatch` 11.0.3 (2026-10-28) without the security exception.
  - **Its new dependency:** 11.x depends on `@isaacs/nonrecursive-glob-match`, whose latest release, 1.1.0 (2026-11-05), was too new, so a second override pins 1.0.3 (2026-10-28), which `^1.0.3` allows.
  - **Compatibility:** 11.0 removed `makeRe()`, `Minimatch#regexp`, `matchOne()` and the `AST` class; the lint packages use only `new Minimatch(...)`, `match()`, `pattern` and `minimatch()`. 11.0 also changed partial matching with `**`, which eslint uses to decide which folders to walk, so the list of linted files was compared before and after: the same 163 files, 0 problems, and a planted `any` is still reported.
  - **Remove both overrides** once `eslint` and `typescript-eslint` are upgraded to versions that depend on a fixed `minimatch`.
- **.NET 10.0.15 (2026-11-16, `#0013b`, under the exception above):**
  - **Found** while re-checking versions at the start of M14, as the playbook asks (a package audit does not cover the runtime). The app ran on 10.0.13, two security releases behind:
    - 10.0.14 (2026-10-25, out of band) fixes a Brotli decoder out-of-bounds write (CVE-2026-111497) and re-releases two October fixes that 10.0.13 did not complete (CVE-2026-70197, CVE-2026-102870) ([release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md), [Microsoft support](https://support.microsoft.com/help/5133966));
    - 10.0.15 (2026-11-10) fixes 17 CVEs, among them ASP.NET Core elevation of privilege, denial of service, information disclosure and a security feature bypass ([release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md), [Microsoft support](https://support.microsoft.com/help/5135241)).
  - **Changed:** SDK 10.0.404 (`global.json`, the image's build stage), runtime image `aspnet:10.0.15-noble-chiseled-extra`, every Microsoft 10.0.x package and `dotnet-ef` 10.0.15. 10.0.15 is 6 days old, so this uses the security exception. The only transitive change is `Microsoft.OpenApi` 2.12.2 → 2.13.0 (2026-10-19, meets the rule), from `Microsoft.AspNetCore.OpenApi` 10.0.15. `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 is still its latest release.
  - **Owner's machine:** the owner chose to install SDK 10.0.404 with winget (installer hash verified by winget); the older SDKs stay side by side.
  - **Verified:** the test host loads `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.0.15 (host trace); 750/750 tests; NuGet reports no vulnerable package; the production image reports runtime 10.0.15 and passes the end-to-end journey.

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
- **Exceptions (2026-11-18, owner's choice):** an advisory with no fixed version can be listed in `tools/audit-exceptions.json` with its package, a reason and an expiry date. `tools/npm-audit.mjs` runs `npm audit --json` and fails on every moderate-or-higher advisory that is not listed, or whose date has passed, and reports listed advisories that no longer appear. Both npm audit steps use it. Chosen over auditing only production dependencies, which would hide every future build-tool advisory. First entry: GHSA-fc5q-39xc-vjpf (`picomatch` ReDoS, high, all versions up to the latest 4.0.7, no fix published), reached only through build tooling; until 2026-12-18 (K5).

## D021: A public session endpoint instead of `GET /api/me` (M02)
- **Date:** 2026-10-21
- **Decision:** `GET /api/auth/session` is public and returns `{ user }` (profile and active memberships) or `{ user: null }`; it also issues the `XSRF-TOKEN` cookie. It replaces the planned `GET /api/me` (SPEC 7 updated). `PATCH /api/me` and `POST /api/me/password` stay user-scoped and return or refresh the session.
- **Why:** The SPA needs one call on startup that works for guests too (no console 401 on every visit) and gives it the antiforgery token before the first unsafe request. Two endpoints returning the same user would drift.
- **Shape:** memberships carry `organizationId`, `slug`, `name` and `role` (camelCase string, like every enum in the API); `permissions` join them in M03.

## D022: Authentication and account details (M02)
- **Date:** 2026-10-21
- **Rate limits:** sign-up and sign-in share a fixed window of 10 requests per 60 s per client IP (`RateLimiting:AuthPermitLimit`, `AuthWindowSeconds`), answered with `429` and `Retry-After`. Per-account brute force is covered by Identity's lockout (5 failures, 15 minutes) with the same generic answer as a wrong password, so it does not enumerate accounts. Behind a proxy the client IP needs forwarded headers (M11).
- **Sessions:** the security stamp is validated on every request (one indexed user read), so a password change ends other sessions immediately; the session that changed it gets a refreshed cookie.
- **Where the rules live:**
  - request attributes check only shape (present, bounded length);
  - the handlers (name, time zone) and Identity (email, password) check content and return every field error at once;
  - a stricter attribute would stop the request first and hide the other errors (found in testing).
- **Error keys:** validation problems always use JSON property names (`displayName`), whether from request validation (which reports C# names) or from a handler; normalized in one place (`ProblemDetailsSetup`).
- **Unknown routes:** an anonymous fallback endpoint answers `404` problem details. Without it the fallback authorization policy answered `401` for any unknown URL, including file-like paths that `MapFallback`'s default pattern skips.
- **Ports kept out of Application (D003):**
  - `IIdentityService` (Identity's `UserManager` without Identity types);
  - `IDatabaseLocks` (`SELECT … FOR UPDATE` on the user row for the five-owned-organizations limit; reused for plan limits in M08);
  - `IDatabaseErrors` (recognizes a unique-index violation without referencing Npgsql).
- **Slug races:** the creator checks the candidates, inserts, and on a unique-index violation tries the next candidate in the same transaction; EF Core rolls the failed insert back to its automatic savepoint. A same-email sign-up race hits `ux_users_normalized_user_name` and becomes the normal "email already in use" field error.
- **Demo seed:** `DEMO_PASSWORD` must satisfy the password rules; a weak value stops startup with a clear message rather than seeding half the data. `.env.example` carries a valid placeholder.
- **Verified:**
  - lock mutation check: without the row lock, 8 parallel creates all succeeded (3 of 3 runs; the user would own 9); with it, exactly 4 succeed (5 of 5 runs);
  - migration guard mutation check: a model change without a migration fails `Model_matches_the_migrations`.

## D023: CDK runtime styles under the strict CSP; no component styles (M02)
- **Date:** 2026-10-21
- **Found:** opening a CDK menu on the production build under `style-src 'self'` logged a CSP violation. The CDK injects the overlay's structural CSS at runtime as an Angular component style, which Angular writes as an inline `<style>` element.
- **Decision:** `CspSafeStyleLoader` replaces the CDK's internal `_CdkPrivateStyleLoader` with a no-op. The same rules are loaded from `overlay-prebuilt.css` and `a11y-prebuilt.css` (angular.json). A component test fails if opening a menu adds a `<style>` (shown failing before the fix). Components do not use `styles` either, for the same reason: Tailwind classes and `styles.css` only (CLAUDE.md).
- **Alternatives:**
  - per-request CSP nonces (`ngCspNonce`): need server-side rewriting of `index.html`, which only exists once the API serves the SPA (M11); reconsider then if a library needs it;
  - a `sha256` hash of the CDK's CSS in the CSP: breaks silently whenever that CSS changes.
- **Trade-offs:** relies on a CDK-internal API; re-check on every CDK upgrade (the test and the per-milestone CSP browser check would catch a regression).
- **Verified:** production build served with the API's exact CSP: sign-in, switcher menu, user menu and sign-out with 0 console errors and 0 CSP violations; the full M02 browser suite 96/96 there.

## D024: How the tenancy core is built (M03)
- **Date:** 2026-10-21
- **Query filter:** one model-building convention gives every `ITenantOwned` entity EF Core 10's *named* query filter `Tenant` (`organization_id = current organization`), read from the context's `ITenantContext` per query. Without a resolved organization the filter matches nothing (fail closed). A named filter lets later filters (for example archived rows) coexist, and lets the bypass drop only the tenant filter.
- **Save guard:** `TenantGuard` runs in `SaveChanges`/`SaveChangesAsync`:
  - it stamps new rows without an organization;
  - it refuses inserts for another organization and updates or deletes of another organization's rows;
  - it refuses any change to `organization_id`, and any tenant-owned write while no organization is resolved;
  - violations throw `TenantIsolationException`, which the API logs as a critical security event (event id 9001) and answers with a bare 500.
  - For memberships EF Core itself refuses the move (the key contains `organization_id`); the guard's own move check gets its test with clients in M04.
- **One tenant per scope:** `TenantContext` can be entered once; a second, different organization or user throws. Creating an organization enters it for its Owner after the organization row is saved, so the Owner membership passes the guard like any other write. The seeder uses one DI scope per organization.
- **The bypass:** `AcrossTenants()` (ignores only the `Tenant` filter) is the only way past it. A source-scan architecture test bans `IgnoreQueryFilters` everywhere else and pins its call sites:
  - the session (memberships everywhere);
  - the owned-organization count;
  - tenant resolution.

  A source scan was chosen over IL analysis because a call site is a line a reviewer reads, and it needs no extra package (D015). Test code may use the bypass to inspect data.
- **Mutation check:** without the filter convention, three tests fail (model coverage and two behaviour tests).

## D025: Endpoint classification and the isolation harness (M03)
- **Date:** 2026-10-21
- **Classification:** every endpoint is `.Public()`, `.UserScoped()` or inside `MapTenantGroup()` (`/api/orgs/{orgId:guid}`, with `.RequirePermission(...)`).
- **Tenant resolution:** a group endpoint filter loads the caller's active membership. If there is none, it answers 404 `organization-not-found`, the same for a foreign organization, an unknown id or a removed member. If the permission is missing, it answers 403 `permission-denied`. Otherwise it enters the tenant and scopes logs with `OrganizationId`/`UserId`. A malformed id never matches the `guid` route constraint and gets the plain 404.
- **Harness** (`tests/TenantHub.IntegrationTests/Isolation`), all read from the running app:
  - **coverage:** every endpoint classified; the public set equals a reviewed list; tenant routes declare a known permission; nothing else is routed under an organization; every tenant endpoint has a line in `TenantEndpointCase.All` and vice versa;
  - **cross-tenant matrix:** for each line, A's Owner aims at B: 404 identical to an unknown organization, B unchanged (SHA-256 of B's rows), nothing of B in the body; a removed member gets 404; signed-out callers get 401;
  - **permission matrix:** each line × each role, expected result from the role map (pinned to the SPEC table by PermissionsTests);
  - **model coverage:** filter plus an organization-first index on each tenant-owned entity, and a reviewed list of the rest.

  Later milestones add one `TenantEndpointCase` line per endpoint; the tests themselves stay the same.
- **Test-only endpoints** prove two paths no real endpoint needs yet: an Owner-only route (403 for the other roles) and a route with a deliberate cross-tenant write (the guard stops it; security log; nothing changes in B).
- **Mutation checks** (2026-10-21, all restored afterwards):
  - (a) no filter convention → 3 failures;
  - (b) membership check ignoring the caller → 15 failures (every cross-tenant row);
  - (c) an unclassified endpoint → the classification test; a tenant endpoint without a case → the case test; a tenant-owned entity with neither an organization-first key nor the organization foreign key → the index test.

  The index test does not fail when only the key order changes, because EF Core indexes the foreign key to organizations by itself; the check still protects an entity that lacks both.
- **Health endpoints** answer any HTTP method (`MapHealthChecks`); harmless for read-only probes and recorded as such in the reviewed public list.

## D026: Lost access in the web app (M03)
- **Date:** 2026-10-21
- **Decision:** the session's memberships carry `permissions`; navigation shows only what they allow (`SessionStore.can`). Opening an organization calls `GET /api/orgs/{orgId}`, which is also the server-side membership check. Any tenant call answering 404 `organization-not-found` reloads the session and opens the picker with "You no longer have access to that organization". Other 404s (a missing client or job) are left to the page.
- **Why:** a member can be removed while signed in. The next request is the earliest moment the app can know, and the session reload makes the switcher stop offering the organization.
- **Browser-test finding:** an intermittent failure (about 1 in 3 full runs) of a keyboard check after sign-out was traced with Angular's dev-mode `ng` API. The router updates the URL when it creates the page, and the page's bindings apply on the next frame (measured under 25 ms). Playwright sent Tab and Enter inside that gap, so `FormGroupDirective` had no form yet. People cannot act before the first paint, so the app was not changed; the check now waits for a bound attribute (`#email[aria-invalid]`) and passed 6 of 6 runs.

## D027: Clients, audit recording and the list pattern (M04)
- **Date:** 2026-10-22
- **Audit recording (confirms D012):**
  - aggregates raise `IAuditableEvent`s that carry the action, a readable summary and the changed fields (old → new);
  - `AuditWriter` turns them into `audit_entries` rows just before `SaveChanges`, with the actor's display name as a snapshot and the request's trace id;
  - the change and its entry commit or roll back together: tested with a rolled-back transaction, with a change the database refuses, and with the audit table missing (the API answers 500 and saves nothing);
  - `changes` is one `jsonb` value per entry (read whole, never queried per field).
- **Clients:**
  - `citext` name, so case-insensitive uniqueness, sorting and comparison need no `lower()` expressions; a partial unique index on `(organization_id, name) WHERE status = 'Active'`, so an archived name can be reused;
  - the `(organization_id, id)` alternate key, the target of the composite foreign keys from jobs in M06;
  - `xmin` mapped to a domain `Version`: the edit form sends back the version it loaded, and a newer one means 409 `concurrency-conflict`;
  - archive and restore are idempotent; delete is for clients without jobs (the check arrives with jobs in M06);
  - duplicate names come from the unique index (409 `client-name-taken`), so there is no race between a check and an insert.
- **Errors:** domain errors gain a `NotFound` kind (404 with a code, for example `client-not-found`). Field errors from the domain are one `DomainValidationException` (400, every field at once).
- **The guard's move check** became reachable: audit entries are keyed by `id` alone, so EF Core allows the change and the guard is what refuses it (tested). Clients, like memberships, carry `organization_id` in a key, so EF Core refuses that move first.
- **Isolation suite:**
  - endpoints with an `{id}` get a resource (A's standing client, a fresh client for deletes);
  - a new matrix sends B's ids under A's own route (404 `client-not-found`, B unchanged);
  - cases are prepared before the "before" snapshot, so test setup is not mistaken for a leak (found when setup archived B's client);
  - every audited endpoint must write an entry, and every state-changing endpoint must be marked audited.

  Mutation check: client handlers bypassing the filter fail six tests.
- **The list pattern (web):** the query (search, status, sort, page) is the URL, bound to inputs; a reusable paged-list SignalStore only fetches and ignores late answers. Sort keys and status values are allow-listed on both sides; unknown values fall back to the defaults. Table on wide screens, cards on narrow ones.
- **Query plans** (10,000 synthetic clients in one organization, PostgreSQL 18.6):
  - the default page (active, by name) uses `ux_clients_active_name` (0.66 ms);
  - the count and the substring search scan the table (1.7 ms and 6.3 ms).

  A trigram index (`pg_trgm`) would serve the search; not added until data sizes need it (re-check at M10).
- **Dialogs and the CSP:** the CDK dialog container's own component styles became an inline `<style>`, which the CSP blocked. A style-free subclass with the same template and host bindings (`CspSafeDialogContainer`) is passed by `Confirm`; its one rule lives in `styles.css`. It is not registered app-wide, because that pulled the dialog code into the initial bundle (+18 kB transferred, measured). All dialogs open through `Confirm` (or pass the same container).
- **Accessibility finding:** the CDK now leaves `aria-modal` false unless asked; confirm dialogs set it.

## D028: Members and invitations (M05)
- **Date:** 2026-10-22
- **The token never travels in a URL the server sees.** Email links are `<App__PublicUrl>/invite#<token>`: browsers do not send fragments, so the token stays out of server, proxy and access logs and out of `Referer` headers. The API takes it in request bodies: `POST /api/invitations/preview`, `/sign-up` and `/accept` instead of the `{token}` routes first planned in SPEC 7. Request paths are in every log scope in production (JSON logs include scopes), so a path token would have been logged on every request; a test captures all logs and scopes at Trace level through a whole flow and finds no token.
- **Links come from `App__PublicUrl`**, required at startup; never from the request's `Host` header, which a client controls.
- **One file bypasses the tenant filter for invitations** (`InvitationAcceptance.cs`, approved in the M05 brief): the invitee is not a member yet, so the invitation is found by its token hash. Before anything is written, the scope enters the invitation's organization, so every write passes the tenant guard as usual.
- **Serialization:** membership changes, invitations and acceptances take a row lock on the organization (`SELECT … FOR UPDATE`). Two Owners demoting each other at once leave exactly one Owner (without the lock the test fails 3 of 3 runs); the M08 seat check will sit under the same lock.
- **Leaving is `DELETE /members/me`** with `org.read`, rather than `DELETE /members/{ownId}` with a conditional permission: each route declares one permission, so the permission matrix stays generic. `DELETE /members/{ownId}` answers 409 `use-leave`.
- **The SPA does not re-implement the membership rules.** Session memberships carry `grantableRoles` and `invitableRoles`; the member list carries `canChangeRole`, `canRemove` and `canLeave` per row, all computed by `MembershipRules`. A role change answers `204` and the page reloads the list, because the flags depend on everyone's roles.
- **Rules about who may act on whom answer 403** (`owner-protected`, `role-not-grantable`: `DomainErrorKind.Forbidden`); conflicts with the organization's state stay 409 (`last-owner`, `use-leave`, `cannot-change-own-role`).
- **Inviting the same email again** reuses the open invitation row with a new token and a new week (resend), so there is never more than one link per email; inviting a current member is 409 `already-member`; a removed member who accepts gets their old membership row back.
- **The accept page** keeps the token in `sessionStorage` for the detour through sign-in, so the sign-in URL carries only `returnUrl=/invite`.

## D029: Jobs: workflow, resource rules, numbering and the list (M06)
- **Date:** 2026-10-23
- **The workflow is data.** `JobWorkflow.Transitions` is the SPEC 5.4 table (from, to, needs assignee, needs scheduled date, needs reason, may the assignee do it). Tests walk all 25 cells; the API derives the 409 `allowed` list and the detail page's options from the same rows.
- **Requirements are checked at the move, not held as invariants.** "Scheduled needs an assignee" applies when scheduling. Removing a member unassigns their open jobs without moving them back (SPEC 5.2), and a manager can still complete or cancel such a job.
- **Who may do what is decided in the domain from a `JobActor`** (user id, `jobs.manage`, `jobs.work`), built from the tenant context's permissions. Routes declare one permission each (`jobs.work` for edit and move, `jobs.manage` for delete), so the permission matrix stays generic; the resource rule ("only your own job") answers `403 job-not-yours`. Without `jobs.manage` a member may create a job unassigned or for themselves, edit their own job, but never assign others or set the scheduled date (`403 manager-only`).
- **The SPA does not re-implement any of this** (D008): the job view carries `canEdit`, `canAssign`, `canDelete` and the allowed moves with what each still needs; a blocked move is shown disabled with its reason.
- **Numbering:** `Organization.TakeJobNumber()` under the organization-row lock (D028) in the create transaction. Parallel creates leave no gaps or duplicates; without the lock the test fails every run. M08's open-job limit check goes under the same lock.
- **Composite foreign keys** `(organization_id, client_id)` → clients and `(organization_id, assignee_user_id)` → memberships, with NO ACTION so deleting an organization can still cascade through jobs, clients and memberships in one statement. A raw-SQL test proves the database refuses cross-organization references.
- **References:** a new or changed client must be active and a new or changed assignee an active member (400 on the field); unchanged references stay valid (a job keeps an archived client or a former assignee). Deleting a client with jobs is `409 client-has-jobs`.
- **History:** `GET /jobs/{id}/history` (`jobs.read`, newest first, at most 200) reads the job's audit entries, so everyone who can read a job sees its history (US-40) before the M07 viewer exists. Field changes name people and clients (snapshots), not ids.
- **Indexes from EXPLAIN on 10,000 jobs** (PROGRESS): `ux_jobs_number` serves the default sort and most filtered pages; `ix_jobs_due (organization_id, due_on, number)` serves the due-date sort once its ascending form relies on PostgreSQL's NULLS LAST instead of an `IS NULL` key (4.1 → 0.3 ms); the partial `ix_jobs_open_due` answers open and overdue counts from the index (1.8 → 0.6 ms), for the dashboard and the M08 limit. Not indexed, measured and accepted: the descending due sort and title search (about 5 ms each at 10,000 jobs in one organization). A trigram index would serve substring search if real data needs it.
- **List URL:** multi-value filters are comma-joined in the SPA URL (`status=new,scheduled`), one query parameter per input, and sent to the API as repeated parameters (`status[]` in SPEC 7). Unknown values fall back to defaults on both sides.
- **Pickers:** the client and assignee selects load all members and the first 100 clients by name (the maximum page). Larger organizations reach other clients' jobs from the client pages, which link to the filtered list; a search-as-you-type picker is a V2 candidate.
- **Money on screen** uses `Intl.NumberFormat` rather than `CurrencyPipe`, which pulled Angular's currency table into the initial bundle (+7 kB).

## D030: The audit log: append-only in the database, keyset paging, no totals (M07)
- **Date:** 2026-10-23
- **Append-only (confirms D012):** a `BEFORE UPDATE` trigger on `audit_entries` raises `restrict_violation` for any update, from the application or anywhere else. `DELETE` stays allowed for organization deletion (cascade) and the demo reset (M09). Tested with raw SQL; removing the trigger fails exactly those tests.
- **Keyset paging, measured with 50,000 entries in one organization:**
  - Offset paging grows with depth (page 1 0.7 ms; 25,000 deep 16–18 ms; the last page 34–35 ms), and totals are scans (6–13 ms).
  - Keyset paging on `(occurred_at, id)` with the index `(organization_id, occurred_at desc, id desc)` costs 0.15–0.25 ms at any depth.
  - The redundant `occurred_at <= @at` in the cursor condition gives PostgreSQL an index bound; EF Core cannot express a row comparison without the provider.
  - The API returns `nextCursor` and no total. The page offers "Older entries" and "Newest". The cursor is in the URL, so reload and Back work.
- **Date ranges are days in the organization's time zone:** "from" is the instant that day starts there, "to" runs until the next day starts. A day whose midnight falls in a daylight-saving gap starts at the first valid local time (tested with Chile).
- **`entityExists`:** checked per page with one tenant-filtered query per entity type. The viewer links only to clients and jobs that exist, members who are active, and invitations that are open. History stays readable without the entity because summaries and changed values are snapshots of names.
- **Summaries reviewed:** every action is driven through the API in one test, which asserts that each appears, starts with a capitalized verb (or with the person it is about), and contains no ids in the summary or the changed values. "Removed X" became "Removed X from the organization"; a missing name reads "a member" in the middle of a sentence.
- **Filters:** actor, action, type, entity id and date range. Unknown values match nothing or are ignored, as in the other lists. The action list in the SPA is a fixed catalog, grouped by entity.
- **Members cannot reach it:** no navigation entry (permission-driven, as before), an explanation on the page without calling the API, and `403` from the API (permission matrix).

## D031: Plan limits, plan changes and organization settings (M08)
- **Date:** 2026-10-23
- **Policy in the domain (confirms D013):**
  - `PlanLimits` for each plan (null means unlimited), `PlanUsage`, and `PlanPolicy.EnsureRoomFor` / `BlockingLimits`;
  - `Organization.ChangePlan(target, usage)` refuses a downgrade with every blocking limit;
  - the organization became an aggregate whose settings and plan changes are audited (`organization.updated`, `organization.plan_changed`).
- **Where limits are checked:** `PlanLimitsCheck` counts the usage under the organization-row lock, in the transaction of every command that consumes a limit:
  - inviting, unless the invitation replaces a pending one;
  - renewing an expired invitation;
  - accepting an invitation (its seat is already counted, so this only refuses an organization that is over its limit);
  - creating a client, and restoring an archived one;
  - creating a job, and reopening a completed one.

  Without the lock, the races fail every run (PROGRESS).
- **Seats** are active members plus pending invitations, where pending means not accepted, not revoked and not expired. An expired invitation frees its seat until it is renewed.
- **Refusals carry data, not just text:**
  - `403 plan-limit-reached` with `plan`, `limit`, `allowed` and `usage`;
  - `409 plan-downgrade-blocked` with `blocking`.

  The SPA shows one shared message with a link to the plan for Owners (US-50: "the same clear message").
- **Deleting an organization:**
  - the name is typed (whitespace collapsed, case-sensitive);
  - the database cascades everything the organization owns, its audit entries included (deletes are allowed by the M07 trigger);
  - so the deletion is a structured warning in the logs (event 2001), not an audit entry;
  - `DELETE /` reads its optional JSON body itself: a bound body made routing skip the endpoint for a body-less request, which then got the not-found fallback.
- **Isolation harness:** a "fresh organization" resource stands in for the requested one, so `DELETE /` runs through every matrix without consuming the fixture. A reviewed `NotAuditedBecause` reason exempts it from the audit rule. The fixture's organizations are on Business, so limits do not interfere; limit tests use their own Free organizations.
- **UI:**
  - meters use `role="meter"` with the usage as text; the fill uses width classes in 5% steps, not an inline style (CSP, D023);
  - a plan change asks for confirmation;
  - a refused downgrade lists the blocking limits;
  - the shell closes open dialogs on every router navigation (bug found in the browser pass).


## D032: Dashboard, demo seed and demo mode (M09)
- **Date:** 2026-10-24
- **Dashboard (US-60):** one endpoint, `GET /api/orgs/{orgId}/dashboard` (`org.read`).
  - The job counts are one grouped statement with filtered counts; active clients, my next 7 days and seats are small separate queries; recent activity reuses the audit list query (8 entries, only with `audit.read`).
  - The response carries the organization's `today` and `monthStart`, so the SPA links each tile to a list that counts the same rows.
  - "Completed this month" needed a list filter that did not exist: the jobs list gained `completedFrom`, a day in the organization's zone. Every number is checked against hand-written SQL on the seed, and the month boundary in Pacific/Auckland with a moving clock.
- **The chart:** the repository has no dataviz guidance, so the chart follows accessible-chart practice:
  - horizontal bars on one scale from zero, each labelled with its status and count in text;
  - colour is never the only cue, and the bars are decoration (`aria-hidden`);
  - each row links to the filtered list;
  - a "Show as table" toggle (`aria-pressed`) gives a real table with a total.
- **Seed (SPEC 8.3):** replayed through the domain in time order with a fixed random seed per organization:
  - each step is saved at its own moment by the person who would do it, so job numbers, dates and the audit log agree;
  - dates are relative to the day of seeding;
  - the same day gives the same data (a fingerprint test).
- **Demo mode (`DEMO_MODE`):**
  - **Endpoints:**
    - `GET /api/demo/accounts` and `POST /api/demo/sign-in { role }` are always mapped (so the coverage tests classify them) and answer 404 outside demo mode;
    - sign-in needs no password and shares the auth rate limit;
    - the session response carries `demoMode`, and the SPA asks for the accounts only then: elsewhere every page load logged the 404 as a console error.
  - **Reset:** a hosted service (Api project, because Infrastructure has no hosting dependency) runs `DemoReset` when due.
    - Due means one interval after the seeded organizations were created. The schedule lives in the data, so a restart neither resets early nor skips a reset; the service re-reads it at least every 15 minutes.
    - A reset deletes the seeded organizations (the database cascades their data and audit entries), deletes visitor organizations older than 24 hours, and seeds again. Deleting comes before seeding, so for a few seconds the demo organizations are missing.
    - Running more than one API instance is not supported in demo mode (no cross-instance lock); the demo runs one instance (M12).
  - **Protections:** in demo mode, `DemoProtection` refuses with `403 demo-protected`:
    - a demo account (one of the seeded emails) changing its password;
    - a demo account's role change, removal or leaving in a seeded organization;
    - deleting a seeded organization.

    Visitors can still rename, change plans or invite in the seeded organizations; the next reset restores them.
  - **Email and limits:** email is always off in demo mode, whatever `Email:Mode` says. The auth and invitation limits drop to `RateLimiting:DemoAuthPermitLimit` and `DemoInvitationPermitLimit` (5 a minute by default).
  - **Startup:** demo mode refuses to start without `DEMO_PASSWORD`, and `DEMO_RESET_INTERVAL_HOURS` must be 1 to 168.
  - **Bypasses:** no new tenant bypass was needed. Organizations are not tenant-filtered, and the seeder works through ordinary tenant contexts, one per actor and step. So the allow-list (`TenantBypassAllowListTests`) is unchanged, although SPEC 3.3 had pre-approved the seed and reset.
- **UI:**
  - the plan types, labels and usage meters moved to `shared/` (Settings and the dashboard both use them);
  - the landing page replaces its placeholder;
  - the demo banner sits at the top of both layouts.

## D033: Polish, known issues and the lock mode (M10)
- **Date:** 2026-10-24
- **Data-protection keys (K1, the owner chose to fix it in M10):**
  - the keys that sign session and antiforgery cookies live in PostgreSQL (`data_protection_keys`, via `PersistKeysToDbContext`), under the fixed application name "TenantHub", so a restarted container or a second instance accepts existing cookies;
  - the keys are stored unencrypted, like ASP.NET Core's default file store on Linux. Whoever can read the database can already read all data; encrypting them with a certificate can come with hosting (M12) if needed;
  - the table is not tenant-owned and is listed in `NotTenantOwned` (the owner chose a database table);
  - package `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.13: an owner-approved exception to the two-week rule (11 days old). 10.0.12 brings `System.Security.Cryptography.Xml` and `Pkcs` 10.0.12 with high-severity advisories (GHSA-846f-f764-76mx, GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj, GHSA-r955-8q5j-3h83) that the 10.0.13 servicing release fixes; seven of our packages were already on 10.0.13.
- **Visitor accounts (K2, owner's choice):** each demo reset deletes accounts that are over a day old, are not demo accounts, and that no membership, invitation or organization refers to any more. The query is a tenant bypass in `DemoReset.cs`, added to the allow-list under the approval in SPEC 3.3 and the M09 brief.
- **Lock mode:** `LockOrganizationAsync` and `LockUserAsync` take `FOR NO KEY UPDATE`, not `FOR UPDATE`.
  - The locks exist to serialize the commands that take them (limits, membership rules, numbering). `FOR NO KEY UPDATE` still conflicts with itself and with deletes, but not with the `FOR KEY SHARE` that every foreign-key check takes.
  - With `FOR UPDATE`, every insert in the organization waited for the lock, and a job transition writing its audit entries deadlocked with a job creation queued for it. Found by the load test, at about 1,000 operations with 20-way concurrency; the plan-limit and numbering races still pass.
- **Save confirmation:** client and job forms navigate to the detail page with `notice=created|saved`, shown as a status message, like the organization picker's notices. The parameter stays in the URL until the user moves on.
- **Performance:**
  - Lighthouse ≥ 90 on mobile needs compressed assets; the SPA will be served by the API with `MapStaticAssets` (pre-compressed) in M11 (K3);
  - the public layout defers the account menu so the CDK overlay leaves the landing page's critical path.
- **Tools:** `tools/contrast.mjs` (token contrast) and `tools/load-jobs.mjs` (jobs list under load, through the API) are plain Node scripts with no dependencies. The browser audit script stays out of the repository until M11 adds Playwright.

## D034: Production image, Compose, end-to-end test (M11)
- **Date:** 2026-10-25
- **Serving the SPA:**
  - the API serves it from `wwwroot` with `MapStaticAssets`, which fingerprints and pre-compresses (Brotli, gzip) at publish; this closed K3;
  - static assets are mapped only when `wwwroot/index.html` exists (the image), so development and tests keep their endpoints. `MapStaticAssets` adds its own `{**path:file}` endpoint that conventions do not reach, and the coverage tests would rightly flag it as unclassified;
  - the existing `{**path}` fallback now returns `index.html` (no-cache, with the CSP) for page requests: GET or HEAD, outside `/api`, no file extension. Everything else is still a 404 problem, so the public-endpoint list is unchanged.
- **Image:**
  - multi-stage: `node:24.20.0-alpine3.24` builds the SPA, `sdk:10.0.402` (the `global.json` SDK) restores in locked mode and publishes, and `aspnet:10.0.13-noble-chiseled-extra` runs it as the image's non-root `app` user;
  - the `-extra` variant is required: plain chiseled has no tzdata, and organizations live in IANA zones (found when the container never became healthy);
  - the 10.0.13 runtime matches our 10.0.13 packages, the release the owner approved for its security fixes (D033). The .NET tags float to patched rebuilds (the current ones were built 2026-10-21); pinning by digest would freeze them, and is the owner's call for M12;
  - the health check is the app's own `--health-check` mode, which asks `/health` (database included), because chiseled images have no shell or curl.
- **Compose:**
  - `docker-compose.yml` is the whole product (PostgreSQL, Mailpit, the image), separate from `docker-compose.dev.yml`: its own project name and volume, and only the app (8080) and the Mailpit inbox (8026) on localhost;
  - it composes the container's connection string and SMTP host itself and takes the rest from `.env`;
  - it sets `Auth__RequireHttps=false`, for plain http on localhost only (documented in the file); a public deployment terminates TLS in front of the app and keeps it on.
- **Forwarded headers:** off unless `ForwardedHeaders:Enabled`. Only the configured `KnownNetworks` and `KnownProxies` (and loopback) are trusted, with one hop. The M12 host decides the values.
- **End-to-end:**
  - `tests/e2e` is its own small npm project (@playwright/test 1.64.0, @axe-core/playwright 4.13.0) with a lock file, audited in CI;
  - every run uses new accounts and organizations, so runs are independent of each other and of the seed;
  - the CI job starts the stack from `.env.example` exactly as the README says, and uploads traces, the report and container logs when it fails. It is part of **CI result**.

## D035: No public demo for now; the demo is a one-command run (M12)
- **Date:** 2026-10-25
- **Decision (owner):** no hosted demo at v1.0. A visitor tries TenantHub with `cp .env.example .env` and `docker compose up --build --wait` (the production image with demo data; M11 measured about 50 s from a fresh clone with base images present). Hosting can be added later without code changes: the image, forwarded headers, demo mode and the key store are ready.
- **Findings (2026-10-25), for one container plus PostgreSQL:**
  - **Render free web service:** sleeps after 15 minutes without traffic and takes about a minute to wake; 750 free instance hours a month; Render says not to use free instances in production. Its free PostgreSQL expires 30 days after creation, so it would be paired with Neon ([Render docs](https://render.com/docs/free)).
  - **Neon Free:** 100 CU-hours a month per project, 1 GB of storage per project, suspends after 5 minutes idle ([Neon FAQ](https://neon.com/faqs/free-plan-limits-and-quotas)).
  - **Google Cloud Run:** about 2 million requests, 180,000 vCPU-seconds and 360,000 GiB-seconds free a month; scales to zero; .NET cold starts estimated at a few seconds; needs a billing account. The official pricing page did not render, so these figures are from several consistent secondary sources ([agentdeals](https://agentdeals.dev/vendor/google-cloud-run), [freetiers](https://www.freetiers.com/directory/google-cloud-run)).
  - **Azure Container Apps (Consumption):** the same monthly grant sizes; no charge while scaled to zero; needs a subscription. Secondary sources, including a Microsoft Q&A answer ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5560085/ongoing-costs-for-azure-container-app-environment)).
  - **Fly.io:** no free allowance for new organizations; the smallest machine, shared-cpu-1x 256 MB, is about $2.19 a month; Managed Postgres starts at $38 a month ([Fly pricing](https://fly.io/docs/about/pricing/)).
  - **Koyeb:** no free web service any more (entry plan $29 a month); a free Postgres with 5 active hours a month ([Koyeb pricing](https://www.koyeb.com/pricing)).
  - **Hetzner Cloud:** shared plans from €5.99 (not available) and €6.49 a month excluding VAT; the stack would run with this repository's Compose file plus a TLS proxy ([Hetzner Cloud](https://www.hetzner.com/cloud)).
  - **GitHub Codespaces:** 120 core-hours and 15 GB-month free for personal accounts. It runs in the visitor's own account, so it is not a public link ([GitHub docs](https://docs.github.com/billing/managing-billing-for-github-codespaces/about-billing-for-github-codespaces)).
- **Why:** the owner preferred no running cost and no accounts to maintain at this stage. The free options either sleep for about a minute or need a payment method.
- **If hosting is added:**
  - run with `DEMO_MODE=true`, which turns email off and lowers rate limits, and with `ForwardedHeaders` set for the host's proxy;
  - keep `Auth__RequireHttps` on (the default outside Development);
  - pin the base images by digest;
  - follow `docs/CHECKLISTS.md` → Deployment checklist.

## D036: MIT licence (M12)
- **Date:** 2026-10-25
- **Decision (owner):** the repository is released under the MIT licence, copyright Bojan Petkovski.
- **Why:** a portfolio project is meant to be read, run and reused; MIT is the familiar permissive choice. The data in it is fictional, so the licence covers code and documentation only.

## D037: v2 scope: bring the business's clients and repeating work into the product (M13)
- **Date:** 2026-11-16
- **Status:** approved by the owner on 2026-11-16 (PR #15 merged).
- **Decision:** v2 adds recurring service agreements, a dispatch board, a client portal (quotes, work requests, sign-off), completion proof (checklists, photos), notifications, API keys with signed webhooks, exports and plan entitlements, with PostgreSQL row-level security and a transactional outbox underneath (`docs/SPEC-V2.md`). Ten milestones, M14–M23, in five phases.
- **Why:**
  - Each feature removes a daily chore of the businesses in SPEC 1: typing repeat visits, planning the week from a list, answering client calls, proving work was done, copying data to other tools.
  - Each one also demonstrates a part of SaaS engineering that clients ask freelancers for and that most portfolio starters skip: a second identity boundary inside the tenant, isolation enforced by the database, background work that is transactional and safe on several instances, time-zone-correct generation, safe uploads, outbound webhooks without SSRF, and plan entitlements.
  - The portal is the headline: v1 shows that organizations are isolated; v2 shows a second boundary (organization → client) for people who are not staff, proven the same way.
- **Alternatives considered from the v1 "later" list (SPEC 4.2):**
  - **Real billing with Stripe:** valuable, but it needs the owner's Stripe account and inbound webhooks, and payment flows are already the subject of Fitness Merchant. Plan entitlements give the same "what you get depends on the plan" mechanic without a payment provider. Still later.
  - **Custom roles:** the permission design (D008) makes them a data change, but a small business rarely notices them. Still later.
  - **Kanban board:** generic; the dispatch board answers the same need (see the work, move it) in the shape this domain uses.
  - **SSO, platform-admin console:** enterprise and operator features with little value in a demo of small businesses.
  - **Comments on jobs:** replaced by more specific features (quote comments, problem reports, work requests).
- **Trade-offs:** about 86 hours across ten pull requests, similar to v1. The cut list in `PROGRESS.md` says what goes first if time runs short; row-level security, the portal and its isolation matrix, the outbox, and the upload and webhook safety rules are not on it.

## D038: Row-level security with a separate runtime role (decided at M14, below)
- **Date:** 2026-11-16
- **Decision (as proposed; see "Decided at M14" for what changed):** every tenant-owned table gets `ENABLE` and `FORCE ROW LEVEL SECURITY` and a policy on `organization_id = current_setting('app.organization_id')::uuid`, set per transaction with `set_config(..., true)`. The application runs as `tenant_hub_app`, which owns nothing and is not a superuser; migrations run as the owner. Supersedes the "deferred" part of D007.
- **Why:**
  - It is the layer that also catches raw SQL and any query that forgets the filter, which D007 left to code review.
  - In v1 the application connects as the PostgreSQL image's `POSTGRES_USER`, which is a superuser. Superusers bypass row-level security, so policies alone would change nothing; the role split is the real work.
  - A transaction-local setting cannot leak to the next request on a pooled connection, which was the risk that made D007 defer it.
- **To decide at M14:**
  - how the allow-listed bypasses get through: a second `BYPASSRLS` role used only by the allow-listed code, or `SECURITY DEFINER` functions for exactly those queries;
  - how reads outside an explicit transaction get the setting (an interceptor that opens one, or a connection-level setting reset on return to the pool, proven by a test);
  - query plans with the policy, by `EXPLAIN` on the jobs list and the audit timeline;
  - how Testcontainers, Compose and `.env.example` create the second role.
- **Alternatives:** schema or database per tenant (rejected in D005); policies with the app still connecting as owner (bypassed, so pointless); a view per tenant (impractical with EF Core).
- **Decided at M14 (2026-11-17):**
  - **Users and configuration.** `ConnectionStrings:Database` is now the API's own user, `ConnectionStrings:DatabaseAdmin` the owner. With `MigrateOnStartup`, the API migrates as the owner, then creates or repairs its own user from the runtime connection string: no superuser, no `BYPASSRLS`, no `CREATEROLE`/`CREATEDB`/replication; data rights on every table except the migration history; `EXECUTE` on the definer functions. The password is passed as a query parameter into transaction-local settings, never in SQL text. It is set only when signing in with it fails, so a normal start writes nothing to the role catalog. Startup refuses an API user that is a superuser, has `BYPASSRLS` or owns a table. If the database cannot be reached it logs a warning and starts, as before (readiness reports the database).
  - **No `FORCE ROW LEVEL SECURITY`** (changed from the proposal). `FORCE` binds the table owner, which is the migration role (data fixes in migrations would see nothing) and the owner of the definer function (which would then see nothing either). The API's user never owns a table (startup check), so `FORCE` would protect nothing more.
  - **Settings are session-level, set before commands, not per transaction** (changed from the proposal). EF Core reads outside transactions, and a scope enters its tenant part-way through (after resolution; after creating an organization). `RowSecurityInterceptor` sets `app.organization_id`, `app.user_id` and `app.invitation_token_hash` before any EF Core command whose scope differs from what the connection last received. Open, close and rollback make it set them again. Npgsql also resets session state when a connection returns to its pool: verified, the same backend showed `''` after the release. So there are two independent guards against a leak, and a test runs four scopes on one physical connection. Unset values are `''`, which the policies turn into null, matching no row.
  - **Bypasses, as rules instead of switches.** No second role. The read-only bypasses become permissive `SELECT` policies that say what is allowed. `own_memberships`: a user's own memberships, for the session, tenant resolution, the five-owned count and "already a member". `presented_invitation`: the invitation whose token hash the request holds. The allow-listed files declare them (`RowSecurity.ReadAsUser`, `PresentInvitationToken`), and the architecture test keeps those calls to the allow-listed files. The demo reset (K2) needed a real cross-tenant question. It asks the `SECURITY DEFINER` function `user_has_tenant_references(uuid)`, owned by the owner, with `EXECUTE` revoked from `PUBLIC`, which returns one boolean; DemoReset left the `AcrossTenants()` list. A test pins the list of definer functions.
  - **One connection per tenant request.** With a pooled connection per query, every query paid a pool reset and a separate `set_config` round trip. Jobs-list p50 rose from 12.5–13.0 ms to 19.0 ms in the first measurement. `TenantResolutionFilter` now opens the connection before resolving, and EF Core keeps it until the request ends. Measured results are in `PROGRESS.md`. The trade-off: a request holds its connection while it runs, including during an invitation's SMTP send.
  - **Query plans** (`EXPLAIN ANALYZE`, 10,000 jobs, as the owner and as the API's user). The jobs page, an open-jobs count and the audit timeline keep their index scans, and the policy becomes a one-time filter, because the query already filters on `organization_id`. Execution is 0.12–0.40 ms either way. A query with no filter uses the policy expression as its index condition.
  - **Tests.**
    - `RowSecurityTests` (10, in the isolation suite) and `DatabaseRoleTests` (4).
    - Tests that arrange or inspect with raw SQL now connect as the owner, so the database guarantees (append-only trigger, composite keys) are shown to hold even for it.
  - **Mutation checks** (recorded in `PROGRESS.md`):
    - no jobs policy: the coverage test names `jobs`;
    - no query filter on `Job`: the matrices still pass (272/272);
    - no filter and no policy: the matrix fails on 5 job endpoints;
    - no settings: 415 of 485 integration tests fail. All 134 matrix failures are 500s from PostgreSQL refusing writes (`42501`, new row violates row-level security) while the fixture signs up its users: the layer fails closed;
    - the definer function answering "no": the demo-reset regression test fails (an old visitor who is only a member of a surviving organization would be deleted).

## D039: A transactional outbox and database-held schedules (decided at M15, below)
- **Date:** 2026-11-16
- **Decision (as proposed):** side effects (email, notifications, webhooks, file clean-up) are written to `outbox_messages` in the same `SaveChanges` as the change, and a hosted worker delivers them, claiming rows with `FOR UPDATE SKIP LOCKED`. Recurring work lives in `scheduled_tasks` with a due time and a lease. Invitation emails keep their synchronous v1 contract (D011).
- **Why:** a change and its side effects cannot disagree (no email for a rolled-back change, no lost email for a committed one), several instances can run without double delivery, and a host that sleeps catches up. The demo reset (D032) already keeps its schedule in the data; this generalizes it.
- **Alternatives:** sending after commit inside the request (v1; lost on a crash, slows the request); a message broker (another service to run for a one-container product); Hangfire or Quartz (a dependency with its own tables for what two small tables and one worker do).
- **Decided at M15 (2026-11-17):**
  - **Writing.**
    - `IOutbox.Add` puts a tracked row in the request's `AppDbContext`, so whichever `SaveChanges` writes the change and its audit entries writes the message too, in the same transaction.
    - That already gives what the brief asked of "extending the audit step", so no separate step was added. The step will matter when domain events become messages (M20).
    - A message type says whether it belongs to an organization (taken from the tenant context, never from the caller) or to a person (account emails have no organization).
  - **Secrets.**
    - A type that carries a secret is encrypted with Data Protection under the purpose `TenantHub.Outbox` plus the type name, so one type's payload cannot be read as another's.
    - The keys are in PostgreSQL (K1), so every instance can decrypt. The row holds the ciphertext as a JSON string.
    - A delivered secret message is deleted at once. A dead one keeps its row for the record, but its payload is replaced by `null`.
  - **Dispatching.**
    - A short transaction claims up to 20 due rows (`FOR UPDATE SKIP LOCKED`, oldest `available_at` first). It pushes each one's `available_at` 5 minutes ahead, which is the claim's lease, and counts the attempt.
    - Delivery runs outside any transaction, in a scope of its own, with a 1-minute timeout (shorter than the lease).
    - The outcome is written only if `attempts` still has the value this claim set. The attempt count is the fencing token: a dispatcher that outlived its lease cannot overwrite the next claim.
    - A dispatcher that dies leaves its messages to come back when the lease ends.
  - **Retries.**
    - After failed attempts 1 to 5, the message waits 1 min, 5 min, 15 min, 1 h and 4 h. The sixth failure makes it dead (about 5.4 hours after the first try) and logs a warning.
    - An unknown type, an unreadable payload and email switched off are permanent failures: dead at once.
    - The row keeps the error's class (an exception type, or a delivery code such as `email-not-accepted`), never its message, which can quote an address.
  - **Idempotency.**
    - Handlers must tolerate a repeat after a crash between delivery and recording.
    - SMTP cannot be made exactly-once, so the email handler gives every email the message id as its `Message-ID`, and mail systems see a repeat as the same message.
  - **Waking.** An in-process signal wakes the dispatcher when a transaction that wrote messages commits (a transaction interceptor; without a transaction, after the save), so an email leaves within moments. Otherwise the dispatcher polls every 30 seconds.
  - **Scheduler.**
    - SPEC-V2 8 asked for the primary key `(name, organization_id)`, but a primary key cannot hold the null of an application-wide task. The table has a surrogate `id` and a unique index on `(name, organization_id)` with `NULLS NOT DISTINCT`.
    - A short transaction claims a due task with `SKIP LOCKED` and gives it a 10-minute lease. The task runs outside that transaction and is completed only while the lease is still its own. The lease time is the fencing token; it is truncated to microseconds so it survives the round trip to PostgreSQL.
    - The next due time is counted from the run, so a host that slept through ten intervals runs the task once on waking.
    - A failed run is retried in 5 minutes, with its error class recorded.
    - Rows of tasks this build does not know are left alone.
    - Application-wide tasks get their row at start (`ON CONFLICT DO NOTHING`, so two instances make one row).
  - **Index (2026-11-18, review after M16):** with a row per organization that has agreements, the claim (`ORDER BY due_at LIMIT 1 FOR UPDATE SKIP LOCKED`) scanned the whole table; `ix_scheduled_tasks_due` makes it an index scan (PROGRESS, Measured results).
  - **Retention** is the first task: every hour, it deletes delivered messages after 7 days and dead ones after 30.
  - **The demo reset moved onto the scheduler,** as the brief allowed if the change was small.
    - `DemoResetService` is gone. `DemoResetTask` (demo mode only) asks `DemoReset` whether a reset is due, and returns when the next one is.
    - The schedule still lives in the seeded organizations' creation time (D032).
    - The lease now keeps two instances from resetting at once, which D032 could not.
  - **Hosting.**
    - The dispatcher and the scheduler are plain classes in Infrastructure; two hosted services in the Api project drive them (Infrastructure has no hosting dependency, D032).
    - `BackgroundWork:Enabled=false` turns both off. The integration tests do that and drive them one step at a time.
  - **Tokens: Identity's token flow, not a table of hashed tokens.**
    - `UserManager` makes and checks reset and verification tokens. The security stamp inside them makes a reset token single use and voids older ones.
    - Identity's `DataProtectorTokenProvider` reads the system clock, so expiry could not be tested. `AccountTokenProvider` does the same with the injected `TimeProvider`, and carries the user id, so a link holds one opaque token.
    - Reset tokens live 30 minutes, verification tokens 7 days.
    - A table would also have been a new table that is not tenant-owned, which needs the owner's approval.
    - Consequence: changing the password also voids open verification links. The banner sends a new one.
  - **Password reset.**
    - The request always answers `202` with no body, and does the same cheap work for any address: no password hashing, no SMTP. A test compares the median times of known and unknown addresses.
    - It is limited per client address (`RateLimiting:AccountEmailPermitLimit`, 5 per 10 minutes) and per account (one email per 2 minutes). The time of the last email is kept in Identity's `user_tokens`, so there is no new table.
    - With email off, nothing is written.
    - A successful reset rotates the security stamp, so every session ends (this browser's too). It also ends a lockout: proving the mailbox is as good as the old password.
  - **Email verification is required only while email is on.**
    - Without email no link could be sent, and nothing the account does sends mail (invitations hand the link to the inviter). So the demo and deployments without SMTP ask nothing.
    - These accounts count as verified: accounts from before M15 (the migration), demo accounts (the seed), and accounts made from an invitation link (the link went to that address, or the inviter vouched for it).
    - The session's `emailVerified` means "verified, or not asked".
    - Inviting, and sending an invitation again, refuse with `403 email-not-verified`.
    - `POST /api/auth/verify-email` is public (the link may be opened in any browser) and idempotent.
    - It is rate-limited like the other token links (the invitation policy), not like sign-in: it checks no password and sends nothing.
  - **Links** carry the token in the fragment (`/reset-password#…`, `/verify-email#…`), as invitations do (D028). The token never reaches the server's or a proxy's logs, or a mail scanner that prefetches links.
  - **Allow-lists.**
    - `outbox_messages` and `scheduled_tasks` were added to the model's not-tenant-owned list, as SPEC-V2 8 approved.
    - The dispatcher and the scheduler needed no tenant bypass: both tables are not tenant-owned, and no M15 handler touches tenant data. So none was added. M16 and M20 will add theirs when their handlers enter a tenant.
  - **A pre-existing test-order bug** was fixed on the way.
    - The archive and restore matrix cases changed the shared standing client. A refused restore left it archived, so "create a job" for it failed with 400 whenever xUnit's order ran it later.
    - It failed on `dev` locally on 2026-11-17. Both cases now use a fresh client.

## D040: Client contacts are a separate identity with email-link sign-in (decided at M18, below)
- **Date:** 2026-11-16
- **Decision (proposed):** client contacts are rows in `client_contacts`, not ASP.NET Core Identity users. They sign in with a one-time link (hashed token, 15 minutes, single use) and get their own cookie scheme. Portal endpoints are a fourth endpoint class with their own isolation matrix, and row-level security also checks `client_id` in portal sessions.
- **Why:** a contact belongs to exactly one client of one organization. Making contacts users would mix them into the membership model, where one mistake could give a client staff access. A cleaning company's clients will not keep a password for it. A separate scheme means a staff cookie never authenticates a portal request, and the reverse.
- **Alternatives:** Identity users with a "contact" role (shared tables and code paths with staff); public tokenized links per visit (no session, no list of visits, links forwarded around); passwords (friction, and a second password-reset flow).
- **Trade-offs:** sign-in depends on email, so the demo needs Mailpit or a demo shortcut like the v1 demo buttons (decided at M18).
- **Decided at M18 (2026-11-18):**
  - **Contacts** (`client_contacts`, tenant-owned and client-owned): at most 5 active per client, email unique among them (a filtered unique index as backstop), no seats. Removing is final: the row stays for the audit and the requests, and giving access again is a new contact, so an old session cannot come back to life. Adding one needs a verified staff email (SPEC-V2 4.1) and sends an invitation through the outbox that carries no secret, only the portal's address.
  - **Sign-in.**
    - The link is `/portal/{slug}/sign-in#token`: 32 random bytes, SHA-256 stored, 15 minutes, spent by one conditional `UPDATE … WHERE used_at IS NULL`, so two requests with one link sign in once.
    - Asking always answers 202 with no body. Unknown portals, plans without the portal, unknown or removed contacts, archived clients and the cooldown all end silently.
    - Rate limits: 5 link requests per address per 10 minutes, and one link a minute per contact (the per-email limit).
    - The sign-in email is a secret outbox message, encrypted at rest and deleted once sent (D039).
  - **No tenant bypass was needed.** Changed from SPEC-V2 8, which had approved one for portal sign-in. Before a session exists, the request names its organization by slug, as a staff route names it by id, and the handler enters that one organization with no user and no permissions. The tenant filter and row-level security then apply exactly as for any request: it reads one contact by email, or one link by its hash. So `POST /api/portal/sign-in/confirm` takes `{ orgSlug, token }` instead of `{ token }`; the SPA has the slug from its URL. No `AcrossTenants()` call or widened policy was added.
  - **Session.**
    - A cookie scheme of its own: `th_portal`, path `/api/portal`, `HttpOnly`, `SameSite=Strict`, `Secure` outside local http, 7 days, not sliding.
    - A middleware gives every `/api/portal` request the portal identity or none, before authorization and antiforgery run. So a staff cookie cannot authorize a portal endpoint, a portal cookie never reaches a staff one, and portal antiforgery tokens are bound to the contact.
    - The portal's `XSRF-TOKEN` cookie has path `/portal`. The portal pages read it before the staff one; the staff pages never see it.
  - **Portal-scoped endpoints** are a fourth class (`MapPortalGroup`).
    - The portal policy, then `PortalResolutionFilter`. On every request it checks that the plan has the portal, the client is active and the contact is not removed. Otherwise it clears the cookie and answers `401 portal-session-ended`.
    - The scope enters as the contact (`TenantContext.EnterAsContact`): no user, no staff permissions.
    - Handlers take the client from `PortalContext` and name it in every query.
  - **The client boundary in the database:** `app.client_id` is set in portal sessions, and a RESTRICTIVE `client_isolation` policy on clients (by id), jobs, agreements, contacts and work requests narrows every other policy. Staff sessions leave it unset and are unaffected. The save guard refuses writes for another client, and the audit names the contact (`actor_kind = contact`).
  - **Mutation checks (2026-11-18):**
    - The handlers' client filters removed, policies kept: the portal matrix still passes, 22/22.
    - The policies removed as well: 6/22 fail.
    - `PortalResolutionFilter` removed: 23/49 portal tests fail.
  - **Visits** are the client's jobs: upcoming means still open; past means completed or cancelled in the last 12 months. Contacts see the assignee's display name only.
  - **Demo.** The seed gives three Brightline clients with an upcoming quoted job a contact (the client's own contact person), dated before the organization. In demo mode, which sends no email, `POST /api/portal/demo/sign-in` (404 otherwise) signs in such a contact without a link. Only contacts older than the organization qualify, so a contact a demo visitor adds is never offered or accepted.
  - **Endpoint shape changes** (SPEC-V2 7 updated): `DELETE /contacts/{id}` instead of `/clients/{id}/contacts/{contactId}` (a contact belongs to one client; one id keeps the matrix uniform), and `GET /requests/open-count` for the sidebar.

## D041: Visits are jobs generated from agreements on a rolling horizon (decided at M16, below)
- **Date:** 2026-11-16
- **Decision (as proposed):** an agreement's occurrences become ordinary jobs, 60 days ahead, generated by the scheduler and idempotent on `(agreement_id, occurrence_on)`. Recurrence is a small value object in the domain (weekly, monthly by day, monthly by nth weekday) over dates in the organization's time zone, not RFC 5545.
- **Why:** visits then get the v1 workflow, list, board, audit and limits for free. A materialized horizon keeps every read a plain query (no expansion in lists or on the board) and makes "edit this visit" ordinary. Dates rather than instants avoid daylight-saving problems; "today" already uses the organization's zone (SPEC 5.4, D030, D032).
- **Alternatives:** expanding occurrences at read time (every list, count and limit would need it); generating everything up front (unbounded rows, and an edit rewrites the whole future); an RFC 5545 library (much more than the three patterns these businesses use, and a dependency to vet).
- **Trade-offs:** generated visits count toward the open-jobs limit, so a Free organization with a weekly agreement uses some of its 50 open jobs; the agreement page says when generation stops at the limit.
- **Decided at M16 (2026-11-18):**
  - **Recurrence.**
    - `Recurrence` holds only the fields of its kind and compares by value, the weekdays included.
    - `Occurrences(anchor, from, to)` is pure. Intervals count from the start: weeks from the Monday of the start's week, months from the start's month. So asking from a later date keeps the cycle.
    - Days 29–31 are refused; "the last day" covers month ends.
    - `Schedule` adds the start and an end date or a number of visits. The number counts from the start, so visits before "from" use it up.
    - The rule is stored as `jsonb` and validated again on the way in.
  - **Generation rules are pure domain functions** (`VisitGeneration`).
    - What is missing is the occurrences from today (never the past, so resuming never back-fills) to today + 60 that have no visit yet. A skipped or cancelled visit still exists, so its date is not generated again.
    - Across the agreements of a run, the nearest dates come first. When the open-jobs room runs out, the far end waits, and those agreements are marked held.
    - The application loads, under the organization lock, the agreements and their existing dates, asks, and writes.
  - **Where it runs.**
    - In the command that creates, edits or resumes an agreement, in the same transaction under the organization lock, so the visits are there when the page opens.
    - Daily per organization on the scheduler (D039): a per-organization `scheduled_tasks` row, written with the first agreement in the same transaction. It is due five minutes after the organization's next local midnight, so after downtime one run catches up, since today's horizon covers every missed day.
    - The run enters the organization as the system (`TenantContext.EnterAsSystem`): no user and no permissions, the tenant guard and row-level security as for a request. No tenant bypass was needed.
    - The unique index `(organization_id, agreement_id, occurrence_on)` (filtered on agreement visits) is the backstop under the lock.
  - **A visit** is a job with `agreement_id`, `occurrence_on`, `changed_by_hand` and the template's fields, scheduled and due on its date. Its creator is the agreement's creator; its audit entries name "System (agreement <title>)".
  - **Edits.**
    - A following visit is the agreement's, today or later, New or Scheduled, and not changed by hand.
    - On an edit, following visits on a date the schedule still has take the template's fields, with the status following the assignee as at generation. Those on a dropped date are deleted, recorded as the system: nobody touched them, and deleting keeps a dropped date from blocking a later schedule. New dates are generated in the same transaction.
    - Any edit of a visit through the job API sets `changed_by_hand`. The agreement page lists those visits.
  - **Status.**
    - Pause stops generation and keeps the visits.
    - Resume checks that the client is active and that the plan has room for another active agreement.
    - End cancels the visits from today on that are New or Scheduled ("Agreement ended"), whether or not they were changed by hand, and keeps the rest. An ended agreement cannot change.
    - Archiving a client pauses its active agreements (reason "client archived"), recorded as the archiving user's change.
    - Deleting a client, which needs it to have no jobs, deletes its agreements, which then have no visits either.
    - Removing a member clears them from agreement templates, as v1 unassigns their open jobs.
  - **Limits.**
    - Active agreements: Free 3, Team 200, Business unlimited. They count in `PlanUsage` and therefore in downgrade checks and the plan meters.
    - When the open-jobs limit stops generation, the agreement stays active with `paused_reason = PlanLimit`. A system audit entry is written when the hold starts and when it ends, not on every daily run. The daily run carries on when room appears.
  - **Audit (SPEC-V2 5.9).**
    - `actor_kind` (`User`, `Contact`, `ApiKey`, `System`) and `actor_ref` are added; existing rows default to `User` (a column default, not an UPDATE of the append-only table).
    - An auditable event may name its own actor (`AuditActor`). That is how a generated visit inside a user's command is still recorded as the system.
    - A scope without a user is recorded as the system.
    - The actor name is capped at 80 characters, so long titles are shortened in "System (agreement …)".
  - **Jobs gain `estimated_minutes`** (1–1440) in the API and the job form. A PUT replaces every field, so leaving it out would have wiped a visit's estimate.
  - **The preview** (`POST /agreements/preview`) needs only `agreements.read`: it saves nothing, so it is not audited (a reviewed exception in the audit rule test).
  - **Seed.**
    - Brightline's three agreements started weeks ago: created that morning, the daily runs of each past visit day replayed, and each past visit done by its assignee.
    - Harbor & Pine's agreement is created last, today. It fills the room left under the Free plan's 50 open jobs and shows the plan-limit hold, the "near its limits" story of SPEC 8.3. Created earlier in the replay, the planned jobs created after it pushed the organization to 51.
  - **Measured:** see PROGRESS, Measured results. The first generation for 200 agreements takes 1.8–3.5 s while holding the organization lock; that happens once, when they are created together. A daily run adding 100 visits takes 144–299 ms.
- **Review after M16 (2026-11-18, `#0016a`):**
  - A visit can no longer be deleted (`409 visit-not-deletable`, `canDelete` false): its row is what keeps its date from being generated again, so a deleted New or skipped visit came back with the next daily run. Visits are skipped or cancelled; only an edit of the agreement withdraws untouched ones.
  - When the open-jobs limit cuts a run short, `generated_through` is the day before the first date left out, not the horizon, so "visits exist up to" is true.
  - The agreements list orders active, paused, ended.
  - The form starts a new agreement on the organization's today (its time zone, from the loaded organization), not the browser's, and previews only when a schedule field changes.
  - `scheduled_tasks` is indexed on `due_at` for the claim (D039).

## D042: Plan entitlements next to numeric limits (decided at M18, below)
- **Date:** 2026-11-16
- **Decision (proposed):** `PlanPolicy` gains features (portal; API and webhooks) and two limits (active agreements, file storage). A missing feature is `403 plan-feature-unavailable`. A downgrade that would turn features off needs a confirmation (`confirmFeatureLoss`) and keeps the data.
- **Why:** real SaaS plans differ by feature as much as by quantity; checking both in one domain policy keeps the rules in one tested place (D013). Keeping data on downgrade makes an accidental downgrade reversible.
- **Alternatives:** refusing the downgrade while a feature is in use (forces the Owner to delete contacts or webhooks first); feature flags outside the plan (a second system for the same question).
- **Decided at M18 (2026-11-18):** `PlanFeatures` in the domain: the portal on Team and Business, integrations on Business (used from M21). A missing feature is `403 plan-feature-unavailable` with `feature` and the cheapest plan that has it; every contact, quote and request endpoint checks it. A feature is *in use* while it has data that would stop working: the portal while any client has an active contact. A downgrade checks limits first (`plan-downgrade-blocked`), then features in use: `409 plan-feature-loss` with `features` until the Owner repeats it with `confirmFeatureLoss: true`. The audit says what was turned off. Data is kept: contacts lose access at once (the portal filter checks the plan) and get it back with an upgrade. `GET /api/orgs/{orgId}` lists the plan's features and every plan's.

## D043: Uploaded photos are re-encoded with SkiaSharp and stored outside the database (M19)
- **Date:** 2026-11-16 (proposed); decided 2026-11-19 (M19).
- **Decision:**
  - **Pipeline.** An upload is read up to 10 MB (one byte more is `413 photo-too-large`). It is accepted by its first bytes only (JPEG, PNG or WebP signatures), whatever its name or declared type, and the decoder must agree. Anything else is `400 photo-not-accepted`.
  - **Size before pixels.** The size in pixels is read from the header before any pixel is decoded: at most 12,000 px on a side, 50 megapixels for a JPEG (which decodes at a reduced scale) and 25 for PNG or WebP (which decode whole). Beyond that the answer is `413`. At most two uploads decode at once.
  - **Re-encoding.** The camera's orientation is applied to the pixels. The first frame is decoded into sRGB, resized to at most 2,560 px on the long edge, plus a 320 px thumbnail. Both are encoded in the upload's format with no colour profile, EXIF, XMP or text. Nothing of the original file but its pixels is stored.
  - **Storage.** Files go through an `IFileStore` port with a local-directory implementation under keys the application makes: `{organization}/{128 random bits}.{ext}`. A key that does not match that shape is refused before any path is built. The production image creates `/app/data/files` for its non-root user, and Compose mounts a volume there.
  - **Write order.** Files are written **before** the transaction. Under the organization lock, the photo count (10 per job) and the storage limit are checked again and the row is saved. If anything fails after the write, the files are deleted at once. A crash in between leaves files without a row: nothing serves them, the plan does not count them, and the sweep removes them. A row never points at a missing file, which writing after the commit could not promise.
  - **Storage limit.** The limit is a `SUM` of the organization's rows under the lock, not the `storage_bytes_used` counter in SPEC-V2 8. Rows are also deleted by cascade (a job, an organization), which a counter would have to follow by hand. The organization's rows are covered by the tenant-first index. `bytes` counts the photo and its thumbnail.
  - **Clean-up.** Removing a photo or deleting a job writes a `files.delete` outbox message with its keys. Deleting an organization writes one with its folder. The message type has no organization, so the organization's cascade does not delete it. The demo reset deletes organizations without the outbox, so it removes their folders itself. A weekly `files.sweep` task removes files older than a day that no row names, and folders of organizations that no longer exist. It reads each organization in its own scope as the system, inside row-level security.
  - **Serving.** Photos are served only through the job's or the portal visit's endpoints, with the stored type, `Content-Disposition: inline` under a file name of ours, `nosniff`, and `Cache-Control: private, max-age=3600`.
  - **CSP.** `img-src` gains `blob:` only, for the preview shown while a photo uploads. Photos are same-origin, so `'self'` already serves them. A pipeline test checks that no other directive changed.
- **The image library, researched 2026-11-19:**
  - **SixLabors.ImageSharp 4.1.3** (2026-10-09). Fully managed, and its published advisories are fixed: the September 2026 TIFF and ICC ones from 4.1.2 and 3.2.0 (GHSA-jjfr-hcj7-qf5w and others), the August TIFF fax ones (GHSA-jj3q-cwqj-842r, GHSA-v76p-62qx-wwq2), and the 2025 GIF loop (GHSA-rxmq-m78w-7wmc, CVE-2025-54575). Licence: the Six Labors Split License, Apache 2.0 for open-source use.
    - The owner first chose it. Building it then showed that from 3.2.0 and 4.0.0 a **build-time licence-key check fails every Release build** ("No Six Labors license found"), so CI and the Docker image would fail.
    - The free community key needs the owner to apply at licensing.sixlabors.com, names the assembly, and expires.
    - 3.1.12 has no key check but carries the advisories above, which the NuGet gate would fail. Presented to the owner again.
  - **SkiaSharp 4.154.0 with SkiaSharp.NativeAssets.Linux.NoDependencies 4.154.0** (both published 2026-10-28, 22 days old), chosen by the owner.
    - MIT, and no key or account.
    - The Linux native library needs only glibc (`libpthread`, `libdl`, `libm`, `libc`, `ld-linux`; no fontconfig). Verified in the real image: the seed decoded and re-encoded 13 photos at startup in `aspnet:10.0.15-noble-chiseled-extra`.
    - Past the August 2026 fix for CVE-2026-58641 (fixed in 4.148). Every later stable release (4.154.1 to 4.157.0) was under two weeks old.
    - Trade-off: native code in the image, so future decoder fixes come through SkiaSharp releases. `dotnet list package --vulnerable` covers it in CI.
  - **Sources:**
    - nuget.org pages and the registration API for both packages (publish dates);
    - github.com/SixLabors/ImageSharp/security/advisories and the GitHub advisory pages;
    - docs.sixlabors.com/articles/imagesharp (licence keys);
    - the package's own `build/SixLabors.ImageSharp.targets`;
    - github.com/SixLabors/ImageSharp/discussions/3129;
    - herodevs.com/vulnerability-directory/cve-2026-58641;
    - the SkiaSharp.NativeAssets.Linux.NoDependencies description.
- **Alternatives:** `bytea` in PostgreSQL (simple, but bloats the database and its backups); storing files as uploaded (keeps location metadata and trusts the client's content type); writing files after the commit (a failed write leaves a row without its file); a storage counter (drifts under cascades).

## D044: API keys and HMAC-signed webhooks with an SSRF guard (pending M21)
- **Date:** 2026-11-16
- **Decision (proposed):** API keys are random, shown once, stored as SHA-256 hashes with a visible prefix, and map to a fixed permission set (read-only or read-write) that never includes administration. Webhooks are delivered from the outbox, signed as `t=<unix>,v1=<HMAC-SHA256(secret, "t.body")>`, retried for about a day, and sent only to public addresses checked at connection time, without redirects.
- **Why:** receivers already know this signing pattern from widely used platforms (a timestamp against replays, a version prefix for rotation). The SSRF guard is the part most webhook implementations miss: a customer-supplied URL must not reach the server's own network.
- **Alternatives:** OAuth client credentials (more than a small-business integration needs); signing without a timestamp (replayable); checking the URL only when it is saved (DNS can change afterwards).

## D045: v2 numbering and documents (M13)
- **Date:** 2026-11-16
- **Decision:** v2 continues the milestone numbers (M13 is this plan, M14–M23 the work) and the branch rule `#00nn-…`, so the CI branch-name check and the history stay uniform. `docs/SPEC-V2.md` holds the v2 specification while it is being built; `SPEC.md` stays the v1 contract until M23 merges the two. Story ids continue in `docs/USER-STORIES.md`.
- **Why:** a separate document keeps what is built apart from what is planned while the work is in progress, without rewriting `SPEC.md` up front.
- **Trade-offs:** two specifications for about ten milestones; SPEC-V2 names the `SPEC.md` section wherever it changes a v1 rule.

## D046: The dispatch board: one week query, one move command, Move to for keyboards (M17)
- **Date:** 2026-11-18
- **Decision:**
  - **Reading.** `GET /board` returns one week, Monday to Sunday in the organization's zone. `week` may be any day of it; the default is the current week. It returns:
    - the week's cards, filtered by client and status;
    - the open jobs with no day, nearest due first, capped at 50 with the total (the jobs list has the rest);
    - the load per person and day, a SQL `GROUP BY` of estimates over jobs that are not cancelled, ignoring the filters so that a filter never hides how full a day is;
    - the people: active members whose role can work on jobs, plus a former member who still has a card that week;
    - `canPlan` (`jobs.manage`), so the page never re-derives permissions (D008).
  - **Moving.** `POST /jobs/{id}/schedule` (`jobs.manage`) takes the assignee, the day (or none), the version shown and `moveDueDate`. One domain method, `Job.Schedule`, applies it:
    - closed jobs are refused (`409 job-closed`);
    - a day after the due date is refused (`409 after-due-date`) unless the due date moves along;
    - a New job given a person and a day becomes Scheduled through the ordinary transition;
    - no day keeps the person and the status, as the job form allows (SPEC-V2 5.2 as built);
    - a visit moved by hand is marked `changed_by_hand`;
    - the audit entry reads "Moved JOB-0042 on the board: assignee, scheduled date".
  - **Why a command and not `PUT /jobs/{id}`:** a PUT replaces every field, so the board would have to send fields it does not show, and an edit made meanwhile to the title would conflict with a move. The board changes two fields, so it gets its own audited step with the same version check.
  - **Capacity.** `organizations.daily_capacity_minutes`, 60–1440 minutes, default 480 (SPEC-V2 5). `PATCH /orgs/{id}` takes it as an optional field, so an older client that sends only the name and time zone keeps it. Settings asks for hours.
  - **No optimistic moves.** A card stays where it is until the API answers; then the board reloads (new versions, new load). A refused move needs no rollback, and a conflict shows what the other person did.
- **Deviations from SPEC-V2 4.3, logged here:**
  - **Arrow-key moves are left out.** "Move to…" on every card gives the keyboard path WCAG 2.5.7 asks for: any person, any day, any week, or back to "Not scheduled". Arrow keys on the grid would need a roving focus that competes with the screen reader's own table navigation (the grid is a table with row and column headers). They can be added later without changing the API.
  - **The day-by-day list is used below 1024 px, not 768.** The seven columns need about 900 px; at 768 they squeezed each card to about 90 px. Measured in the browser pass at 375/768/1024/1280.
- **Findings in the browser pass (production image, real CSP):**
  - **CSP.** CDK drag-and-drop loads its "resets" as a runtime `<style>` on the first pickup. `CspSafeStyleLoader` (D023) skips it, so the rules (`.cdk-drag-preview` in `@layer cdk-resets`, and `pointer-events: none` inside the preview and placeholder) are in `styles.css`. The layer is declared before Tailwind's, so it ranks below every utility, as the CDK intends. A spec fails if picking up a card inserts a `<style>`, and was seen to fail without the loader. Re-check on every CDK upgrade.
  - **DI.** Cards first rendered through `ngTemplateOutlet`, whose embedded views take their injector from where the template is declared. Each `cdkDrag` therefore found no `cdkDropList`, became a free drag and never dropped. Cards are now a component placed inside each list. A spec checks every drag's container, and was seen to fail on the previous page.
  - **Date field.** Binding the day input's `[value]` to a signal its own input handler set made Chrome wipe the whole date when one part was cleared, because a half-typed date reads as `""`. The value is now written once and read on submit. "Half-typed" is asked of `validity.badInput` at that moment, because clearing the last part fires no input event. This is checked in Chromium by a script, since jsdom has no date segments.
  - **Overflow and focus.** Screen-reader text inside the scrolling grid escaped it, because absolutely positioned elements use the nearest positioned ancestor, and widened the page at 1024 px; the scroll area is now `relative`. After a keyboard move, focus goes to the moved card's "Move to…", or to the heading when the card left the week.
- **Indexes:** none added. EXPLAIN as `tenant_hub_app` with 500 jobs per organization: week cards 0.48 ms, queue 0.14 ms, load 0.22 ms, each through `ix_jobs_organization_id_client_id` (PROGRESS, Measured results). An `(organization_id, scheduled_on)` index is the change to make if one organization reaches tens of thousands of jobs.
- **Seed:** one-off demo jobs get estimates (60–210 minutes, none on every seventh), taken from the job's index rather than the seeded random sequence so the rest of the demo data is unchanged.
- **Alternatives:** a client-side week assembled from `GET /jobs` (one request per filter combination and the load computed in the browser); optimistic moves with rollback (more state, and a conflict would first show the wrong place); a separate "capacity per member" (SPEC-V2 out of scope).

## D047: Quotes and work requests in the portal (M18)
- **Date:** 2026-11-18
- **Decision:**
  - **Quotes (SPEC-V2 5.4).**
    - The approval lives on the job (`approval_status` and who and when). Staff with `portal.manage` send a New or Scheduled job with a quoted amount, for a client with an active contact.
    - While the approval is Pending or Declined, the job cannot move to In progress (`409 quote-not-approved`). This is the one condition added to the v1 transition table; the transition options show it as the reason.
    - A contact decides once. The decision records the approved amount, the contact and an optional comment of up to 1,000 characters. The job's version check makes two contacts deciding at once one decision; the loser gets `409 quote-already-decided`.
    - Jobs from before quotes default to NotRequested and behave exactly as in v1.
  - **Reset.** Changing the amount *or the client* of a Pending or Approved quote resets it to NotRequested and records `job.quote_reset`. Wider than SPEC-V2 5.4, which names only an approved quote's amount: a pending quote with a new amount would otherwise be approved for a price the client never saw, and a new client's contacts would decide on another client's quote.
  - **Work requests (SPEC-V2 5.5).**
    - A request is client-owned. It stays Open until converted or declined, with no other moves. A contact may have at most 10 open requests, and the preferred day must not be before the organization's today.
    - Converting is the job form, prefilled from the request through the URL; saving posts to `/requests/{id}/convert`. That creates the job (limit-checked like any new job) and converts the request in one transaction, through a hook in `JobCommands.CreateAsync`. The job is always for the request's client, whatever the form says, so the contact can follow it. It also needs `jobs.create`, checked in the handler, since a route declares one permission (D028).
    - Declining needs a reason of 1–500 characters, which the contact reads.
    - Deleting a job that a request became (a New job can be deleted) clears the link; the request stays converted. Otherwise the foreign key would turn the delete into a 500.
  - **Staff screens:** the job page's "Client approval" panel (`canSend` and `cannotSend` from the API); the Requests inbox with the status in the URL and an open count in the navigation; the client page's portal contacts.
  - **Portal screens:** its own lazy area and layout, and the 401 from a portal call goes to that portal's sign-in. Two defects found in the browser pass and fixed, each with a regression spec shown failing first:
    - a link followed over an already open sign-in page changes only the fragment, which the page never read;
    - a session answer from before the sign-in, arriving after it, put the contact back to signed out.
- **Alternatives:** a separate quotes table (a job has one price; history is in the audit log); converting by a server-side copy without the form (staff could not adjust the job before it exists); keeping a deleted job's link (a dangling reference).

## D048: Checklists belong to the job; ticking is work, not an edit (M19)
- **Date:** 2026-11-19
- **Decision:**
  - **Where it lives.** The checklist is part of the `Job` aggregate (a child collection in `checklist_items`). The job decides who may change it: whoever may edit the job, while it is New, Scheduled or In progress. A completed or cancelled job's checklist and photos stay as they are until it is reopened (`409 job-closed`), since they are what the client signs off.
  - **The precondition.** Completed is refused while a required item is open (`409 checklist-incomplete` with `openItems`). The transition options show the same reason, and the domain tests run it against every v1 transition. Every loader that can move a job to Completed includes the checklist. A test completes through the API, so a missing include would show.
  - **One writer.** Checklist changes and status changes take the job's row lock (`FOR NO KEY UPDATE`). Without it, an untick racing a completion left a completed job with an open required item in the first round of a race test. With it, eight rounds hold.
  - **Ticking.** Ticking records who and when, but leaves the job row (and its version) alone, so ticking never makes someone's job edit a conflict. Adding, editing, removing or moving an item on a visit marks it changed by hand (SPEC-V2 5.1), like any edit.
  - **Agreements.** An agreement's checklist template (`jsonb`, at most 30 items) is copied into each generated visit. Following visits take a changed template too, as long as nothing on their checklist is ticked: a visit being worked through keeps the list it was started with.
  - **Client sign-off.** On the job: `client_feedback`, its text, when, and the contact (an extra column beyond SPEC-V2 8, so staff see who answered). Once per completed visit; reopening clears it. Two contacts at once make one sign-off through the version check.
  - **Portal.** A visit's checklist and photos are shown only once it is completed. `checklist_items` and `attachments` have no client of their own, so their client policy (`EnableClientIsolationThroughJob`) reads `jobs` under that table's policies. A coverage test requires it for every table of a job's rows.
  - **Mutation checks.** Without that policy, the two row-level security tests fail. Without the portal handler's client filter, the policy alone keeps the portal matrix green.
- **Alternatives:** the checklist as its own aggregate (then the precondition needs the caller to pass it in, and a forgotten load is silent); bumping the job's version on every tick (concurrent edits and ticks would conflict all day); an optimistic retry instead of the row lock (more code for the same order).

## D049: The session cookie is not reissued on every request (M19)
- **Date:** 2026-11-19
- **Found:** a staff photo came back with `Cache-Control: no-cache, no-store`, not the `private` cache SPEC-V2 5.6 asks for.
  - The security stamp is validated on every request (D022's zero interval, so a password change ends other sessions at once).
  - Identity's validator also asks the cookie handler to renew the cookie after each check.
  - So every staff response carried a new `Set-Cookie`, and the handler marks such responses `no-store`.
- **Decision:** after the validator runs, `ShouldRenew` is set back to false. The stamp is still checked on every request, and sliding expiration still renews the cookie once half of its seven days have passed. The photo test fails without the change; the account and session tests pass with it.
- **Alternatives:** a cache header only on photo routes (overwritten by the handler anyway); a longer validation interval (a changed password would leave other sessions open for that long).
