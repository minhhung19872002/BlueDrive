using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Application.Features.Access;
using Npgsql;

namespace BlueDrive.Benchmarks;

/// <summary>Sizes of the seeded world (spec 4.5 by default: 1M files, 50k unique scopes, 5,000 users, 2,000 groups).</summary>
public sealed record PerfOptions(int Users = 5_000, int Groups = 2_000, int Spaces = 50, int TopFolders = 50, int SubFolders = 20, int Files = 1_000_000,
    int BigFolder = 100_000, int ListFolder = 1_000)
{
    public static PerfOptions Parse(string[] args)
    {
        var o = new PerfOptions();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!int.TryParse(args[i + 1], out var v)) continue;
            o = args[i] switch
            {
                "--users" => o with { Users = v },
                "--groups" => o with { Groups = v },
                "--spaces" => o with { Spaces = v },
                "--top-folders" => o with { TopFolders = v },
                "--sub-folders" => o with { SubFolders = v },
                "--files" => o with { Files = v },
                "--big-folder" => o with { BigFolder = v },
                "--list-folder" => o with { ListFolder = v },
                _ => o,
            };
        }
        return o;
    }

    /// <summary>Folders with their own permissions (every seeded folder breaks inheritance).</summary>
    public int UniqueScopes => Spaces * TopFolders * (SubFolders + 1);

    public int FilesPerFolder => Math.Max(1, (int)Math.Ceiling((Files - BigFolder - ListFolder) / (double)(Spaces * TopFolders * SubFolders)));
}

/// <summary>What the measurements need from the seeded world.</summary>
public sealed record PerfData(Guid UserId, string Username, Guid Space0, Guid FileInScope, Guid ListFolderId, Guid BigFolderId, long Files, long Folders, long Scopes,
    long Entries, int Users, int Groups, TimeSpan SeedTime);

/// <summary>
/// Seeds a large world quickly: a few template rows are made through the API (so every column is right), then cloned in SQL with
/// generate_series. The permission model rows (securables, scopes, entries, scope_grants) are written in the same shape the projection
/// writes them, so the engine and the SQL trimming see an ordinary (just large) database.
/// </summary>
public static class Seeder
{
    public static async Task<PerfData> SeedAsync(PerfHost host, PerfOptions o)
    {
        var sw = Stopwatch.StartNew();
        var admin = await host.LoginAsync("admin", PerfHost.AdminPassword, PerfHost.AdminNewPassword);
        var roles = await PerfHost.ReadAsync(await admin.GetAsync("/api/v1/roles"));
        var editorRole = roles.EnumerateArray().First(r => r.GetProperty("code").GetString() == "bien_tap").GetProperty("id").GetGuid();
        var levels = (await PerfHost.ReadAsync(await admin.GetAsync("/api/v1/permission-levels"))).EnumerateArray()
            .ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("id").GetGuid());

