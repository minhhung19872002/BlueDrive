using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SensitivityLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_label_id",
                table: "workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "label_id",
                table: "folders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "label_id",
                table: "files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sensitivity_labels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    block_external = table.Column<bool>(type: "boolean", nullable: false),
                    block_anyone_links = table.Column<bool>(type: "boolean", nullable: false),
                    view_only_outside_space = table.Column<bool>(type: "boolean", nullable: false),
                    watermark = table.Column<bool>(type: "boolean", nullable: false),
                    block_sync = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensitivity_labels", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_labels_name",
                table: "sensitivity_labels",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensitivity_labels");

            migrationBuilder.DropColumn(
                name: "default_label_id",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "label_id",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "label_id",
                table: "files");
        }
    }
}
