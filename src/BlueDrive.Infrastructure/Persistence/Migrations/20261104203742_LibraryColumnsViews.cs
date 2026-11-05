using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LibraryColumnsViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_column_values",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    column_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    text_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    number_value = table.Column<decimal>(type: "numeric", nullable: true),
                    date_value = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_column_values", x => new { x.file_id, x.column_id });
                });

            migrationBuilder.CreateTable(
                name: "folder_column_defaults",
                columns: table => new
                {
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    column_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folder_column_defaults", x => new { x.folder_id, x.column_id });
                });

            migrationBuilder.CreateTable(
                name: "library_columns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    unique = table.Column<bool>(type: "boolean", nullable: false),
                    default_value = table.Column<string>(type: "text", nullable: true),
                    validation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    validation_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    choices = table.Column<List<string>>(type: "text[]", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_columns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "library_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "library_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    columns = table.Column<List<string>>(type: "text[]", nullable: false),
                    sort_by = table.Column<string>(type: "text", nullable: true),
                    sort_descending = table.Column<bool>(type: "boolean", nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    group_by = table.Column<string>(type: "text", nullable: true),
                    flat = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_views", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_column_values_column_id_text_value",
                table: "file_column_values",
                columns: new[] { "column_id", "text_value" });

            migrationBuilder.CreateIndex(
                name: "ix_library_columns_library_id_key",
                table: "library_columns",
                columns: new[] { "library_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_library_templates_library_id",
                table: "library_templates",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_library_views_library_id_owner_user_id",
                table: "library_views",
                columns: new[] { "library_id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_column_values");

            migrationBuilder.DropTable(
                name: "folder_column_defaults");

            migrationBuilder.DropTable(
                name: "library_columns");

            migrationBuilder.DropTable(
                name: "library_templates");

            migrationBuilder.DropTable(
                name: "library_views");
        }
    }
}
