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

    /// <summary>User for v1 rows and staff actions; System, Contact or ApiKey from v2 (SPEC-V2 5.9).</summary>
    public ActorKind ActorKind { get; private set; }

    /// <summary>The contact or API key that acted; null for users and the system.</summary>
    public Guid? ActorRef { get; private set; }

    public string Action { get; private set; } = "";

    public string EntityType { get; private set; } = "";

    public Guid EntityId { get; private set; }

    public string Summary { get; private set; } = "";

    public IReadOnlyList<FieldChange> Changes { get; private set; } = [];

    public string? TraceId { get; private set; }

    /// <summary>The event's own actor when it names one, otherwise the scope's user (or the system when there is none).</summary>
    public static AuditEntry From(IAuditableEvent change, Guid? actorUserId, string actorName, DateTimeOffset occurredAt, string? traceId)
    {
        // No user in the scope (a scheduled task, the seed's system steps) means the system acted.
        var actor = change.Actor ?? new AuditActor(actorUserId is null ? ActorKind.System : ActorKind.User, actorUserId, null, actorName);
        return new()
        {
            OrganizationId = change.OrganizationId,
            OccurredAt = occurredAt,
            ActorUserId = actor.UserId,
            ActorName = actor.Name,
            ActorKind = actor.Kind,
            ActorRef = actor.Ref,
            Action = change.Action,
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            Summary = change.Summary,
            Changes = change.Changes,
            TraceId = traceId,
        };
    }
}
