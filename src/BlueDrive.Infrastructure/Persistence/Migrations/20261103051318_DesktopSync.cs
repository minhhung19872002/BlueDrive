using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DesktopSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "user_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "folders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "files",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_journal",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    op = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    path = table.Column<string>(type: "text", nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_journal", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    os = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    app_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    machine_id_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    wipe_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    wipe_requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    wiped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    secret_protected = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorization_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    device_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_authorization_codes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_journal_item_id",
                table: "change_journal",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_journal_occurred_at",
                table: "change_journal",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_change_journal_workspace_id_seq",
                table: "change_journal",
                columns: new[] { "workspace_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_user_id_status",
                table: "devices",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_codes_code_hash",
                table: "oauth_authorization_codes",
                column: "code_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_journal");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "oauth_authorization_codes");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "files");
        }
    }
}
