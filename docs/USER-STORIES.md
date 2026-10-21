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
