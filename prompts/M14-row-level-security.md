# M14: Row-level security: the database enforces the tenant

Branch: `#0014-postgres-row-level-security-layer`

## Goal
PostgreSQL itself refuses another organization's rows. The application runs as a role that cannot bypass the policies, the setting cannot leak between requests, and the tests prove both.

## Context
SPEC-V2 3.1, 9 and criterion 12; D007, D024, D025, D033 (lock mode), D038. No new user stories: this milestone has no visible feature, so its proof is the tests and the mutation checks.

Re-check before starting (D019): .NET 10 and Npgsql patch releases since 10.0.13, especially security releases; the PostgreSQL 18 row-level security documentation for `FORCE ROW LEVEL SECURITY`, `BYPASSRLS` and `current_setting(..., true)`. Record what you found and the date.

## Scope
1. **Roles.** A migration (or an init script for fresh databases, decided here) creates `tenant_hub_app` with only `SELECT`, `INSERT`, `UPDATE` and `DELETE` on the tables and `USAGE` on the sequences; it owns nothing and is not a superuser. Migrations keep running as the owner. `.env.example`, both Compose files, the Dockerfile's documentation and the Testcontainers fixture use the split. The API refuses to start if its runtime role is a superuser or has `BYPASSRLS` (outside the allow-listed path), so a misconfiguration fails loudly.
2. **Policies.** For every tenant-owned table: `ENABLE` and `FORCE ROW LEVEL SECURITY` and one policy for all commands, `USING` and `WITH CHECK` on `organization_id = current_setting('app.organization_id', true)::uuid`. A missing setting matches nothing (fail closed).
3. **Setting the tenant.** Every database command in a tenant context runs with `app.organization_id` set for its transaction. Decide (D038) between opening a transaction for every unit of work and a connection-level setting reset when the connection returns to the pool; whichever is chosen, a test proves no leak.
4. **Bypasses.** The allow-listed `AcrossTenants()` uses in `TenantBypassAllowListTests` get one narrow way through (D038: a `BYPASSRLS` role on a separate connection used only there, or `SECURITY DEFINER` functions). The allow-list test also covers the new mechanism, so a new bypass still needs the owner's approval.
5. **Coverage test.** It enumerates tenant-owned entities from the EF Core model, queries `pg_class` and `pg_policies`, and fails if a table lacks `relrowsecurity`, `relforcerowsecurity` or a policy using `organization_id`. Shown failing on purpose once (record how).
6. **Query plans.** `EXPLAIN (ANALYZE, BUFFERS)` for the jobs list and the audit timeline with policies on, compared with the M10 plans; indexes still used. Re-run `tools/load-jobs.mjs` on the same machine and record p50/p95/p99 next to the M10 numbers.
7. **Documentation.** `docs/ARCHITECTURE.md` isolation section and `SPEC-V2` 3.1 updated with what was decided; CLAUDE.md commands for anything new (creating the role locally).

## Out of scope
The portal's `app.client_id` (M18). New entities. Changes to the EF Core filters, the guard or the composite keys: they stay as layers 2–4.

## Tests required
- Coverage test (step 5).
- Raw SQL as `tenant_hub_app`: with the setting for A, a `SELECT` sees none of B's rows; an `INSERT` or `UPDATE` with B's `organization_id` fails the `WITH CHECK`; with no setting, nothing is visible.
- **Leak test:** two tenants on one physical connection (pool size 1), A then B then no tenant, asserting what each sees.
- The full v1 suite, including the 301 isolation tests, passes under the runtime role.
- Startup refusal when the runtime role is a superuser.
- **Mutation checks** (recorded in `PROGRESS.md`): (a) remove the EF Core filter on `Job`: the cross-tenant matrix still passes; (b) also drop the jobs policy: it fails; (c) remove the per-transaction setting: tenant reads return nothing and the suite fails loudly rather than leaking.

## Acceptance criteria
- SPEC-V2 criterion 12 met and recorded.
- v1 behaviour unchanged: all API, web and end-to-end tests pass against the production image with the role split.
- The jobs list p95 with RLS on is recorded next to the M10 number; still under 200 ms.

## Verification required
Full test suite; mutation checks (a)–(c); `EXPLAIN` output in `PROGRESS.md`; `docker compose up --build --wait` from `.env.example` and the Playwright journey; a fresh-database run of the API with the documented commands.
