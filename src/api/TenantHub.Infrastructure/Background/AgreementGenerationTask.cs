using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Agreements;
using TenantHub.Application.Notifications;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Background;

/// <summary>
/// The daily visit generation of one organization (SPEC-V2 4.9, D041). The scheduler gives each run its own scope, and
/// the run enters that organization as the system: one tenant per task, as SPEC-V2 8 describes the scheduler.
/// </summary>
internal sealed class AgreementGenerationTask(DailyVisitGeneration generation) : IScheduledTask
{
    public Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken) =>
        generation.RunAsync(run.OrganizationId ?? throw new InvalidOperationException("Visit generation runs for one organization."), cancellationToken);
}

/// <summary>Writes per-organization scheduler rows in the caller's transaction (the agreement that needs one).</summary>
internal sealed class RecurringWork(AppDbContext db) : IRecurringWork
{
    public async Task ScheduleDigestAsync(Guid organizationId, DateTimeOffset due, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO scheduled_tasks (id, name, organization_id, due_at)
            VALUES ({Guid.CreateVersion7(due)}, {OverdueDigest.TaskName}, {organizationId}, {due})
            ON CONFLICT (name, organization_id) DO UPDATE SET due_at = EXCLUDED.due_at
            """,
            cancellationToken);

    public async Task EnsureDailyGenerationAsync(Guid organizationId, DateTimeOffset firstDue, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO scheduled_tasks (id, name, organization_id, due_at)
            VALUES ({Guid.CreateVersion7(firstDue)}, {IRecurringWork.DailyGeneration}, {organizationId}, {firstDue})
            ON CONFLICT (name, organization_id) DO NOTHING
            """,
            cancellationToken);
}
