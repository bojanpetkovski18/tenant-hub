using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => new { x.user_id, x.type });
                    table.ForeignKey(
                        name: "fk_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_memberships_organization_id_user_id",
                        columns: x => new { x.organization_id, x.user_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_created",
                table: "notifications",
                columns: new[] { "organization_id", "user_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_source_user",
                table: "notifications",
                columns: new[] { "organization_id", "source_id", "user_id" },
                unique: true);

            // Row-level security (D038). Preferences are a person's, not an organization's (SPEC-V2 8), so they have none.
            migrationBuilder.EnableTenantIsolation("notifications");

            // Every existing organization gets its daily digest at the next 07:00 in its time zone (SPEC-V2 5.7); new
            // ones get it when they are created.
            migrationBuilder.Sql("""
                INSERT INTO scheduled_tasks (id, name, organization_id, due_at)
                SELECT gen_random_uuid(), 'notifications.digest', o.id,
                    CASE
                        WHEN date_trunc('day', now() AT TIME ZONE o.time_zone) + interval '7 hours' > now() AT TIME ZONE o.time_zone
                        THEN (date_trunc('day', now() AT TIME ZONE o.time_zone) + interval '7 hours') AT TIME ZONE o.time_zone
                        ELSE (date_trunc('day', now() AT TIME ZONE o.time_zone) + interval '1 day 7 hours') AT TIME ZONE o.time_zone
                    END
                FROM organizations o
                ON CONFLICT (name, organization_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM scheduled_tasks WHERE name = 'notifications.digest';");
            migrationBuilder.DisableTenantIsolation("notifications");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
