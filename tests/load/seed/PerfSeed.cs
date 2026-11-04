#:project ../../../src/BlueDrive.Infrastructure/BlueDrive.Infrastructure.csproj
#:property PublishAot=false
#:package Newtonsoft.Json@13.0.4

// Load-test data for the local perf stack (scripts/perf-up.ps1, docs/bao-cao-kiem-thu-tai.md). Uses the application's own
// DbContext and audit hash so the data looks exactly like production data:
//   1. sample contents (Word, Excel, PowerPoint, video, audio, image, text) uploaded through the API, so they are encrypted,
//      scanned and thumbnailed by the real pipeline;
//   2. N users (roles: admin / approver / viewer / editor) with personal workspaces, 10 group workspaces with mixed member roles;
//   3. folders and F file records spread over 3 years, pointing at the sample contents (catalog items, tags, approvals);
//   4. shares between users;
//   5. A audit rows over the last 12 months that extend the existing hash chain (AuditLogger.ComputeHash).
// Each step is skipped when its data is already there, so the tool can be re-run.
// Environment: PERF_CONNECTION, PERF_BASE_URL, PERF_ADMIN_PASSWORD, PERF_USER_PASSWORD (from deploy/.env.perf, never hard-coded).
// Arguments: --users 100 --files 200000 --audit 2000000

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Auditing;
using BlueDrive.Infrastructure.Office;
using BlueDrive.Infrastructure.Persistence;
using BlueDrive.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SkiaSharp;

var userCount = Arg("--users", 100);
var fileCount = Arg("--files", 200_000);
var auditCount = Arg("--audit", 2_000_000);
var connection = Env("PERF_CONNECTION");
var baseUrl = Env("PERF_BASE_URL").TrimEnd('/');
var adminPassword = Env("PERF_ADMIN_PASSWORD");
var userPassword = Env("PERF_USER_PASSWORD");
var rnd = new Random(20261104);
var now = DateTime.UtcNow;
var clock = Stopwatch.StartNew();

await using var dataSource = new NpgsqlDataSourceBuilder(connection).Build();
AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(dataSource).UseSnakeCaseNamingConvention().Options);

// --- 1. Sample contents through the API -------------------------------------------------------------------------------------
await using (var cmd = dataSource.CreateCommand("UPDATE users SET must_change_password = false WHERE username = 'admin'"))
    await cmd.ExecuteNonQueryAsync();
using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromMinutes(5) };
var login = await http.PostAsJsonAsync("api/v1/auth/login", new { username = "admin", password = adminPassword });
if (!login.IsSuccessStatusCode) Fail($"Đăng nhập admin lỗi {(int)login.StatusCode}: {await login.Content.ReadAsStringAsync()}");
var auth = await login.Content.ReadFromJsonAsync<JsonElement>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
var adminId = Guid.Parse(auth.GetProperty("user").GetProperty("id").GetString()!);
var adminWs = Guid.Parse(auth.GetProperty("user").GetProperty("personalWorkspaceId").GetString()!);

