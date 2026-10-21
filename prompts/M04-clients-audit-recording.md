# M04: Clients, composite keys and audit recording

Branch: `#0004-clients-composite-keys-and-audit-recording`

## Goal
The first complete tenant-scoped feature: clients with list, detail and edit screens. It introduces the patterns every later feature follows: composite tenant keys, optimistic concurrency, URL-synced list state, and audit entries written from domain events in the same transaction.

## Context
SPEC 4.1 (clients, audit log), 5.6, 7 (clients endpoints), 8.1 (clients, audit_entries), 8.2; D012. User story US-20. Confirm D012 (how changed fields are captured) and record it.

## Scope
1. Domain: `Client` with `Update`, `Archive` and `Restore`, raising domain events; unit tests for the rules in 5.6.
2. Persistence: `clients` with unique `(organization_id, id)` (the target for later composite foreign keys), the partial unique index on active names, `xmin` concurrency; `audit_entries` table and indexes (the `UPDATE` trigger comes in M07).
3. Audit recording: a SaveChanges step collects domain events from tracked aggregates and writes audit entries (actor id and name snapshot, action, entity, summary, changed fields, trace id) before commit. Unit tests for the summaries; an integration test that a failure after the change leaves neither the change nor the entry.
4. Endpoints from SPEC 7 for clients, with server-side search, status filter, allow-listed sorting and paging; `409` with a `code` for duplicate names, concurrency conflicts and deleting a client that has jobs (the job check activates in M06).
5. Isolation: add a `TenantEndpointCase` line for every clients endpoint (cross-tenant and permission matrices), including B's client ids under A's route; the coverage tests must pass without allow-list changes. Clients are the first tenant-owned entity keyed by `id` alone: add the guard test that moving a client to another organization is refused (D024).
6. A test that every state-changing endpoint writes at least one audit entry (a table extended by later milestones).
7. Angular:
   - a reusable list pattern: a SignalStore for query state synced with the URL, a data table (sortable headers with `aria-sort`, paging, empty and error states, skeleton rows) that becomes cards on narrow screens;
   - clients list, detail (information and a placeholder for jobs), create and edit forms with server errors mapped to fields, archive and restore with confirmation, delete with the `409` explanation;
   - permission-based visibility (Viewer sees no edit actions) and a conflict dialog with "reload".
8. Dev seed: about 40 clients for the larger organization.

## Out of scope
Jobs (M06), the audit viewer (M07), limits on active clients (M08).

## Tests required
Domain rules; handlers (success, validation, not found, conflict); API permission and isolation rows; audit entries and rollback; web list store URL sync, form errors, Viewer visibility.

## Acceptance criteria
- All tests pass, including the isolation and permission matrices with the new endpoints.
- In the browser: create, edit, search, sort, page, archive, restore and delete clients at the three widths; reload keeps the list state; two tabs editing the same client produce the conflict dialog; a Viewer cannot edit.
- `audit_entries` rows exist for each action, checked with `psql`.

## Verification required
Tests, browser pass, `psql` check of audit rows, and `EXPLAIN` of the list query with the seed data (recorded in PROGRESS).
