using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuotaDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "default_quota_gb",
                table: "roles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "default_quota_gb",
                table: "organizations",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_quota_gb",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "default_quota_gb",
                table: "organizations");
        }
    }
}
