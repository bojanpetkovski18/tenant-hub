# M16: Recurring service agreements

Branch: `#0016-recurring-service-agreements`

## Goal
Repeating work is described once and its visits appear by themselves, on the right dates in the organization's time zone, never twice, and within the plan's limits.

## Context
SPEC-V2 2.2 (`agreements.*`), 4.2, 4.8 (active agreements limit), 5.1, 5.9, 7 (agreements), 8 (`agreements`, job columns), criterion 14; D013, D029, D031, D039, D041. Stories US-70, US-71.

## Scope
1. **Domain.**
   - `Recurrence` value object: weekly (weekdays, every N weeks), monthly by day (1–28 or last), monthly by nth weekday (first–fourth or last), every N months; `Occurrences(from, to)` as a pure function over `DateOnly`.
   - `Agreement` aggregate: create, update template, change recurrence, pause, resume, end, with the rules in 5.1; raises events for the audit.
   - A generation service: given an agreement, the organization's today, the horizon and the existing occurrence dates, it returns the visits to create; plus the rule for which future visits an edit updates (New or Scheduled, not `changed_by_hand`).
   - `PlanPolicy` gains the active-agreements limit (Free 3, Team 200, Business unlimited); generated visits use the open-jobs limit.
2. **Jobs.** Columns `agreement_id`, `occurrence_on`, `changed_by_hand`, `estimated_minutes` (the board uses it in M17) and the unique index. Editing a generated visit sets `changed_by_hand`. `POST /jobs/{id}/skip`. The list gains an `agreementId` filter.
3. **Generation.** Runs in a tenant context per organization, under the organization lock, as "System":
   - immediately in the command that creates, edits or resumes an agreement;
   - daily per organization from the scheduler (M15), catching up after downtime.
   Stops at the open-jobs limit, setting `paused_reason`.
4. **Endpoints** from SPEC-V2 7, including `POST /agreements/preview`.
5. **Audit:** `actor_kind` and `actor_ref` columns (5.9); "System (agreement <title>)" for generated visits; agreement changes by users.
6. **Seed:** Brightline gets a handful of agreements (weekly office cleaning, fortnightly windows, monthly deep clean), replayed through the domain like the rest of the seed (D032); Harbor & Pine one, near its limits.
7. **Angular:**
   - Agreements list (`createPagedListStore`, query in the URL), detail (next visits, visits changed by hand, generation state) and form with a recurrence editor and a live preview of the next eight dates;
   - client page Agreements tab; job detail links to its agreement and offers "Skip";
   - permissions from the session.

## Out of scope
Checklist templates (M19); the board (M17); notifications when generation pauses (M20, the agreement page shows it now).

## Tests required
- **Recurrence:** a table of cases over a full year for each type, including 31st-of-month and "last day" rules, 29 February 2028, every-N intervals across year ends, and a start date in the middle of a cycle.
- **Time zones:** "today" for generation computed in Pacific/Auckland and America/Santiago with a moving `TimeProvider` around midnight and the DST changes.
- **Idempotency:** running generation twice, and on two schedulers at once, creates each visit once (unique index plus test).
- **Edit rules:** future untouched visits updated, visits changed by hand kept and listed, past visits never touched; skip; end; resume never back-fills the past.
- **Limits:** generation stops at the open-jobs limit and reports it; the active-agreements limit holds under concurrency (two creations with one left).
- Isolation, permission and audit matrices for every new endpoint; composite keys for agreement → client and job → agreement.
- **Web:** recurrence editor validation and preview; list states; permission-based visibility.

## Acceptance criteria
- SPEC-V2 criterion 14 met.
- In the browser: create a weekly agreement and see 60 days of visits in the jobs list; edit it and see untouched visits change; skip one; end it.

## Verification required
Full test suite; the concurrency tests repeated (record runs); browser pass at three widths, keyboard, both themes; generation time for 200 agreements in one organization measured and recorded.
