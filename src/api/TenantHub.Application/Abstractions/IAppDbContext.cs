using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
