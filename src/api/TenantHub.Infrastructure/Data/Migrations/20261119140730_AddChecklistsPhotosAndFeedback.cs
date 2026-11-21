using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChecklistsPhotosAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_feedback",
                table: "jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "client_feedback_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "client_feedback_by_contact_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_feedback_text",
                table: "jobs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checklist_template",
                table: "agreements",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    thumbnail_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_attachments_jobs_organization_id_job_id",
                        columns: x => new { x.organization_id, x.job_id },
                        principalTable: "jobs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_attachments_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_attachments_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checklist_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checklist_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_checklist_items_jobs_organization_id_job_id",
                        columns: x => new { x.organization_id, x.job_id },
                        principalTable: "jobs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_checklist_items_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_checklist_items_users_done_by_user_id",
                        column: x => x.done_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_organization_id_client_feedback_by_contact_id",
                table: "jobs",
                columns: new[] { "organization_id", "client_feedback_by_contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_created_by_user_id",
                table: "attachments",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_job",
                table: "attachments",
                columns: new[] { "organization_id", "job_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_attachments_storage_key",
                table: "attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_checklist_items_done_by_user_id",
                table: "checklist_items",
                column: "done_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_checklist_items_job",
                table: "checklist_items",
                columns: new[] { "organization_id", "job_id", "position" });

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_client_contacts_organization_id_client_feedback_by_con",
                table: "jobs",
                columns: new[] { "organization_id", "client_feedback_by_contact_id" },
                principalTable: "client_contacts",
                principalColumns: new[] { "organization_id", "id" });

            // Row-level security (D038): the organization's rows only, and in a portal session only rows of the
            // session client's jobs (SPEC-V2 3.2).
            migrationBuilder.EnableTenantIsolation("checklist_items");
            migrationBuilder.EnableClientIsolationThroughJob("checklist_items");
            migrationBuilder.EnableTenantIsolation("attachments");
            migrationBuilder.EnableClientIsolationThroughJob("attachments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableClientIsolation("attachments");
            migrationBuilder.DisableTenantIsolation("attachments");
            migrationBuilder.DisableClientIsolation("checklist_items");
            migrationBuilder.DisableTenantIsolation("checklist_items");

            migrationBuilder.DropForeignKey(
                name: "fk_jobs_client_contacts_organization_id_client_feedback_by_con",
                table: "jobs");

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "checklist_items");

            migrationBuilder.DropIndex(
                name: "ix_jobs_organization_id_client_feedback_by_contact_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "client_feedback",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "client_feedback_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "client_feedback_by_contact_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "client_feedback_text",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "checklist_template",
                table: "agreements");
        }
    }
}
