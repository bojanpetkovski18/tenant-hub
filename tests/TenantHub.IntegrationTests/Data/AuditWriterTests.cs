using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Data;

/// <summary>D012: events become audit entries in the same save; the guard covers them like any other row.</summary>
public sealed class AuditWriterTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientDetails Details(string name) => ClientDetails.Validate(name, null, "office@acme.example", null, null, null);

    [Fact]
    public async Task Saving_a_change_writes_its_entry_with_the_actor_snapshot()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);

        var client = Client.Create(data.OrgA, Details("Acme Offices"), DateTimeOffset.UtcNow);
        db.Clients.Add(client);
        await db.SaveChangesAsync(Ct);

        var entry = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal(("client.created", client.Id, data.OwnerA, "owner@alpha.example"), (entry.Action, entry.EntityId, entry.ActorUserId, entry.ActorName));
        Assert.Equal(data.OrgA, entry.OrganizationId);
        Assert.Equal("office@acme.example", entry.Changes.Single(c => c.Field == "email").To);
        Assert.Empty(client.DomainEvents);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_the_change_nor_its_entry()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using (var db = data.ContextFor(data.OrgA, data.OwnerA))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Clients.Add(Client.Create(data.OrgA, Details("Acme Offices"), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
            // Something later in the same command fails: dispose without commit.
        }

        await using var check = PostgresFixture.CreateDbContext(data.ConnectionString);
        Assert.Equal(0, await check.Clients.AcrossTenants().CountAsync(Ct));
        Assert.Equal(0, await check.AuditEntries.AcrossTenants().CountAsync(Ct));
    }

    [Fact]
    public async Task A_change_the_database_refuses_writes_no_entry()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using (var db = data.ContextFor(data.OrgA, data.OwnerA))
        {
            db.Clients.Add(Client.Create(data.OrgA, Details("Acme Offices"), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = data.ContextFor(data.OrgA, data.OwnerA))
        {
            // Same active name (case-insensitive): the unique index refuses the insert.
            db.Clients.Add(Client.Create(data.OrgA, Details("ACME offices"), DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using var check = PostgresFixture.CreateDbContext(data.ConnectionString);
        Assert.Equal(1, await check.Clients.AcrossTenants().CountAsync(Ct));
        Assert.Equal(1, await check.AuditEntries.AcrossTenants().CountAsync(Ct));
    }

    [Fact]
    public async Task Moving_an_audit_entry_to_another_organization_is_refused_by_the_guard()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);
        db.Clients.Add(Client.Create(data.OrgA, Details("Acme Offices"), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        var entry = await db.AuditEntries.SingleAsync(Ct);

        // Audit entries are keyed by id alone, so EF Core allows the change and the guard is what stops it (D024).
        db.Entry(entry).Property(e => e.OrganizationId).CurrentValue = data.OrgB;

        var error = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("Moving", error.Message, StringComparison.Ordinal);
    }
}
