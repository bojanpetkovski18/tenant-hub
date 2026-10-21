using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>SPEC 3.3, points 2 and 3, against a real database: the read filter and the write guard.</summary>
public sealed class TenantGuardTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Queries_see_only_the_current_organization()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);

        // No Where clause: the filter alone scopes the query.
        var memberships = await db.Memberships.ToListAsync(Ct);

        Assert.Equal(2, memberships.Count);
        Assert.All(memberships, m => Assert.Equal(data.OrgA, m.OrganizationId));
        Assert.Null(await db.Memberships.FirstOrDefaultAsync(m => m.UserId == data.OwnerB, Ct));
    }

    [Fact]
    public async Task Without_an_organization_queries_return_nothing_and_only_the_bypass_sees_everything()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = PostgresFixture.CreateDbContext(data.ConnectionString);

        Assert.Empty(await db.Memberships.ToListAsync(Ct));
        Assert.Equal(4, await db.Memberships.AcrossTenants().CountAsync(Ct));
    }

    [Fact]
    public async Task Inserting_a_row_for_another_organization_is_refused()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);
        var orgB = await db.Organizations.SingleAsync(o => o.Id == data.OrgB, Ct);

        db.Memberships.Add(orgB.AddMember(data.MemberA, Role.Admin, DateTimeOffset.UtcNow, data.OwnerA));

        var error = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("another organization", error.Message, StringComparison.Ordinal);
        await AssertMembershipCountAsync(data, data.OrgB, 2);
    }

    [Fact]
    public async Task Updating_or_deleting_another_organizations_row_is_refused()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);
        // Loaded through the bypass, as a bug might: the guard still stops the write.
        var foreign = await db.Memberships.AcrossTenants().SingleAsync(m => m.UserId == data.ViewerB, Ct);

        db.Entry(foreign).Property(m => m.RemovedAt).CurrentValue = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));

        db.Entry(foreign).State = EntityState.Deleted;
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));

        await AssertMembershipCountAsync(data, data.OrgB, 2);
    }

    [Fact]
    public async Task Moving_a_row_to_another_organization_is_refused()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);
        var own = await db.Memberships.SingleAsync(m => m.UserId == data.MemberA, Ct);

        // For memberships EF Core stops it first: organization_id is part of the primary key, which EF never
        // modifies. The guard's own "moving" check is exercised by the first entity keyed by id alone (clients, M04).
        var error = Assert.Throws<InvalidOperationException>(
            () => db.Entry(own).Property(m => m.OrganizationId).CurrentValue = data.OrgB);
        Assert.Contains("part of a key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_tenant_owned_rows_outside_any_organization_is_refused()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = PostgresFixture.CreateDbContext(data.ConnectionString);
        var orgA = await db.Organizations.SingleAsync(o => o.Id == data.OrgA, Ct);

        db.Memberships.Add(orgA.AddMember(data.OwnerB, Role.Admin, DateTimeOffset.UtcNow, data.OwnerA));

        var error = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("outside any organization", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_row_without_an_organization_gets_the_current_one()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);
        var orgA = await db.Organizations.SingleAsync(o => o.Id == data.OrgA, Ct);
        var membership = orgA.AddMember(data.OwnerB, Role.Viewer, DateTimeOffset.UtcNow, data.OwnerA);
        db.Memberships.Add(membership);
        db.Entry(membership).Property(m => m.OrganizationId).CurrentValue = Guid.Empty;

        await db.SaveChangesAsync(Ct);

        Assert.Equal(data.OrgA, membership.OrganizationId);
        await AssertMembershipCountAsync(data, data.OrgA, 3);
    }

    [Fact]
    public async Task Organizations_are_not_tenant_owned_and_stay_visible_by_id()
    {
        var data = await TwoTenants.CreateAsync(postgres);
        await using var db = data.ContextFor(data.OrgA, data.OwnerA);

        // The organization is the tenant itself; access to it is decided by the membership check (SPEC 8.1).
        Assert.Equal(2, await db.Organizations.CountAsync(Ct));
        Assert.Equal(typeof(Organization), db.Model.FindEntityType(typeof(Organization))!.ClrType);
    }

    private static async Task AssertMembershipCountAsync(TwoTenants data, Guid organizationId, int expected)
    {
        await using var db = PostgresFixture.CreateDbContext(data.ConnectionString);
        Assert.Equal(expected, await db.Memberships.AcrossTenants().CountAsync(m => m.OrganizationId == organizationId, Ct));
    }
}
