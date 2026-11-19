using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_comment",
                table: "jobs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approval_decided_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approval_decided_by_contact_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approval_sent_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approval_sent_by_user_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_status",
                table: "jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Jobs from before quotes were never sent: they behave exactly as in v1 (SPEC-V2 5.4).
                defaultValue: "NotRequested");

            migrationBuilder.AddColumn<decimal>(
                name: "approved_amount",
                table: "jobs",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_approval_sent_by_user_id",
                table: "jobs",
                column: "approval_sent_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_organization_id_approval_decided_by_contact_id",
                table: "jobs",
                columns: new[] { "organization_id", "approval_decided_by_contact_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_client_contacts_organization_id_approval_decided_by_co",
                table: "jobs",
                columns: new[] { "organization_id", "approval_decided_by_contact_id" },
                principalTable: "client_contacts",
                principalColumns: new[] { "organization_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_users_approval_sent_by_user_id",
                table: "jobs",
                column: "approval_sent_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_jobs_client_contacts_organization_id_approval_decided_by_co",
                table: "jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_jobs_users_approval_sent_by_user_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_approval_sent_by_user_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_organization_id_approval_decided_by_contact_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_comment",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_decided_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_decided_by_contact_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_sent_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_sent_by_user_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approval_status",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "approved_amount",
                table: "jobs");
        }
    }
}
