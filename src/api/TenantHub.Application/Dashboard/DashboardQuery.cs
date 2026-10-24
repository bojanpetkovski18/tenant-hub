using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Audit;
using TenantHub.Application.Organizations;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Dashboard;

public sealed record DashboardTiles(int OpenJobs, int OverdueJobs, int CompletedThisMonth, int ActiveClients);

public sealed record StatusCount(JobStatus Status, int Count);

public sealed record UpcomingJob(Guid Id, string Label, string Title, string ClientName, DateOnly ScheduledOn, JobStatus Status, bool IsOverdue);

/// <summary>
/// The dashboard (US-60), in one round trip. <see cref="Today"/> and <see cref="MonthStart"/> are the organization's
/// (SPEC 4.1: "overdue" and "this month" use its time zone), so the SPA can link each tile to the matching list.
/// <see cref="RecentActivity"/> is null for callers without <c>audit.read</c>.
/// </summary>
public sealed record DashboardView(
    DateOnly Today,
    DateOnly MonthStart,
    string TimeZone,
    DashboardTiles Tiles,
    IReadOnlyList<StatusCount> JobsByStatus,
    IReadOnlyList<UpcomingJob> MyNextSevenDays,
    Plan Plan,
    PlanUsage Usage,
    PlanLimits Limits,
    IReadOnlyList<AuditEntryView>? RecentActivity);

/// <summary>
/// Everything the dashboard shows, aggregated in SQL: the job counts are one grouped statement (counts with
/// filters), the rest are small indexed queries. Tenant-scoped like every query; no organization condition here.
/// </summary>
public sealed class DashboardQuery(
    IAppDbContext db,
    ITenantContext tenant,
    PlanLimitsCheck limits,
    ListAuditEntriesQuery audit,
    TimeProvider time)
{
    public const int UpcomingLimit = 10;
    public const int ActivityLimit = 8;

    public async Task<DashboardView> HandleAsync(CancellationToken cancellationToken)
    {
        var organization = await db.Organizations
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new { o.TimeZone, o.Plan })
            .SingleAsync(cancellationToken);
        var now = time.GetUtcNow();
        var today = Organization.TodayIn(organization.TimeZone, now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthStartsAt = Organization.StartOfDayIn(organization.TimeZone, monthStart);

        var counts = await db.Jobs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                New = g.Count(j => j.Status == JobStatus.New),
                Scheduled = g.Count(j => j.Status == JobStatus.Scheduled),
                InProgress = g.Count(j => j.Status == JobStatus.InProgress),
                Completed = g.Count(j => j.Status == JobStatus.Completed),
                Cancelled = g.Count(j => j.Status == JobStatus.Cancelled),
                Overdue = g.Count(j => j.DueOn < today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled),
                CompletedThisMonth = g.Count(j => j.Status == JobStatus.Completed && j.CompletedAt >= monthStartsAt),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var activeClients = await db.Clients.CountAsync(c => c.Status == ClientStatus.Active, cancellationToken);

        var me = tenant.UserId!.Value;
        var weekEnd = today.AddDays(7);
        var upcoming = await db.Jobs
            .AsNoTracking()
            .Where(j => j.AssigneeUserId == me && j.ScheduledOn >= today && j.ScheduledOn < weekEnd
                && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
            .OrderBy(j => j.ScheduledOn)
            .ThenBy(j => j.Number)
            .Take(UpcomingLimit)
            .Select(j => new
            {
                j.Id,
                j.Number,
                j.Title,
                ClientName = db.Clients.Where(c => c.Id == j.ClientId).Select(c => c.Name).FirstOrDefault(),
                ScheduledOn = j.ScheduledOn!.Value,
                j.Status,
                j.DueOn,
            })
            .ToListAsync(cancellationToken);

        var byStatus = new List<StatusCount>
        {
            new(JobStatus.New, counts?.New ?? 0),
            new(JobStatus.Scheduled, counts?.Scheduled ?? 0),
            new(JobStatus.InProgress, counts?.InProgress ?? 0),
            new(JobStatus.Completed, counts?.Completed ?? 0),
            new(JobStatus.Cancelled, counts?.Cancelled ?? 0),
        };
        var open = (counts?.New ?? 0) + (counts?.Scheduled ?? 0) + (counts?.InProgress ?? 0);
        var usage = new PlanUsage(await limits.SeatsAsync(cancellationToken), activeClients, open);

        IReadOnlyList<AuditEntryView>? activity = null;
        if (tenant.HasPermission(Permissions.AuditRead))
        {
            activity = (await audit.HandleAsync(new AuditListRequest(null, null, null, null, null, null, null, ActivityLimit), cancellationToken)).Items;
        }

        return new DashboardView(
            today,
            monthStart,
            organization.TimeZone,
            new DashboardTiles(open, counts?.Overdue ?? 0, counts?.CompletedThisMonth ?? 0, activeClients),
            byStatus,
            [.. upcoming.Select(j => new UpcomingJob(j.Id, Job.FormatNumber(j.Number), j.Title, j.ClientName ?? "Unknown client", j.ScheduledOn, j.Status, Job.IsOverdue(j.DueOn, j.Status, today)))],
            organization.Plan,
            usage,
            PlanLimits.For(organization.Plan),
            activity);
    }
}
