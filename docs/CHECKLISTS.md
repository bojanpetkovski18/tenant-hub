# Checklists and definition of done

## Definition of done (every milestone)
A milestone is done when all of the following are true and shown in the milestone report:
1. The brief's scope is implemented and nothing outside it was added.
2. `dotnet build` passes with warnings as errors; `npm run lint`, `npm run format:check` and the web build pass.
3. All tests pass, including the architecture tests and the isolation suite. New behaviour has new tests; bug fixes have a regression test that was shown failing first.
4. Every new tenant-owned entity and endpoint is covered by the isolation coverage tests without new allow-list entries (or with entries the owner approved).
5. UI work was used in a real browser at 375, 768 and 1280 px, by keyboard, in both themes, with a clean console and no serious or critical axe findings.
6. A forgotten migration fails the suite; new migrations were reviewed as SQL.
7. No secrets, real personal data or employer material were added; logs added in this milestone leak no tokens or personal data.
8. `PROGRESS.md`, `DECISIONS.md` and the CLAUDE.md Commands section are updated; numbers come from `PROGRESS.md` → Measured results.
9. CI is green on the branch; the branch is pushed and the PR description has been given.

## Testing checklist (per feature)
- **Domain:** valid and invalid inputs at the boundaries in SPEC 5; every state transition allowed and refused; invariants (last Owner, role grants, limits).
- **Handlers:** success path, validation errors, not found inside the tenant, permission refused, resource rule refused, concurrency conflict (`409`), plan limit (`403 plan-limit-reached`).
- **API (integration):** status codes and problem-details shape; the permission table rows for the endpoint (allowed and refused roles); antiforgery required on unsafe methods; rate limits where specified.
- **Isolation:** the endpoint appears in the cross-tenant matrix; foreign ids under the own route and the own ids under a foreign route give `404`; the other organization's data is unchanged; a new entity is tenant-owned or on the reviewed allow-list.
- **Audit:** each state-changing endpoint writes the expected entry, and a rolled-back command writes none.
- **Web:** component tests for forms (validation and server errors), permission-based visibility, and the loading, empty and error states.
- **Browser pass:** the user stories of the milestone, step by step, at the three widths and by keyboard.

## Deployment checklist (M11–M12)
- Production image builds from a clean clone; runs as non-root; image size recorded.
- Migrations apply on start; the health endpoint reports the database.
- `.env.example` documents every setting; production has no development defaults (cookie `Secure`, HSTS, CSP, Scalar UI off).
- Demo mode: seeded organizations, demo sign-in buttons, scheduled reset, cleanup of visitor organizations after 24 hours, rate limits, email off.
- Forwarded headers configured for the host's proxy (correct client IP for rate limits, correct scheme for cookies).
- Secret scan of the full history and the working tree is clean; personal-data search is clean.
- Dependency audit (`npm audit`, `dotnet list package --vulnerable --include-transitive`) is clean or each finding is explained.
- The Playwright journey passes against the deployed or composed production image.
- README: problem, solution, demo access, screenshots, architecture overview, isolation proof, setup, tests, decisions, AI-assistance statement.
- Hosting costs and free-tier limits recorded with the date they were checked (D016).
