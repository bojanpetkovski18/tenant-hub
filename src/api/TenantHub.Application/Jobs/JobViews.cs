using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Jobs;

/// <summary>
/// One job as the detail page and the edit form need it, with what the caller may do (so the SPA hides the rest
/// without re-implementing the rules, D008): the allowed moves with what is still missing for each, and the edit,
/// reassign and delete rights.
/// </summary>
public sealed record JobView(
    Guid Id,
    int Number,
    string Label,
    string Title,
    string? Description,
    Guid ClientId,
    string ClientName,
    Guid? AssigneeUserId,
    string? AssigneeName,
    JobStatus Status,
    JobPriority Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount,
    int? EstimatedMinutes,
    Guid? AgreementId,
    string? AgreementTitle,
    DateOnly? OccurrenceOn,
    bool ChangedByHand,
    bool IsOverdue,
    DateTimeOffset CreatedAt,
    string CreatedByName,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    uint Version,
    bool CanEdit,
    bool CanAssign,
    bool CanDelete,
    bool CanSkip,
    IReadOnlyList<JobTransitionOption> Transitions);

public static class JobErrors
{
    public static DomainException NotFound() =>
        new("job-not-found", "Job not found.", DomainErrorKind.NotFound);

    public static DomainException ConcurrencyConflict() =>
        new("concurrency-conflict", "Someone else changed this job after you opened it. Reload to see their changes.", DomainErrorKind.Conflict);
}

/// <summary>Builds <see cref="JobView"/>s: names, "today" in the organization's zone, and the caller's rights.</summary>
public sealed class JobViews(IAppDbContext db, ITenantContext tenant, IIdentityService identity, TimeProvider time)
{
    public JobActor Actor() => JobActor.From(tenant.UserId!.Value, tenant.Permissions);

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        Organization.TodayIn(await TimeZoneAsync(cancellationToken), time.GetUtcNow());

    public Task<string> TimeZoneAsync(CancellationToken cancellationToken) =>
        db.Organizations
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => o.TimeZone)
            .SingleAsync(cancellationToken);

    /// <summary>Display names for audit text and the view: clients by id and people by user id.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        IEnumerable<Guid> clientIds,
        IEnumerable<Guid?> userIds,
        CancellationToken cancellationToken)
    {
        var clients = clientIds.Distinct().ToList();
        var users = userIds.OfType<Guid>().Distinct().ToList();
        var names = new Dictionary<Guid, string>();

        foreach (var client in await db.Clients.AsNoTracking().Where(c => clients.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(cancellationToken))
        {
            names[client.Id] = client.Name;
        }

        foreach (var (id, profile) in await identity.FindProfilesAsync(users, cancellationToken))
        {
            names[id] = profile.DisplayName;
        }

        return names;
    }

    public async Task<JobView> ForAsync(Job job, CancellationToken cancellationToken)
    {
        var names = await NamesAsync([job.ClientId], [job.AssigneeUserId, job.CreatedByUserId], cancellationToken);
        var today = await TodayAsync(cancellationToken);
        var agreementTitle = job.AgreementId is { } agreementId
            ? await db.Agreements.Where(a => a.Id == agreementId).Select(a => a.Title).SingleOrDefaultAsync(cancellationToken)
            : null;
        var actor = Actor();
        return new JobView(
            job.Id,
            job.Number,
            job.Label,
            job.Title,
            job.Description,
            job.ClientId,
            names.GetValueOrDefault(job.ClientId, "Unknown client"),
            job.AssigneeUserId,
            job.AssigneeUserId is { } assignee ? names.GetValueOrDefault(assignee, "A former member") : null,
            job.Status,
            job.Priority,
            job.ScheduledOn,
            job.DueOn,
            job.QuotedAmount,
            job.EstimatedMinutes,
            job.AgreementId,
            agreementTitle,
            job.OccurrenceOn,
            job.ChangedByHand,
            job.IsOverdue(today),
            job.CreatedAt,
            names.GetValueOrDefault(job.CreatedByUserId, "A former member"),
            job.UpdatedAt,
            job.CompletedAt,
            job.Version,
            CanEdit: job.CanEdit(actor),
            CanAssign: actor.CanManage,
            CanDelete: actor.CanManage && job.Status is JobStatus.New or JobStatus.Cancelled,
            // Skipping is cancelling a visit with a fixed reason: who may cancel, for visits of an agreement (SPEC-V2 2.2).
            CanSkip: job.AgreementId is not null && tenant.HasPermission(Permissions.AgreementsManage)
                && job.AllowedFor(actor).Contains(JobStatus.Cancelled),
            job.OptionsFor(actor));
    }
}
