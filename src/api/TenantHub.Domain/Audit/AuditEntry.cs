namespace TenantHub.Domain.Audit;

/// <summary>
/// One line of an organization's history (SPEC 4.1, 8.1). Written in the same transaction as the change it
/// describes, never edited afterwards (a database trigger enforces that from M07). The actor's name is a
/// snapshot, so the history still reads correctly after a rename or removal.
/// </summary>
public sealed class AuditEntry : ITenantOwned
{
    private AuditEntry()
    {
    }

    public long Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string ActorName { get; private set; } = "";

    public string Action { get; private set; } = "";

    public string EntityType { get; private set; } = "";

    public Guid EntityId { get; private set; }

    public string Summary { get; private set; } = "";

    public IReadOnlyList<FieldChange> Changes { get; private set; } = [];

    public string? TraceId { get; private set; }

    public static AuditEntry From(IAuditableEvent change, Guid? actorUserId, string actorName, DateTimeOffset occurredAt, string? traceId) =>
        new()
        {
            OrganizationId = change.OrganizationId,
            OccurredAt = occurredAt,
            ActorUserId = actorUserId,
            ActorName = actorName,
            Action = change.Action,
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            Summary = change.Summary,
            Changes = change.Changes,
            TraceId = traceId,
        };
}
