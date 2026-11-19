using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;

namespace TenantHub.Application.Portal;

public sealed record PortalVisitSummary(Guid Id, string Label, string Title, DateOnly? ScheduledOn, JobStatus Status, string? AssigneeName);

public sealed record PortalVisitDetail(
    Guid Id,
    string Label,
    string Title,
    string? Description,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    JobStatus Status,
    string? AssigneeName,
    DateTimeOffset? CompletedAt);

/// <summary>
/// The contact's visits (US-82, SPEC-V2 2.3): their client's jobs, upcoming (still open) or past (completed or
/// cancelled in the last 12 months). Every query names the session's client explicitly; row-level security on
/// <c>app.client_id</c> enforces the same underneath, and the portal matrix proves both.
/// </summary>
public sealed class PortalVisits(IAppDbContext db, PortalContext portal, IIdentityService identity, TimeProvider time)
{
    public const string NotFoundCode = "visit-not-found";
    private static readonly TimeSpan PastWindow = TimeSpan.FromDays(365);

    public async Task<PagedList<PortalVisitSummary>> ListAsync(string? when, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var paging = PageRequest.From(page, pageSize);
        var mine = db.Jobs.AsNoTracking().Where(j => j.ClientId == portal.ClientId);
        IQueryable<Job> query;
        if (string.Equals(when, "past", StringComparison.OrdinalIgnoreCase))
        {
            var since = time.GetUtcNow() - PastWindow;
            query = mine
                .Where(j => (j.Status == JobStatus.Completed || j.Status == JobStatus.Cancelled) && j.UpdatedAt >= since)
                .OrderByDescending(j => j.ScheduledOn).ThenByDescending(j => j.Number);
        }
        else
        {
            query = mine
                .Where(j => j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
                .OrderBy(j => j.ScheduledOn == null).ThenBy(j => j.ScheduledOn).ThenBy(j => j.Number);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(paging.Skip).Take(paging.PageSize)
            .Select(j => new { j.Id, j.Number, j.Title, j.ScheduledOn, j.Status, j.AssigneeUserId })
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.Select(r => r.AssigneeUserId), cancellationToken);
        return new PagedList<PortalVisitSummary>(
            [.. rows.Select(r => new PortalVisitSummary(r.Id, Job.FormatNumber(r.Number), r.Title, r.ScheduledOn, r.Status, Name(names, r.AssigneeUserId)))],
            paging.Page,
            paging.PageSize,
            total);
    }

    public async Task<PortalVisitDetail> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.ClientId == portal.ClientId, cancellationToken)
            ?? throw NotFound();
        var names = await NamesAsync([job.AssigneeUserId], cancellationToken);
        return new PortalVisitDetail(
            job.Id, job.Label, job.Title, job.Description, job.ScheduledOn, job.DueOn, job.Status, Name(names, job.AssigneeUserId), job.CompletedAt);
    }

    public static DomainException NotFound() => new(NotFoundCode, "Visit not found.", DomainErrorKind.NotFound);

    /// <summary>Display names only (SPEC-V2 2.3): nothing else about the organization's people.</summary>
    private async Task<IReadOnlyDictionary<Guid, UserProfile>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken) =>
        await identity.FindProfilesAsync([.. ids.OfType<Guid>().Distinct()], cancellationToken);

    private static string? Name(IReadOnlyDictionary<Guid, UserProfile> names, Guid? id) =>
        id is { } user && names.TryGetValue(user, out var profile) ? profile.DisplayName : null;
}
