using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agreement_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "changed_by_hand",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "estimated_minutes",
                table: "jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "occurrence_on",
                table: "jobs",
                type: "date",
                nullable: true);

            // Existing entries were all made by staff users (SPEC-V2 5.9). A default, not an UPDATE: the table is
            // append-only (D030), and adding a column with a default rewrites no row.
            migrationBuilder.AddColumn<string>(
                name: "actor_kind",
                table: "audit_entries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.AddColumn<Guid>(
                name: "actor_ref",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agreements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quoted_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    estimated_minutes = table.Column<int>(type: "integer", nullable: true),
                    recurrence = table.Column<string>(type: "jsonb", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    max_visits = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paused_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    generated_through = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agreements", x => x.id);
                    table.UniqueConstraint("ak_agreements_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_agreements_clients_organization_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" });
                    table.ForeignKey(
                        name: "fk_agreements_memberships_organization_id_assignee_user_id",
                        columns: x => new { x.organization_id, x.assignee_user_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "user_id" });
                    table.ForeignKey(
                        name: "fk_agreements_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agreements_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Layer 6 for the new tenant-owned table (D038); RowSecurityTests checks it.
            migrationBuilder.EnableTenantIsolation("agreements");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_agreement_occurrence",
                table: "jobs",
                columns: new[] { "organization_id", "agreement_id", "occurrence_on" },
                unique: true,
                filter: "agreement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_agreements_created_by_user_id",
                table: "agreements",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agreements_organization_id_assignee_user_id",
                table: "agreements",
                columns: new[] { "organization_id", "assignee_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agreements_organization_id_client_id",
                table: "agreements",
                columns: new[] { "organization_id", "client_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_agreements_organization_id_agreement_id",
                table: "jobs",
                columns: new[] { "organization_id", "agreement_id" },
                principalTable: "agreements",
                principalColumns: new[] { "organization_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_jobs_agreements_organization_id_agreement_id",
                table: "jobs");

            migrationBuilder.DisableTenantIsolation("agreements");

            migrationBuilder.DropTable(
                name: "agreements");

            migrationBuilder.DropIndex(
                name: "ux_jobs_agreement_occurrence",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "agreement_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "changed_by_hand",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "estimated_minutes",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "occurrence_on",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "actor_kind",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "actor_ref",
                table: "audit_entries");
        }
    }
}
