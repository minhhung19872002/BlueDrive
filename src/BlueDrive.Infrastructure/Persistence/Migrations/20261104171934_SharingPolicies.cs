using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharingPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "access_request_deciders",
                table: "workspaces",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "item_managers");

            migrationBuilder.AddColumn<bool>(
                name: "allow_item_sharing",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "external_sharing",
                table: "workspaces",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "members_share_outside",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "external_sharing",
                table: "folders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_sharing",
                table: "files",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_request_deciders",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "allow_item_sharing",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "external_sharing",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "members_share_outside",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "external_sharing",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "external_sharing",
                table: "files");
        }
    }
}
