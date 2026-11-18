namespace TenantHub.Infrastructure.Background;

/// <summary>
/// A side effect waiting to be delivered (SPEC-V2 8, D039). Infrastructure, not tenant-owned: only the dispatcher
/// reads these rows, across organizations by design, and acts for one message at a time.
/// <see cref="AvailableAt"/> is both the retry time and the claim's lease: a claimed message is pushed into the
/// future, so a dispatcher that dies holding it lets it come back once the lease runs out.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>Null for account emails, which belong to a person, not an organization.</summary>
    public Guid? OrganizationId { get; init; }

    public string Type { get; init; } = "";

    /// <summary>JSON. When the type carries a secret, a JSON string holding the Data Protection ciphertext.</summary>
    public string Payload { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>Claims so far. Also the claim's fencing token: only the claim that set it may complete the message.</summary>
    public int Attempts { get; set; }

    /// <summary>The class of the last failure (an exception type or a delivery code); never addresses or secrets.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? DeadAt { get; set; }
}

/// <summary>
/// Recurring work held in the database (SPEC-V2 4.9): when it is next due and, while it runs, until when the
/// instance running it holds it. Several instances share one table, and a restart neither skips nor repeats a run.
/// </summary>
public sealed class ScheduledTask
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Null for work of the whole application; per organization for work like agreement generation (M16).</summary>
    public Guid? OrganizationId { get; init; }

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastError { get; set; }
}
