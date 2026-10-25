using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Identity;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// Two organizations written straight to the database, each through a context that has entered it, so the data
/// passes the same guard as production writes. A has an Owner and a Member; B has an Owner and a Viewer.
/// </summary>
public sealed record TwoTenants(string ConnectionString, Guid OrgA, Guid OwnerA, Guid MemberA, Guid OrgB, Guid OwnerB, Guid ViewerB)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TwoTenants> CreateAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            foreach (var (id, email) in Users)
            {
                db.Users.Add(new AppUser { Id = id, UserName = email, Email = email, DisplayName = email, CreatedAt = now });
            }
            await db.SaveChangesAsync(Ct);
        }

        var orgA = await CreateOrganizationAsync(connectionString, "Alpha Cleaning", "alpha-cleaning", OwnerAId, (MemberAId, Role.Member));
        var orgB = await CreateOrganizationAsync(connectionString, "Bravo Gardens", "bravo-gardens", OwnerBId, (ViewerBId, Role.Viewer));
        return new TwoTenants(connectionString, orgA, OwnerAId, MemberAId, orgB, OwnerBId, ViewerBId);
    }

    /// <summary>A context working inside <paramref name="organizationId"/>, like a tenant-scoped request.</summary>
    public AppDbContext ContextFor(Guid organizationId, Guid userId, Role role = Role.Owner)
    {
        var tenant = new TenantContext();
        tenant.Enter(organizationId, userId, role);
        return PostgresFixture.CreateDbContext(ConnectionString, tenant);
    }

    private static readonly Guid OwnerAId = Guid.Parse("0199f0a0-0000-7000-8000-00000000a001");
    private static readonly Guid MemberAId = Guid.Parse("0199f0a0-0000-7000-8000-00000000a002");
    private static readonly Guid OwnerBId = Guid.Parse("0199f0a0-0000-7000-8000-00000000b001");
    private static readonly Guid ViewerBId = Guid.Parse("0199f0a0-0000-7000-8000-00000000b002");

    private static readonly (Guid Id, string Email)[] Users =
    [
        (OwnerAId, "owner@alpha.example"),
        (MemberAId, "member@alpha.example"),
        (OwnerBId, "owner@bravo.example"),
        (ViewerBId, "viewer@bravo.example"),
    ];

    private static async Task<Guid> CreateOrganizationAsync(
        string connectionString, string name, string slug, Guid ownerId, (Guid UserId, Role Role) other)
    {
        var now = DateTimeOffset.UtcNow;
        var organization = Organization.Create(name, slug, "UTC", ownerId, now);
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(Ct);
        }

        var tenant = new TenantContext();
        tenant.Enter(organization.Id, ownerId, Role.Owner);
        await using (var db = PostgresFixture.CreateDbContext(connectionString, tenant))
        {
            db.Memberships.Add(organization.AddOwner(ownerId, now));
            db.Memberships.Add(organization.AddMember(other.UserId, other.Role, now, ownerId));
            await db.SaveChangesAsync(Ct);
        }

        return organization.Id;
    }
}
