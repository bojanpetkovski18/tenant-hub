using Microsoft.EntityFrameworkCore;
using TenantHub.Domain;

namespace TenantHub.Application.Tenancy;

/// <summary>
/// The only way past the tenant query filter (SPEC 3.3, point 5). Every call site is on a reviewed allow-list
/// (TenantHub.ArchitectureTests, <c>TenantBypassAllowListTests</c>); <c>IgnoreQueryFilters</c> itself appears
/// nowhere else. Only the tenant filter is ignored, so filters added later still apply.
/// </summary>
public static class TenantQueries
{
    public const string FilterName = "Tenant";

    public static IQueryable<T> AcrossTenants<T>(this DbSet<T> set)
        where T : class, ITenantOwned =>
        set.IgnoreQueryFilters([FilterName]);
}
