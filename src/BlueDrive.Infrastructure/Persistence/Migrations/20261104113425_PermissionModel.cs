using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermissionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:ltree", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "acl_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_additive = table.Column<bool>(type: "boolean", nullable: false),
                    additive_target_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acl_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "acl_scopes",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acl_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acl_scopes", x => x.scope_id);
                });

            migrationBuilder.CreateTable(
                name: "group_nestings",
                columns: table => new
                {
                    parent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_group_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_nestings", x => new { x.parent_group_id, x.child_group_id });
                });

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    personal_drive_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_libraries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "membership_versions",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_versions", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "permission_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    permissions = table.Column<long>(type: "bigint", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_levels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "personal_drives",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quota_bytes = table.Column<long>(type: "bigint", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_drives", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "principal_memberships",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_principal_memberships", x => new { x.user_id, x.principal_id });
                });

            migrationBuilder.CreateTable(
                name: "principals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_principals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scope_grants",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permissions = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_grants", x => new { x.scope_id, x.principal_id });
                });

            migrationBuilder.CreateTable(
                name: "securables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    library_id = table.Column<Guid>(type: "uuid", nullable: true),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    personal_drive_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "ltree", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    has_unique_acl = table.Column<bool>(type: "boolean", nullable: false),
                    acl_scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_securables", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "space_group_members",
                columns: table => new
                {
                    space_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_space_group_members", x => new { x.space_group_id, x.principal_id });
                });

            migrationBuilder.CreateTable(
                name: "space_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    permission_level_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_space_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "spaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    cover_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    org_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    policy = table.Column<string>(type: "jsonb", nullable: false),
                    quota_bytes = table.Column<long>(type: "bigint", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    default_label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spaces", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_expires_at",
                table: "acl_entries",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_principal_id",
                table: "acl_entries",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_acl_entries_scope_id",
                table: "acl_entries",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_nestings_child_group_id",
                table: "group_nestings",
                column: "child_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_libraries_personal_drive_id",
                table: "libraries",
                column: "personal_drive_id");

            migrationBuilder.CreateIndex(
                name: "ix_libraries_space_id",
                table: "libraries",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_permission_levels_code",
                table: "permission_levels",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_personal_drives_user_id",
                table: "personal_drives",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_principal_memberships_principal_id",
                table: "principal_memberships",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_principals_kind_ref_id",
                table: "principals",
                columns: new[] { "kind", "ref_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scope_grants_principal_id_scope_id",
                table: "scope_grants",
                columns: new[] { "principal_id", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_securables_acl_scope_id",
                table: "securables",
                column: "acl_scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_securables_parent_id",
                table: "securables",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_securables_path",
                table: "securables",
                column: "path")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_securables_personal_drive_id",
                table: "securables",
                column: "personal_drive_id");

            migrationBuilder.CreateIndex(
                name: "ix_securables_space_id",
                table: "securables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_space_group_members_principal_id",
                table: "space_group_members",
                column: "principal_id");

            migrationBuilder.CreateIndex(
                name: "ix_space_groups_root_id_kind",
                table: "space_groups",
                columns: new[] { "root_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_spaces_org_unit_id",
                table: "spaces",
                column: "org_unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acl_entries");

            migrationBuilder.DropTable(
                name: "acl_scopes");

            migrationBuilder.DropTable(
                name: "group_nestings");

            migrationBuilder.DropTable(
                name: "libraries");

            migrationBuilder.DropTable(
                name: "membership_versions");

            migrationBuilder.DropTable(
                name: "permission_levels");

            migrationBuilder.DropTable(
                name: "personal_drives");

            migrationBuilder.DropTable(
                name: "principal_memberships");

            migrationBuilder.DropTable(
                name: "principals");

            migrationBuilder.DropTable(
                name: "scope_grants");

            migrationBuilder.DropTable(
                name: "securables");

            migrationBuilder.DropTable(
                name: "space_group_members");

            migrationBuilder.DropTable(
                name: "space_groups");

            migrationBuilder.DropTable(
                name: "spaces");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:ltree", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
