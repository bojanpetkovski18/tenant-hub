using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalSignInTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_sign_in_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_sign_in_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_portal_sign_in_tokens_client_contacts_organization_id_conta",
                        columns: x => new { x.organization_id, x.contact_id },
                        principalTable: "client_contacts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_portal_sign_in_tokens_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_sign_in_tokens_contact_created",
                table: "portal_sign_in_tokens",
                columns: new[] { "organization_id", "contact_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_portal_sign_in_tokens_token_hash",
                table: "portal_sign_in_tokens",
                columns: new[] { "organization_id", "token_hash" },
                unique: true);

            // Layer 6 (D038): sign-in links are tenant-owned.
            migrationBuilder.EnableTenantIsolation("portal_sign_in_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_sign_in_tokens");
        }
    }
}
