using TenantHub.Domain.Jobs;

namespace TenantHub.Domain.Agreements;

/// <summary>The visits a generation run creates, earliest first, and the agreements the plan limit cut short.</summary>
public sealed record GenerationPlan(IReadOnlyList<(Agreement Agreement, DateOnly Date)> Visits, IReadOnlySet<Guid> CutShort);

/// <summary>What an edit does to an agreement's following visits: those still on a visit date are updated, the rest withdrawn.</summary>
public sealed record EditPlan(IReadOnlyList<Job> Update, IReadOnlyList<Job> Withdraw);

/// <summary>
/// The rules of the rolling horizon (SPEC-V2 5.1, D041), as pure functions: the application loads the agreements and
/// their existing visit dates under the organization lock, asks here, and writes the answer.
/// </summary>
public static class VisitGeneration
{
    /// <summary>Visits exist for occurrences up to this many days after the organization's today.</summary>
    public const int HorizonDays = 60;

    public static DateOnly HorizonEnd(DateOnly today) => today.AddDays(HorizonDays);

    /// <summary>
    /// The visit dates an agreement still lacks, from today (never the past: resuming does not back-fill) to the
    /// horizon. Only active agreements generate; a skipped or cancelled visit still exists, so its date is not missing.
    /// </summary>
    public static IEnumerable<DateOnly> Missing(Agreement agreement, DateOnly today, IReadOnlySet<DateOnly> existing) =>
        agreement.Status == AgreementStatus.Active
            ? agreement.Schedule.Occurrences(today, HorizonEnd(today)).Where(date => !existing.Contains(date))
            : [];

    /// <summary>
    /// The visits to create for several agreements of one organization. <paramref name="room"/> is how many more open
    /// jobs the plan allows (null: unlimited); the nearest dates are created first, so a limit cuts the far end.
    /// </summary>
    public static GenerationPlan Plan(
        IEnumerable<(Agreement Agreement, IReadOnlySet<DateOnly> Existing)> agreements,
        DateOnly today,
        int? room)
    {
        var wanted = agreements
            .SelectMany(a => Missing(a.Agreement, today, a.Existing).Select(date => (a.Agreement, Date: date)))
            .OrderBy(v => v.Date)
            .ThenBy(v => v.Agreement.CreatedAt)
            .ThenBy(v => v.Agreement.Id)
            .ToList();
        var allowed = room is { } r ? Math.Max(r, 0) : wanted.Count;
        return new GenerationPlan(
            wanted.Take(allowed).ToList(),
            wanted.Skip(allowed).Select(v => v.Agreement.Id).ToHashSet());
    }

    /// <summary>
    /// After the agreement changed: its following visits (<see cref="Job.FollowsAgreement"/>) on a date it still has
    /// are updated; those on a date it no longer has are withdrawn. Visits changed by hand, past ones and ones in progress
    /// or closed are not passed in and are never touched.
    /// </summary>
    public static EditPlan AfterEdit(Agreement agreement, IEnumerable<Job> visits, DateOnly today)
    {
        var following = visits.Where(v => v.FollowsAgreement(agreement.Id, today)).ToList();
        var dates = agreement.Schedule.Occurrences(today, HorizonEnd(today)).ToHashSet();
        return new EditPlan(
            [.. following.Where(v => dates.Contains(v.OccurrenceOn!.Value))],
            [.. following.Where(v => !dates.Contains(v.OccurrenceOn!.Value))]);
    }

    /// <summary>Ending cancels the visits from today on that are New or Scheduled, by hand-changed or not (SPEC-V2 5.1).</summary>
    public static IEnumerable<Job> CancelledByEnd(Agreement agreement, IEnumerable<Job> visits, DateOnly today) =>
        visits.Where(v => v.AgreementId == agreement.Id && v.OccurrenceOn >= today && v.Status is JobStatus.New or JobStatus.Scheduled);
}
