using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
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
    DateTimeOffset? CompletedAt)
{
    /// <summary>The proof (SPEC-V2 5.6), once the visit is completed: before that it is work in progress.</summary>
    public IReadOnlyList<PortalChecklistItem> Checklist { get; init; } = [];

    public IReadOnlyList<PortalPhoto> Photos { get; init; } = [];

    public PortalFeedback? Feedback { get; init; }

    /// <summary>Completed and not signed off yet: the contact may confirm it or report a problem, once.</summary>
    public bool CanGiveFeedback { get; init; }
}

public sealed record PortalChecklistItem(string Text, bool Required, bool Done);

public sealed record PortalPhoto(Guid Id, int Width, int Height);

public sealed record PortalFeedback(ClientFeedback Kind, string? Text, DateTimeOffset At, string? ByName);

/// <summary>
/// The contact's visits (US-82, SPEC-V2 2.3): their client's jobs, upcoming (still open) or past (completed or
/// cancelled in the last 12 months). Every query names the session's client explicitly; row-level security on
/// <c>app.client_id</c> enforces the same underneath, and the portal matrix proves both.
/// </summary>
public sealed class PortalVisits(IAppDbContext db, PortalContext portal, IIdentityService identity, IFileStore files, TimeProvider time)
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
        return await DetailAsync(job, cancellationToken);
    }

    /// <summary>A photo of one of the client's completed visits; anything else is "not found".</summary>
    public async Task<PhotoFile> OpenPhotoAsync(Guid visitId, Guid photoId, bool thumbnail, CancellationToken cancellationToken)
    {
        var number = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == visitId && j.ClientId == portal.ClientId && j.Status == JobStatus.Completed)
            .Select(j => (int?)j.Number)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();
        var photo = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == photoId && a.JobId == visitId, cancellationToken)
            ?? throw PhotoCommands.NotFound();
        return await PhotoCommands.OpenAsync(photo, Job.FormatNumber(number), thumbnail, files, cancellationToken);
    }

    /// <summary>
    /// The contact confirms a completed visit or reports a problem (US-91, SPEC-V2 5.6): once; two contacts at the same
    /// moment make one sign-off (the version check), and the other hears that it is already given.
    /// </summary>
    public async Task<PortalVisitDetail> RecordFeedbackAsync(Guid visitId, bool confirmed, string? text, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == visitId && j.ClientId == portal.ClientId, cancellationToken)
            ?? throw NotFound();
        job.RecordFeedback(confirmed, text, portal.ContactId, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException("feedback-already-given", "This visit changed while you were answering. Reload to see where it stands.");
        }

        return await DetailAsync(job, cancellationToken);
    }

    public static DomainException NotFound() => new(NotFoundCode, "Visit not found.", DomainErrorKind.NotFound);

    private async Task<PortalVisitDetail> DetailAsync(Job job, CancellationToken cancellationToken)
    {
        var names = await NamesAsync([job.AssigneeUserId], cancellationToken);
        var detail = new PortalVisitDetail(
            job.Id, job.Label, job.Title, job.Description, job.ScheduledOn, job.DueOn, job.Status, Name(names, job.AssigneeUserId), job.CompletedAt);
        if (job.Status != JobStatus.Completed)
        {
            return detail;
        }

        var checklist = await db.ChecklistItems.AsNoTracking()
            .Where(i => i.JobId == job.Id)
            .OrderBy(i => i.Position)
            .Select(i => new PortalChecklistItem(i.Text, i.Required, i.DoneAt != null))
            .ToListAsync(cancellationToken);
        var photos = await db.Attachments.AsNoTracking()
            .Where(a => a.JobId == job.Id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new PortalPhoto(a.Id, a.Width, a.Height))
            .ToListAsync(cancellationToken);
        var by = job.ClientFeedbackByContactId is { } contact
            ? await db.ClientContacts.Where(c => c.Id == contact).Select(c => c.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        return detail with
        {
            Checklist = checklist,
            Photos = photos,
            Feedback = job.ClientFeedback is { } kind ? new PortalFeedback(kind, job.ClientFeedbackText, job.ClientFeedbackAt!.Value, by) : null,
            CanGiveFeedback = job.ClientFeedback is null,
        };
    }

    /// <summary>Display names only (SPEC-V2 2.3): nothing else about the organization's people.</summary>
    private async Task<IReadOnlyDictionary<Guid, UserProfile>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken) =>
        await identity.FindProfilesAsync([.. ids.OfType<Guid>().Distinct()], cancellationToken);

    private static string? Name(IReadOnlyDictionary<Guid, UserProfile> names, Guid? id) =>
        id is { } user && names.TryGetValue(user, out var profile) ? profile.DisplayName : null;
}
