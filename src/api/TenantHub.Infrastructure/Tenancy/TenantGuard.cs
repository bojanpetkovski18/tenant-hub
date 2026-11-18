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

            if (tenant.ClientId is { } client)
            {
                CheckClient(entry, entity, client);
            }
        }
    }

    /// <summary>
    /// In a portal session (SPEC-V2 3.2) a write may touch only the session's client: its own row and its client-owned
    /// rows, never moved to another client.
    /// </summary>
    private static void CheckClient(EntityEntry<ITenantOwned> entry, string entity, Guid client)
    {
        var property = entry.Entity switch
        {
            IClientOwned => entry.Property(nameof(IClientOwned.ClientId)),
            Domain.Clients.Client => entry.Property(nameof(Domain.Clients.Client.Id)),
            _ => null,
        };
        if (property is null)
        {
            return;
        }

        var current = (Guid)property.CurrentValue!;
        var original = entry.State == EntityState.Added ? current : (Guid)property.OriginalValue!;
        if (current != client || original != client)
        {
            throw new TenantIsolationException($"{entry.State} {entity} of another client in a portal session.");
        }
    }
}
