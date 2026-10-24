using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Data;

/// <summary>
/// Row locks that serialize the commands that take them (limits, membership rules, numbering). FOR NO KEY UPDATE, not
/// FOR UPDATE: it conflicts with itself and with deletes, but not with the FOR KEY SHARE that every foreign-key
/// check takes on the referenced row. FOR UPDATE made each insert in the organization wait for the lock, and a
/// transition writing its audit entries deadlocked with a job creation queued for it (M10 load test, D033).
/// </summary>
internal sealed class DatabaseLocks(AppDbContext db) : IDatabaseLocks
{
    public async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureTransaction();

        // Interpolation becomes a parameter, not string concatenation.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR NO KEY UPDATE", cancellationToken);
    }

    public async Task LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM organizations WHERE id = {organizationId} FOR NO KEY UPDATE", cancellationToken);
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
