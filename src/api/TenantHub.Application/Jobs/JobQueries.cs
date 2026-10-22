using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;

namespace TenantHub.Application.Jobs;

public sealed record JobListItem(
    Guid Id,
    int Number,
    string Label,
    string Title,
    Guid ClientId,
    string ClientName,
    Guid? AssigneeUserId,
    string? AssigneeName,
    JobStatus Status,
    JobPriority Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    bool IsOverdue,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The list filters of US-33. <see cref="Assignee"/> is a user id, <c>me</c> or <c>none</c>; unknown text is ignored,
/// like an unknown sort, so a hand-edited URL still shows a list.
/// </summary>
public sealed record ListJobsRequest(
    string? Search,
    IReadOnlyList<JobStatus> Statuses,
    IReadOnlyList<JobPriority> Priorities,
    string? Assignee,
    Guid? ClientId,
    bool Overdue,
    DateOnly? DueFrom,
    DateOnly? DueTo,
    string? Sort,
    PageRequest Paging);

/// <summary>
/// The jobs list (US-33): search, filters, sort and paging, all in SQL. Sorting comes from a fixed allow-list; an
/// unknown value falls back to the default. "Overdue" and the overdue marker use today in the organization's zone.
/// </summary>
public sealed partial class ListJobsQuery(IAppDbContext db, ITenantContext tenant, IIdentityService identity, JobViews views)
{
    public const string DefaultSort = "-number";

    public static readonly IReadOnlyList<string> Sorts =
        ["number", "-number", "due", "-due", "updated", "-updated", "priority", "-priority", "title", "-title"];

    public async Task<PagedList<JobListItem>> HandleAsync(ListJobsRequest request, CancellationToken cancellationToken)
    {
        var today = await views.TodayAsync(cancellationToken);
        var jobs = db.Jobs.AsNoTracking();

        if (request.Statuses.Count > 0)
        {
            var statuses = request.Statuses.ToList();
            jobs = jobs.Where(j => statuses.Contains(j.Status));
        }

        if (request.Priorities.Count > 0)
        {
            var priorities = request.Priorities.ToList();
            jobs = jobs.Where(j => priorities.Contains(j.Priority));
        }

        switch (request.Assignee?.Trim().ToLowerInvariant())
        {
            case "me":
                var me = tenant.UserId!.Value;
                jobs = jobs.Where(j => j.AssigneeUserId == me);
                break;
            case "none":
                jobs = jobs.Where(j => j.AssigneeUserId == null);
                break;
            case { } text when Guid.TryParse(text, out var assignee):
                jobs = jobs.Where(j => j.AssigneeUserId == assignee);
                break;
        }

        if (request.ClientId is { } clientId)
        {
            jobs = jobs.Where(j => j.ClientId == clientId);
        }

        if (request.Overdue)
        {
            jobs = jobs.Where(j => j.DueOn < today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled);
        }

        if (request.DueFrom is { } from)
        {
            jobs = jobs.Where(j => j.DueOn >= from);
        }

        if (request.DueTo is { } to)
        {
            jobs = jobs.Where(j => j.DueOn <= to);
        }

        if (request.Search?.Trim() is { Length: > 0 } search)
        {
            // "JOB-0042", "job42" or "42" find that number; any text also matches titles (parameterized, in SQL).
            var term = search.ToLowerInvariant();
            int? number = NumberPattern().Match(search) is { Success: true } match ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(...) and strpos; never runs as .NET string code.
            jobs = jobs.Where(j => (number != null && j.Number == number) || j.Title.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await jobs.CountAsync(cancellationToken);
        var sorted = Sort(jobs, request.Sort);

        var rows = await sorted
            .Skip(request.Paging.Skip)
            .Take(request.Paging.PageSize)
            .Select(j => new
            {
                j.Id,
                j.Number,
                j.Title,
                j.ClientId,
                ClientName = db.Clients.Where(c => c.Id == j.ClientId).Select(c => c.Name).FirstOrDefault(),
                j.AssigneeUserId,
                j.Status,
                j.Priority,
                j.ScheduledOn,
                j.DueOn,
                IsOverdue = j.DueOn < today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled,
                j.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var people = await identity.FindProfilesAsync([.. rows.Select(r => r.AssigneeUserId).OfType<Guid>().Distinct()], cancellationToken);
        var items = rows
            .Select(r => new JobListItem(
                r.Id,
                r.Number,
                Job.FormatNumber(r.Number),
                r.Title,
                r.ClientId,
                r.ClientName ?? "Unknown client",
                r.AssigneeUserId,
                r.AssigneeUserId is { } id ? people.GetValueOrDefault(id)?.DisplayName ?? "A former member" : null,
                r.Status,
                r.Priority,
                r.ScheduledOn,
                r.DueOn,
                r.IsOverdue,
                r.UpdatedAt))
            .ToList();

        return new PagedList<JobListItem>(items, request.Paging.Page, request.Paging.PageSize, total);
    }

    private static IQueryable<Job> Sort(IQueryable<Job> jobs, string? sort) =>
        (Sorts.Contains(sort) ? sort : DefaultSort) switch
        {
            "number" => jobs.OrderBy(j => j.Number),
            "title" => jobs.OrderBy(j => j.Title).ThenBy(j => j.Number),
            "-title" => jobs.OrderByDescending(j => j.Title).ThenBy(j => j.Number),
            "updated" => jobs.OrderBy(j => j.UpdatedAt).ThenBy(j => j.Number),
            "-updated" => jobs.OrderByDescending(j => j.UpdatedAt).ThenBy(j => j.Number),

            // Jobs without a due date go last in both directions.
            "due" => jobs.OrderBy(j => j.DueOn == null).ThenBy(j => j.DueOn).ThenBy(j => j.Number),
            "-due" => jobs.OrderBy(j => j.DueOn == null).ThenByDescending(j => j.DueOn).ThenBy(j => j.Number),

            // Stored as names, so rank them: Urgent > High > Normal > Low.
            "priority" => jobs.OrderBy(j => j.Priority == JobPriority.Urgent ? 3 : j.Priority == JobPriority.High ? 2 : j.Priority == JobPriority.Normal ? 1 : 0).ThenBy(j => j.Number),
            "-priority" => jobs.OrderByDescending(j => j.Priority == JobPriority.Urgent ? 3 : j.Priority == JobPriority.High ? 2 : j.Priority == JobPriority.Normal ? 1 : 0).ThenBy(j => j.Number),
            _ => jobs.OrderByDescending(j => j.Number),
        };

    [GeneratedRegex(@"^(?:job[- ]?)?(\d{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}

public sealed class GetJobQuery(IAppDbContext db, JobViews views)
{
    public async Task<JobView> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken)
            ?? throw JobErrors.NotFound();
        return await views.ForAsync(job, cancellationToken);
    }
}

public sealed record JobHistoryEntry(long Id, DateTimeOffset OccurredAt, string ActorName, string Action, string Summary, IReadOnlyList<FieldChange> Changes);

/// <summary>
/// A job's history for everyone who can read the job (US-40): its audit entries, newest first. The job itself is
/// loaded first, so another organization's id (or a deleted job) is simply "not found".
/// </summary>
public sealed class JobHistoryQuery(IAppDbContext db)
{
    public const int MaxEntries = 200;

    public async Task<IReadOnlyList<JobHistoryEntry>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id, cancellationToken))
        {
            throw JobErrors.NotFound();
        }

        return await db.AuditEntries
            .AsNoTracking()
            .Where(a => a.EntityType == "job" && a.EntityId == id)
            .OrderByDescending(a => a.Id)
            .Take(MaxEntries)
            .Select(a => new JobHistoryEntry(a.Id, a.OccurredAt, a.ActorName, a.Action, a.Summary, a.Changes))
            .ToListAsync(cancellationToken);
    }
}
