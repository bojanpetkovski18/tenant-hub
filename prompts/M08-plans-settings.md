# M08: Plans, limits and organization settings

Branch: `#0008-plans-limits-and-organization-settings`

## Goal
Plan limits hold under concurrent requests, Owners can change plans (demo billing), and organizations can be renamed, re-timezoned and deleted.

## Context
SPEC 4.1 (plans and limits, organization settings), 5.5, 7 (organization and plan endpoints); D013. User stories US-50 and US-51.

## Scope
1. Domain: `PlanLimits` and `PlanPolicy` (can this change happen given this usage; which limits block a downgrade); unit tests for every boundary.
2. Enforcement: the commands that consume a limit (invite, accept invitation, create or restore client, create or reopen job) lock the organization row, count usage and refuse with `403 plan-limit-reached` (limit name, limit, usage).
3. Concurrency test: one seat left, two simultaneous invitations → exactly one succeeds. Repeat for clients and jobs. Show one test failing without the lock (mutation check, recorded).
4. Endpoints: `GET /` with usage, `PATCH /` (name, time zone validated against IANA names), `PUT /plan`, `DELETE /` (name confirmation; cascades; audit entry kept only in the logs since the organization's entries are deleted).
5. Isolation, permission and audit matrices extended.
6. Angular: Settings with General (name, time zone picker), Plan and usage (meters, plan cards, downgrade refusal listing the blocking limits) and Danger zone (delete with typed confirmation); the shared "limit reached" message with a link to the plan page for Owners; after deletion, the user returns to the organization picker.

## Out of scope
Real payments (V2), proration, invoices.

## Tests required
Policy boundaries; concurrency tests; downgrade refusal; delete cascade (no rows left for that organization in any tenant-owned table, checked by enumerating the model); web meters and refusal display.

## Acceptance criteria
- All tests pass, including the concurrency tests in repeated runs (record how many).
- In the browser: reach a limit on the Free organization and see the message; upgrade, continue, try to downgrade and see the blocking limits; rename; change the time zone and see overdue markers change; delete an organization.

## Verification required
Tests; browser pass; the mutation check of the lock recorded in PROGRESS.
