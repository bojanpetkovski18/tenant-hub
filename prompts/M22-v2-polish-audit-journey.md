# M22: v2 polish, audit and the second end-to-end journey

Branch: `#0022-v2-polish-audit-and-second-journey`

## Goal
Every v2 screen, staff and portal, meets the v1 quality bar, the second journey runs in CI against the production image, and the isolation mutations are re-run for v2.

## Context
SPEC-V2 9 and criteria 12, 20 and 21; SPEC 9 and 10; `docs/AUDIT.md` (the M10 method); `docs/CHECKLISTS.md`; D033, D034. The playbook's polish phase: open every dialog, menu, toast and overlay, and trigger every error path.

## Scope
1. **Known issues:** go through `PROGRESS.md` → Known issues with the owner first.
2. **UI audit** of every new screen and state at 320, 375, 768 and 1280 px and 200 % zoom:
   - keyboard only, with visible focus and logical order;
   - axe in both themes; reduced motion; console clean on the production build under the real CSP;
   - board drags, upload, the notification panel, every new dialog and every new error path.

   Add the results to `docs/AUDIT.md` as a v2 section, and fix what it finds.
3. **Second journey** (criterion 20): a new Playwright spec against the composed image: agreement → board (a keyboard move) → contact signs in from the Mailpit link → approves the quote → the assignee completes the checklist with a photo → the contact confirms → a signed webhook reaches the test receiver, verified in the test. It fails on console errors and CSP violations, and runs axe on each page. Three runs in a row locally before calling it stable; no retries hiding flakiness.
4. **Isolation re-check:** the M03 and M14 mutation checks again, plus the portal and API-key mutations from M18 and M21, recorded.
5. **Performance:** jobs list and board p95 on the measurement setup (D033); Lighthouse on the dashboard, the board and the portal's visits page (mobile, production image); initial JavaScript per entry point. Record everything in `PROGRESS.md`.
6. **Seed and demo mode:** the v2 data appears in the demo organizations, survives a demo reset consistently (fingerprint test), and the demo shows the portal without email (M18 decision).

## Out of scope
New features. Anything the audit finds that needs a design change goes to the owner as a known issue.

## Tests required
The second journey; regression tests (shown failing first) for every bug the audit finds; the mutation checks.

## Acceptance criteria
- SPEC-V2 criteria 12, 20 and 21 met and recorded.
- v1 criteria still met: the first journey, the isolation suite, Lighthouse ≥ 90 on landing and dashboard.

## Verification required
Everything CI runs, locally; both journeys three times in a row; the audit table complete; the numbers in `PROGRESS.md` with method, machine and date.
