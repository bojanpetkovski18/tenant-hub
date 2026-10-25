using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Audit;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Audit;
using TenantHub.Infrastructure.Identity;
using TenantHub.Infrastructure.Tenancy;

namespace TenantHub.Infrastructure.Data;

/// <summary>
/// Identity's user tables (no role tables: roles are per membership) plus the domain. Table and column names are
/// snake_case (EFCore.NamingConventions); the Identity tables are renamed to match SPEC 8.1.
/// Tenant isolation (SPEC 3.3): every <see cref="ITenantOwned"/> entity gets the named query filter
/// <see cref="TenantQueries.FilterName"/>, and every save goes through <see cref="TenantGuard"/>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext? tenant = null, TimeProvider? time = null)
    : IdentityUserContext<AppUser, Guid>(options), IAppDbContext, IDataProtectionKeyContext
{
    /// <summary>The keys that sign session and antiforgery cookies, shared by every instance (K1).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // No tenant (design time, migrations, tests that only inspect the model): tenant-owned queries return nothing.
    private readonly ITenantContext _tenant = tenant ?? new TenantContext();
    private AuditWriter? _audit;

    private AuditWriter Audit => _audit ??= new AuditWriter(this, _tenant, time ?? TimeProvider.System);

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Read by the query filters; EF Core evaluates it per query for this context instance.</summary>
    internal Guid? CurrentOrganizationId => _tenant.OrganizationId;

    // Audit entries first, so they pass the tenant guard like every other row, then one transaction for all.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Audit.AddEntries();
        TenantGuard.Check(ChangeTracker, _tenant);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await Audit.AddEntriesAsync(cancellationToken);
        TenantGuard.Check(ChangeTracker, _tenant);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("citext");

        builder.Entity<AppUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.DisplayName).HasMaxLength(AppUser.DisplayNameMaxLength);
            // Identity names these indexes in PascalCase; the user name is the email, so the unique one also makes
            // emails unique in the database, not only in UserManager.
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ux_users_normalized_user_name").IsUnique();
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // One convention for every tenant-owned entity, so a new one cannot be forgotten.
        var applyTenantFilter = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            applyTenantFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder builder)
        where T : class, ITenantOwned =>
        builder.Entity<T>().HasQueryFilter(TenantQueries.FilterName, e => e.OrganizationId == CurrentOrganizationId);
}
