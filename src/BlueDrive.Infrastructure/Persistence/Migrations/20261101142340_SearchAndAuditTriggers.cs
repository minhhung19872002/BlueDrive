using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <summary>Accent-insensitive full-text search + append-only audit log enforcement.</summary>
    public partial class SearchAndAuditTriggers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION f_unaccent(text) RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS
                $$ SELECT lower(public.unaccent('public.unaccent'::regdictionary, replace(replace($1, 'đ', 'd'), 'Đ', 'D'))) $$;

                CREATE OR REPLACE FUNCTION files_search_vector_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW.search_vector :=
                    setweight(to_tsvector('simple', f_unaccent(regexp_replace(coalesce(NEW.name, ''), '[_\.\-]+', ' ', 'g'))), 'A') ||
                    setweight(to_tsvector('simple', f_unaccent(coalesce(NEW.title, ''))), 'A') ||
                    setweight(to_tsvector('simple', f_unaccent(coalesce(NEW.description, ''))), 'B') ||
                    setweight(to_tsvector('simple', f_unaccent(coalesce(array_to_string(NEW.tags, ' '), ''))), 'B');
                  RETURN NEW;
                END $$;

                CREATE TRIGGER trg_files_search_vector
                  BEFORE INSERT OR UPDATE OF name, title, description, tags ON files
                  FOR EACH ROW EXECUTE FUNCTION files_search_vector_update();

                CREATE INDEX ix_files_name_unaccent_trgm ON files USING gin (f_unaccent(name) gin_trgm_ops);
                CREATE INDEX ix_folders_name_unaccent_trgm ON folders USING gin (f_unaccent(name) gin_trgm_ops);
                CREATE INDEX ix_users_full_name_unaccent_trgm ON users USING gin (f_unaccent(full_name) gin_trgm_ops);
                CREATE INDEX ix_organizations_name_unaccent_trgm ON organizations USING gin (f_unaccent(name) gin_trgm_ops);

                CREATE OR REPLACE FUNCTION audit_logs_block_modify() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'audit_logs là bảng chỉ ghi thêm (append-only), không được sửa/xóa';
                END $$;

                CREATE TRIGGER trg_audit_logs_no_update BEFORE UPDATE OR DELETE ON audit_logs
                  FOR EACH ROW EXECUTE FUNCTION audit_logs_block_modify();
                CREATE TRIGGER trg_audit_logs_no_truncate BEFORE TRUNCATE ON audit_logs
                  FOR EACH STATEMENT EXECUTE FUNCTION audit_logs_block_modify();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_logs_no_truncate ON audit_logs;
                DROP TRIGGER IF EXISTS trg_audit_logs_no_update ON audit_logs;
                DROP FUNCTION IF EXISTS audit_logs_block_modify();
                DROP INDEX IF EXISTS ix_organizations_name_unaccent_trgm;
                DROP INDEX IF EXISTS ix_users_full_name_unaccent_trgm;
                DROP INDEX IF EXISTS ix_folders_name_unaccent_trgm;
                DROP INDEX IF EXISTS ix_files_name_unaccent_trgm;
                DROP TRIGGER IF EXISTS trg_files_search_vector ON files;
                DROP FUNCTION IF EXISTS files_search_vector_update();
                DROP FUNCTION IF EXISTS f_unaccent(text);
                """);
        }
    }
}
