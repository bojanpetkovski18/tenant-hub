# M17: Dispatch board with keyboard moves

Branch: `#0017-dispatch-board-with-keyboard-moves`

## Goal
Managers plan the week by moving visits between people and days, with the mouse, by touch or by keyboard, without lost updates and with an honest view of each person's load.

## Context
SPEC-V2 2.2 (board permissions), 4.3, 5.2, 7 (`GET /board`), 9 (accessibility, performance), criterion 15; D023 (CDK styles under the CSP), D027 (dialogs), D029. Story US-75.

Check before starting: how Angular CDK drag and drop applies its positioning (through `element.style`, which a CSP without `'unsafe-inline'` allows, or through style attributes or elements, which it blocks), on the production build under the real CSP, before building on it. Record the finding (D023 style).

## Scope
1. **API.** `GET /board?week=` returns, for the organization's week (Monday start, its zone), the active members, the jobs scheduled in that week (with what the card needs and the concurrency token), the open unscheduled jobs (paged or capped, decided here), each person's planned minutes per day, and the daily capacity. One round trip; aggregation in SQL; `EXPLAIN` recorded.
2. **Moves** use the existing job update (assignee and `scheduled_on`, with the token) and the New → Scheduled transition, so no new write rules appear on the board. If one call cannot do both atomically, add one command, `POST /jobs/{id}/schedule`, that does it in one transaction with the same domain rules. Moving past the due date asks to move the due date too (5.2).
3. **Settings:** daily capacity in Settings → General (`organizations.daily_capacity_minutes`), audited. `estimated_minutes` on the job form.
4. **Angular board:**
   - a grid of people × days, with Unassigned and Unscheduled areas; week navigation with the week in the URL; client and status filters;
   - drag and drop (CDK) for pointer and touch;
   - a keyboard path: cards are focusable; Enter or "Move to…" opens a small dialog (via `Confirm`/`CspSafeDialogContainer`, D027) with person and day; arrow-key moves are optional, decided here;
   - every completed move is announced through a live region ("JOB-0042 moved to Maya, Tuesday 18 November");
   - load per cell (planned hours of capacity) with an icon and text when over;
   - conflicts: the card returns, a message explains, the board reloads;
   - Members and Viewers see it read-only; below 768 px it is a day-by-day list per person with "Move to…".
5. **Navigation:** "Board" in the sidebar for everyone with `jobs.read`; the dashboard's "my next 7 days" links to the board for managers.

## Out of scope
Time-of-day slots; per-member capacity; travel time or maps; drag between weeks (use week navigation and "Move to…").

## Tests required
- **API:** board contents per week and zone (a week crossing a DST change; a week crossing a year end); permission and isolation matrices; a conflicting move gives `409` and changes nothing; refused moves (Completed job, missing assignee) give the documented codes.
- **Load:** the board week p95 with 10,000 jobs across 20 organizations (extend `tools/load-jobs.mjs` or add a sibling tool), recorded.
- **Web component tests:** the "Move to…" dialog by keyboard, the announcement text, rollback on conflict, read-only rendering for Members, the phone list.

## Acceptance criteria
- SPEC-V2 criterion 15 met.
- In the browser: plan a week by mouse and by keyboard only; force a conflict from a second browser; see overload marking; use the phone layout at 375 px.

## Verification required
Full test suite; browser pass at 375, 768 and 1280 px, keyboard only, both themes, with a screen reader's announcements checked through the live region's text; console clean under the production CSP while dragging; axe clean with a dialog open; the performance number recorded in `PROGRESS.md`.
