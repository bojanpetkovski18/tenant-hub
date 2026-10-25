# M10: Polish: states, responsive, accessibility, performance

Branch: `#0010-polish-states-responsive-a11y-performance`

## Goal
Every screen is finished: consistent states, responsive at every width, accessible, fast. Every deferred known issue is reviewed with the owner.

## Context
SPEC sections 6 and 9 (targets). Start by listing every item in `PROGRESS.md` → Known issues and asking the owner which to fix now.

## Scope
1. **States audit:** a table of every screen × loading, empty, error and success; fix the gaps.
2. **Responsive audit:** every screen at 375, 768 and 1280 px, plus 320 px and 200 % zoom spot checks.
3. **Accessibility audit:** axe on every screen in both themes (fix all serious and critical findings); keyboard pass; token contrast check; `prefers-reduced-motion`; landmarks and heading order. Write the results to `docs/AUDIT.md`.
4. **Performance:** a committed load script in `tools/` for the jobs list API (10,000 jobs across 20 organizations, SPEC 9 target p95 < 200 ms), the per-request membership lookup cost (D006), Lighthouse on the landing page and dashboard (mobile, production build), and the initial bundle size. Fix what misses a target; record everything in PROGRESS → Measured results.
5. **Isolation re-check:** repeat the M03 mutation checks on the full application and record the results.
6. **Clean-up:** dead code, TODOs resolved or logged, `npm audit` and `dotnet list package --vulnerable --include-transitive` reviewed.

## Out of scope
New features.

## Tests required
Regression tests for every bug found in the audits, each shown failing first.

## Acceptance criteria
- The states table has no gaps; axe reports zero serious or critical findings on every screen.
- Measured numbers are recorded; any missed target is listed with a reason and the owner's decision.
- Every known issue is fixed or explicitly re-deferred by the owner.

## Verification required
All audits run in a real browser on the production build (served by the API) as well as the dev server, because CSP differences only show up in production.
