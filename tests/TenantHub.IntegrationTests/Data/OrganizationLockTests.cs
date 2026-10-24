using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

/// <summary>
/// The organization lock (D013, D029) serializes the commands that take it, and nothing else. Found by the M10 load
/// script: taken FOR UPDATE, it also blocked the foreign-key checks of every insert in the organization (FOR KEY
/// SHARE), and a transition writing its audit entries deadlocked with a job creation waiting for the lock.
/// Deterministic: a short lock_timeout instead of timing.
/// </summary>
public sealed class OrganizationLockTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string ConnectionString, Guid Org, Guid Owner)> OrganizationAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Locked Cleaners")).EnsureSuccessStatusCode();
        var user = (await owner.SessionAsync()).User!;
        return (connectionString, user.Memberships.Single().OrganizationId, user.Id);
    }

    private static AppDbContext As(string connectionString, Guid org, Guid owner)
    {
        var tenant = new TenantContext();
        tenant.Enter(org, owner, Role.Owner);
        return PostgresFixture.CreateDbContext(connectionString, tenant);
    }

    [Fact]
    public async Task Holding_the_organization_lock_does_not_block_inserts_that_reference_the_organization()
    {
        var (connectionString, org, owner) = await OrganizationAsync();
        await using var holder = As(connectionString, org, owner);
        await using var lockTransaction = await holder.Database.BeginTransactionAsync(Ct);
        await new DatabaseLocks(holder).LockOrganizationAsync(org, Ct);

        await using var other = As(connectionString, org, owner);
        await using var insertTransaction = await other.Database.BeginTransactionAsync(Ct);
        await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1s'", Ct);
        other.Clients.Add(Client.Create(org, ClientDetails.Validate("Harbour View", null, null, null, null, null), DateTimeOffset.UtcNow));

        // Before the fix: 55P03 lock_not_available after one second, because FOR UPDATE blocks FOR KEY SHARE.
        await other.SaveChangesAsync(Ct);
        await insertTransaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task The_organization_lock_still_serializes_the_commands_that_take_it()
    {
        var (connectionString, org, owner) = await OrganizationAsync();
        await using var holder = As(connectionString, org, owner);
        await using var lockTransaction = await holder.Database.BeginTransactionAsync(Ct);
        await new DatabaseLocks(holder).LockOrganizationAsync(org, Ct);

        await using var other = As(connectionString, org, owner);
        await using var otherTransaction = await other.Database.BeginTransactionAsync(Ct);
        await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'", Ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => new DatabaseLocks(other).LockOrganizationAsync(org, Ct));

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, (error as PostgresException ?? error.InnerException as PostgresException)?.SqlState);
    }
}
