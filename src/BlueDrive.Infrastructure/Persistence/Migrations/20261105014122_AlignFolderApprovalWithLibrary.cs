using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignFolderApprovalWithLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Approval of uploads is set per library ("Phê duyệt nội dung"); a folder no longer has a switch of its own. Folders whose flag was
            // turned on alone follow their library again: a library folder's own settings, otherwise the Space's default library (none = off).
            migrationBuilder.Sql("""
                UPDATE folders f SET requires_approval = COALESCE(
                    (SELECT ls.content_approval FROM folders lf JOIN library_settings ls ON ls.id = lf.id
                     WHERE lf.is_library AND lf.workspace_id = f.workspace_id AND f.path LIKE lf.path || '%' LIMIT 1),
                    CASE WHEN EXISTS (SELECT 1 FROM folders lf WHERE lf.is_library AND lf.workspace_id = f.workspace_id AND f.path LIKE lf.path || '%')
                         THEN false END,
                    (SELECT ls.content_approval FROM library_settings ls WHERE ls.workspace_id = f.workspace_id AND ls.is_default LIMIT 1),
                    false)
                WHERE f.requires_approval IS DISTINCT FROM COALESCE(
                    (SELECT ls.content_approval FROM folders lf JOIN library_settings ls ON ls.id = lf.id
                     WHERE lf.is_library AND lf.workspace_id = f.workspace_id AND f.path LIKE lf.path || '%' LIMIT 1),
                    CASE WHEN EXISTS (SELECT 1 FROM folders lf WHERE lf.is_library AND lf.workspace_id = f.workspace_id AND f.path LIKE lf.path || '%')
                         THEN false END,
                    (SELECT ls.content_approval FROM library_settings ls WHERE ls.workspace_id = f.workspace_id AND ls.is_default LIMIT 1),
                    false);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
