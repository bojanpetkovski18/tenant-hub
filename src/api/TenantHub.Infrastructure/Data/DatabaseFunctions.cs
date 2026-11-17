namespace TenantHub.Infrastructure.Data;

/// <summary>
/// SQL functions callable from LINQ. Each <c>SECURITY DEFINER</c> function is a reviewed way past row-level
/// security, created by a migration and listed in <c>RowSecurityTests</c>; adding one needs the owner's approval.
/// </summary>
internal static class DatabaseFunctions
{
    public const string UserHasTenantReferencesName = "user_has_tenant_references";

    /// <summary>
    /// Whether any membership, or any invitation sent by the user, exists in any organization. Runs as the owner, so
    /// it sees every organization, and answers only yes or no for one user: the demo reset uses it to delete
    /// visitor accounts nothing refers to any more (K2, D033, D038).
    /// </summary>
    public static bool UserHasTenantReferences(Guid userId) =>
        throw new NotSupportedException($"{nameof(UserHasTenantReferences)} runs in the database only.");
}
