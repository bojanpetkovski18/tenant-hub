# Progress

Estimates are rough focused-work hours for agent and owner together, including review. Status values: `todo`, `in progress`, `in review`, `done`, `blocked`.

| # | Milestone | Branch | Estimate | Status | Verified on | Notes |
|---|---|---|---|---|---|---|
| P0 | Project setup (spec, stories, rules, briefs, CI) | `#0000-project-setup` | 2 h | done | 2026-10-21 | PR #1 merged; CI green on the PR and on `dev` (secret scan, repo lint) |
| M01 | Skeleton, architecture tests, CI, app shell | `#0001-solution-skeleton-architecture-tests-and-shell` | 6 h | done | 2026-10-21 | PR #2 merged; CI green on the PR (all 7 gates) and on `dev`. 38 API tests (incl. architecture rules shown failing) + 16 web tests; 115/115 browser checks on the dev server and 115/115 on the production build under the API CSP; merge-gate CI (D020) |
| M02 | Accounts and organizations | `#0002-accounts-sign-up-and-organizations` | 8 h | in review | 2026-10-21 | 109 API tests (54 domain, 38 integration incl. Testcontainers) + 52 web tests; 96/96 browser checks on the dev server and on the production build under the API CSP; lock and migration-guard mutation checks |
| M03 | Tenancy core and isolation tests | `#0003-tenancy-core-permissions-and-isolation-tests` | 8 h | todo | none | The project's centrepiece; do not rush |
| M04 | Clients and audit recording | `#0004-clients-composite-keys-and-audit-recording` | 8 h | todo | none | Sets the list, form and audit patterns |
| M05 | Members and invitations | `#0005-members-and-invitations` | 8 h | todo | none | |
| M06 | Jobs | `#0006-jobs-workflow-numbering-and-lists` | 10 h | todo | none | |
| M07 | Audit log viewer | `#0007-audit-log-viewer-and-append-only` | 5 h | todo | none | |
| M08 | Plans, limits and settings | `#0008-plans-limits-and-organization-settings` | 6 h | todo | none | |
| M09 | Dashboard, seed data, demo mode | `#0009-dashboard-seed-data-and-demo-mode` | 8 h | todo | none | |
| M10 | Polish | `#0010-polish-states-responsive-a11y-performance` | 8 h | todo | none | |
| M11 | E2E, Docker, Compose | `#0011-e2e-production-docker-and-compose` | 6 h | todo | none | |
| M12 | Demo hosting, README, portfolio | `#0012-demo-hosting-readme-and-portfolio` | 6 h | todo | none | Hosting chosen here (D016) |

Total estimate: about 89 hours. Fitness Merchant's 15 milestones were estimated at 92 hours and finished in 7 calendar days; that is a reference point, not a promise.

## Risks
- **Isolation harness complexity (M03).** Enumerating endpoints and building the cross-tenant matrix generically is the hardest part. Mitigation: start with a plain table of cases and automate the enumeration only for the coverage checks.
- **Concurrency tests that flake (M06, M08).** Mitigation: deterministic two-transaction tests alongside the parallel HTTP tests, and repeated runs recorded.
- **Scope creep in the UI.** Mitigation: the list, form and dialog patterns are built once in M04 and reused.
- **Free hosting limits (M12).** Mitigation: the demo also runs with one command locally and in Codespaces.

## Cut list
If the schedule slips, these move to V2 in this order, with the owner's approval:
1. Landing page (M09): the sign-in page with demo buttons is enough.
2. Account page (US-04): display name and password change.
3. Audit viewer filters beyond actor and date range (M07).
4. Organization time-zone editing (M08): fixed at sign-up.

The isolation suite, permissions, invitations, the job workflow, plan limits under concurrency and the audit trail are not on the cut list: they are the project.

## Measured results
Every number cited in the README or docs must appear here, with its method, machine and date.

