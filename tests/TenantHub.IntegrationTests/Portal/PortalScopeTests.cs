using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// A portal scope below the API (SPEC-V2 3.2, 5.9): the save guard keeps writes to the session's client, and what the
/// contact changes is audited as the contact.
/// </summary>
public sealed class PortalScopeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_guard_refuses_a_write_for_another_client_in_a_portal_scope()
    {
        var tenants = await SharedApiTenants.GetAsync<PortalScopeTests>(postgres);
        var (client, contact) = await ClientWithContactAsync(tenants);
        await using var db = PortalContext(tenants, client, contact);
        var other = Client.Create(tenants.OrgA, ClientDetails.Validate($"Other {Guid.NewGuid():N}", null, null, null, null, null), DateTimeOffset.UtcNow);

        db.ClientContacts.Add(ClientContact.Add(other, "Someone Else", "else@other.example", 0, false, await tenants.UserIdOfAsync(Domain.Memberships.Role.Owner), DateTimeOffset.UtcNow));

        var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("another client", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_contact_changes_is_audited_as_the_contact()
    {
        var tenants = await SharedApiTenants.GetAsync<PortalScopeTests>(postgres);
        var (client, contact) = await ClientWithContactAsync(tenants);
        await using (var db = PortalContext(tenants, client, contact))
        {
            var self = await db.ClientContacts.SingleAsync(c => c.Id == contact, Ct);
            self.Remove("Portal Client", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        await using var owner = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var entry = await owner.AuditEntries.AcrossTenants().SingleAsync(a => a.EntityId == contact && a.Action == "contact.removed", Ct);
        Assert.Equal((ActorKind.Contact, (Guid?)contact, (Guid?)null, "Matrix Contact"), (entry.ActorKind, entry.ActorRef, entry.ActorUserId, entry.ActorName));
    }

    private static async Task<(Guid Client, Guid Contact)> ClientWithContactAsync(ApiTenants tenants)
    {
        var client = await tenants.CreateClientAsync(tenants.OrgA);
        return (client, await tenants.CreateContactAsync(tenants.OrgA, client));
    }

    private static AppDbContext PortalContext(ApiTenants tenants, Guid client, Guid contact)
    {
        var tenant = new TenantContext();
        tenant.EnterAsContact(tenants.OrgA, client, contact);
        return PostgresFixture.CreateDbContext(tenants.ConnectionString, tenant);
    }
}
