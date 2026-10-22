using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Data;

internal sealed class DatabaseLocks(AppDbContext db) : IDatabaseLocks
{
    public async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureTransaction();

        // Interpolation becomes a parameter, not string concatenation.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);
    }

    public async Task LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM organizations WHERE id = {organizationId} FOR UPDATE", cancellationToken);
    }

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A row lock outside a transaction would be released immediately.");
        }
    }
}

internal sealed class DatabaseErrors : IDatabaseErrors
{
    public bool IsUniqueViolation(Exception exception, string indexName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && postgres.ConstraintName == indexName;
}
