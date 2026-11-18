using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_jobs_organization_id_id",
                table: "jobs",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "work_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    preferred_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decline_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_requests_client_contacts_organization_id_contact_id",
                        columns: x => new { x.organization_id, x.contact_id },
                        principalTable: "client_contacts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_requests_clients_organization_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_requests_jobs_organization_id_job_id",
                        columns: x => new { x.organization_id, x.job_id },
                        principalTable: "jobs",
                        principalColumns: new[] { "organization_id", "id" });
                    table.ForeignKey(
                        name: "fk_work_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_requests_users_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_requests_decided_by_user_id",
                table: "work_requests",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_requests_organization_id_client_id",
                table: "work_requests",
                columns: new[] { "organization_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_work_requests_organization_id_contact_id",
                table: "work_requests",
                columns: new[] { "organization_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_work_requests_organization_id_job_id",
                table: "work_requests",
                columns: new[] { "organization_id", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_work_requests_status_created",
                table: "work_requests",
                columns: new[] { "organization_id", "status", "created_at" });

            // Layer 6 (D038) and the client boundary (SPEC-V2 3.2): requests are tenant-owned and client-owned.
            migrationBuilder.EnableTenantIsolation("work_requests");
            migrationBuilder.EnableClientIsolation("work_requests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_jobs_organization_id_id",
                table: "jobs");
        }
    }
}
