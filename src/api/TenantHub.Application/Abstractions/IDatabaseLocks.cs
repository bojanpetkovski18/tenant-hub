namespace TenantHub.Application.Abstractions;

/// <summary>
/// Row locks (<c>SELECT … FOR UPDATE</c>) that serialize commands which check a count and then insert, so two
/// concurrent requests cannot both pass the check (D013). Call inside a transaction; the lock ends with it.
/// </summary>
public interface IDatabaseLocks
{
    Task LockUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Serializes membership and invitation changes in one organization (the last-Owner rule, seats).</summary>
    Task LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Serializes changes to one job's checklist with its status changes (SPEC-V2 5.6): an item unticked while the job
    /// is completed would otherwise leave a completed job with an open required item. Row-level security limits it to
    /// the entered organization's jobs.
    /// </summary>
    Task LockJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Serializes the recording of deliveries to one webhook endpoint: its failure window and disabling.</summary>
    Task LockWebhookEndpointAsync(Guid endpointId, CancellationToken cancellationToken);
}

/// <summary>Recognizes provider-specific database errors without the application referencing the provider.</summary>
public interface IDatabaseErrors
{
    bool IsUniqueViolation(Exception exception, string indexName);
}
