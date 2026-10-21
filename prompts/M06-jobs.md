# M06: Jobs: workflow, numbering, lists and detail

Branch: `#0006-jobs-workflow-numbering-and-lists`

## Goal
Jobs are created for clients, numbered per organization, assigned to members and moved through the workflow, with resource-level permissions (members work only on their own jobs) and a filterable list.

## Context
SPEC 4.1 (jobs), 5.4, 7 (jobs endpoints), 8.1 (jobs), 8.2 (Job aggregate); D008. User stories US-30 to US-33.

## Scope
1. Domain: the `Job` aggregate with the transition table from 5.4 as data, `TransitionTo` with actor and reason, date and amount rules, domain events; `Organization.NextJobNumber`.
2. Persistence: `jobs` with the composite foreign keys to clients and memberships, unique `(organization_id, number)`, `xmin`; indexes chosen from `EXPLAIN` of the list query on a dataset of 10,000 jobs (record the plans).
3. Numbering: the counter is incremented under the organization-row lock in the same transaction; a concurrency test creates jobs in parallel and checks there are no gaps or duplicates.
4. Endpoints from SPEC 7; `GET /jobs/{id}` returns the transitions allowed for the caller; refused transitions return `409` with `code: invalid-transition` and the allowed list; `jobs.work` rules for assigned members.
5. Wire up the pending hooks: deleting a client with jobs → `409`; removing a member unassigns their open jobs, with audit entries.
6. Database proof: an integration test that inserts a job referencing another organization's client with raw SQL and expects the foreign-key violation.
7. Isolation, permission and audit matrices extended; add the resource-rule cases (a member transitions someone else's job → `403`).
8. Angular: jobs list using the M04 list pattern with every filter in US-33 (status and priority multi-select, assignee with "me" and "unassigned", client, overdue, due range); "My jobs" shortcut; create and edit forms; detail page with status actions (reason dialog for cancel and reopen), history from audit entries for this job, and the conflict dialog; client detail shows the client's jobs.
9. Dev seed: about 150 jobs in every status for the larger organization, spread across members.

## Out of scope
Open-job plan limit (M08), dashboard (M09), kanban board (V2).

## Tests required
- Domain: every cell of the transition table (allowed and refused), required fields per transition, date and amount boundaries, overdue computed in the organization's time zone around midnight.
- Integration: numbering under concurrency; resource rules; the foreign-key proof; filters checked against a SQL oracle on the seed data.
- Web: filter store and URL sync; status actions shown per permission.

## Acceptance criteria
- All tests pass.
- In the browser as Owner and as Member: create, assign, schedule, start, complete, cancel with a reason, reopen; a Member cannot act on someone else's job; filters and sorting survive reload; the job history shows each change.

## Verification required
Tests; browser pass as two roles; `EXPLAIN ANALYZE` of the list query recorded in PROGRESS.
