namespace TenantHub.Domain.Jobs;

/// <summary>Where a job's quote stands with the client (SPEC-V2 5.4). Jobs never sent stay <see cref="NotRequested"/>.</summary>
public enum JobApprovalStatus
{
    NotRequested,
    Pending,
    Approved,
    Declined,
}

/// <summary>The quote rules (SPEC-V2 5.4), kept apart from the job so they read as one table.</summary>
public static class JobQuotes
{
    public const int CommentMaxLength = 1000;

    /// <summary>A job whose quote waits for a decision, or was declined, cannot start (409 <c>quote-not-approved</c>).</summary>
    public static bool BlocksStart(JobApprovalStatus status) => status is JobApprovalStatus.Pending or JobApprovalStatus.Declined;

    public static string? WhyNotStart(JobApprovalStatus status) => status switch
    {
        JobApprovalStatus.Pending => "The client has not approved the quote yet.",
        JobApprovalStatus.Declined => "The client declined the quote. Send a new one first.",
        _ => null,
    };

    /// <summary>Why the quote cannot be sent now, or null when it can (the client's contacts are checked by the caller).</summary>
    public static string? WhyNotSend(JobStatus status, decimal? quotedAmount, JobApprovalStatus approval) =>
        status is not (JobStatus.New or JobStatus.Scheduled) ? "Only New or Scheduled jobs can be sent for approval."
        : quotedAmount is null ? "Add a quoted amount first."
        : approval == JobApprovalStatus.Pending ? "The quote is already waiting for the client."
        : approval == JobApprovalStatus.Approved ? "The client already approved this quote."
        : null;

    public static string Amount(decimal amount) => amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
