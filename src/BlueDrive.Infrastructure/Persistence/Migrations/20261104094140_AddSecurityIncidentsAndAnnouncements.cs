using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityIncidentsAndAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler_id = table.Column<Guid>(type: "uuid", nullable: true),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    affected_systems = table.Column<string>(type: "text", nullable: true),
                    cause = table.Column<string>(type: "text", nullable: true),
                    coordinating_parties = table.Column<string>(type: "text", nullable: true),
                    remediation = table.Column<string>(type: "text", nullable: true),
                    conclusion = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_announcements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_events_security_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "security_incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_incident_id",
                table: "incident_events",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_alert_id",
                table: "security_incidents",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_code",
                table: "security_incidents",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_status_reported_at",
                table: "security_incidents",
                columns: new[] { "status", "reported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_system_announcements_is_active_starts_at",
                table: "system_announcements",
                columns: new[] { "is_active", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_events");

            migrationBuilder.DropTable(
                name: "system_announcements");

            migrationBuilder.DropTable(
                name: "security_incidents");
        }
    }
}