var resources = typeof(OnlyOfficeProvider).Assembly;
byte[] Resource(string suffix)
{
    using var s = resources.GetManifestResourceStream(resources.GetManifestResourceNames().First(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
}
var samples = new (string Name, Func<byte[]> Bytes)[]
{
    ("perf-mau.docx", () => Resource("blank.docx")),
    ("perf-mau.xlsx", () => Resource("blank.xlsx")),
    ("perf-mau.pptx", () => Resource("blank.pptx")),
    ("perf-mau.mp4", () => Resource("video-mau.mp4")),
    ("perf-mau.mp3", () => Resource("am-thanh-mau.mp3")),
    ("perf-mau.jpg", () => Poster()),
    ("perf-mau.txt", () => "Báo cáo tiến độ truyền thông dân số - dữ liệu kiểm thử tải.\n"u8.ToArray()),
};
await using (var db = NewDb())
{
    var have = await db.Files.Where(f => f.WorkspaceId == adminWs && f.Name.StartsWith("perf-mau.")).Select(f => f.Name).ToListAsync();
    foreach (var (name, bytes) in samples.Where(s => !have.Contains(s.Name)))
    {
        using var form = new MultipartFormDataContent { { new StringContent(adminWs.ToString()), "workspaceId" } };
        form.Add(new ByteArrayContent(bytes()), "file", name);
        var r = await http.PostAsync("api/v1/files/upload", form);
        if (!r.IsSuccessStatusCode) Fail($"Tải lên {name} lỗi {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }
}
Log("Chờ Worker quét và tạo ảnh thu nhỏ cho tệp mẫu");
List<StoredFile> sampleFiles = [];
for (var i = 0; ; i++)
{
    await using var db = NewDb();
    sampleFiles = await db.Files.AsNoTracking().Include(f => f.Versions).Where(f => f.WorkspaceId == adminWs && f.Name.StartsWith("perf-mau.") && !f.IsDeleted).ToListAsync();
    var thumbsDone = sampleFiles.Where(f => f.MediaType is MediaType.Image or MediaType.Video).All(f => f.ThumbnailKey is not null);
    if (sampleFiles.Count == samples.Length && sampleFiles.All(f => f.ScanStatus is not (ScanStatus.Pending or ScanStatus.Scanning)) && thumbsDone) break;
    if (i >= 90) Fail("Worker chưa xử lý xong tệp mẫu sau 3 phút (xem docker compose -p bluedrive-perf logs worker).");
    await Task.Delay(2000);
}
Log($"Tệp mẫu sẵn sàng: {string.Join(", ", sampleFiles.Select(f => $"{f.Name}={f.ScanStatus}"))}");

// --- 2. Users and workspaces -------------------------------------------------------------------------------------------------
List<User> users;
List<Workspace> groups;
await using (var db = NewDb())
{
    if (!await db.Users.AnyAsync(u => u.Username == "perf001"))
    {
        var orgs = await db.Organizations.Where(o => o.ParentId != null).OrderBy(o => o.SortOrder).Select(o => o.Id).ToListAsync();
        if (orgs.Count == 0) orgs = await db.Organizations.Select(o => o.Id).ToListAsync();
        var roles = await db.Roles.ToDictionaryAsync(r => r.Code, r => r.Id);
        var hash = new PasswordService(null!, null!, TimeProvider.System).Hash(userPassword);
        string[] family = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Vũ", "Đặng", "Bùi", "Đỗ", "Hồ"];
        string[] given = ["Thị Hoa", "Văn Nam", "Thu Hằng", "Minh Đức", "Lan Anh", "Quốc Bảo", "Ngọc Mai", "Hữu Phúc", "Thanh Tâm", "Gia Huy"];
        for (var i = 1; i <= userCount; i++)
        {
            var role = i <= 3 ? "quan_tri" : i <= 13 ? "phe_duyet" : i <= 33 ? "chi_xem" : "bien_tap";
            var u = new User
            {
                Username = $"perf{i:000}", FullName = $"{family[i % family.Length]} {given[(i / family.Length) % given.Length]} {i:000}",
                Position = role switch { "quan_tri" => "Quản trị hệ thống", "phe_duyet" => "Trưởng phòng", "chi_xem" => "Chuyên viên", _ => "Biên tập viên" },
                OrganizationId = orgs[i % orgs.Count], PasswordHash = hash, PasswordChangedAt = now, QuotaBytes = 100L * 1024 * 1024 * 1024,
                CreatedAt = now, CreatedBy = adminId,
            };
            u.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = roles[role] });
            db.Users.Add(u);
            db.PasswordHistories.Add(new PasswordHistory { UserId = u.Id, PasswordHash = hash, CreatedAt = now });
            var ws = new Workspace { Name = $"Tài liệu của {u.FullName}", OwnerId = u.Id, OrganizationId = u.OrganizationId, Type = WorkspaceType.Personal,
                QuotaBytes = u.QuotaBytes, CreatedAt = now, CreatedBy = u.Id };
            ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = u.Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = now });
            db.Workspaces.Add(ws);
        }
        await db.SaveChangesAsync();

        var all = await db.Users.Include(u => u.UserRoles).Where(u => u.Username.StartsWith("perf")).OrderBy(u => u.Username).ToListAsync();
        var roleOf = await db.Roles.ToDictionaryAsync(r => r.Id, r => r.Code);
        string[] topics = ["Chiến dịch Ngày Dân số Việt Nam", "Già hóa dân số", "Mất cân bằng giới tính khi sinh", "Chăm sóc sức khỏe sinh sản",
            "Tư vấn tiền hôn nhân", "Nâng cao chất lượng dân số", "Truyền thông vùng sâu vùng xa", "Bản tin Dân số và Phát triển",
            "Tập huấn cộng tác viên dân số", "Kho ảnh tư liệu"];
        for (var g = 0; g < topics.Length; g++)
        {
            var editors = all.Where(u => roleOf[u.UserRoles[0].RoleId] == "bien_tap").ToList();
            var owner = editors[g * 7 % editors.Count];
            var ws = new Workspace { Name = topics[g], Description = $"Không gian nhóm kiểm thử tải: {topics[g]}", Type = WorkspaceType.Group, OwnerId = owner.Id,
                OrganizationId = owner.OrganizationId, QuotaBytes = 2L * 1024 * 1024 * 1024 * 1024, CreatedAt = now.AddDays(-1000), CreatedBy = owner.Id };
            ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = owner.Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = now });
            // Every user is in about 4 of the 10 groups; the member role follows the user's system role.
            foreach (var u in all.Where((u, idx) => u.Id != owner.Id && (idx + g) % 10 < 4))
            {
                var mr = roleOf[u.UserRoles[0].RoleId] switch
                {
                    "quan_tri" => WorkspaceMemberRole.Manager, "phe_duyet" => WorkspaceMemberRole.Approver, "chi_xem" => WorkspaceMemberRole.Viewer,
                    _ => rnd.Next(5) == 0 ? WorkspaceMemberRole.Uploader : WorkspaceMemberRole.Editor,
                };
                ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = u.Id, MemberRole = mr, AddedAt = now });
            }
            db.Workspaces.Add(ws);
        }
        await db.SaveChangesAsync();
        Log($"Đã tạo {userCount} người dùng và {topics.Length} nhóm tài nguyên");
    }
    users = await db.Users.AsNoTracking().Where(u => u.Username.StartsWith("perf")).OrderBy(u => u.Username).ToListAsync();
    var userIds = users.Select(u => u.Id).ToList();
    groups = await db.Workspaces.AsNoTracking().Include(w => w.Members)
        .Where(w => w.Type == WorkspaceType.Group && w.Description != null && w.Description.StartsWith("Không gian nhóm kiểm thử tải")).ToListAsync();
}

