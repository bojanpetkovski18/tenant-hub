# CLAUDE.md: rules for AI agents working on this repository

## Project summary
TenantHub is a multi-tenant back-office for small service businesses: organizations sign up, invite their team, manage clients and jobs, and every change is audited. Its real subject is the SaaS foundation: tenant isolation enforced in depth and proven by tests, server-enforced role permissions, invitations, plan limits and an append-only audit log. It is an independent portfolio project built with AI assistance. Specification: `docs/SPEC.md`. User stories: `docs/USER-STORIES.md`. Decisions: `DECISIONS.md`. Status: `PROGRESS.md`. Checklists and definition of done: `docs/CHECKLISTS.md`. Milestone briefs: `prompts/`.

The human owner is a professional full-stack developer who supervises, reviews and merges every PR. Explain important decisions briefly; do not explain basics.

## Stack
- **Backend:** ASP.NET Core on .NET 10 LTS (Minimal APIs), EF Core with Npgsql, PostgreSQL, ASP.NET Core Identity (cookie auth). Four projects: Domain, Application, Infrastructure, Api (D003).
- **Frontend:** Angular 22, standalone components, zoneless, signals, NgRx SignalStore, Tailwind CSS v4 and Angular CDK (D014).
- **Tests:**
  - xUnit v3 unit tests (domain and handlers);
  - integration tests with `WebApplicationFactory` and Testcontainers PostgreSQL, including the tenant-isolation suite;
  - architecture tests;
  - Angular component tests (Vitest);
  - one Playwright E2E journey.
- **Infra:** Docker Compose for local dev (PostgreSQL, Mailpit); one production image (the API serves the SPA); GitHub Actions CI; hosting decided at M12 (D016).

Exact versions are pinned in the project files; see `DECISIONS.md` for the reasons.

## Target repository layout
```
src/
  api/
    TenantHub.Domain/          aggregates, value objects, domain events, rules (no EF, no ASP.NET Core)
    TenantHub.Application/     Features/<Feature>/ one handler per command or query; ports (ITenantContext, IAppDbContext)
    TenantHub.Infrastructure/  DbContext, configurations, migrations, Identity, tenancy guard, audit writer, email, seed
    TenantHub.Api/             endpoints per feature, auth, tenant middleware, problem details, security headers
  web/                         Angular app
    src/app/core/              session and organization stores, interceptors, guards, layout
    src/app/shared/            presentational UI components, pipes
    src/app/features/<feature>/
tests/
  TenantHub.Domain.Tests/
  TenantHub.Application.Tests/
  TenantHub.ArchitectureTests/
  TenantHub.IntegrationTests/  includes Isolation/ (coverage tests and cross-tenant matrix)
  e2e/                         Playwright
docs/                          SPEC.md, USER-STORIES.md, CHECKLISTS.md, ARCHITECTURE.md (M12), portfolio/ (M12)
prompts/                       session starter and milestone briefs
.github/workflows/
Dockerfile, docker-compose.yml, docker-compose.dev.yml, TenantHub.slnx
```

## Commands
Fill this in only after a command has actually been run and verified. Format: command, then what it does, then the date verified.

None yet: the solution is created in M01.

## Working rules
- **One milestone at a time.**
  - Read the milestone brief in `prompts/`, then `PROGRESS.md` and the relevant parts of `docs/SPEC.md`.
  - Implement only that scope: no extra features, no unrelated refactors, no premature abstraction.
- **Inspect before changing; preserve existing work.**
  - Look at files and `git status` before editing.
  - Ask before deleting files or running destructive commands (`git reset --hard`, force push, dropping databases or volumes).
- **Ask when blocked.**
  - If an ambiguity changes the design, ask one clear question.
  - Otherwise choose the simplest reasonable option, state the assumption, and log it in `DECISIONS.md`.
- **Tests are part of the feature.**
  - Never delete, skip or weaken a test to get green.
  - For a bug: write the regression test first, show it failing, then fix.
  - Every new tenant-owned entity and every new endpoint must be covered by the isolation suite; the coverage tests fail if it is not. Never add an entity or endpoint to an allow-list to make them pass without the owner's approval.
- **Verify for real.**
  - Run the build and tests after each step and read the output.
  - For UI work, run the app and use it in a real browser at 375, 768 and 1280 px; check the console and keyboard navigation.
  - Before deployment, test the production build too: CSP and other production-only behaviour differ from the dev server.
- **Never claim what was not verified** (compiles, tests pass, endpoint or page works, isolation, performance, security). Say plainly what could not be run.
- **Measure before stating numbers.** Record the method, machine and date in `PROGRESS.md` → Measured results. Numbers in the README or docs must come from there.
- **Time-sensitive facts** (versions, hosting limits, prices): research current sources and record what was found and when. New dependencies are pinned to a version that is at least two weeks old.
- **Known issues** the owner chooses to defer go to `PROGRESS.md` → Known issues. Remind the owner when they become relevant, and in M10.

