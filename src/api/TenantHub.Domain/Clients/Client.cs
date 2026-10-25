namespace TenantHub.Domain.Clients;

public enum ClientStatus
{
    Active,
    Archived,
}

/// <summary>
/// A customer of the organization (SPEC 5.6). Archiving hides a client from new work but keeps its history;
/// deleting is only for clients without jobs (checked by the handler from M06). Every change raises an auditable
/// event with the fields that changed.
/// </summary>
public sealed class Client : AggregateRoot, ITenantOwned
{
    private Client()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = "";

    public string? ContactName { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Address { get; private set; }

    public string? Notes { get; private set; }

    public ClientStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency token; the database bumps it on every update.</summary>
    public uint Version { get; private set; }

    public ClientDetails Details => new(Name, ContactName, Email, Phone, Address, Notes);

    public static Client Create(Guid organizationId, ClientDetails details, DateTimeOffset now)
    {
        var client = new Client
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Status = ClientStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        client.Apply(details);
        // Every field that was set, with no previous value (the name's "" placeholder is not a real value).
        var empty = new ClientDetails("", null, null, null, null, null);
        var changes = empty.ChangesTo(details).Select(c => c with { From = null }).ToList();
        client.Raise(new ClientEvent(client, "client.created", $"Created client “{client.Name}”", changes));
        return client;
    }

    /// <summary>Applies the new details; returns false (and records nothing) when nothing changed.</summary>
    public bool Update(ClientDetails details, DateTimeOffset now)
    {
        var changes = Details.ChangesTo(details);
        if (changes.Count == 0)
        {
            return false;
        }

        var previousName = Name;
        Apply(details);
        UpdatedAt = now;
        var fields = string.Join(", ", changes.Select(c => ClientDetails.Labels[c.Field]));
        var subject = previousName == Name ? $"“{Name}”" : $"“{previousName}” (now “{Name}”)";
        Raise(new ClientEvent(this, "client.updated", $"Updated client {subject}: {fields}", changes));
        return true;
    }

    /// <summary>Idempotent: archiving an archived client changes and records nothing.</summary>
    public void Archive(DateTimeOffset now) => ChangeStatus(ClientStatus.Archived, now, "client.archived", "Archived");

    /// <summary>Idempotent, like <see cref="Archive"/>.</summary>
    public void Restore(DateTimeOffset now) => ChangeStatus(ClientStatus.Active, now, "client.restored", "Restored");

    /// <summary>Records the deletion; the caller removes the row in the same save.</summary>
    public void MarkDeleted() => Raise(new ClientEvent(this, "client.deleted", $"Deleted client “{Name}”", []));

    private void ChangeStatus(ClientStatus target, DateTimeOffset now, string action, string verb)
    {
        if (Status == target)
        {
            return;
        }

        var change = new FieldChange("status", Status.ToString(), target.ToString());
        Status = target;
        UpdatedAt = now;
        Raise(new ClientEvent(this, action, $"{verb} client “{Name}”", [change]));
    }

    private void Apply(ClientDetails details)
    {
        Name = details.Name;
        ContactName = details.ContactName;
        Email = details.Email;
        Phone = details.Phone;
        Address = details.Address;
        Notes = details.Notes;
    }
}

public sealed record ClientEvent(Client Client, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Client.OrganizationId;

    public string EntityType => "client";

    public Guid EntityId => Client.Id;
}
