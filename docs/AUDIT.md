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
| Date fields showed no focus ring: Chrome focuses their inner parts | `:focus-within` ring for date and time inputs | browser audit: 9 stops without a ring (date fields on 4 screens) before, none after |
| The signed-in public header overflowed at 320 px by 24 px | below 360 px the wordmark shows its logo only; the link keeps its label | browser audit: overflow before, none after |

## Known limits

- The save notice is a query parameter, as on the organization picker: it shows again on reload until the user moves
  on.
- Screen readers, Firefox and Safari were not tested in M10.

# UI audit, v2 screens (M22)

Every screen and overlay added in v2, staff and portal, checked on 2026-11-25 with the M10 method above, extended as the
M22 brief asks (SPEC-V2 criterion 21). Numbers are also in `PROGRESS.md` → Measured results.

## Method (v2)

- **Where:** the production image (`docker build`, then `docker compose -f docker-compose.yml -f docker-compose.e2e.yml
  up --no-build`), whose E2E environment differs from Production only in the webhook test receiver and the per-address
  limits (D052): the same Content-Security-Policy and headers.
- **Data:** the demo seed; Brightline moved to Business for the audit (integrations) and back to Team after; a new
  Business organization for the empty states; a seeded contact and a new contact (no visits yet) for the portal.
- **Tools:** Playwright 1.64.0 driving Chromium 1248; @axe-core/playwright 4.13.0 with the WCAG 2.0, 2.1 and 2.2 A and
  AA tags plus best practices, run after animations settle.
- **Per screen, scripted** (368 checks, all passing on the final image; the script saves screenshots of every screen at 375 and 1280 px, and a sample of them was looked at by eye):
  - **success:** axe in the light and the dark theme at 375 and 1280 px, exactly one `h1`, and a clean console;
  - **widths:** no sideways scroll at 320, 375, 640 (1280 px at 200 % zoom), 768 and 1280 px;
  - **keyboard:** Tab through up to 40 stops; every stop shows the 3 px ring;
  - **loading:** the screen's own API calls held for 2.5 s; a skeleton (`aria-busy`) or a "Loading…" label shows;
  - **reduced motion:** with `prefers-reduced-motion: reduce` no animation keeps running while it loads;
  - **error:** the screen's own API calls answering 500; an alert with "Try again" shows, and passes axe;
  - **empty:** the same screen in the new organization, or for the new contact, passing axe.
- **Every v2 overlay,** opened by keyboard: focus moves into it, axe in both themes, it fits at 320 and 375 px, Escape
  closes it and focus returns to the control that opened it.
- **Error paths and interactions:** a pointer drag on the board (announced, no CSP violation), an upload that is not an
  image, Complete with required items open, empty submits on the agreement form and the portal's forms, an `http`
  webhook address, Pause and Resume, and portal sign-out.
- **Not scripted:** screen-reader output, Firefox and Safari (as in M10). The portal's "Check your email" page was checked in a separate run (one h1, axe in both themes, widths, focus), which found the focus finding below.

## States (v2)

✓ = present and checked in the browser; "—" means the screen has no such state.

| Screen | Loading | Empty | Error | Success |
|---|---|---|---|---|
| Forgot password; reset password and verify email (invalid link) | — (forms) | — | ✓ the invalid link explained | ✓ |
| Agreements | ✓ skeleton | ✓ "No agreements yet" | ✓ retry | ✓ |
| Agreement | ✓ skeleton | — | ✓ retry, **with its heading (fixed in M22)** | ✓ Pause and Resume say what they did |
| New / edit agreement | ✓ skeleton (edit), "Loading clients…" (pickers) | ✓ **"No active clients yet" with a link (fixed in M22)** | ✓ **the picker failure is an alert (fixed in M22)**; field errors | ✓ |
| Board | ✓ skeleton | ✓ "Everything open has a day"; "Nothing planned" per day on narrow screens | ✓ retry | ✓ drag and "Move to…" announced |
| Requests | ✓ skeleton | ✓ "No open requests" | ✓ retry | ✓ |
| Client: portal contacts and agreements | ✓ skeleton | ✓ "No contacts yet"; **on Free, the plan that has the portal, without asking the API (fixed in M22)** | ✓ retry | ✓ |
| Job: checklist, photos, quote, feedback | ✓ skeletons | not scripted | ✓ retry; a non-image upload refused with a message; Complete disabled with the open items named | ✓ |
| Account → Notifications | ✓ skeleton | — | ✓ retry | ✓ |
| Settings (capacity, plan features) | — (from the shell) | — | as M10 | ✓ |
| Settings → Integrations | ✓ skeleton | ✓ no keys, no endpoints | ✓ retry; an `http` address refused at the field | ✓ shown-once key and secret |
| Notification panel | not scripted | ✓ opened in the new organization | not scripted | ✓ |
| Portal sign-in, check your email, link expired | — (forms) | — | ✓ an expired or used link explained, **with a fresh form (fixed in M22)** | ✓ |
| Portal visits (upcoming, past) | ✓ skeleton | ✓ "No upcoming visits" / "No past visits" | ✓ retry | ✓ |
| Portal visit | ✓ skeleton | — | ✓ retry, **with its heading (fixed in M22)** | ✓ proof, sign-off |
| Portal quotes | ✓ skeleton | ✓ | ✓ retry | ✓ approve and decline dialogs |
| Portal requests, new request | ✓ skeleton | ✓ "No requests yet" | ✓ retry; field errors | ✓ |

## Overlays (v2)

Sixteen, all passing: the notification panel (with and without notifications); End agreement; Skip this visit; the board's
"Move to…" dialog; Decline a request; Remove a contact; the photo viewer; Remove a photo; Revoke a key; New secret;
Remove an endpoint; the plan switch confirmation; the portal's Approve, Decline and "Looks good" dialogs. The
shown-once key and secret dialogs (Escape does not close them, by design) and the open delivery log pass axe and fit at
320 px.

## Findings and fixes (v2)

| Finding | Fix | Regression check |
|---|---|---|
| On the Free plan, every client page asked for its portal contacts and the browser logged the 403 | the section reads the plan's features from the organization the shell loaded, as Integrations does (M21) | Vitest, shown failing first |
| The agreement, portal visit, job and client pages lost their only `h1` when they failed to load (axe `page-has-heading-one`); the job and client pages are v1's | each error state keeps a heading | Vitest ×4, shown failing first |
| The agreement form's picker failure was a plain paragraph, not announced, and an organization with no active clients got an empty picker with no hint (the job form's M10 rules) | an alert with "Try again", and "No active clients yet" with a link | Vitest ×2, shown failing first |
| A link followed over the portal sign-in page, after an empty submit there, kept "Enter your email address." under the expired-link notice (only when the link opens in the same tab) | a link resets the form | Vitest, shown failing first |
| After asking for a portal sign-in link or a password reset link, the form (and the focused button) disappeared and focus fell to the page | focus moves to the "Check your email" heading or the sent message, and "Use another address" or "try another address" puts it back in the field | Vitest ×2, shown failing first |
| A demo visitor's email choices survived the demo reset (found by the M22 fingerprint test, not by the browser) | the reset clears the demo accounts' notification preferences | integration test, shown failing first |

Not defects, recorded so they are not reported again: Pause acts at once (it is undone by Resume, and the page says
what it did); Complete is disabled with the open items named rather than refused by the API.

## Known limits (v2)

- Screen readers, Firefox and Safari were not tested, as in M10. 200 % zoom is checked as a 640 px wide viewport.
- The board scores below the other pages in Lighthouse and the portal's visits page shifts its footer (K6).
