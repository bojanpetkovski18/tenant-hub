# M09: Dashboard, full seed data and demo mode

Branch: `#0009-dashboard-seed-data-and-demo-mode`

## Goal
A dashboard that summarizes the organization, realistic fictional data that makes every screen convincing, and a demo mode that lets a visitor try every role safely.

## Context
SPEC 4.1 (dashboard, demo mode), 8.3; user stories US-60 and US-61. Check the `dataviz` guidance before building the chart.

## Scope
1. `GET /api/orgs/{orgId}/dashboard`: tiles, jobs by status, my next 7 days, plan usage, and recent activity for `audit.read`. One round trip; aggregation in SQL; "this month" and "overdue" in the organization's time zone.
2. Seed: the organizations, people, clients and jobs from SPEC 8.3, generated through the domain (so audit history is consistent) with a fixed random seed; dates relative to "today" so the demo never looks stale. Idempotent.
3. Demo mode (`DEMO_MODE=true`):
   - `GET /api/demo/accounts` and `POST /api/demo/sign-in { role }`;
   - sign-in page buttons "Try as Owner / Admin / Member / Viewer" with one-line explanations of each role;
   - scheduled reset of the seeded organizations (configurable interval, stored schedule so restarts do not reset early) and deletion of visitor organizations older than 24 hours;
   - email disabled, stricter rate limits, a banner "Demo: data resets every N hours";
   - demo accounts cannot change their password or be removed from their seeded organizations.
4. Angular: the dashboard (tiles linking to filtered lists, an accessible bar chart with a table alternative, my next 7 days, usage meters, recent activity); the landing page (`/`) with a short product pitch and "Try the demo".
5. Isolation and permission matrices extended; the demo seed and reset are the approved `IgnoreQueryFilters` uses.

## Out of scope
Polish beyond these screens (M10).

## Tests required
Dashboard numbers against a SQL oracle on the seed; time-zone month boundary; seed idempotency and determinism; demo endpoints off when demo mode is off; reset restores the seed and removes old visitor organizations; demo account protections.

## Acceptance criteria
- All tests pass.
- In the browser: each demo role opens a convincing organization; dashboard tiles match the lists they link to; the reset works; the landing page reads well at three widths.

## Verification required
Tests; browser pass for each demo role; dashboard numbers compared with `psql` and recorded in PROGRESS.