| Metric | Value | Method | Machine | Date |
|---|---|---|---|---|
| CI tool checksums (gitleaks 8.30.1 linux_x64, actionlint 1.7.12 linux_amd64, editorconfig-checker 4.0.2 linux-amd64) | all three match the values pinned in `ci.yml` | each release's published checksum file (`sha256sum` of the downloaded archive for editorconfig-checker); `git ls-remote` of `actions/checkout` tag v7.0.1 = `3d3c42e` | M1 | 2026-10-21 |
| P0 repository checks | actionlint: 0 findings; editorconfig-checker: 0 findings; gitleaks `git` (all commits) and `dir`: no leaks; tracked files contain no e-mail addresses outside `*.example` and no local paths (only the owner's GitHub username in the compare-link template) | Windows builds of the same tool versions, checksum-verified against each release's checksum file; `git grep` for e-mail and path patterns | M1 | 2026-10-21 |
| .NET test suite after M01 (38 tests: 14 domain, 10 application, 7 architecture, 7 integration) | 3.1–3.2 s | `dotnet test --solution TenantHub.slnx` (MTP summary), Debug and Release | M1 | 2026-10-21 |
| Web initial bundle after M01 (shell, router, theme store; layouts and pages lazy) | 261.64 kB raw / 69.66 kB estimated transfer | `npm run build` output (Angular CLI estimate, not a network measurement) | M1 | 2026-10-21 |
| Browser checks, M01 | dev server 115/115, production build under the API's CSP 115/115: 5 routes × 375/768/1280 px × light/dark with no horizontal scroll, sidebar or drawer by width, 0 serious/critical axe findings, clean console and 0 CSP violations; keyboard pass at 375 px (skip link, focus ring, drawer focus trap, Escape returns focus, navigation closes the drawer); theme persists across reload | Playwright 1.63.0 + @axe-core/playwright 4.14.0, Chromium; production build served by a static server sending the exact CSP from `SecurityHeadersMiddleware` | M1 | 2026-10-21 |
| Skip-link bug found in the browser pass | before the fix, Enter on the skip link went to `/#main` (the landing page); regression test shown failing, then passing | Playwright probe; Vitest `app-shell.spec.ts` | M1 | 2026-10-21 |
| Merge-gate checks run locally | `dotnet format --verify-no-changes` exit 0 (exit 2 on an injected violation); `dotnet restore --locked-mode` exit 0 (NU1004 on an injected version drift); branch-name regex and WIP-commit filter tested on 9 and 6 samples; actionlint 0 findings; editorconfig-checker 0 findings on the staged tree | commands in CLAUDE.md; tools checksum-verified | M1 | 2026-10-21 |
| Dependency vulnerabilities after M01 | npm 0 (4 before the D019 patches: 3 high, 1 moderate); NuGet 0 including transitive | `npm audit`; `dotnet list package --vulnerable --include-transitive` | M1 | 2026-10-21 |
| Dev services | PostgreSQL 18.6 and Mailpit healthy via `up -d --wait`; ports bound to 127.0.0.1 only; Mailpit UI 200 | `docker compose -f docker-compose.dev.yml ps`; `psql select version()`; `curl` | M1 | 2026-10-21 |
| .NET test suite after M02 (109 tests: 54 domain, 10 application, 7 architecture, 38 integration with Testcontainers PostgreSQL 18.6) | 13.8–14.8 s (2 runs) | `dotnet test --solution TenantHub.slnx --no-build` (MTP summary) | M1 | 2026-10-21 |
| Web tests after M02 | 52 tests, 3 consecutive passes | `npm run test:ci` | M1 | 2026-10-21 |
| Web initial bundle after M02 (HttpClient, forms, session store; pages lazy) | 313.13 kB raw / 85.01 kB estimated transfer | `npm run build` output (Angular CLI estimate) | M1 | 2026-10-21 |
| Owned-organization limit under concurrency | with the row lock: exactly 4 of 8 parallel creates succeed (5 of 5 runs); without it: all 8 succeed (3 of 3 runs) | `Concurrent_requests_cannot_exceed_the_owned_organization_limit`, lock removed by hand for the mutation check | M1 | 2026-10-21 |
| Browser checks, M02 | 96/96 on the dev server and 96/96 on the production build served with the API's exact CSP (API proxied): sign-up, reload, create and switch organization, two tabs in two organizations, cookie flags (`th_session` HttpOnly + SameSite=Strict; `XSRF-TOKEN` readable + SameSite=Strict), localStorage only `th-theme`/`th-last-org`, name and password change, wrong password, guard redirect and return, unknown organization, keyboard sign-out via the user menu, keyboard empty submit; 6 screens × 375/768/1280 px × light/dark with no overflow and 0 serious/critical axe findings; 0 CSP violations | Playwright 1.63.0 + @axe-core/playwright 4.14.0, Chromium, seeded dev database | M1 | 2026-10-21 |
| Bugs found in the M02 browser pass | 2: axe label-content-name-mismatch on the menu buttons (18 findings); CDK overlay `<style>` blocked by the CSP (regression test shown failing first). Both fixed | as above | M1 | 2026-10-21 |
| API log check | 675 lines after the browser pass: no passwords, session or XSRF cookies; SQL parameter values masked (`?`) | `grep` of the API console log | M1 | 2026-10-21 |
| Antiforgery from the command line | sign-in without `X-XSRF-TOKEN`: 400 "Invalid or missing anti-forgery token."; with it: 200 | `curl` through the dev proxy | M1 | 2026-10-21 |

Machines:
- **M1:** AMD Ryzen 7 8840HS (16 logical CPUs), 14 GB RAM, Windows 11 Pro 10.0.26200, Node 24.20.0, .NET SDK 10.0.402, Docker 29.8.2.

## Known issues
Deferred by the owner. Re-check them when relevant, and in M10.

| # | Issue | Found | Plan |
|---|---|---|---|
| K1 | Data-protection keys (which sign session and antiforgery cookies) use ASP.NET Core's default store. Fine on a developer machine, but a container would lose them on restart and sign everyone out. | M02 | Persist them (database or mounted volume) with the production image in M11; Fitness Merchant solved it with a database key repository (its D032). |

## Next step
The owner reviews and merges the M02 PR once **CI result** is green (and applies the branch ruleset from `docs/CI.md` if not done yet). Then M03 (tenancy core and the isolation test harness) starts from a freshly pulled `dev`.
