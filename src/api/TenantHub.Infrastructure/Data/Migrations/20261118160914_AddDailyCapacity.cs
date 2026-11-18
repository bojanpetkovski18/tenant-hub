using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing organizations start with a working day of 8 hours, the same default as new ones.
            migrationBuilder.AddColumn<int>(
                name: "daily_capacity_minutes",
                table: "organizations",
                type: "integer",
                nullable: false,
                defaultValue: 480);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "daily_capacity_minutes",
                table: "organizations");
        }
    }
}
