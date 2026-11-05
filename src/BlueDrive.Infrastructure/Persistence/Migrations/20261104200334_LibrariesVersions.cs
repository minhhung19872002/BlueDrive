using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LibrariesVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_library",
                table: "folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "checked_out_at",
                table: "files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "checked_out_by",
                table: "files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "published_version_id",
                table: "files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_published",
                table: "file_versions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "major",
                table: "file_versions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "minor",
                table: "file_versions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing versions become major versions (1, 2, 3 …); every file's current version is its published one.
            migrationBuilder.Sql("UPDATE file_versions SET major = version_no, minor = 0, is_published = TRUE;");
            migrationBuilder.Sql("UPDATE files SET published_version_id = current_version_id;");

            migrationBuilder.CreateTable(
                name: "library_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    versioning = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    major_limit = table.Column<int>(type: "integer", nullable: false),
                    minor_limit = table.Column<int>(type: "integer", nullable: false),
                    delete_versions_older_than_days = table.Column<int>(type: "integer", nullable: true),
                    require_checkout = table.Column<bool>(type: "boolean", nullable: false),
                    content_approval = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_extensions = table.Column<List<string>>(type: "text[]", nullable: false),
                    max_file_size_mb = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_library_settings_workspace_id",
                table: "library_settings",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "library_settings");

            migrationBuilder.DropColumn(
                name: "is_library",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "checked_out_at",
                table: "files");

            migrationBuilder.DropColumn(
                name: "checked_out_by",
                table: "files");

            migrationBuilder.DropColumn(
                name: "published_version_id",
                table: "files");

            migrationBuilder.DropColumn(
                name: "is_published",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "major",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "minor",
                table: "file_versions");
        }
    }
}
