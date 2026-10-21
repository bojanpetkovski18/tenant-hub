using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Metadata;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC 3.4, coverage: every entity is either tenant-owned, with the tenant filter and an index that starts with
/// <c>organization_id</c>, or on this reviewed list. A new entity is covered automatically, or fails here.
/// Widening <see cref="NotTenantOwned"/> needs the owner's approval (CLAUDE.md).
/// </summary>
public sealed class ModelCoverageTests
{
    private static readonly Dictionary<Type, string> NotTenantOwned = new()
    {
        [typeof(AppUser)] = "accounts span organizations (D010)",
        [typeof(IdentityUserClaim<Guid>)] = "Identity data of an account",
        [typeof(IdentityUserLogin<Guid>)] = "Identity data of an account",
        [typeof(IdentityUserToken<Guid>)] = "Identity data of an account",
        [typeof(Organization)] = "the tenant itself; access is checked through the membership (SPEC 3.2)",
    };

    private static IModel Model()
    {
        using var db = PostgresFixture.CreateDbContext(OfflineApiFactory.UnreachableDatabase);
        return db.Model;
    }

    public static TheoryData<string> TenantOwnedEntities() =>
        new(Model().GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).Select(t => t.ClrType.Name));

    [Fact]
    public void Every_entity_is_tenant_owned_or_explicitly_listed()
    {
        var unclassified = Model().GetEntityTypes()
            .Select(t => t.ClrType)
            .Where(type => !typeof(ITenantOwned).IsAssignableFrom(type) && !NotTenantOwned.ContainsKey(type))
            .Select(type => type.Name)
            .ToList();

        Assert.True(unclassified.Count == 0, $"Implement ITenantOwned or justify in NotTenantOwned: {string.Join(", ", unclassified)}");
    }

    [Theory]
    [MemberData(nameof(TenantOwnedEntities))]
    public void Tenant_owned_entities_have_the_tenant_filter(string entity)
    {
        var type = Model().GetEntityTypes().Single(t => t.ClrType.Name == entity);

        var filter = type.GetDeclaredQueryFilters().SingleOrDefault(f => f.Key == TenantQueries.FilterName);

        Assert.NotNull(filter);
        Assert.Contains(nameof(ITenantOwned.OrganizationId), filter.Expression!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TenantOwnedEntities))]
    public void Tenant_owned_entities_have_an_index_starting_with_the_organization(string entity)
    {
        var type = Model().GetEntityTypes().Single(t => t.ClrType.Name == entity);
        var keys = type.GetKeys().Select(k => k.Properties).Concat(type.GetIndexes().Select(i => i.Properties));

        // Every tenant-scoped query filters on organization_id first; without such an index each one scans.
        Assert.Contains(keys, properties => properties[0].Name == nameof(ITenantOwned.OrganizationId));
    }
}
