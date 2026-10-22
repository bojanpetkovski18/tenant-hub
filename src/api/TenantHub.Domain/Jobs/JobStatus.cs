namespace TenantHub.Domain.Jobs;

public enum JobStatus
{
    New,
    Scheduled,
    InProgress,
    Completed,
    Cancelled,
}

public enum JobPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public static class JobLabels
{
    public static string Of(JobStatus status) => status switch
    {
        JobStatus.New => "New",
        JobStatus.Scheduled => "Scheduled",
        JobStatus.InProgress => "In progress",
        JobStatus.Completed => "Completed",
        JobStatus.Cancelled => "Cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string Of(JobPriority priority) => priority.ToString();

    /// <summary>Completed and Cancelled jobs are closed: not overdue, not counted as open, not unassigned on removal.</summary>
    public static bool IsOpen(this JobStatus status) => status is not (JobStatus.Completed or JobStatus.Cancelled);
}
