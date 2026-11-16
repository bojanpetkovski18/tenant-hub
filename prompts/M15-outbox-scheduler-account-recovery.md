# M15: Outbox, scheduler, password reset and email verification

Branch: `#0015-outbox-scheduler-and-account-recovery`

## Goal
Side effects leave the request safely: messages are written with the change and delivered once by a worker that can run on several instances, and recurring work runs from a schedule in the database. Password reset and email verification are its first users.

## Context
SPEC-V2 4.1, 4.9, 7 (public and user-scoped), 8 (`outbox_messages`, `scheduled_tasks`), 9, criterion 16; D011, D022, D032, D033 (data-protection keys in PostgreSQL), D039. Stories US-05, US-06.

The owner approves `outbox_messages` and `scheduled_tasks` as not tenant-owned, and the dispatcher and scheduler bypasses, by approving SPEC-V2 section 8. Add exactly those to the allow-lists, nothing more.

## Scope
1. **Outbox.** `outbox_messages` and an `IOutbox` port in Application. Messages are added to the same `SaveChanges` as the change and its audit entries (extend the step that writes audit entries; do not open a second transaction). Payloads carrying a secret are encrypted with Data Protection under a purpose per message type and deleted when sent.
2. **Dispatcher.** A hosted service claims a batch (`FOR UPDATE SKIP LOCKED`, ordered by `available_at`), runs the handler for each type, marks it processed, and on failure records the error class (no secrets, no addresses) and backs off. After the last attempt the message is dead and logged as a warning. Processed messages are deleted after 7 days. Handlers are idempotent by message id.
3. **Scheduler.** `scheduled_tasks` with a lease: a task is claimed with `SKIP LOCKED` and a lease time, runs, and sets its next due time. The first task is retention clean-up (processed outbox messages). Agreement generation (M16) and the digest (M20) plug in later. Move the demo reset's schedule onto it only if that is a small change; otherwise record why it stays (D032).
4. **Email through the outbox.** An email handler uses the existing `IEmailSender`. Invitations keep their synchronous path and `delivery` contract (D011).
5. **Password reset.** Request (always `202`, rate-limited), confirm (single use, 30 minutes, the account's security stamp changes so every session ends), pages "Forgot password" and "Reset password". Decide Identity's token providers or a hashed-token table (D039) and log it.
6. **Email verification.** Sent at sign-up and on request; `POST /api/auth/verify-email`; the session carries `emailVerified`; a banner in the shell; inviting refuses `403 email-not-verified`. Existing and demo accounts are verified by the migration and the seed.
7. **Demo mode.** Email is off: the reset page says so and the verification banner is hidden for demo accounts.
8. Isolation, permission and audit matrices extended for every new endpoint; endpoint classification for the public and user-scoped ones.

## Out of scope
Notifications (M20) and webhooks (M21), beyond leaving room for their message types. Moving invitations onto the outbox.

## Tests required
- A rolled-back command leaves no message (forced failure after `SaveChanges` work, as in v1 criterion 8).
- Two dispatchers against one database deliver every message exactly once (count deliveries with a recording sender), repeated runs recorded.
- Retry and dead-letter with a failing handler and a controllable `TimeProvider`.
- Two schedulers: a due task runs once; a crashed run (lease expires) is picked up again; a task due while "asleep" runs once on wake, not once per missed interval.
- Reset: unknown email gives the same answer and timing class; a token works once, expires, and ends other sessions; the token never appears in logs (captured log scan) or in the outbox payload in clear text (read the row).
- Verification: banner, refusal of invitations, resend rate limit.
- Web: forgot and reset forms (validation, server errors, expired link), banner.

## Acceptance criteria
- SPEC-V2 criterion 16 met.
- In the browser with Mailpit: reset a password from the email and see another signed-in browser lose its session; sign up, see the banner, verify from the email, invite.

## Verification required
Full test suite, the two-instance tests repeated (record runs); browser pass at three widths and by keyboard; `docker compose up --build --wait` and the existing journey; the API log checked for tokens and links.
