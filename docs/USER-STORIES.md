# TenantHub: User stories

Each story has an id, the milestone that delivers it and acceptance criteria. Business rules are in `SPEC.md` section 5 and are not repeated here. Priority: **P1** is required for the MVP demo, **P2** is MVP but can move to V2 if the schedule slips (see `PROGRESS.md` → Cut list).

## Accounts and organizations

**US-01 Sign up with an organization** (M02, P1)
As a business owner, I want to create an account and my organization in one step, so that I can start using the product immediately.
- Given valid details, an account, an organization on the Free plan and an Owner membership are created in one transaction; I am signed in and land on the new organization's dashboard.
- An email that is already registered gives a field error without revealing more than "already in use, sign in instead".
- Invalid input shows field errors from the server; nothing is created.
- Sign-up is rate-limited; too many attempts give a clear "try again later".

**US-02 Sign in and out** (M02, P1)
- Correct credentials sign me in and open my last organization, or the organization picker if there is none or several and no last one.
- Wrong credentials give one generic message. After 5 failures the account is locked for 15 minutes with the same generic message.
- Signing out ends the session on the server; the back button does not show protected data.

**US-03 Belong to several organizations** (M02, P1)
- The organization switcher lists my organizations with my role in each; switching changes the URL (`/o/{slug}`) and all data on the page.
- Two tabs can show two organizations at the same time without interfering.
- I can create another organization (up to 5 that I own) and become its Owner.

**US-04 Manage my account** (M02, P2)
- I can change my display name and my password (current password required); other sessions are signed out after a password change.

## Members and invitations

**US-10 Invite a teammate** (M05, P1)
As an Admin or Owner, I want to invite people by email with a role.
- The invitation appears as pending with its role and expiry date; the invitee gets an email, or I get a copy-link dialog when email is disabled.
- Admins cannot invite Owners. Inviting a current member gives a clear error. Inviting the same email again replaces the pending invitation.
- When the seat limit is reached, I see the limit and the current usage, and a link to the plan page (Owners only).

**US-11 Accept an invitation** (M05, P1)
- With a valid link and no account: a sign-up form with the email fixed creates my account and membership and opens the organization.
- With a valid link while signed in with the same email: one click joins the organization.
- Signed in with another email: I see which email the invitation is for and can sign out.
- Expired, revoked or used links show a specific message and no form.

**US-12 Manage members** (M05, P1)
- I can change a member's role and remove a member within the rules (last Owner, Admins and Owners, no self role change). Refused actions are disabled in the UI and refused by the API.
- A removed member loses access on their next request and sees "you no longer have access to this organization".
- I can leave an organization unless I am its last Owner.

## Clients

**US-20 Maintain the client list** (M04, P1)
As a member, I want to keep my organization's clients in one place.
- I can create, edit, archive and restore clients; names are unique among active clients.
- The list supports search, status filter, sorting and paging, all reflected in the URL so a link reproduces the view.
- Deleting a client with jobs is refused with a suggestion to archive.
- Viewers see the list and details but no edit actions.
- Saving over someone else's newer change shows a conflict message with "reload".

## Jobs

**US-30 Create and plan a job** (M06, P1)
- I can create a job for an active client; it gets the next number (`JOB-0001`, …) for my organization.
- Admins and Owners can assign it to any active member and schedule it; members can create jobs but not assign others.

**US-31 Work on my jobs** (M06, P1)
As a member, I want to see my jobs and move them forward.
- "My jobs" (assignee = me) is one click from the list; I can start and complete jobs assigned to me.
- I cannot edit or transition jobs assigned to someone else; the actions are not shown and the API refuses them.

**US-32 Follow the workflow** (M06, P1)
- The detail page shows only the transitions allowed for me in the current state.
- Cancelling and reopening require a reason, which appears in the job's history.
- A refused transition explains why and lists the allowed ones.

**US-33 Find jobs** (M06, P1)
- I can filter by status, assignee (me, unassigned, a person), client, priority, overdue and due-date range, search by number or title, sort and page. Filters live in the URL.
- Overdue jobs are marked using my organization's time zone.

## Audit

**US-40 See who changed what** (M07, P1)
As an Admin or Owner, I want an activity history.
- Every change appears with actor, time, action, entity and the changed fields (old → new).
- I can filter by person, action, entity type and date range, and open the related entity.
- A job's detail page shows that job's history to everyone who can read the job.
- Nobody can edit or delete entries.

## Plans and settings

**US-50 Understand and change my plan** (M08, P1)
- Settings → Plan shows each limit with usage (for example "Seats 3 of 3").
- An Owner can switch plans ("demo billing", no payment). A downgrade that does not fit is refused, listing each blocking limit.
- Hitting a limit anywhere gives the same clear message.

**US-51 Organization settings** (M08, P1)
- Admins and Owners can rename the organization and change its time zone.
- An Owner can delete the organization after typing its name; every member loses access and all its data is removed.

## Dashboard and demo

**US-60 See the state of my business** (M09, P1)
- The dashboard shows open, overdue and completed-this-month jobs, active clients, jobs by status, my next 7 days and plan usage; Admins and Owners also see recent activity.
- Each tile links to the matching filtered list. The chart has a table alternative.

**US-61 Try the demo** (M09, P1)
As a prospective client evaluating the developer, I want to try the product without signing up.
- The sign-in page offers "Try as Owner / Admin / Member / Viewer" in demo mode; each opens a seeded organization with realistic data.
- Demo data resets on a schedule; organizations created by visitors disappear after 24 hours.

---

# v2 stories

