using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareLinkScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "level_id",
                table: "share_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "share_links",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                table: "share_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing links keep working: only the hash of their token stays. "Hạn chế" links are "people with access"; public ones "anyone",
            // with the level their switches gave (editor / viewer / view only).
            migrationBuilder.Sql("""
                UPDATE share_links SET
                    token_hash = encode(sha256(convert_to(token, 'UTF8')), 'hex'),
                    scope = CASE WHEN require_login THEN 'existing' ELSE 'anyone' END,
                    level_id = CASE WHEN require_login THEN NULL
                        WHEN allow_edit THEN '0193f000-0000-7000-8000-000000000003'::uuid
                        WHEN allow_download THEN '0193f000-0000-7000-8000-000000000006'::uuid
                        ELSE '0193f000-0000-7000-8000-000000000007'::uuid END;
                """);

            migrationBuilder.DropIndex(
                name: "ix_share_links_token",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "token",
                table: "share_links");

            migrationBuilder.CreateTable(
                name: "share_link_members",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_share_link_members", x => new { x.link_id, x.user_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_share_links_token_hash",
                table: "share_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_share_link_members_user_id",
                table: "share_link_members",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "share_link_members");

            migrationBuilder.DropIndex(
                name: "ix_share_links_token_hash",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "level_id",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "share_links");

            migrationBuilder.AddColumn<string>(
                name: "token",
                table: "share_links",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_share_links_token",
                table: "share_links",
                column: "token",
                unique: true);
        }
    }
}
