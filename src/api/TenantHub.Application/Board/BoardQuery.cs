using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Board;

/// <summary>A job as a board card needs it, with the version a move sends back.</summary>
public sealed record BoardCard(
    Guid Id,
    int Number,
    string Title,
    Guid ClientId,
    string ClientName,
    Guid? AssigneeUserId,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    JobStatus Status,
    JobPriority Priority,
    int? EstimatedMinutes,
    bool IsVisit,
    uint Version)
{
    public string Label => Job.FormatNumber(Number);
}

/// <summary>A row of the board: a person who can do the work, or someone who still has work in this week.</summary>
public sealed record BoardPerson(Guid UserId, string Name, bool Active);

/// <summary>Planned minutes for one person (null: unassigned) on one day: the jobs that are not cancelled.</summary>
public sealed record BoardLoad(Guid? AssigneeUserId, DateOnly Day, int Minutes, int Jobs);

/// <summary>
/// One week of the board (SPEC-V2 4.3, US-75): the organization's Monday-to-Sunday in its time zone, the people, the
/// jobs scheduled in the week, open jobs without a day (the nearest due first, at most <see cref="BoardQuery.UnscheduledCap"/>),
/// each person's planned minutes per day, and the daily capacity they are compared with. <see cref="CanPlan"/> is
/// whether the caller may move cards (<c>jobs.manage</c>).
/// </summary>
public sealed record BoardView(
    DateOnly WeekStart,
    DateOnly Today,
    int DailyCapacityMinutes,
    IReadOnlyList<BoardPerson> People,
    IReadOnlyList<BoardCard> Cards,
    IReadOnlyList<BoardCard> Unscheduled,
    int UnscheduledTotal,
    IReadOnlyList<BoardLoad> Load,
    bool CanPlan);

public sealed record BoardRequest(DateOnly? Week, Guid? ClientId, IReadOnlyList<JobStatus> Statuses);

/// <summary>
/// The board read (SPEC-V2 7, <c>GET /board</c>): four queries, filtering and the load sums in SQL, nothing loaded
/// beyond the week. Names come from one profile lookup.
/// </summary>
public sealed class BoardQuery(IAppDbContext db, ITenantContext tenant, IIdentityService identity, TimeProvider time)
{
    /// <summary>
    /// The unscheduled area shows the nearest-due open jobs; the total says how many more the jobs list has.
    /// </summary>
    public const int UnscheduledCap = 50;

    public async Task<BoardView> HandleAsync(BoardRequest request, CancellationToken cancellationToken)
    {
        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new { o.TimeZone, o.DailyCapacityMinutes })
            .SingleAsync(cancellationToken);
        var today = Organization.TodayIn(organization.TimeZone, time.GetUtcNow());

        // Any day of the week stands for its week; the default is this week. Monday is the first day (ISO).
        var anyDay = request.Week ?? today;
        var weekStart = anyDay.AddDays(-(((int)anyDay.DayOfWeek + 6) % 7));
        var weekEnd = weekStart.AddDays(6);

        var jobs = db.Jobs.AsNoTracking();
        if (request.ClientId is { } clientId)
        {
            jobs = jobs.Where(j => j.ClientId == clientId);
        }

        var inWeek = jobs.Where(j => j.ScheduledOn >= weekStart && j.ScheduledOn <= weekEnd);
        var shown = request.Statuses.Count > 0 ? inWeek.Where(j => request.Statuses.Contains(j.Status)) : inWeek;
        var cards = await Project(shown.OrderBy(j => j.ScheduledOn).ThenBy(j => j.Number)).ToListAsync(cancellationToken);

        // Open jobs without a day: the planning queue.
        var unscheduled = jobs.Where(j => j.ScheduledOn == null && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled);
        var unscheduledTotal = await unscheduled.CountAsync(cancellationToken);
        var queue = await Project(unscheduled
                .OrderBy(j => j.DueOn == null)
                .ThenBy(j => j.DueOn)
                .ThenBy(j => j.Number)
                .Take(UnscheduledCap))
            .ToListAsync(cancellationToken);

        // The load counts every job of the day that is not cancelled, whatever the filters show: it is the real day.
        var load = await db.Jobs.AsNoTracking()
            .Where(j => j.ScheduledOn >= weekStart && j.ScheduledOn <= weekEnd && j.Status != JobStatus.Cancelled)
            .GroupBy(j => new { j.AssigneeUserId, Day = j.ScheduledOn!.Value })
            .Select(g => new BoardLoad(g.Key.AssigneeUserId, g.Key.Day, g.Sum(j => j.EstimatedMinutes ?? 0), g.Count()))
            .ToListAsync(cancellationToken);

        // Rows: everyone active who can do the work, and anyone else who still has a card this week.
        // Who can do work is the permission map's answer (D008), not a role name.
        var workers = (await db.Memberships.AsNoTracking()
                .Where(m => m.RemovedAt == null)
                .Select(m => new { m.UserId, m.Role })
                .ToListAsync(cancellationToken))
            .Where(m => Permissions.For(m.Role).Contains(Permissions.JobsWork))
            .Select(m => m.UserId)
            .ToList();
        var others = cards.Select(c => c.AssigneeUserId).OfType<Guid>().Except(workers).ToList();
        var profiles = await identity.FindProfilesAsync([.. workers.Concat(others).Distinct()], cancellationToken);
        var people = workers.Select(id => new BoardPerson(id, profiles.GetValueOrDefault(id)?.DisplayName ?? "A member", Active: true))
            .Concat(others.Select(id => new BoardPerson(id, profiles.GetValueOrDefault(id)?.DisplayName ?? "A former member", Active: false)))
            .OrderBy(p => !p.Active)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new BoardView(
            weekStart,
            today,
            organization.DailyCapacityMinutes,
            people,
            cards,
            queue,
            unscheduledTotal,
            load,
            tenant.HasPermission(Permissions.JobsManage));
    }

    private IQueryable<BoardCard> Project(IQueryable<Job> jobs) =>
        jobs.Select(j => new BoardCard(
            j.Id,
            j.Number,
            j.Title,
            j.ClientId,
            db.Clients.Where(c => c.Id == j.ClientId).Select(c => c.Name).FirstOrDefault() ?? "Unknown client",
            j.AssigneeUserId,
            j.ScheduledOn,
            j.DueOn,
            j.Status,
            j.Priority,
            j.EstimatedMinutes,
            j.AgreementId != null,
            j.Version));
}
