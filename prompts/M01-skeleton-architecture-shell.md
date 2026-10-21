# M01: Solution skeleton, architecture tests, CI and app shell

Branch: `#0001-solution-skeleton-architecture-tests-and-shell`

## Goal
A building, tested and linted skeleton: the four backend projects with their dependency rules enforced by tests, a local PostgreSQL and Mailpit via Docker Compose, CI that enforces the quality gates, and an Angular app shell (layout, navigation, theming, routing) that later milestones fill in.

## Context
- SPEC sections 6 and 9; decisions D003, D004, D014, D015, D017.
- Re-check current stable versions before pinning anything (rule: at least two weeks old) and record them with release dates in DECISIONS.md (update D003, D004, D014):
  - .NET 10 SDK and runtime patch (10.0.402 / 10.0.13 on 2026-10-21);
  - EF Core and Npgsql;
  - Angular (22.2.3 was the latest on 2026-10-21) and `@ngrx/signals` (22.0.1);
  - Tailwind CSS v4, the PostgreSQL and Mailpit images, xUnit v3, Testcontainers, the architecture-test library (NetArchTest or ArchUnitNET: pick one, record why).
- Fitness Merchant solved the same setup problems (Microsoft.Testing.Platform via `global.json`, Vitest as the Angular runner, Tailwind v4 with CSP). Reusing the owner's own patterns is fine; read them, do not copy blindly.

## Scope
1. `TenantHub.slnx`, `global.json`, `Directory.Build.props` and `Directory.Packages.props`:
   - `Nullable` enabled, `TreatWarningsAsErrors` on, analyzers at `latest-recommended`;
   - Central Package Management.
2. Projects as in CLAUDE.md: `TenantHub.Domain`, `TenantHub.Application`, `TenantHub.Infrastructure`, `TenantHub.Api` with the references allowed by D003 and nothing else.
3. `TenantHub.Api`:
   - `GET /health/live` (liveness; database readiness comes in M02);
   - problem details with `traceId` for every error, including unknown routes and unhandled exceptions;
   - JSON console logging;
   - security-headers middleware (CSP, nosniff, Referrer-Policy, Permissions-Policy, frame-ancestors);
   - OpenAPI plus Scalar in Development only;
   - options bound from environment variables and validated at startup (fail fast).
4. Test projects: `TenantHub.Domain.Tests`, `TenantHub.Application.Tests`, `TenantHub.ArchitectureTests`, `TenantHub.IntegrationTests` (`WebApplicationFactory`). Each has at least one real test.
5. Architecture tests:
   - Domain depends on no other solution project and no ASP.NET Core or EF Core assembly;
   - Application does not depend on Infrastructure or Api;
   - Infrastructure does not depend on Api.
   Show each one failing once (temporarily add a forbidden reference) and record that in PROGRESS.
6. `src/web` Angular app:
   - standalone, zoneless, strict TypeScript and templates; ESLint (angular-eslint, including template accessibility rules) and Prettier;
   - Tailwind v4 with design tokens as CSS variables for light and dark themes (SPEC 6 design direction: slate surfaces, one teal accent);
   - theme toggle that respects `prefers-color-scheme` and remembers the choice;
   - app shell: skip link; sidebar with an organization-switcher placeholder and navigation (Dashboard, Jobs, Clients, Members, Audit log, Settings); top bar with a user-menu placeholder; drawer below 1024 px using a CDK focus trap;
   - a public layout for the landing, sign-in and sign-up placeholders;
   - lazy routes with placeholder pages for every route in SPEC section 6, plus a real 404 page;
   - dev proxy `/api` and `/health` → API;
   - `@ngrx/signals` installed with one trivial store (theme) to prove the setup.
7. `docker-compose.dev.yml`: PostgreSQL and Mailpit, named volume, healthchecks, credentials from `.env`; ports bound to `127.0.0.1`.
8. Extend `.github/workflows/ci.yml` (secret scan and repository lint since P0) with: API restore, build with warnings as errors and all tests; web `npm ci`, lint, format check, tests and production build; NuGet and npm caching.
9. Fill the CLAUDE.md "Commands" section with every command actually run and verified.

## Out of scope
Database schema, authentication, real pages, the production Dockerfile (M11) and hosting.

## Tests required
- API: liveness returns 200; security headers are present; an unknown route returns problem details with `traceId`.
- Architecture: the three dependency rules.
- Web: the shell renders the navigation; the theme toggle switches and persists; the 404 route renders; the drawer opens and closes.

## Acceptance criteria
- `dotnet build` passes with warnings as errors; all tests pass locally and in CI (verified on the pushed branch; if it cannot be checked from here, say so).
- `npm run lint`, the format check, the tests and `npm run build` pass.
- In a browser at 375, 768 and 1280 px: no horizontal scroll; the drawer opens and closes with the keyboard and Escape and focus returns to the toggle; the theme toggle works and persists; the console is clean; the skip link works; focus is visible in both themes.

## Verification required
Run every command and read the output. Use the app in a real browser at the three widths with one keyboard-only pass. Report the pinned versions with their release dates.
