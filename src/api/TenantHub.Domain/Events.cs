namespace TenantHub.Domain;

/// <summary>Something that happened in the domain, raised by an aggregate and dispatched when it is saved.</summary>
public interface IDomainEvent;

/// <summary>A field that changed, with readable before and after values (null when absent).</summary>
public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>
/// A domain event that belongs in the audit log (SPEC 4.1, D012). The aggregate that raises it knows what changed
/// and how to say it, so entries read as "Updated client “Acme”: email" rather than a generic row diff.
/// </summary>
public interface IAuditableEvent : IDomainEvent
{
    Guid OrganizationId { get; }

    /// <summary>Stable, dotted, lowercase: <c>client.updated</c>.</summary>
    string Action { get; }

    string EntityType { get; }

    Guid EntityId { get; }

    string Summary { get; }

    IReadOnlyList<FieldChange> Changes { get; }
}

/// <summary>Base for aggregates that raise domain events; the persistence layer collects them on save.</summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _events = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    public void ClearDomainEvents() => _events.Clear();

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);
}

/// <summary>Several fields are invalid at once; the API returns them together as a validation problem.</summary>
public sealed class DomainValidationException(IReadOnlyDictionary<string, string[]> errors)
    : DomainException("validation-failed", "Some fields need attention.", DomainErrorKind.Validation)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
