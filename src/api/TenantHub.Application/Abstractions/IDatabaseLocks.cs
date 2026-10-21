namespace TenantHub.Application.Abstractions;

/// <summary>
/// Row locks (<c>SELECT … FOR UPDATE</c>) that serialize commands which check a count and then insert, so two
/// concurrent requests cannot both pass the check (D013). Call inside a transaction; the lock ends with it.
/// </summary>
public interface IDatabaseLocks
{
    Task LockUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Recognizes provider-specific database errors without the application referencing the provider.</summary>
public interface IDatabaseErrors
{
    bool IsUniqueViolation(Exception exception, string indexName);
}
