using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2AuthAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_files_folders_folder_id",
                table: "files");

            migrationBuilder.DropForeignKey(
                name: "fk_folders_folders_parent_id",
                table: "folders");

            migrationBuilder.DropForeignKey(
                name: "fk_organizations_organizations_parent_id",
                table: "organizations");

            migrationBuilder.AddForeignKey(
                name: "fk_files_folders_folder_id",
                table: "files",
                column: "folder_id",
                principalTable: "folders",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_folders_folders_parent_id",
                table: "folders",
                column: "parent_id",
                principalTable: "folders",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_organizations_organizations_parent_id",
                table: "organizations",
                column: "parent_id",
                principalTable: "organizations",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_files_folders_folder_id",
                table: "files");

            migrationBuilder.DropForeignKey(
                name: "fk_folders_folders_parent_id",
                table: "folders");

            migrationBuilder.DropForeignKey(
                name: "fk_organizations_organizations_parent_id",
                table: "organizations");

            migrationBuilder.AddForeignKey(
                name: "fk_files_folders_folder_id",
                table: "files",
                column: "folder_id",
                principalTable: "folders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_folders_folders_parent_id",
                table: "folders",
                column: "parent_id",
                principalTable: "folders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_organizations_organizations_parent_id",
                table: "organizations",
                column: "parent_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
