using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Portal;

/// <summary>A request as the contact follows it: the outcome, and the job it became.</summary>
public sealed record PortalRequestView(
    Guid Id,
    string Title,
    string? Description,
    DateOnly? PreferredOn,
    WorkRequestStatus Status,
    DateTimeOffset CreatedAt,
    string? DeclineReason,
    string? JobLabel,
    string RequestedBy);

/// <summary>
/// Work requests in the portal (US-84, SPEC-V2 5.5): the client's requests (every contact of the client sees them),
/// and a new one from the signed-in contact. The client and the contact come from the session.
/// </summary>
public sealed class PortalRequests(IAppDbContext db, PortalContext portal, TimeProvider time)
{
    public async Task<IReadOnlyList<PortalRequestView>> ListAsync(CancellationToken cancellationToken)
    {
        var mine =
            from r in db.WorkRequests.AsNoTracking()
            where r.ClientId == portal.ClientId
            join c in db.ClientContacts on r.ContactId equals c.Id
            join j in db.Jobs on r.JobId equals (Guid?)j.Id into jobs
            from j in jobs.DefaultIfEmpty()
            orderby r.CreatedAt descending
            select new { Request = r, Contact = c.Name, JobNumber = j == null ? (int?)null : j.Number };
        var rows = await mine
            .Take(100)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(x => new PortalRequestView(
            x.Request.Id, x.Request.Title, x.Request.Description, x.Request.PreferredOn, x.Request.Status, x.Request.CreatedAt,
            x.Request.DeclineReason, x.JobNumber is { } number ? Job.FormatNumber(number) : null, x.Contact))];
    }

    public async Task<PortalRequestView> SubmitAsync(string? title, string? description, DateOnly? preferredOn, CancellationToken cancellationToken)
    {
        var contact = await db.ClientContacts.SingleAsync(c => c.Id == portal.ContactId, cancellationToken);
        var open = await db.WorkRequests.CountAsync(r => r.ContactId == contact.Id && r.Status == WorkRequestStatus.Open, cancellationToken);
        var zone = await db.Organizations.Where(o => o.Id == portal.OrganizationId).Select(o => o.TimeZone).SingleAsync(cancellationToken);
        var now = time.GetUtcNow();
        var request = WorkRequest.Submit(contact, title, description, preferredOn, open, Organization.TodayIn(zone, now), now);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return new PortalRequestView(
            request.Id, request.Title, request.Description, request.PreferredOn, request.Status, request.CreatedAt, null, null, contact.Name);
    }
}
