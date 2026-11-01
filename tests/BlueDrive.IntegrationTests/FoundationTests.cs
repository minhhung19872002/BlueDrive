using System.Net;
using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Auditing;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BlueDrive.IntegrationTests;

[Collection(ApiCollection.Name)]
public class FoundationTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_endpoint_reports_healthy_database()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("postgres").And.Contain("Healthy");
    }

    [Fact]
    public async Task Swagger_document_is_served()
    {
        var res = await factory.CreateClient().GetAsync("/api/swagger/v1/swagger.json");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_vector_matches_unaccented_query()
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        // Raw insert exercises the DB trigger independently of the application layer.
        var sql = """
            WITH u AS (INSERT INTO users (id, username, full_name, password_hash, is_active, is_locked, failed_login_count,
                         must_change_password, quota_bytes, used_bytes, security_stamp, created_at)
                       VALUES (gen_random_uuid(), 'fts_' || substr(md5(random()::text), 1, 8), 'FTS', 'x', true, false, 0, false, 0, 0, 0, now())
                       RETURNING id),
                 w AS (INSERT INTO workspaces (id, name, owner_id, quota_bytes, used_bytes, is_locked, type, created_at)
                       SELECT gen_random_uuid(), 'fts', id, 0, 0, false, 'personal', now() FROM u RETURNING id)
            INSERT INTO files (id, workspace_id, name, extension, mime_type, media_type, size_bytes, tags, approval_status, scan_status,
                               is_deleted, is_trash_root, created_at)
            SELECT gen_random_uuid(), w.id, 'Báo cáo Dân số 2025.docx', 'docx', 'application/msword', 'document', 1, ARRAY['Kế hoạch hóa'],
                   'approved', 'clean', false, false, now() FROM w
            """;
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();

        var count = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM files WHERE search_vector @@ plainto_tsquery('simple', f_unaccent('dan so'))", conn).ExecuteScalarAsync())!;
        count.Should().BeGreaterThan(0);
        var byTag = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM files WHERE search_vector @@ plainto_tsquery('simple', f_unaccent('ke hoach hoa'))", conn).ExecuteScalarAsync())!;
        byTag.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Audit_log_is_append_only_and_hash_chained()
    {
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: "Kiểm thử chuỗi hash", Detail: new { key = "giá trị" }));
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, ResourceName: "Bản ghi thứ hai"));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var last = await db.AuditLogs.OrderByDescending(a => a.Id).Take(2).ToListAsync();
        last[0].PrevHash.Should().Be(last[1].Hash);
        foreach (var row in last) AuditLogger.ComputeHash(row).Should().Be(row.Hash, "the stored hash must be reproducible from the stored row");

        var act = () => db.Database.ExecuteSqlRawAsync("UPDATE audit_logs SET resource_name = 'sửa trái phép'");
        await act.Should().ThrowAsync<PostgresException>();
        var del = () => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs");
        await del.Should().ThrowAsync<PostgresException>();
    }
}
