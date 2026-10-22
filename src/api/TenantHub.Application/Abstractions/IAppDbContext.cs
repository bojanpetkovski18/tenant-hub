using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TenantHub.Domain.Audit;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Abstractions;

/// <summary>
/// The application's view of the database. Handlers use it directly (EF Core is already a unit of work and
/// repository, D003); the implementation lives in Infrastructure.
/// </summary>
public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }

    DbSet<Membership> Memberships { get; }

    DbSet<Client> Clients { get; }

    DbSet<AuditEntry> AuditEntries { get; }

    DbSet<Invitation> Invitations { get; }

    DbSet<Job> Jobs { get; }

    /// <summary>For optimistic concurrency: handlers set the version the client last saw as the original value.</summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
