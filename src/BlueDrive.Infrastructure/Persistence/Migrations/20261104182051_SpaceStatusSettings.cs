using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpaceStatusSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "activity_for_members",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "allow_download",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "allow_sync",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cover_file_id",
                table: "workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "workspaces",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTime>(
                name: "status_changed_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template",
                table: "workspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trash_retention_days",
                table: "workspaces",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workspace_group_members",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    member_role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_group_members", x => new { x.workspace_id, x.principal_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_group_members_principal_id",
                table: "workspace_group_members",
                column: "principal_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_group_members");

            migrationBuilder.DropColumn(
                name: "activity_for_members",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "allow_download",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "allow_sync",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "cover_file_id",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "status",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "template",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "trash_retention_days",
                table: "workspaces");
        }
    }
}
