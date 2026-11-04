using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareLinkAllowEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_edit",
                table: "share_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_edit",
                table: "share_links");
        }
    }
}