// --- 3. Folders and files ----------------------------------------------------------------------------------------------------
await using (var db = NewDb())
{
    var existing = await db.Files.CountAsync(f => f.Title != null && f.Title.StartsWith("[perf]"));
    if (existing < fileCount / 2)
    {
        var personal = await db.Workspaces.AsNoTracking().Where(w => w.Type == WorkspaceType.Personal && users.Select(u => u.Id).Contains(w.OwnerId)).ToListAsync();
        var catalogItems = await db.CatalogItems.Select(i => i.Id).ToListAsync();
        string[] subjects = ["Kế hoạch truyền thông", "Báo cáo tổng kết", "Áp phích", "Tờ rơi", "Infographic", "Video phóng sự", "Spot phát thanh",
            "Bài trình bày tập huấn", "Bảng theo dõi sản phẩm", "Biên bản họp", "Kịch bản tiểu phẩm", "Ảnh hoạt động", "Bản tin", "Công văn"];
        string[] about = ["dân số và phát triển", "già hóa dân số", "mất cân bằng giới tính khi sinh", "kế hoạch hóa gia đình", "sức khỏe sinh sản vị thành niên",
            "chất lượng dân số", "tầm soát trước sinh", "sàng lọc sơ sinh", "tư vấn tiền hôn nhân", "người cao tuổi"];
        string[] places = ["Hà Nội", "Hải Phòng", "Nghệ An", "Thừa Thiên Huế", "Đà Nẵng", "Gia Lai", "Đắk Lắk", "TP Hồ Chí Minh", "Cần Thơ", "Lào Cai", "Sơn La", "Cà Mau"];
        string[] tagPool = ["dân số", "truyền thông", "chiến dịch", "ấn phẩm", "tập huấn", "báo cáo", "video", "ảnh", "infographic", "kế hoạch", "khẩn", "lưu trữ"];
        string[] folderNames = ["Năm 2024", "Năm 2025", "Năm 2026", "Ấn phẩm in", "Video - Âm thanh", "Hình ảnh", "Văn bản chỉ đạo", "Báo cáo", "Tư liệu gốc", "Bản thảo"];

        // Folder tree: group workspaces 10 x 8 + 10 top folders; personal workspaces 4 folders.
        var targets = new List<(Workspace Ws, Guid? Folder, Guid[] Writers)>();
        var folders = new List<Folder>();
        foreach (var ws in groups)
        {
            var writers = ws.Members.Where(m => m.MemberRole is WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager or WorkspaceMemberRole.Editor or WorkspaceMemberRole.Uploader)
                .Select(m => m.UserId).ToArray();
            for (var t = 0; t < folderNames.Length; t++)
            {
                var top = new Folder { WorkspaceId = ws.Id, Name = folderNames[t], CreatedAt = now.AddDays(-rnd.Next(400, 1000)), CreatedBy = ws.OwnerId };
                top.Path = StorageNames.FolderPath(null, top.Id);
                folders.Add(top);
                targets.Add((ws, top.Id, writers));
                for (var s = 0; s < 8; s++)
                {
                    var sub = new Folder { WorkspaceId = ws.Id, ParentId = top.Id, Name = $"{places[(t + s) % places.Length]} - đợt {s + 1}",
                        CreatedAt = top.CreatedAt.AddDays(rnd.Next(1, 300)), CreatedBy = writers[rnd.Next(writers.Length)] };
                    sub.Path = StorageNames.FolderPath(top, sub.Id);
                    folders.Add(sub);
                    targets.Add((ws, sub.Id, writers));
                }
            }
        }
        var groupTargets = targets.Count;
        foreach (var ws in personal)
        {
            targets.Add((ws, null, [ws.OwnerId]));
            for (var t = 0; t < 4; t++)
            {
                var f = new Folder { WorkspaceId = ws.Id, Name = folderNames[t * 2], CreatedAt = now.AddDays(-rnd.Next(30, 900)), CreatedBy = ws.OwnerId };
                f.Path = StorageNames.FolderPath(null, f.Id);
                folders.Add(f);
                targets.Add((ws, f.Id, [ws.OwnerId]));
            }
        }
        foreach (var chunk in folders.Chunk(2000))
        {
            db.Folders.AddRange(chunk);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        Log($"Đã tạo {folders.Count} thư mục");

        var orgOf = users.ToDictionary(u => u.Id, u => u.OrganizationId);
        for (var done = existing; done < fileCount;)
        {
            var batch = Math.Min(5000, fileCount - done);
            for (var k = 0; k < batch; k++)
            {
                // 65 % of the files live in group workspaces.
                var target = rnd.Next(100) < 65 ? targets[rnd.Next(groupTargets)] : targets[groupTargets + rnd.Next(targets.Count - groupTargets)];
                var sample = sampleFiles[rnd.Next(sampleFiles.Count)];
                var version = sample.Versions.OrderByDescending(v => v.VersionNo).First();
                var owner = target.Writers[rnd.Next(target.Writers.Length)];
                var created = now.AddMinutes(-rnd.Next(60, 3 * 365 * 24 * 60));
                var title = $"{subjects[rnd.Next(subjects.Length)]} {about[rnd.Next(about.Length)]} {places[rnd.Next(places.Length)]} {created:yyyy}";
                var file = new StoredFile
                {
                    WorkspaceId = target.Ws.Id, FolderId = target.Folder, Name = $"{title} - {done + k + 1:000000}{sample.Extension}", Extension = sample.Extension,
                    MimeType = sample.MimeType, MediaType = sample.MediaType, SizeBytes = sample.SizeBytes, ChecksumSha256 = sample.ChecksumSha256,
                    Title = "[perf] " + title, Description = rnd.Next(4) == 0 ? $"Tư liệu {about[rnd.Next(about.Length)]} phục vụ truyền thông tại {places[rnd.Next(places.Length)]}." : null,
                    Tags = [.. Enumerable.Range(0, rnd.Next(4)).Select(_ => tagPool[rnd.Next(tagPool.Length)]).Distinct()],
                    ApprovalStatus = rnd.Next(100) switch { < 94 => ApprovalStatus.Approved, < 97 => ApprovalStatus.Pending, < 99 => ApprovalStatus.Draft, _ => ApprovalStatus.Rejected },
                    ScanStatus = sample.ScanStatus, Width = sample.Width, Height = sample.Height, DurationSeconds = sample.DurationSeconds,
                    ThumbnailKey = sample.ThumbnailKey, PreviewKey = sample.PreviewKey, UploaderOrganizationId = orgOf.GetValueOrDefault(owner),
                    CreatedAt = created, CreatedBy = owner, UpdatedAt = rnd.Next(5) == 0 ? created.AddDays(rnd.Next(1, 60)) : null,
                };
                if (file.UpdatedAt > now) file.UpdatedAt = null;
                if (file.ApprovalStatus == ApprovalStatus.Approved) { file.ApprovedAt = created; file.ApprovedBy = owner; }
                var v = new FileVersion { FileId = file.Id, VersionNo = 1, StorageKey = version.StorageKey, SizeBytes = version.SizeBytes, Checksum = version.Checksum,
                    CreatedBy = owner, CreatedAt = created, ChangeNote = "Dữ liệu kiểm thử tải", IsEncrypted = version.IsEncrypted, WrappedKey = version.WrappedKey };
                file.CurrentVersionId = v.Id;
                file.Versions.Add(v);
                foreach (var c in catalogItems.Count == 0 ? [] : Enumerable.Range(0, rnd.Next(3)).Select(_ => catalogItems[rnd.Next(catalogItems.Count)]).Distinct())
                    file.CatalogItems.Add(new FileCatalogItem { FileId = file.Id, CatalogItemId = c });
                db.Files.Add(file);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            done += batch;
            Log($"Đã tạo {done}/{fileCount} tệp");
        }
        await using (var cmd = dataSource.CreateCommand("""
            UPDATE workspaces w SET used_bytes = s.total FROM (SELECT workspace_id, sum(size_bytes) AS total FROM files WHERE NOT is_deleted GROUP BY workspace_id) s
            WHERE s.workspace_id = w.id;
            UPDATE users u SET used_bytes = w.used_bytes FROM workspaces w WHERE w.owner_id = u.id AND w.type = 'personal';
            ANALYZE;
            """))
            await cmd.ExecuteNonQueryAsync();
    }
    else Log($"Đã có {existing} tệp kiểm thử: bỏ qua bước tạo tệp");
}

// --- 4. Shares ---------------------------------------------------------------------------------------------------------------
await using (var db = NewDb())
{
    if (!await db.Shares.AnyAsync(s => s.Message == "[perf]"))
    {
        var personalIds = await db.Workspaces.Where(w => w.Type == WorkspaceType.Personal && users.Select(u => u.Id).Contains(w.OwnerId)).Select(w => w.Id).ToListAsync();
        var shareable = await db.Files.AsNoTracking().Where(f => personalIds.Contains(f.WorkspaceId)).OrderBy(f => f.Id).Take(3000)
            .Select(f => new { f.Id, f.CreatedBy }).ToListAsync();
        foreach (var f in shareable)
        {
            var to = users[rnd.Next(users.Count)].Id;
            if (to == f.CreatedBy) continue;
            db.Shares.Add(new Share { ResourceType = ResourceType.File, ResourceId = f.Id, SharedBy = f.CreatedBy!.Value, SharedWithType = PrincipalType.User, SharedWithId = to,
                AccessLevel = (AccessLevel)rnd.Next(1, 4), Message = "[perf]", CreatedAt = now.AddDays(-rnd.Next(1, 300)) });
        }
        await db.SaveChangesAsync();
        Log($"Đã tạo {db.ChangeTracker.Entries<Share>().Count()} lượt chia sẻ");
    }
}

// --- 5. Audit log: extend the hash chain -------------------------------------------------------------------------------------
long auditHave;
await using (var cmd = dataSource.CreateCommand("SELECT count(*) FROM audit_logs"))
    auditHave = (long)(await cmd.ExecuteScalarAsync())!;
if (auditHave < auditCount)
{
    var toAdd = auditCount - auditHave;
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
    {
        lockCmd.Parameters.AddWithValue("k", 0x426C7565_41756469L);
        await lockCmd.ExecuteNonQueryAsync();
    }
    string? prev;
    await using (var prevCmd = new NpgsqlCommand("SELECT hash FROM audit_logs ORDER BY id DESC LIMIT 1", conn, tx))
        prev = await prevCmd.ExecuteScalarAsync() as string;
    List<(Guid Id, string Name, Guid? Ws)> resources2;
    await using (var db = NewDb())
        resources2 = (await db.Files.AsNoTracking().OrderBy(f => f.Id).Take(20_000).Select(f => new { f.Id, f.Name, f.WorkspaceId }).ToListAsync())
            .Select(f => (f.Id, f.Name, (Guid?)f.WorkspaceId)).ToList();
    (AuditAction Action, int Weight)[] mix = [(AuditAction.View, 40), (AuditAction.Download, 20), (AuditAction.Login, 12), (AuditAction.Upload, 8), (AuditAction.Edit, 5),
        (AuditAction.Rename, 3), (AuditAction.Move, 3), (AuditAction.Share, 3), (AuditAction.Delete, 2), (AuditAction.Logout, 2), (AuditAction.LoginFailed, 1), (AuditAction.Comment, 1)];
    var bag = mix.SelectMany(m => Enumerable.Repeat(m.Action, m.Weight)).ToArray();
    var start = now.AddDays(-365);
    var step = (now.AddMinutes(-5) - start).Ticks / toAdd;
    await using (var writer = await conn.BeginBinaryImportAsync("""
        COPY audit_logs (user_id, username, organization_id, action, resource_type, resource_id, resource_name, workspace_id, ip, user_agent, detail, occurred_at, prev_hash, hash)
        FROM STDIN (FORMAT BINARY)
        """))
    {
        for (long i = 0; i < toAdd; i++)
        {
            var u = users[rnd.Next(users.Count)];
            var action = bag[rnd.Next(bag.Length)];
            var isFile = action is not (AuditAction.Login or AuditAction.Logout or AuditAction.LoginFailed);
            var res = resources2[rnd.Next(resources2.Count)];
            var log = new AuditLog
            {
                UserId = u.Id, Username = u.Username, OrganizationId = u.OrganizationId, Action = action,
                ResourceType = isFile ? ResourceType.File : null, ResourceId = isFile ? res.Id : null, ResourceName = isFile ? res.Name : null, WorkspaceId = isFile ? res.Ws : null,
                Ip = $"10.{rnd.Next(1, 30)}.{rnd.Next(256)}.{rnd.Next(1, 255)}", UserAgent = "Mozilla/5.0 (BlueDrive kiểm thử tải)",
                Detail = rnd.Next(3) == 0 ? """{"device":"web"}""" : null,
                OccurredAt = new DateTime(start.Ticks + step * i - (start.Ticks + step * i) % 10, DateTimeKind.Utc), PrevHash = prev,
            };
            log.Hash = AuditLogger.ComputeHash(log);
            prev = log.Hash;
            await writer.StartRowAsync();
            await writer.WriteAsync(u.Id, NpgsqlDbType.Uuid);
            await writer.WriteAsync(log.Username, NpgsqlDbType.Text);
            if (log.OrganizationId is { } org) await writer.WriteAsync(org, NpgsqlDbType.Uuid); else await writer.WriteNullAsync();
            await writer.WriteAsync(EnumNames.ToSnake(log.Action), NpgsqlDbType.Text);
            if (log.ResourceType is { } rt) await writer.WriteAsync(EnumNames.ToSnake(rt), NpgsqlDbType.Text); else await writer.WriteNullAsync();
            if (log.ResourceId is { } rid) await writer.WriteAsync(rid, NpgsqlDbType.Uuid); else await writer.WriteNullAsync();
            if (log.ResourceName is { } rn) await writer.WriteAsync(rn, NpgsqlDbType.Text); else await writer.WriteNullAsync();
            if (log.WorkspaceId is { } wid) await writer.WriteAsync(wid, NpgsqlDbType.Uuid); else await writer.WriteNullAsync();
            await writer.WriteAsync(log.Ip, NpgsqlDbType.Text);
            await writer.WriteAsync(log.UserAgent, NpgsqlDbType.Text);
            if (log.Detail is { } d) await writer.WriteAsync(d, NpgsqlDbType.Jsonb); else await writer.WriteNullAsync();
            await writer.WriteAsync(log.OccurredAt, NpgsqlDbType.TimestampTz);
            if (log.PrevHash is { } ph) await writer.WriteAsync(ph, NpgsqlDbType.Text); else await writer.WriteNullAsync();
            await writer.WriteAsync(log.Hash, NpgsqlDbType.Text);
            if ((i + 1) % 250_000 == 0) Log($"Nhật ký: {i + 1}/{toAdd} dòng");
        }
        await writer.CompleteAsync();
    }
    await tx.CommitAsync();
    await using (var cmd = dataSource.CreateCommand("ANALYZE audit_logs"))
        await cmd.ExecuteNonQueryAsync();
    Log($"Đã thêm {toAdd} dòng nhật ký nối tiếp chuỗi băm");
}
else Log($"Đã có {auditHave} dòng nhật ký: bỏ qua");

Log("Hoàn tất nạp dữ liệu kiểm thử tải");
return 0;

int Arg(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
    : throw new InvalidOperationException($"Thiếu biến môi trường {name} (scripts/perf-up.ps1 đặt từ deploy/.env.perf).");

void Log(string message) => Console.WriteLine($"[{clock.Elapsed:mm\\:ss}] {message}");

static void Fail(string message) => throw new InvalidOperationException(message);

static byte[] Poster()
{
    using var bmp = new SKBitmap(1280, 720);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(new SKColor(11, 83, 148));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(640, 360, 200, paint);
    }
    using var image = SKImage.FromBitmap(bmp);
    return image.Encode(SKEncodedImageFormat.Jpeg, 85).ToArray();
}
