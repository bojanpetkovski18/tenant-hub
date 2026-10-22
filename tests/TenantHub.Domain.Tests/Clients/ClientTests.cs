using TenantHub.Domain.Audit;
using TenantHub.Domain.Clients;

namespace TenantHub.Domain.Tests.Clients;

public sealed class ClientTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static ClientDetails Details(string name = "Acme Offices", string? email = "facilities@acme.example") =>
        ClientDetails.Validate(name, "Dana Reyes", email, "+00 000 0000", "1 Example Street", "Key at reception");

    [Fact]
    public void Details_are_normalized()
    {
        var details = ClientDetails.Validate("  Acme   Offices ", "  ", "", "  +00 000  ", null, "  Line one\nLine two  ");

        Assert.Equal(new ClientDetails("Acme Offices", null, null, "+00 000", null, "Line one\nLine two"), details);
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once()
    {
        var error = Assert.Throws<DomainValidationException>(() => ClientDetails.Validate(
            "A", new string('c', 121), "not-an-email", new string('9', 41), new string('a', 301), new string('n', 2001)));

        Assert.Equal(["address", "contactName", "email", "name", "notes", "phone"], error.Errors.Keys.Order());
        Assert.Equal(DomainErrorKind.Validation, error.Kind);
    }

    [Theory]
    [InlineData("Ab", true)]
    [InlineData("A", false)]
    [InlineData("", false)]
    public void Name_needs_at_least_two_characters(string name, bool valid)
    {
        var act = () => ClientDetails.Validate(name, null, null, null, null, null);

        if (valid)
        {
            Assert.Equal(name, act().Name);
        }
        else
        {
            Assert.Contains("name", Assert.Throws<DomainValidationException>(act).Errors.Keys);
        }
    }

    [Theory]
    [InlineData("a@b.example", true)]
    [InlineData("first.last+tag@sub.example", true)]
    [InlineData("no-at.example", false)]
    [InlineData("two@@at.example", false)]
    [InlineData("space in@x.example", false)]
    [InlineData("nodot@example", false)]
    public void Email_must_look_like_an_address(string email, bool valid)
    {
        var act = () => ClientDetails.Validate("Acme", null, email, null, null, null);

        if (valid)
        {
            Assert.Equal(email, act().Email);
        }
        else
        {
            Assert.Contains("email", Assert.Throws<DomainValidationException>(act).Errors.Keys);
        }
    }

    [Fact]
    public void Create_is_active_and_records_the_fields_that_were_set()
    {
        var client = Client.Create(Org, Details(), Now);

        Assert.Equal((Org, ClientStatus.Active, Now, Now), (client.OrganizationId, client.Status, client.CreatedAt, client.UpdatedAt));
        var created = Assert.IsType<ClientEvent>(Assert.Single(client.DomainEvents));
        Assert.Equal(("client.created", "Created client “Acme Offices”"), (created.Action, created.Summary));
        Assert.Equal(["name", "contactName", "email", "phone", "address", "notes"], created.Changes.Select(c => c.Field));
        Assert.All(created.Changes, c => Assert.Null(c.From));
    }

    [Fact]
    public void Update_records_only_the_fields_that_changed()
    {
        var client = Client.Create(Org, Details(), Now);
        client.ClearDomainEvents();

        var changed = client.Update(Details(email: "new@acme.example"), Now.AddHours(1));

        Assert.True(changed);
        var updated = Assert.IsType<ClientEvent>(Assert.Single(client.DomainEvents));
        Assert.Equal("Updated client “Acme Offices”: email", updated.Summary);
        Assert.Equal([new FieldChange("email", "facilities@acme.example", "new@acme.example")], updated.Changes);
        Assert.Equal(Now.AddHours(1), client.UpdatedAt);
    }

    [Fact]
    public void A_rename_names_both_the_old_and_the_new_name()
    {
        var client = Client.Create(Org, Details(), Now);
        client.ClearDomainEvents();

        client.Update(Details(name: "Acme Group"), Now);

        Assert.Equal("Updated client “Acme Offices” (now “Acme Group”): name", ((ClientEvent)client.DomainEvents[0]).Summary);
    }

    [Fact]
    public void An_update_without_changes_records_nothing()
    {
        var client = Client.Create(Org, Details(), Now);
        client.ClearDomainEvents();

        Assert.False(client.Update(Details(), Now.AddHours(1)));
        Assert.Empty(client.DomainEvents);
        Assert.Equal(Now, client.UpdatedAt);
    }

    [Fact]
    public void Archive_and_restore_are_idempotent_and_recorded_once()
    {
        var client = Client.Create(Org, Details(), Now);
        client.ClearDomainEvents();

        client.Archive(Now);
        client.Archive(Now);
        client.Restore(Now);
        client.Restore(Now);

        Assert.Equal(["client.archived", "client.restored"], client.DomainEvents.Cast<ClientEvent>().Select(e => e.Action));
        Assert.Equal(new FieldChange("status", "Active", "Archived"), ((ClientEvent)client.DomainEvents[0]).Changes.Single());
        Assert.Equal(ClientStatus.Active, client.Status);
    }

    [Fact]
    public void An_audit_entry_copies_the_event_and_the_actor_snapshot()
    {
        var client = Client.Create(Org, Details(), Now);
        var actor = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

        var entry = AuditEntry.From((IAuditableEvent)client.DomainEvents[0], actor, "Olivia Bennett", Now, "trace-1");

        Assert.Equal(
            (Org, actor, "Olivia Bennett", "client.created", "client", client.Id, "Created client “Acme Offices”", "trace-1"),
            (entry.OrganizationId, entry.ActorUserId, entry.ActorName, entry.Action, entry.EntityType, entry.EntityId, entry.Summary, entry.TraceId));
        Assert.Equal(6, entry.Changes.Count);
    }
}
