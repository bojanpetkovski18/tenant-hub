using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditTimelineIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_entries_organization_id_occurred_at",
                table: "audit_entries");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_timeline",
                table: "audit_entries",
                columns: new[] { "organization_id", "occurred_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_entries_timeline",
                table: "audit_entries");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_organization_id_occurred_at",
                table: "audit_entries",
                columns: new[] { "organization_id", "occurred_at" },
                descending: new[] { false, true });
        }
    }
}
