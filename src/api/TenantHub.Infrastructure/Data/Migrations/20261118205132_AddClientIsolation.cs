using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <summary>
    /// The client boundary in the database (SPEC-V2 3.2, D040): in a portal session (<c>app.client_id</c> set), the
    /// client-owned tables and the clients table itself show and accept only the session's client.
    /// </summary>
    public partial class AddClientIsolation : Migration
    {
        private static readonly string[] ClientOwnedTables = ["agreements", "client_contacts", "jobs"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in ClientOwnedTables)
            {
                migrationBuilder.EnableClientIsolation(table);
            }

            migrationBuilder.EnableClientIsolation("clients", column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in ClientOwnedTables)
            {
                migrationBuilder.DisableClientIsolation(table);
            }

            migrationBuilder.DisableClientIsolation("clients");
        }
    }
}
