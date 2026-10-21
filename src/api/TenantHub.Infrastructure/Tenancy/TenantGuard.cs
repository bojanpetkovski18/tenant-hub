using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;

namespace TenantHub.Infrastructure.Tenancy;

/// <summary>
/// The write side of tenant isolation (SPEC 3.3, point 3), run on every SaveChanges:
/// new tenant-owned rows get the current organization stamped (or must already carry it), and nothing tenant-owned
/// is inserted, updated or deleted outside the current organization, or moved to another one.
/// </summary>
internal static class TenantGuard
{
    public static void Check(ChangeTracker changeTracker, ITenantContext tenant)
    {
        foreach (var entry in changeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            var entity = entry.Metadata.ClrType.Name;
            var organizationId = entry.Property(e => e.OrganizationId);
            if (tenant.OrganizationId is not { } current)
            {
                throw new TenantIsolationException($"{entry.State} {entity} outside any organization.");
            }

            switch (entry.State)
            {
                case EntityState.Added when organizationId.CurrentValue == Guid.Empty:
                    organizationId.CurrentValue = current;
                    break;
                case EntityState.Added when organizationId.CurrentValue != current:
                    throw new TenantIsolationException($"Insert of {entity} for another organization.");
                case EntityState.Modified or EntityState.Deleted when organizationId.OriginalValue != current:
                    throw new TenantIsolationException($"{entry.State} {entity} of another organization.");
                case EntityState.Modified when organizationId.CurrentValue != organizationId.OriginalValue:
                    throw new TenantIsolationException($"Moving {entity} to another organization.");
            }
        }
    }
}