## Git
- Start each milestone from a freshly pulled `dev`, on a new branch `#00nn-descriptive-name`: the milestone number plus 3–7 kebab-case words saying what it delivers. The planned names are in `PROGRESS.md`.
- Commit in small steps; each commit builds and passes its tests. Subjects are short and imperative ("Add client list endpoint"), with one logical change per commit.
- Push the branch when done. The owner opens and merges the PR into `dev`. Never push to `dev` or `main` unless told to. Never force-push shared branches.
- End commit messages with the co-author trailer given by the environment, if any.

## Code standards
- **Strict typing everywhere.**
  - C#: nullable reference types enabled, with `TreatWarningsAsErrors`.
  - TypeScript: `strict`, `strictTemplates`, `noImplicitOverride`, `noPropertyAccessFromIndexSignature`.
  - No `any` without a comment explaining why.
- **Layering (D003), enforced by architecture tests.**
  - Domain references nothing else in the solution and no framework packages.
  - Application references Domain (and EF Core abstractions); never Infrastructure or Api.
  - Endpoints are thin: bind, declare the permission, call a handler. No business logic.
  - Light CQRS: one handler class per command or query (`CreateJobHandler`, `ListJobsQuery`), registered directly. No mediator framework and no generic repository.
- **Domain rules live in the domain.** State transitions, the last-Owner rule, role grants and plan policy are domain code with unit tests; handlers orchestrate.
- **Tenancy.**
  - Tenant-owned entities implement `ITenantOwned`; never set `OrganizationId` by hand outside the guard and seeding.
  - Never take the organization from a body, query string, header or claim; only from `ITenantContext`.
  - `IgnoreQueryFilters()` only in the allow-listed places (SPEC 3.3).
- **Database work belongs in the database.** Filtering, sorting, paging and aggregation run in SQL; never load whole tables. Use `AsNoTracking` and projections for reads.
- **Money** is `decimal` / `numeric(12,2)`, never floating point.
- **Validation and errors.**
  - Server-side validation is authoritative; client-side validation is a convenience.
  - Errors are RFC 7807 problem details with a `traceId`, and a stable `code` for business-rule errors.
- **UI states and accessibility.**
  - Every screen has loading, empty, error and success states.
  - Everything interactive has an accessible name and keyboard support; visible focus; respect `prefers-reduced-motion`.
  - The UI hides actions the user lacks permission for, using the permissions from `GET /api/me`; it never re-implements the role map.
- **Logging and time.**
  - Structured logging with organization id and user id in scope. Never log secrets, passwords, cookies, tokens, invitation links or full request bodies.
  - Time comes from an injected `TimeProvider`; timestamps are UTC; "today" for an organization uses its time zone.
- **Style.** Match the surrounding code: naming, comment density and idioms. Comments explain *why*, not *what*.

## Security rules
- **Secrets.**
  - No secrets in the repository. Configuration comes from environment variables.
  - `.env.example` contains placeholders only; `.env` is git-ignored.
- **Authentication and authorization.**
  - Every endpoint requires authentication except the public ones listed in SPEC section 7.
  - Tenant-scoped endpoints require an active membership (`404` otherwise) and declare a permission (`403` otherwise).
  - Resource-level rules (assigned jobs, Owner rules) are checked in handlers or the domain, and tested.
- **Queries.** Parameterized only (EF Core LINQ, or `FromSql` with interpolation parameters). Sort columns and other identifiers come from fixed allow-lists, never from input.
- **Tokens.** Invitation tokens are random (32 bytes), stored hashed, compared in constant time, single use and never logged.
- **Abuse protection.**
  - Rate-limit sign-up, sign-in, invitation creation and invitation acceptance.
  - Antiforgery on cookie-authenticated unsafe requests.
- **Headers and containers.**
  - Security headers: strict CSP (no inline scripts), HSTS in production, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`.
  - Containers run as non-root.
- **Before the repository goes public,** scan the files and the full git history for secrets and personal data, and report the result.

## Data and legal rules
- **Fictional data only.**
  - Organizations, people, clients and jobs are invented.
  - Emails use the reserved `example` domain (`*.example`).
  - Phone numbers are obviously fake.
- **No real brand names or logos.** Any artwork is original to this repo.
- **No real personal data, and no employer code, data or business rules, ever.**
- The README must state that the project was built with AI assistance.

## Milestone report format
At the end of every milestone, give exactly this:
```
## Milestone report: <name>
Status: complete | partial | blocked
Changed: <files/areas>
Verified (commands actually run and results):
- <command>: <result>
Not verified / could not run: <list or "none">
Acceptance criteria: <each criterion, pass/fail, how checked>
Assumptions and decisions logged: <list>
Known issues: <list or "none">
Suggested next step: <one line>
```
After every push, give a pull request description in the chat, not in a file, as raw Markdown inside a fenced code block so the owner can paste it into GitHub, plus the compare link `https://github.com/bojanpetkovski18/tenant-hub/compare/dev...<branch>?expand=1` with `#` encoded as `%23`. The description covers the whole branch, starts with a line naming its readers, and has these sections:
- What changed (grouped)
- Verified locally (a table of real results)
- Not verified
- Review notes
- Out of scope

Before the report, update `PROGRESS.md` and `DECISIONS.md`, plus this file's Commands section for any newly verified commands.
