using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// "Nhóm tài nguyên - Chỉnh sửa" (workspace.edit) now decides who, besides the owner, may change a group workspace.
    /// The Biên tập role gets it by default; existing installations receive it once here so its managers keep that right
    /// (the seeder only fills permissions of a role when it creates the role).
    /// </summary>
    public partial class GrantEditorWorkspaceEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // On a fresh database roles and permissions are not seeded yet: nothing to do, the seeder applies the defaults.
            migrationBuilder.Sql("""
                INSERT INTO role_permissions (role_id, permission_code)
                SELECT r.id, 'workspace.edit' FROM roles r
                WHERE r.code = 'bien_tap' AND EXISTS (SELECT 1 FROM permissions p WHERE p.code = 'workspace.edit')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM role_permissions
                WHERE permission_code = 'workspace.edit' AND role_id IN (SELECT id FROM roles WHERE code = 'bien_tap');
                """);
        }
    }
}
