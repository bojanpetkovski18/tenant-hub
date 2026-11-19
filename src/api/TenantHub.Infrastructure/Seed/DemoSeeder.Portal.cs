using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Seed;

public sealed partial class DemoSeeder
{
    /// <summary>
    /// The portal's story at Brightline (SPEC 8.3, M18): three clients with an upcoming quoted job get a contact (the
    /// client's own contact person); one quote waits for a decision, one was approved by its contact; two requests are
    /// open and one was declined with a reason. Written after the history by the same domain methods as the API,
    /// contacts acting as themselves. Contacts are dated before the organization: that is how "Try as a client" tells
    /// the seeded ones from any a demo visitor adds later (<c>PortalSignIn.DemoContactsAsync</c>, D040).
    /// </summary>
    private async Task SeedPortalAsync(Organization organization, Guid ownerId, SeedClock clock, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var today = Organization.TodayIn(organization.TimeZone, now);
        var contacts = new List<ClientContact>();
        var quoted = new List<Guid>();
        await using (var staff = Context(organization.Id, ownerId, Role.Owner, clock))
        {
            // The clients whose next one-off job has a quoted amount, soonest first: the seed's plan is deterministic, so
            // these are the same clients every time it runs on a given day.
            var next = await staff.Jobs.AsNoTracking()
                .Where(j => j.AgreementId == null && j.QuotedAmount != null && j.ScheduledOn >= today
                    && (j.Status == JobStatus.New || j.Status == JobStatus.Scheduled))
                .Join(
                    staff.Clients.Where(c => c.Status == ClientStatus.Active && c.Email != null && c.ContactName != null),
                    j => j.ClientId,
                    c => c.Id,
                    (j, c) => new { j.Id, j.ClientId, j.ScheduledOn, j.Number })
                .OrderBy(j => j.ScheduledOn).ThenBy(j => j.Number)
                .ToListAsync(cancellationToken);
            // Clients whose recent work has photos first (M19), so two of them can sign a visit off; still soonest first.
            var withProof = (await staff.Jobs.AsNoTracking()
                    .Where(j => j.Status == JobStatus.Completed && staff.Attachments.Any(a => a.JobId == j.Id))
                    .Select(j => j.ClientId)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var chosen = next.GroupBy(j => j.ClientId).Select(g => g.First())
                .OrderBy(j => withProof.Contains(j.ClientId) ? 0 : 1)
                .Take(3)
                .ToList();
            clock.Now = organization.CreatedAt.AddDays(-1);
            foreach (var pick in chosen)
            {
                var client = await staff.Clients.SingleAsync(c => c.Id == pick.ClientId, cancellationToken);
                var contact = ClientContact.Add(client, client.ContactName, client.Email, 0, false, ownerId, clock.Now);
                staff.ClientContacts.Add(contact);
                contacts.Add(contact);
            }

            await staff.SaveChangesAsync(cancellationToken);

            // Each change at its own time, so its audit entry carries that time too.
            foreach (var pick in chosen.Take(2))
            {
                clock.Now = now.AddHours(-30 + (quoted.Count * 6));
                var job = await staff.Jobs.SingleAsync(j => j.Id == pick.Id, cancellationToken);
                job.SendForApproval(ownerId, activeContacts: 1, clock.Now);
                quoted.Add(job.Id);
                await staff.SaveChangesAsync(cancellationToken);
            }
        }

        if (contacts.Count < 3)
        {
            clock.Now = now;
            return; // Not expected with the seed's plan; the rest of the story needs three contacts.
        }

        await using (var asContact = ContactContext(organization.Id, contacts[1], clock))
        {
            clock.Now = now.AddHours(-3);
            var job = await asContact.Jobs.SingleAsync(j => j.Id == quoted[1], cancellationToken);
            job.Decide(approve: true, contacts[1].Id, "Thank you, please go ahead.", clock.Now);
            await asContact.SaveChangesAsync(cancellationToken);
        }

        // Requests: open from the first and third contacts; the second one's is declined with a reason.
        var requests = new (int Contact, string Title, string? Description, int? InDays)[]
        {
            (0, "Deep clean after our office move", "We move into the second floor on the 1st; the old floor needs a full clean.", 10),
            (2, "Extra window cleaning before an open day", null, 6),
            (1, "Carpet shampoo in the waiting room", "Coffee stains near the reception desk.", null),
        };
        var declineId = Guid.Empty;
        foreach (var (index, title, description, inDays) in requests)
        {
            clock.Now = now.AddHours(-26 + (index * 7));
            await using var asContact = ContactContext(organization.Id, contacts[index], clock);
            var contact = await asContact.ClientContacts.SingleAsync(c => c.Id == contacts[index].Id, cancellationToken);
            var request = WorkRequest.Submit(contact, title, description, inDays is { } d ? today.AddDays(d) : null, 0, today, clock.Now);
            asContact.WorkRequests.Add(request);
            await asContact.SaveChangesAsync(cancellationToken);
            if (index == 1)
            {
                declineId = request.Id;
            }
        }

        await using (var staff = Context(organization.Id, ownerId, Role.Owner, clock))
        {
            clock.Now = now.AddHours(-2);
            var request = await staff.WorkRequests.SingleAsync(r => r.Id == declineId, cancellationToken);
            request.Decline("We no longer offer carpet shampooing; we can recommend a specialist.", ownerId, clock.Now);
            await staff.SaveChangesAsync(cancellationToken);
        }

        await SeedSignOffsAsync(organization, contacts, now, clock, cancellationToken);
        clock.Now = now;
    }

    /// <summary>
    /// Client sign-off (M19): the first contact confirmed their latest completed visit with photos, the second reported a
    /// problem with theirs; the third has left theirs for the demo visitor to answer.
    /// </summary>
    private async Task SeedSignOffsAsync(Organization organization, List<ClientContact> contacts, DateTimeOffset now, SeedClock clock, CancellationToken cancellationToken)
    {
        var answers = new (bool Confirmed, string? Text)[]
        {
            (true, null),
            (false, "The meeting room was not vacuumed, and the bins by the back door were still full."),
        };
        foreach (var (contact, (confirmed, text)) in contacts.Zip(answers))
        {
            await using var asContact = ContactContext(organization.Id, contact, clock);
            var visit = await asContact.Jobs
                .Where(j => j.ClientId == contact.ClientId && j.Status == JobStatus.Completed && asContact.Attachments.Any(a => a.JobId == j.Id))
                .OrderByDescending(j => j.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (visit is null)
            {
                continue;
            }

            // A few hours after the visit, but never in the future.
            var answeredAt = visit.CompletedAt!.Value.AddHours(5);
            clock.Now = answeredAt > now ? now : answeredAt;
            visit.RecordFeedback(confirmed, text, contact.Id, clock.Now);
            await asContact.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>A context in the contact's portal session, so what they do is checked and audited as them.</summary>
    private AppDbContext ContactContext(Guid organizationId, ClientContact contact, SeedClock clock)
    {
        var tenant = new TenantContext();
        tenant.EnterAsContact(organizationId, contact.ClientId, contact.Id);
        return new AppDbContext(options, tenant, clock);
    }
}
