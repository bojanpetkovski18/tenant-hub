namespace TenantHub.Domain.Clients;

/// <summary>
/// A person at a client who may use the portal (SPEC-V2 2.1, 5.3). Not an account: no password, no membership; they
/// sign in with an emailed one-time link. A contact belongs to exactly one client; the same email at two clients is
/// two contacts. Removing is final: the row stays (for the audit and their requests) and a new contact is added
/// to give access again, so an old session can never come back to life.
/// </summary>
public sealed class ClientContact : AggregateRoot, IClientOwned
{
    public const int NameMaxLength = 100;
    public const int MaxActivePerClient = 5;

    private ClientContact()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public string Name { get; private set; } = "";

    public string Email { get; private set; } = "";

    /// <summary>Lowercased: unique among a client's active contacts, and what sign-in looks up.</summary>
    public string NormalizedEmail { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public DateTimeOffset? LastSignInAt { get; private set; }

    public bool IsActive => RemovedAt is null;

    /// <summary>
    /// Adds a contact to an active client. <paramref name="activeContacts"/> and <paramref name="emailInUse"/> are
    /// counted by the caller under the organization lock, so two requests cannot both take the fifth place.
    /// </summary>
    public static ClientContact Add(Client client, string? name, string? email, int activeContacts, bool emailInUse, Guid userId, DateTimeOffset now)
    {
        if (client.Status != ClientStatus.Active)
        {
            throw new DomainException("client-archived", $"“{client.Name}” is archived. Restore the client to add contacts.");
        }

        var normalizedName = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmedEmail = (email ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (normalizedName.Length is 0 or > NameMaxLength)
        {
            errors["name"] = [$"Enter a name of 1–{NameMaxLength} characters."];
        }

        if (!EmailAddress.IsValid(trimmedEmail))
        {
            errors["email"] = ["Enter a valid email address."];
        }
        else if (emailInUse)
        {
            errors["email"] = ["This client already has a contact with this email address."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        if (activeContacts >= MaxActivePerClient)
        {
            throw new DomainException(
                "contact-limit-reached",
                $"A client can have at most {MaxActivePerClient} contacts. Remove one before adding another.");
        }

        var contact = new ClientContact
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = client.OrganizationId,
            ClientId = client.Id,
            Name = normalizedName,
            Email = trimmedEmail,
            NormalizedEmail = EmailAddress.Normalize(trimmedEmail),
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        contact.Raise(new ContactEvent(
            contact,
            "contact.added",
            $"Added contact {contact.Name} to client “{client.Name}”",
            [new FieldChange("name", null, contact.Name), new FieldChange("email", null, contact.Email)]));
        return contact;
    }

    /// <summary>Ends their access: the portal checks this on every request (SPEC-V2 3.2). Idempotent.</summary>
    public void Remove(string clientName, DateTimeOffset now)
    {
        if (RemovedAt is not null)
        {
            return;
        }

        RemovedAt = now;
        Raise(new ContactEvent(this, "contact.removed", $"Removed contact {Name} from client “{clientName}”", []));
    }

    /// <summary>Recorded on the contact, not audited: a sign-in changes no business data (as in v1).</summary>
    public void SignedIn(DateTimeOffset now) => LastSignInAt = now;
}

public sealed record ContactEvent(ClientContact Contact, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Contact.OrganizationId;

    public string EntityType => "contact";

    public Guid EntityId => Contact.Id;
}
