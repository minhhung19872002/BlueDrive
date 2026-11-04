using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueDrive.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Trigram indexes for the keyword and IP filters of the "Nhật ký" screen (resource name, unaccented, username, IP prefix).
    /// Without them a keyword or an IP prefix that matches few of 2 000 000 audit rows scanned the whole table: 10-19 s per search
    /// while users worked; with them the page and the capped count take a few milliseconds (load test T7,
    /// docs/bao-cao-kiem-thu-tai.md). GIN rather than a text_pattern_ops b-tree: the IP prefix arrives as a LIKE parameter, which
    /// a b-tree can only use in a custom plan.
    /// </summary>
    public partial class AuditLogTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_audit_logs_resource_name_unaccent_trgm ON audit_logs USING gin (f_unaccent(resource_name) gin_trgm_ops);
                CREATE INDEX IF NOT EXISTS ix_audit_logs_username_trgm ON audit_logs USING gin (username gin_trgm_ops);
                CREATE INDEX IF NOT EXISTS ix_audit_logs_ip_trgm ON audit_logs USING gin (ip gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_audit_logs_ip_trgm;
                DROP INDEX IF EXISTS ix_audit_logs_username_trgm;
                DROP INDEX IF EXISTS ix_audit_logs_resource_name_unaccent_trgm;
                """);
        }
    }
}
