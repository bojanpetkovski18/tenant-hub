# M20: Notifications in the app and by email

Branch: `#0020-in-app-and-email-notifications`

## Goal
People hear about what concerns them (in the app, and by email when they want it) without polling the lists, and nobody is told about their own actions.

## Context
SPEC-V2 4.6, 5.7, 7 (notifications, preferences), 8 (`notifications`, `notification_preferences`); D039. Stories US-100, US-101.

The owner approves `notification_preferences` as not tenant-owned by approving SPEC-V2 section 8.

## Scope
1. **Fan-out.** The domain events listed in 5.7 produce outbox messages in the same transaction (M15); a notification handler decides the recipients inside the tenant (managers, assignee, sender of the quote), excludes the actor, writes `notifications` rows, and queues emails for recipients whose preference is on.
2. **Endpoints:** list (newest first, paged, with the unread count), mark read (ids or all), preferences (user-scoped).
3. **Digest.** A scheduler task per organization at 07:00 in its zone, created when the organization is created and re-timed when its zone changes. It is skipped when there is nothing overdue; the email lists the recipient's overdue jobs, or a count for managers.
4. **Retention:** notifications older than 90 days removed by the scheduler.
5. **Angular:**
   - a bell in the shell with the unread count (refreshed on navigation and every 60 s while visible, stopped while hidden), and a panel built on the CDK overlay under the CSP (D023);
   - links to the job, request or agreement;
   - mark read and mark all read;
   - Account → Notifications for email preferences.
6. **Email templates:** plain text and simple HTML, consistent with the invitation email; no tracking pixels.

## Out of scope
Push or SMS; websockets or server-sent events; notifications for portal contacts beyond the emails already in M18 and M19.

## Tests required
- Each notification type's recipients, including "not the actor" and "removed members get nothing".
- A rolled-back command produces no notification.
- Digest timing in two zones with a moving `TimeProvider` (the DST change days), once per day across two schedulers, skipped when empty.
- Isolation and permission matrices: a user sees only their own notifications in the current organization.
- **Web:** bell count, panel states, keyboard and screen-reader behaviour of the panel, preference form.

## Acceptance criteria
- In the browser with Mailpit: assign a job to another member and see their bell and email; approve a quote as a contact and see the manager's notification; switch an email preference off and see the email stop; trigger the digest with a short interval in development.

## Verification required
Full test suite; browser pass at three widths, keyboard, both themes, with the panel opened in the audit; no visible polling while the tab is hidden (network panel); email content checked in Mailpit for links and for no tokens.
