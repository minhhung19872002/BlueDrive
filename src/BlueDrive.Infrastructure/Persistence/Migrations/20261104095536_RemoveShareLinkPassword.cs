using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveShareLinkPassword : Migration
    {
        /// <summary>Link passwords are gone: a link that had one becomes "Hạn chế" (signed in and with access), never more open.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE share_links SET require_login = true, allow_edit = false WHERE password_hash IS NOT NULL;");
            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "share_links");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "share_links",
                type: "text",
                nullable: true);
        }
    }
}
