# Progress

Estimates are rough focused-work hours for agent and owner together, including review. Status values: `todo`, `in progress`, `in review`, `done`, `blocked`.

| # | Milestone | Branch | Estimate | Status | Verified on | Notes |
|---|---|---|---|---|---|---|
| P0 | Project setup (spec, stories, rules, briefs, CI) | `#0000-project-setup` | 2 h | done | 2026-10-21 | PR #1 merged; CI green on the PR and on `dev` (secret scan, repo lint) |
| M01 | Skeleton, architecture tests, CI, app shell | `#0001-solution-skeleton-architecture-tests-and-shell` | 6 h | in progress | none | Adds the merge-gate CI the owner asked for |
| M02 | Accounts and organizations | `#0002-accounts-sign-up-and-organizations` | 8 h | todo | none | |
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

Machines:
- **M1:** AMD Ryzen 7 8840HS (16 logical CPUs), 14 GB RAM, Windows 11 Pro 10.0.26200, Node 24.20.0, .NET SDK 10.0.402, Docker 29.8.2.

## Known issues
Deferred by the owner. Re-check them when relevant, and in M10.

_None yet._

## Next step
The owner creates the empty GitHub repository `tenant-hub`; the agent pushes `main`, `dev` and `#0000-project-setup`; the owner sets `dev` as the default branch, opens and merges the PR. Then M01 starts.
