# M03: Tenancy core and the isolation test harness

Branch: `#0003-tenancy-core-permissions-and-isolation-tests`

## Goal
The heart of the project: every tenant-scoped request is resolved to a membership, permissions are enforced on the server, tenant-owned data is filtered and guarded in EF Core, and an isolation test harness proves it, built so that future entities and endpoints are covered automatically.

## Context
SPEC sections 2 (permission table), 3 (all of it), 7 (tenant-scoped conventions) and 10 (criteria 1–3, 5); D005–D008, D015. There are no clients or jobs yet: the first tenant-scoped endpoints are `GET /api/orgs/{orgId}` and `GET /api/orgs/{orgId}/members`, and memberships are the first tenant-owned entity.

## Scope
1. `Permission` constants and the role → permission map (Domain), with a unit test that pins the SPEC table row by row.
2. `ITenantContext` (Application) and a request-scoped implementation filled by tenant-resolution middleware or an endpoint filter for routes under `/api/orgs/{orgId}`: active membership → context; none → `404`; malformed id → `404`. A `RequirePermission(...)` endpoint convention returns `403` when the permission is missing.
3. The session (`GET /api/auth/session`, D021) now returns each membership's permissions; the Angular session store exposes `can(permission)` for the current organization, and the sidebar hides navigation the user cannot use.
4. EF Core tenancy:
   - `ITenantOwned` marker; a single model-building convention adds the query filter to every implementing entity;
   - the SaveChanges guard (SPEC 3.3 point 3): stamps new entities, throws on writes to another organization's entities and on `organization_id` changes, logs a security event;
   - `IgnoreQueryFilters()` wrapped in a small, named API so call sites are easy to find; the allow-list lives in the architecture tests.
5. Endpoint classification: each endpoint is tagged public, user-scoped or tenant-scoped (metadata), and tenant-scoped endpoints declare a permission.
6. Endpoints: `GET /api/orgs/{orgId}` (`org.read`) and `GET /api/orgs/{orgId}/members` (`org.read`).
7. Isolation test harness in `tests/TenantHub.IntegrationTests/Isolation/`:
   - **model coverage:** every `ITenantOwned` entity has the filter and an index starting with `organization_id`; every other entity is on a reviewed allow-list;
   - **endpoint coverage:** every endpoint in the route table is classified; every tenant-scoped endpoint declares a permission and has a cross-tenant case;
   - **cross-tenant matrix:** a fixture with two organizations; each tenant-scoped endpoint is called by a user of A with B's route, and later with B's entity ids under A's route; expect `404`, unchanged B data (row counts and a hash), and no B identifiers in the body. Built as a table that later milestones extend with one line per endpoint;
   - **guard tests:** writing another organization's entity through the guard throws; changing `organization_id` throws;
   - **permission matrix:** for each endpoint, every role is called and the result matches the SPEC table.
8. Architecture test: `IgnoreQueryFilters` is used only by the allow-listed types (for now: the sign-in membership lookup and the dev seed).

## Out of scope
Clients, jobs, invitations, the audit log. Row-level security (V2).

## Tests required
As in scope 7 and 8, plus unit tests for the permission map and the guard.

## Acceptance criteria
- All tests pass.
- **Mutation checks**, done by hand and recorded in PROGRESS → Measured results: (a) remove the query-filter convention → isolation tests fail; (b) remove the membership check → the cross-tenant matrix fails; (c) add a tenant-owned entity without an index, and an endpoint without a classification → the coverage tests fail. Restore after each.
- In the browser: a Viewer sees no admin navigation; changing the URL to another organization's slug shows not found; removing a membership in the database makes the next request fail and the UI return to the organization picker.

## Verification required
Run the suite, perform and record the three mutation checks, and show `curl` results for `404` (foreign organization) and `403` (missing permission).
