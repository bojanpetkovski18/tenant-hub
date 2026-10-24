# UI audit (M10)

States, responsive layout and accessibility of every screen, checked in a real browser on 2026-10-24. Numbers and the
method behind them are also in `PROGRESS.md` → Measured results.

## Method

- **Where:** the production build (`npm run build`) served with the API's Content-Security-Policy header in front of
  the API (the API itself serves the SPA from M11), and the dev server (`npm start`). Both runs gave the same results.
- **Data:** the demo seed (SPEC 8.3) for full screens, and a brand-new organization for empty states.
- **Tools:**
  - Playwright 1.63.0 driving Chromium;
  - @axe-core/playwright 4.14.0 (WCAG 2.x A and AA rules plus best practices);
  - `tools/contrast.mjs` for the design tokens.
- **Per screen, scripted:**
  - **success:** axe in the light and the dark theme, exactly one `h1`, and no console errors or CSP violations;
  - **widths:** no horizontal page scroll at 320, 375, 640 (1280 px at 200 % zoom) and 768 px;
  - **loading:** the screen's API call held for 2.5 s; a skeleton (`aria-busy`) or a pending label must show;
  - **error:** the screen's API call answering 500; an alert with "Try again" must show;
  - **empty:** the same screen in an organization with no data;
  - **keyboard:** Tab through up to 40 stops; every stop must show the focus ring;
  - **reduced motion:** `prefers-reduced-motion: reduce`; the loading pulse must not animate.
- **Not scripted:** screen-reader output (no screen reader was run), Firefox and Safari.

## States

✓ = present and checked in the browser. "Pending" is the button label while a form is sending; "—" means the screen
has no such state.

| Screen | Loading | Empty | Error | Success |
|---|---|---|---|---|
| Landing | — (static) | — | — | ✓ |
| Sign in | pending "Signing in…" | — | ✓ field errors, one generic message for refused credentials, demo errors | navigates to the organization |
| Sign up | pending "Creating…" | — | ✓ field and business-rule errors | opens the new organization |
| Accept invitation | ✓ | — | ✓ invalid, expired, revoked or used link explained | joins and opens the organization |
| Organizations | — (from the session) | ✓ "not a member of any organization yet" | — | ✓ notices after delete, leave, lost access |
| Account | pending "Saving…" / "Changing…" | — | ✓ | ✓ status messages |
| Page / organization not found | — | — | ✓ explained, with a way back | — |
| Dashboard | ✓ skeleton | ✓ "Nothing here yet" with first steps | ✓ retry | ✓ |
| Jobs | ✓ skeleton | ✓ "No jobs yet" / "No jobs match" | ✓ retry | ✓ |
| New / edit job | ✓ skeleton (edit), "Loading clients…" (pickers) | ✓ "No active clients yet" with a link **(fixed in M10)** | ✓ retry for the job and **for the pickers (fixed in M10)**, field errors, limit message, conflict dialog | ✓ **"Job created." / "Changes saved." (fixed in M10)** |
| Job | ✓ skeleton | ✓ "No history yet." | ✓ retry, not found | ✓ |
| Clients | ✓ skeleton | ✓ "No clients yet" / "No clients match" | ✓ retry | ✓ |
| New / edit client | ✓ skeleton (edit) | — | ✓ field errors, limit message, conflict dialog | ✓ **"Client created." / "Changes saved." (fixed in M10)** |
| Client | ✓ skeleton | ✓ "No jobs for this client yet." | ✓ retry, not found | ✓ |
| Members | ✓ skeleton | ✓ "No pending invitations" | ✓ retry; rule refusals in dialogs | ✓ status messages |
| Audit log | ✓ skeleton | ✓ "No activity yet" / "No activity matches" | ✓ retry | ✓ |
| Settings | ✓ skeleton | — | ✓ retry, blocked downgrade listed | ✓ "Settings saved.", plan changed |

## Responsive

Every screen at 320, 375, 640 and 768 px: no horizontal page scroll after the fixes below. Wide lists switch to cards
on narrow screens (M04, M06). Screenshots at 375, 768 and 1280 px were reviewed for the new M09 screens.

## Accessibility

- **axe:** 0 violations of any impact on all 20 screens, in both themes, on both servers, and on the empty states.
- **Headings and landmarks:** one `h1` per screen; skip link, `header`, `nav` (labelled), `main`; axe's landmark and
  heading-order rules pass.
- **Keyboard:** every tab stop shows the 3 px focus ring after the fixes below. Dialogs trap focus and return it.
  Charts have a table alternative (`aria-pressed` toggle).
- **Contrast:** `node tools/contrast.mjs` checks 26 token pairs in each theme against WCAG 2.2. The lowest are the
  light theme's warning and success fills (4.58:1, need 3:1) and its success and warning text (5.02:1, need 4.5:1).
- **Reduced motion:** transitions and animations stop.

## Findings and fixes

| Finding | Fix | Regression check |
|---|---|---|
| A saved client or job opened its page with no confirmation | the form passes `notice=created` or `notice=saved`; the detail page shows it as a status message | Vitest, shown failing first |
| The job form's pickers failed silently, and an organization without clients could not start a job with no hint why | loading text, an error with retry, and "No active clients yet" with a link | Vitest, shown failing first |
| With reduced motion the loading pulse kept repeating every 0.01 ms (a flicker) | `animation-iteration-count: 1` in the reduced-motion rule | browser audit: opacity varied between frames before, constant after |
| Date fields showed no focus ring: Chrome focuses their inner parts | `:focus-within` ring for date and time inputs | browser audit: 7 stops without a ring before, none after |
| The signed-in public header overflowed at 320 px by 24 px | below 360 px the wordmark shows its logo only; the link keeps its label | browser audit: overflow before, none after |

## Known limits

- The save notice is a query parameter, as on the organization picker: it shows again on reload until the user moves
  on.
- Screen readers, Firefox and Safari were not tested in M10.
