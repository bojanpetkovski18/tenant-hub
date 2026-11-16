using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Layer 6 of tenant isolation (SPEC-V2 3.1, D038): row-level security on every tenant-owned table, plus the
    /// two narrow readings that replace the database side of the allow-listed bypasses, and one SECURITY DEFINER
    /// function for the demo reset.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        private static readonly string[] TenantTables = ["audit_entries", "clients", "invitations", "jobs", "memberships"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.EnableTenantIsolation(table);
            }

            // Permissive policies add up for SELECT only: a user's own memberships are readable in every organization
            // (the session, tenant resolution, the five-owned limit), and the invitation whose token the request
            // presents is readable before the invitee is a member. Writes still need the entered organization.
            migrationBuilder.Sql($"""
                CREATE POLICY own_memberships ON memberships FOR SELECT
                    USING (user_id = {RowSecuritySql.CurrentUser});
                CREATE POLICY presented_invitation ON invitations FOR SELECT
                    USING (token_hash = {RowSecuritySql.PresentedInvitationToken});
                """);

            // The demo reset deletes visitor accounts only when nothing in any organization refers to them. As the
            // owner it sees every organization, and it answers yes or no for one user, nothing else.
            migrationBuilder.Sql($"""
                CREATE FUNCTION {DatabaseFunctions.UserHasTenantReferencesName}(p_user_id uuid) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path = pg_catalog, public
                AS $$
                    SELECT EXISTS (SELECT 1 FROM public.memberships WHERE user_id = p_user_id)
                        OR EXISTS (SELECT 1 FROM public.invitations WHERE created_by_user_id = p_user_id)
                $$;
                REVOKE ALL ON FUNCTION {DatabaseFunctions.UserHasTenantReferencesName}(uuid) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DROP FUNCTION {DatabaseFunctions.UserHasTenantReferencesName}(uuid);
                DROP POLICY presented_invitation ON invitations;
                DROP POLICY own_memberships ON memberships;
                """);
            foreach (var table in TenantTables)
            {
                migrationBuilder.DisableTenantIsolation(table);
            }
        }
    }
}
