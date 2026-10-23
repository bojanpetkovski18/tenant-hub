using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <summary>
    /// D012: audit entries are never edited. The application has no update path, and this trigger makes the database
    /// refuse one from anywhere else (a bug, a script, a console session). DELETE stays possible: entries go when
    /// their organization is deleted (cascade) and in the demo reset (M09).
    /// </summary>
    public partial class MakeAuditEntriesAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit_entries_reject_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit entries are append-only (entry %)', OLD.id
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER audit_entries_append_only
                    BEFORE UPDATE ON audit_entries
                    FOR EACH ROW EXECUTE FUNCTION audit_entries_reject_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER audit_entries_append_only ON audit_entries;
                DROP FUNCTION audit_entries_reject_update();
                """);
        }
    }
}
