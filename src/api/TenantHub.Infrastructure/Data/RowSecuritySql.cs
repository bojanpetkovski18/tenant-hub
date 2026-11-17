using Microsoft.EntityFrameworkCore.Migrations;

namespace TenantHub.Infrastructure.Data;

/// <summary>
/// The row-level security policies (SPEC-V2 3.1, D038), for migrations. Every tenant-owned table gets
/// <see cref="EnableTenantIsolation"/>; <c>RowSecurityTests</c> fails for a table that does not, so a new entity is
/// covered by its migration or the build is red.
/// </summary>
public static class RowSecuritySql
{
    public const string TenantPolicy = "tenant_isolation";

    /// <summary>The organization the session entered; an unset or empty value is null and matches no row.</summary>
    public const string CurrentOrganization = "nullif(current_setting('app.organization_id', true), '')::uuid";

    public const string CurrentUser = "nullif(current_setting('app.user_id', true), '')::uuid";

    public const string PresentedInvitationToken = "decode(nullif(current_setting('app.invitation_token_hash', true), ''), 'hex')";

    /// <summary>
    /// Reads and writes only rows of the entered organization: <c>USING</c> filters what a command sees,
    /// <c>WITH CHECK</c> refuses inserting or moving a row into another organization. The owner (migrations) is
    /// exempt as table owner; the API's user owns nothing and cannot bypass it (<see cref="DatabaseRoles"/>).
    /// </summary>
    public static void EnableTenantIsolation(this MigrationBuilder migrationBuilder, string table) =>
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            CREATE POLICY {TenantPolicy} ON {table}
                USING (organization_id = {CurrentOrganization})
                WITH CHECK (organization_id = {CurrentOrganization});
            """);

    public static void DisableTenantIsolation(this MigrationBuilder migrationBuilder, string table) =>
        migrationBuilder.Sql($"""
            DROP POLICY {TenantPolicy} ON {table};
            ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
            """);
}
