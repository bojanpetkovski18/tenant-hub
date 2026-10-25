namespace TenantHub.Domain.Jobs;

/// <summary>
/// One row of the transition table (SPEC 5.4). <see cref="AssigneeMayPerform"/> is the "<c>jobs.work</c> if assigned
/// to me" column; every other row needs <c>jobs.manage</c>.
/// </summary>
public sealed record JobTransition(
    JobStatus From,
    JobStatus To,
    bool NeedsAssignee = false,
    bool NeedsScheduledOn = false,
    bool NeedsReason = false,
    bool AssigneeMayPerform = false);

/// <summary>The workflow as data, so tests can walk every cell and the API can list what is allowed.</summary>
public static class JobWorkflow
{
    public const int ReasonMaxLength = 500;

    public static IReadOnlyList<JobTransition> Transitions { get; } =
    [
        new(JobStatus.New, JobStatus.Scheduled, NeedsAssignee: true, NeedsScheduledOn: true),
        new(JobStatus.New, JobStatus.InProgress, NeedsAssignee: true, AssigneeMayPerform: true),
        new(JobStatus.Scheduled, JobStatus.InProgress, NeedsAssignee: true, AssigneeMayPerform: true),
        new(JobStatus.InProgress, JobStatus.Completed, AssigneeMayPerform: true),
        new(JobStatus.New, JobStatus.Cancelled, NeedsReason: true),
        new(JobStatus.Scheduled, JobStatus.Cancelled, NeedsReason: true),
        new(JobStatus.InProgress, JobStatus.Cancelled, NeedsReason: true),
        new(JobStatus.Completed, JobStatus.InProgress, NeedsReason: true),
    ];

    public static JobTransition? Find(JobStatus from, JobStatus to) =>
        Transitions.FirstOrDefault(t => t.From == from && t.To == to);

    public static IEnumerable<JobTransition> From(JobStatus from) => Transitions.Where(t => t.From == from);
}