        // ---- templates through the API
        var templateUser = (await PerfHost.ReadAsync(await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = "perf_mau", fullName = "Người dùng mẫu", email = "perf_mau@example.gov.vn", isActive = true, roleIds = new[] { editorRole }, password = "Init@Pass123",
        }))).GetProperty("id").GetGuid();
        await host.LoginAsync("perf_mau", "Init@Pass123", PerfHost.UserPassword);
        var templateGroup = (await PerfHost.ReadAsync(await admin.PostAsJsonAsync("/api/v1/user-groups", new { name = "Nhóm mẫu", memberIds = new[] { templateUser } })))
            .GetProperty("id").GetGuid();

        await using var db = new NpgsqlConnection(host.ConnectionString);
        await db.OpenAsync();
        async Task<long> Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db) { CommandTimeout = 3600 };
            return await cmd.ExecuteNonQueryAsync();
        }
        async Task<T> Scalar<T>(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, db) { CommandTimeout = 3600 };
            return (T)(await cmd.ExecuteScalarAsync())!;
        }
        async Task<string> Columns(string table, IReadOnlyDictionary<string, string> set)
        {
            var cols = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                             $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND is_generated = 'NEVER' ORDER BY ordinal_position", db))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) cols.Add(r.GetString(0));
            return $"({string.Join(", ", cols.Select(c => $"\"{c}\""))}) SELECT {string.Join(", ", cols.Select(c => set.TryGetValue(c, out var e) ? e : $"t.\"{c}\""))}";
        }
        // INSERT INTO table (all columns) SELECT (template row, some columns replaced) FROM table t <from> WHERE <where>
        async Task<long> Clone(string table, string from, string where, Dictionary<string, string> set) =>
            await Exec($"INSERT INTO {table} {await Columns(table, set)} FROM {table} t {from} WHERE {where}");

        // ---- users and groups (memberships: three groups each)
        await Exec($"CREATE TABLE perf_users AS SELECT i, gen_random_uuid() AS id FROM generate_series(1, {o.Users}) i");
        await Exec($"CREATE TABLE perf_groups AS SELECT i, gen_random_uuid() AS id FROM generate_series(0, {o.Groups - 1}) i");
        await Clone("users", "CROSS JOIN perf_users p", $"t.id = '{templateUser}'", new()
        {
            ["id"] = "p.id", ["username"] = "'perf_u' || p.i", ["full_name"] = "'Người dùng ' || p.i", ["email"] = "NULL", ["email_hash"] = "NULL",
        });
        await Clone("user_roles", "CROSS JOIN perf_users p", $"t.user_id = '{templateUser}'", new() { ["user_id"] = "p.id" });
        await Clone("user_groups", "CROSS JOIN perf_groups g", $"t.id = '{templateGroup}'", new() { ["id"] = "g.id", ["name"] = "'Nhóm hiệu năng ' || g.i" });
        await Exec($"""
            INSERT INTO user_group_members (group_id, user_id, added_at)
            SELECT DISTINCT g.id, p.id, now() FROM perf_users p
            JOIN perf_groups g ON g.i IN (p.i % {o.Groups}, (p.i * 7) % {o.Groups}, (p.i * 13) % {o.Groups})
            """);
        // Principals and flattened memberships of the new model, from users and groups (small: a full rebuild before the big data).
        await PerfHost.ReadAsync(await admin.PostAsync("/api/v1/admin/permission-model/rebuild", null));

        // ---- Spaces, each with ten groups as editors
        var spaces = new List<Guid>();
        var groupIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM perf_groups ORDER BY i", db))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) groupIds.Add(r.GetGuid(0));
        for (var k = 0; k < o.Spaces; k++)
        {
            var ws = (await PerfHost.ReadAsync(await admin.PostAsJsonAsync("/api/v1/workspaces", new { name = $"Space hiệu năng {k}", type = "group" }))).GetProperty("id").GetGuid();
            var members = Enumerable.Range(0, 10).Select(j => new { kind = "group", id = groupIds[(k * 10 + j) % groupIds.Count] }).ToArray();
            await PerfHost.ReadAsync(await admin.PostAsJsonAsync($"/api/v1/workspaces/{ws}/group-members", new { principals = members, role = "editor" }));
            spaces.Add(ws);
        }
        var templateFolder = (await PerfHost.ReadAsync(await admin.PostAsJsonAsync("/api/v1/folders", new { workspaceId = spaces[0], name = "Mẫu" }))).GetProperty("id").GetGuid();
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")), "file", "mau.png" },
            { new StringContent(spaces[0].ToString()), "workspaceId" }, { new StringContent(templateFolder.ToString()), "folderId" },
        };
        var templateFile = (await PerfHost.ReadAsync(await admin.PostAsync("/api/v1/files/upload", form))).GetProperty("fileId").GetGuid();
        await PerfHost.ReadAsync(await admin.PostAsJsonAsync($"/api/v1/items/{templateFolder}/permissions", new
        {
            targets = new[] { new { kind = "group", id = groupIds[0] } }, levelId = levels["viewer"], notify = false,
        }));
        var templateEntry = await Scalar<Guid>($"SELECT id FROM acl_entries WHERE scope_id = '{templateFolder}' AND principal_id = '{groupIds[0]}' LIMIT 1");

        // ---- the tree: Spaces × top folders × sub-folders, every folder with its own ACL, files inheriting from their folder
        await Exec("CREATE TABLE perf_spaces (k int, id uuid, lib uuid)");
        for (var k = 0; k < spaces.Count; k++)
            await Exec($"INSERT INTO perf_spaces VALUES ({k}, '{spaces[k]}', '{PermissionProjection.LibraryOf(spaces[k])}')");
        var templateParentIsLibrary = await Scalar<bool>($"SELECT parent_id = '{PermissionProjection.LibraryOf(spaces[0])}' FROM securables WHERE id = '{templateFolder}'");
        await Exec($"""
            CREATE TABLE perf_folders AS
            SELECT s.k, s.id AS ws, s.lib, t.i AS top, NULL::int AS sub, gen_random_uuid() AS id, NULL::uuid AS parent FROM perf_spaces s CROSS JOIN generate_series(0, {o.TopFolders - 1}) t(i)
            """);
        await Exec($"""
            INSERT INTO perf_folders SELECT f.k, f.ws, f.lib, f.top, s.i, gen_random_uuid(), f.id FROM perf_folders f CROSS JOIN generate_series(0, {o.SubFolders - 1}) s(i) WHERE f.sub IS NULL
            """);
        await Exec("ALTER TABLE perf_folders ADD COLUMN path text, ADD COLUMN lpath text");
        await Exec("""
            UPDATE perf_folders f SET path = '/' || f.id || '/', lpath = replace(f.ws::text, '-', '') || '.' || replace(f.lib::text, '-', '') || '.' || replace(f.id::text, '-', '') WHERE f.sub IS NULL
            """);
        await Exec("""
            UPDATE perf_folders f SET path = p.path || f.id || '/', lpath = p.lpath || '.' || replace(f.id::text, '-', '') FROM perf_folders p WHERE f.parent = p.id
            """);
        // Two extra folders in Space 0: one with many files to list, one with a very large subtree to break inheritance on (both inherit).
        var listFolder = Guid.NewGuid();
        var bigFolder = Guid.NewGuid();
        foreach (var (id, top) in new[] { (listFolder, -1), (bigFolder, -2) })
            await Exec($"""
                INSERT INTO perf_folders SELECT 0, s.id, s.lib, {top}, NULL, '{id}', NULL, '/{id}/', replace(s.id::text, '-', '') || '.' || replace(s.lib::text, '-', '') || '.' || replace('{id}', '-', '')
                FROM perf_spaces s WHERE s.k = 0
                """);
        await Exec("CREATE INDEX ON perf_folders (id)");
        await Clone("folders", "JOIN perf_folders f ON true", $"t.id = '{templateFolder}'", new()
        {
            ["id"] = "f.id", ["workspace_id"] = "f.ws", ["parent_id"] = "f.parent", ["path"] = "f.path",
            ["name"] = "CASE WHEN f.top = -1 THEN 'Danh sách 1000' WHEN f.top = -2 THEN 'Cây lớn' WHEN f.sub IS NULL THEN 'Thư mục ' || f.top ELSE 'Mục ' || f.top || '-' || f.sub END",
        });
        var unique = "f.top >= 0";
        await Clone("securables", "JOIN perf_folders f ON true", $"t.id = '{templateFolder}'", new()
        {
            ["id"] = "f.id", ["parent_id"] = $"COALESCE(f.parent, {(templateParentIsLibrary ? "f.lib" : "f.ws")})", ["library_id"] = "f.lib", ["space_id"] = "f.ws",
            ["path"] = "f.lpath::ltree", ["name"] = "'f'", ["has_unique_acl"] = unique, ["acl_scope_id"] = $"CASE WHEN {unique} THEN f.id ELSE f.ws END",
        });
        await Exec($"INSERT INTO acl_scopes (scope_id, acl_version) SELECT f.id, 1 FROM perf_folders f WHERE {unique}");
        // Each own ACL: the Space's own entries (Space groups) copied, plus a viewer group and an editor group.
        await Clone("acl_entries", "JOIN perf_folders f ON true", $"t.scope_id = f.ws AND {unique}", new() { ["id"] = "gen_random_uuid()", ["scope_id"] = "f.id" });
        await Clone("acl_entries", "JOIN perf_folders f ON true JOIN LATERAL (SELECT 1) x ON true", $"t.id = '{templateEntry}' AND {unique}", new()
        {
            ["id"] = "gen_random_uuid()", ["scope_id"] = "f.id",
            ["principal_id"] = $"(SELECT g.id FROM perf_groups g WHERE g.i = abs(hashtext(f.id::text)) % {o.Groups})",
        });
        await Clone("acl_entries", "JOIN perf_folders f ON true", $"t.id = '{templateEntry}' AND {unique}", new()
        {
            ["id"] = "gen_random_uuid()", ["scope_id"] = "f.id", ["permission_level_id"] = $"'{levels["editor"]}'",
            ["principal_id"] = $"(SELECT g.id FROM perf_groups g WHERE g.i = abs(hashtext(f.id::text || 'e')) % {o.Groups})",
        });

        // ---- files: evenly in the sub-folders, plus the two extra folders
        await Exec($"""
            CREATE TABLE perf_files AS
            SELECT f.id AS folder, f.ws, f.lib, f.lpath, CASE WHEN {unique} THEN f.id ELSE f.ws END AS scope, n.i, gen_random_uuid() AS id
            FROM perf_folders f CROSS JOIN LATERAL generate_series(1, CASE WHEN f.top = -1 THEN {o.ListFolder} WHEN f.top = -2 THEN {o.BigFolder}
                                                                        WHEN f.sub IS NULL THEN 0 ELSE {o.FilesPerFolder} END) n(i)
            """);
        await Clone("files", "JOIN perf_files p ON true", $"t.id = '{templateFile}'", new()
        {
            ["id"] = "p.id", ["workspace_id"] = "p.ws", ["folder_id"] = "p.folder", ["name"] = "'Tài liệu ' || p.i || ' ' || left(p.id::text, 8) || '.png'",
        });
        await Clone("securables", "JOIN perf_files p ON true", $"t.id = '{templateFile}'", new()
        {
            ["id"] = "p.id", ["parent_id"] = "p.folder", ["library_id"] = "p.lib", ["space_id"] = "p.ws",
            ["path"] = "(p.lpath || '.' || replace(p.id::text, '-', ''))::ltree", ["name"] = "'t'", ["acl_scope_id"] = "p.scope", ["has_unique_acl"] = "false",
        });
        // Materialized rights (what SQL trimming joins on), as the engine's editor writes them.
        await Exec("""
            INSERT INTO scope_grants (scope_id, principal_id, permissions)
            SELECT e.scope_id, e.principal_id, bit_or(l.permissions) FROM acl_entries e JOIN permission_levels l ON l.id = e.permission_level_id
            WHERE e.scope_id IN (SELECT id FROM perf_folders) AND e.applies_to_children GROUP BY e.scope_id, e.principal_id
            ON CONFLICT DO NOTHING
            """);
        await Exec("ANALYZE");

        // ---- the measured user: in group 1 (a member group of Space 0), with a file under an own-ACL folder of Space 0
        var fileInScope = await Scalar<Guid>("SELECT p.id FROM perf_files p JOIN perf_folders f ON f.id = p.folder WHERE f.k = 0 AND f.top >= 0 LIMIT 1");
        var user = await Scalar<Guid>("SELECT id FROM perf_users WHERE i = 1");
        var counts = new
        {
            Files = await Scalar<long>("SELECT count(*) FROM files"), Folders = await Scalar<long>("SELECT count(*) FROM folders"),
            Scopes = await Scalar<long>("SELECT count(*) FROM acl_scopes"), Entries = await Scalar<long>("SELECT count(*) FROM acl_entries"),
        };
        return new PerfData(user, "perf_u1", spaces[0], fileInScope, listFolder, bigFolder, counts.Files, counts.Folders, counts.Scopes, counts.Entries,
            o.Users + 2, o.Groups + 1, sw.Elapsed);
    }

    public static string Json(object o) => JsonSerializer.Serialize(o);
}
