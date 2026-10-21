# M07: Audit log viewer and append-only guarantee

Branch: `#0007-audit-log-viewer-and-append-only`

## Goal
Admins and Owners can search the organization's activity, and the database guarantees that entries are never edited.

## Context
SPEC 4.1 (audit log), 7 (`GET /audit`), 8.1 (audit_entries); D012. User story US-40.

## Scope
1. A migration adding a trigger that rejects `UPDATE` on `audit_entries`; an integration test proves it with raw SQL.
2. `GET /api/orgs/{orgId}/audit` with filters (actor, action, entity type, entity id, date range in the organization's time zone) and keyset or offset paging (choose after measuring with 50,000 entries; record the decision).
3. Review every audit summary written so far for clarity and consistency; changed fields show human-readable values (names, not ids, where the entity exists).
4. Isolation and permission matrices extended (`audit.read` only for Admin and Owner).
5. Angular: audit log page with filters in the URL, a readable timeline/table hybrid (actor, time relative and absolute, summary, expandable changed fields), links to entities that still exist, empty and error states.

## Out of scope
CSV export (V2), retention policies.

## Tests required
Trigger test; filter tests against a SQL oracle; time-zone boundary for the date range; permission and isolation rows; web filter store.

## Acceptance criteria
- All tests pass.
- In the browser: actions done in earlier milestones appear with correct summaries; filters work and survive reload; Members cannot reach the page (navigation hidden, API `403`).

## Verification required
Tests; browser pass; the query plan with 50,000 entries recorded in PROGRESS.
