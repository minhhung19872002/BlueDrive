using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionAndHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "preserved_at",
                table: "folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "preserved_at",
                table: "files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "legal_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    scope_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    released_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    released_by = table.Column<Guid>(type: "uuid", nullable: true),
                    release_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    scope_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    years = table.Column<int>(type: "integer", nullable: false),
                    basis = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    on_expiry = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_released_at",
                table: "legal_holds",
                column: "released_at");

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_scope_kind_scope_id",
                table: "retention_policies",
                columns: new[] { "scope_kind", "scope_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_holds");

            migrationBuilder.DropTable(
                name: "retention_policies");

            migrationBuilder.DropColumn(
                name: "preserved_at",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "preserved_at",
                table: "files");
        }
    }
}
