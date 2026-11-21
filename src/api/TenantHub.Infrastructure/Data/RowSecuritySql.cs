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

    public const string ClientPolicy = "client_isolation";

    /// <summary>A portal session's client; unset (staff, the system) is null, which leaves the client policy open.</summary>
    public const string CurrentClient = "nullif(current_setting('app.client_id', true), '')::uuid";

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

    /// <summary>
    /// The client boundary (SPEC-V2 3.2, D040): in a portal session (<c>app.client_id</c> set) a client-owned table shows
    /// and accepts only that client's rows. RESTRICTIVE, so it narrows <see cref="TenantPolicy"/> and every permissive
    /// policy instead of adding to them; without a client (staff) it changes nothing.
    /// </summary>
    public static void EnableClientIsolation(this MigrationBuilder migrationBuilder, string table, string column = "client_id") =>
        migrationBuilder.Sql($"""
            CREATE POLICY {ClientPolicy} ON {table} AS RESTRICTIVE
                USING ({CurrentClient} IS NULL OR {column} = {CurrentClient})
                WITH CHECK ({CurrentClient} IS NULL OR {column} = {CurrentClient});
            """);

    /// <summary>
    /// The client boundary for a job's own rows (checklist items, photos; SPEC-V2 3.2, 5.6), which have no client of their
    /// own: in a portal session a row is visible only while its job is. The subquery reads <c>jobs</c> under that table's
    /// policies, its client policy included, so this follows the job's client wherever the job moves.
    /// </summary>
    public static void EnableClientIsolationThroughJob(this MigrationBuilder migrationBuilder, string table) =>
        migrationBuilder.Sql($"""
            CREATE POLICY {ClientPolicy} ON {table} AS RESTRICTIVE
                USING ({CurrentClient} IS NULL OR EXISTS (SELECT 1 FROM jobs j WHERE j.organization_id = {table}.organization_id AND j.id = {table}.job_id))
                WITH CHECK ({CurrentClient} IS NULL OR EXISTS (SELECT 1 FROM jobs j WHERE j.organization_id = {table}.organization_id AND j.id = {table}.job_id));
            """);

    public static void DisableClientIsolation(this MigrationBuilder migrationBuilder, string table) =>
        migrationBuilder.Sql($"DROP POLICY {ClientPolicy} ON {table};");

    public static void DisableTenantIsolation(this MigrationBuilder migrationBuilder, string table) =>
        migrationBuilder.Sql($"""
            DROP POLICY {TenantPolicy} ON {table};
            ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
            """);
}
