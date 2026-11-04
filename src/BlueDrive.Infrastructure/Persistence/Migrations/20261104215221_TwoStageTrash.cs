using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TwoStageTrash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "original_path",
                table: "folders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "second_stage_at",
                table: "folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_path",
                table: "files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "second_stage_at",
                table: "files",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "original_path",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "second_stage_at",
                table: "folders");

            migrationBuilder.DropColumn(
                name: "original_path",
                table: "files");

            migrationBuilder.DropColumn(
                name: "second_stage_at",
                table: "files");
        }
    }
}