Rules are in `SPEC.md` 5.7–5.15 (the v2 specification was merged into it at M23; each heading names its sections). **P1** is required for v2.0.0; **P2** can move later with the owner's approval (`PROGRESS.md` → v2 cut list). Ids continue each area's numbering.

## Accounts (v2; SPEC 4.2, 5.1)

**US-05 Reset a forgotten password** (M15, P1)
- From the sign-in page I can ask for a reset link; the answer is the same whether or not the email has an account.
- The link works once and for 30 minutes; after I set a new password, every other session of my account is signed out.
- An expired or used link explains itself and offers a new one.

**US-06 Verify my email** (M15, P1)
- After sign-up I get a verification link; until I use it, a banner reminds me and I cannot send invitations.
- I can ask for the link again (rate-limited).

## Recurring work (SPEC 4.3, 5.7)

**US-70 Set up a service agreement** (M16, P1)
As an Admin or Owner, I want to describe repeating work once so that the visits appear by themselves.
- I choose the client, the visit details and a recurrence (weekly on chosen days, monthly on a day, or monthly on the nth weekday), with a start and an optional end, and I see the next dates before saving.
- Visits for the next 60 days appear in the jobs list, numbered like other jobs; more appear as days pass.
- Members and Viewers can see agreements but not change them.

**US-71 Change, pause and end an agreement** (M16, P1)
- Editing the agreement updates future visits that nobody changed by hand; the visits changed by hand are listed so I can review them.
- I can skip one visit, pause and resume the agreement, or end it; ending cancels the future visits that have not started.
- When the plan's open-jobs limit stops generation, the agreement says so.

## Planning (SPEC 4.4, 5.8)

**US-75 Plan the week on a board** (M17, P1)
As an Admin or Owner, I want to see who does what each day and move work around.
- The board shows a week with a row per person and a column per day, plus unassigned and unscheduled work; the week is in the URL.
- I can drag a visit to another person or day, or do the same by keyboard with "Move to…"; the change is announced and saved.
- Each cell shows planned hours against the daily capacity, and overloaded days are marked with an icon and text.
- If someone else changed the visit first, the card returns and the board reloads with a message.
- Members and Viewers see the board without move actions. On a phone it is a list per day.

## Client portal (SPEC 4.5, 5.9–5.11)

**US-80 Give a client access** (M18, P1)
As an Admin or Owner on the Team or Business plan, I want to invite a client's contacts to a portal.
- On the client page I add a contact (name and email); they get an email explaining how to sign in.
- I can remove a contact; they lose access on their next request.
- On the Free plan the portal is shown as a Team feature, and Owners see how to upgrade.

**US-81 Sign in as a client** (M18, P1)
As a client contact, I want to sign in without a password.
- I enter my email on the business's portal page and receive a link that works once within 15 minutes.
- The page says the same thing whether or not my email is known.
- I see only my own company's visits, quotes and requests.

**US-82 See my visits** (M18, P1)
- I see upcoming and past visits with their date, status and who is coming; a visit page shows its details.

**US-83 Approve a quote** (M18, P1)
As a client contact, I want to approve or decline a price before work starts.
- A quote sent to my company appears in Quotes with the amount; I approve or decline it, optionally with a comment.
- Staff see the decision on the job; work cannot start while the quote is pending or declined.
- If staff change the amount after my approval, the quote comes back for a new decision.

**US-84 Request work** (M18, P1)
- I describe the work and an optional preferred date; I can follow the request's status.
- Staff see new requests in an inbox with a count, and convert one into a prefilled job or decline it with a reason that I can read.

## Completion proof (SPEC 4.6, 5.12)

**US-90 Prove the work was done** (M19, P1)
As the person doing the visit, I want to tick off the checklist and add photos.
- Visits from an agreement come with its checklist; I can tick items and add items by hand.
- I cannot complete the job while a required item is open; the message lists the open items.
- I can add up to 10 photos from my phone; location data is removed before they are stored.

**US-91 Confirm a completed visit** (M19, P1)
As a client contact, I want to see the proof and confirm or report a problem.
- A completed visit shows its checklist and photos; I confirm it or describe a problem.
- Staff see my feedback on the job and are notified of problems.

## Notifications (SPEC 4.7, 5.13)

**US-100 Hear about what matters to me** (M20, P1)
- A bell in the app shows how many unread notifications I have; I can open them, follow their links and mark them as read.
- I get notified when I am assigned, and managers when a client approves or declines a quote, requests work or reports a problem.
- In my account settings I choose which notifications also come by email.

**US-101 Daily overdue digest** (M20, P2)
- At 07:00 in my organization's time zone I get one email listing my overdue jobs (managers get the count); no email when there are none.

## Integrations (SPEC 4.8, 5.14)

**US-110 Connect another tool with an API key** (M21, P1)
As an Owner or Admin on the Business plan, I want to give another system access to clients and jobs.
- I create a read-only or read-write key with a name and optional expiry; it is shown once.
- I see when each key was last used and can revoke it immediately.

**US-111 Receive webhooks** (M21, P1)
- I add an HTTPS endpoint, choose events and get a signing secret, shown once, plus documentation of the signature.
- I see each delivery's result and can redeliver one; an endpoint that keeps failing is disabled and I am told.
- Private network addresses are refused.

**US-112 Export data** (M21, P2)
- Admins and Owners export the audit log for a date range as CSV; anyone who can read jobs exports the current jobs list view as CSV.

## Plans (v2; SPEC 4.9, 5.5)

**US-52 Features by plan** (M18, P1)
- Settings → Plan shows which features each plan includes, as well as its limits.
- Downgrading to a plan without a feature I use lists what will be turned off and asks me to confirm; my data is kept and comes back if I upgrade.
