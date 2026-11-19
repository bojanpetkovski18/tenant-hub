using TenantHub.Domain.Clients;

namespace TenantHub.Domain.Tests.Clients;

/// <summary>SPEC-V2 5.3: who can be a contact, how many, and what adding and removing record.</summary>
public sealed class ClientContactTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid User = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static Client Acme() =>
        Client.Create(Org, ClientDetails.Validate("Acme Offices", null, null, null, null, null), Now);

    [Fact]
    public void Adding_normalizes_and_records_who_and_what()
    {
        var client = Acme();

        var contact = ClientContact.Add(client, "  Dana   Reyes ", " Dana.Reyes@Acme.example ", activeContacts: 0, emailInUse: false, User, Now);

        Assert.Equal(("Dana Reyes", "Dana.Reyes@Acme.example", "dana.reyes@acme.example"), (contact.Name, contact.Email, contact.NormalizedEmail));
        Assert.Equal((Org, client.Id, User, true), (contact.OrganizationId, contact.ClientId, contact.CreatedByUserId, contact.IsActive));
        var added = (ContactEvent)Assert.Single(contact.DomainEvents);
        Assert.Equal(("contact.added", "Added contact Dana Reyes to client “Acme Offices”", "contact"), (added.Action, added.Summary, added.EntityType));
    }

    [Fact]
    public void Every_invalid_field_is_reported_and_a_known_email_is_refused()
    {
        var invalid = Assert.Throws<DomainValidationException>(() => ClientContact.Add(Acme(), " ", "not-an-email", 0, false, User, Now));
        var known = Assert.Throws<DomainValidationException>(() => ClientContact.Add(Acme(), "Dana", "dana@acme.example", 1, emailInUse: true, User, Now));

        Assert.Equal(["email", "name"], invalid.Errors.Keys.Order());
        Assert.Equal(["email"], known.Errors.Keys);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void A_client_has_at_most_five_active_contacts(int active, bool allowed)
    {
        var error = Record.Exception(() => ClientContact.Add(Acme(), "Dana", "dana@acme.example", active, false, User, Now));

        Assert.Equal(allowed, error is null);
        if (!allowed)
        {
            Assert.Equal("contact-limit-reached", Assert.IsType<DomainException>(error).Code);
        }
    }

    [Fact]
    public void An_archived_client_gets_no_contacts()
    {
        var client = Acme();
        client.Archive(Now);

        var refused = Assert.Throws<DomainException>(() => ClientContact.Add(client, "Dana", "dana@acme.example", 0, false, User, Now));

        Assert.Equal("client-archived", refused.Code);
    }

    [Fact]
    public void Removing_is_recorded_once()
    {
        var contact = ClientContact.Add(Acme(), "Dana Reyes", "dana@acme.example", 0, false, User, Now);
        contact.ClearDomainEvents();

        contact.Remove("Acme Offices", Now.AddHours(1));
        contact.Remove("Acme Offices", Now.AddHours(2));

        Assert.Equal((false, Now.AddHours(1)), (contact.IsActive, contact.RemovedAt!.Value));
        Assert.Equal("Removed contact Dana Reyes from client “Acme Offices”", ((ContactEvent)Assert.Single(contact.DomainEvents)).Summary);
    }
}
