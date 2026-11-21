using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Agreements;

/// <summary>Recurring work held by the scheduler (D039); the Infrastructure side writes <c>scheduled_tasks</c>.</summary>
public interface IRecurringWork
{
    /// <summary>The scheduler task that generates visits for an organization every day.</summary>
    const string DailyGeneration = "agreements.generate";

    /// <summary>
    /// Makes sure the organization's daily generation task exists, first due at <paramref name="firstDue"/>. Written in
    /// the caller's transaction, so it exists exactly when the agreement that needs it does.
    /// </summary>
    Task EnsureDailyGenerationAsync(Guid organizationId, DateTimeOffset firstDue, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure the organization's daily digest task exists (SPEC-V2 5.7), first due at <paramref name="due"/>, or moves
    /// it there (the organization's time zone changed). In the caller's transaction.
    /// </summary>
    Task ScheduleDigestAsync(Guid organizationId, DateTimeOffset due, CancellationToken cancellationToken);
}

/// <summary>
/// Writes generated visits (SPEC-V2 5.1, D041). The caller holds the organization lock in a transaction, so the job
/// numbers, the open-jobs count and the existing dates cannot change under it; the unique index on
/// <c>(organization_id, agreement_id, occurrence_on)</c> is the backstop. The rules themselves are
/// <see cref="VisitGeneration"/>'s.
/// </summary>
public sealed class VisitGenerator(IAppDbContext db, PlanLimitsCheck limits, JobViews views, TimeProvider time)
{
    /// <summary>When the daily run for an organization comes next: just after its next local midnight.</summary>
    public static DateTimeOffset NextDailyRun(Organization organization, DateTimeOffset now) =>
        Organization.StartOfDayIn(organization.TimeZone, organization.TodayAt(now).AddDays(1)) + TimeSpan.FromMinutes(5);

    /// <summary>Generates the missing visits of <paramref name="agreements"/>; returns how many it created.</summary>
    public async Task<int> GenerateAsync(Organization organization, IReadOnlyList<Agreement> agreements, CancellationToken cancellationToken)
    {
        if (agreements.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var today = organization.TodayAt(now);
        var ids = agreements.Select(a => a.Id).ToList();
        var existing = (await db.Jobs
                .Where(j => j.AgreementId != null && ids.Contains(j.AgreementId.Value) && j.OccurrenceOn >= today)
                .Select(j => new { AgreementId = j.AgreementId!.Value, Date = j.OccurrenceOn!.Value })
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.AgreementId, v => v.Date);

        // Room under the plan's open-jobs limit (SPEC-V2 5.1): the nearest visits first, the rest wait for room.
        int? room = null;
        if (PlanLimits.For(organization.Plan).OpenJobs is { } allowed)
        {
            room = allowed - (await limits.UsageAsync(cancellationToken)).OpenJobs;
        }

        var plan = VisitGeneration.Plan(
            agreements.Select(a => (a, (IReadOnlySet<DateOnly>)existing[a.Id].ToHashSet())),
            today,
            room);

        var names = await views.NamesAsync(
            agreements.Select(a => a.ClientId),
            agreements.Select(a => a.AssigneeUserId),
            cancellationToken);
        foreach (var (agreement, date) in plan.Visits)
        {
            db.Jobs.Add(Job.Generate(agreement, organization.TakeJobNumber(), date, names, now));
        }

        foreach (var agreement in agreements.Where(a => a.Status == AgreementStatus.Active))
        {
            // Covered up to the day before the first date the limit left out; the nearest dates go first, so every date
            // before it exists.
            var cut = plan.LeftOut.TryGetValue(agreement.Id, out var firstLeftOut);
            agreement.RecordGeneration(cut ? firstLeftOut.AddDays(-1) : VisitGeneration.HorizonEnd(today), cut, now);
        }

        return plan.Visits.Count;
    }
}
