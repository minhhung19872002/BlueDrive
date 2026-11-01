using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Operations;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Domain.Entities;
using BlueDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlueDrive.Infrastructure.Operations;

/// <summary>
/// ZIP hand-over package: files/ (current versions, decrypted, original folder tree per workspace), versions/ (older versions),
/// metadata.json (everything), metadata.csv (one row per file), audit-logs.csv and README.md.
/// </summary>
public sealed class DataExporter(AppDbContext db, IFileContentService content, ISensitiveDataProtector pii, IOptions<BackupOptions> options) : IDataExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private string Root => Path.GetFullPath(Path.Combine(options.Value.Root, "exports"));

    public Stream Open(string key)
    {
        var full = Path.GetFullPath(Path.Combine(Root, key));
        if (!full.StartsWith(Root, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
    }

    public async Task<(string Key, string FileName, long Size)> ExportAsync(ReportJob job, Func<int, Task> progress, CancellationToken ct)
    {
        using var opts = JsonDocument.Parse(job.ParametersJson);
        var includeVersions = opts.RootElement.TryGetProperty("includeVersions", out var iv) && iv.GetBoolean();
        var includeAudit = !opts.RootElement.TryGetProperty("includeAuditLogs", out var ia) || ia.GetBoolean();
        Directory.CreateDirectory(Root);
        var fileName = $"bluedrive-du-lieu-ban-giao-{DateTime.UtcNow.AddHours(7):yyyyMMdd-HHmm}.zip";
        var path = Path.Combine(Root, fileName);

        var orgs = await db.Organizations.AsNoTracking().OrderBy(o => o.Path).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role).ToListAsync(ct);
        var groups = await db.UserGroups.AsNoTracking().Include(g => g.Members).ToListAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking().Include(w => w.Members).ToListAsync(ct);
        var folders = await db.Folders.AsNoTracking().Where(f => !f.IsDeleted).ToListAsync(ct);
        var files = await db.Files.AsNoTracking().Include(f => f.CatalogItems).Where(f => !f.IsDeleted).ToListAsync(ct);
        var versions = await db.FileVersions.AsNoTracking().ToListAsync(ct);
        var catalogs = await db.Catalogs.AsNoTracking().Include(c => c.Items).ToListAsync(ct);
        var acl = await db.ResourcePermissions.AsNoTracking().ToListAsync(ct);
        var shares = await db.Shares.AsNoTracking().ToListAsync(ct);
        var comments = await db.Comments.AsNoTracking().Where(c => !c.IsDeleted).ToListAsync(ct);
        var userNames = users.ToDictionary(u => u.Id, u => u.Username);
        var folderById = folders.ToDictionary(f => f.Id);
        var wsName = workspaces.ToDictionary(w => w.Id, w => Safe(w.Type == Domain.Enums.WorkspaceType.Personal ? $"Ca nhan - {userNames.GetValueOrDefault(w.OwnerId, w.OwnerId.ToString())}" : w.Name));

        string FolderPath(Guid? folderId)
        {
            var parts = new List<string>();
            for (var id = folderId; id is { } x && folderById.TryGetValue(x, out var f); id = f.ParentId) parts.Insert(0, Safe(f.Name));
            return string.Join('/', parts);
        }

        var exportPaths = new Dictionary<Guid, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var dir = $"files/{wsName.GetValueOrDefault(f.WorkspaceId, f.WorkspaceId.ToString())}/{FolderPath(f.FolderId)}".TrimEnd('/');
            var p = $"{dir}/{Safe(f.Name)}";
            for (var i = 1; !used.Add(p); i++) p = $"{dir}/{Path.GetFileNameWithoutExtension(Safe(f.Name))} ({i}){Path.GetExtension(f.Name)}";
            exportPaths[f.Id] = p;
        }

        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
        {
            var done = 0;
            foreach (var f in files)
            {
                var v = versions.FirstOrDefault(x => x.Id == f.CurrentVersionId);
                if (v is null) continue;
                await CopyAsync(zip, exportPaths[f.Id], v, f.UpdatedAt ?? f.CreatedAt, ct);
                if (includeVersions)
                    foreach (var old in versions.Where(x => x.FileId == f.Id && x.Id != f.CurrentVersionId))
                        await CopyAsync(zip, $"versions/{f.Id}/v{old.VersionNo}-{Safe(f.Name)}", old, old.CreatedAt, ct);
                if (++done % 50 == 0) await progress(10 + (int)(70.0 * done / Math.Max(1, files.Count)));
            }

            var metadata = new
            {
                format = "bluedrive-export/1.0",
                exportedAt = DateTime.UtcNow,
                appVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                organizations = orgs.Select(o => new { o.Id, o.ParentId, o.Code, o.Name, type = o.Type.ToString(), o.Address, o.Phone, o.Email, o.IsActive }),
                users = users.Select(u => new { u.Id, u.Username, u.FullName, email = pii.Unprotect(u.Email), phone = pii.Unprotect(u.Phone), u.OrganizationId, u.Position, u.IsActive, u.QuotaBytes, u.UsedBytes, roles = u.UserRoles.Select(r => r.Role!.Code) }),
                userGroups = groups.Select(g => new { g.Id, g.Name, g.Description, members = g.Members.Select(m => m.UserId) }),
                catalogs = catalogs.Select(c => new { c.Id, c.Code, c.Name, items = c.Items.Select(i => new { i.Id, i.ParentId, i.Code, i.Name, i.IsActive }) }),
                workspaces = workspaces.Select(w => new { w.Id, w.Name, type = w.Type.ToString(), w.OrganizationId, w.OwnerId, w.QuotaBytes, w.UsedBytes, w.IsLocked, members = w.Members.Select(m => new { m.UserId, role = m.MemberRole.ToString() }) }),
                folders = folders.Select(f => new { f.Id, f.WorkspaceId, f.ParentId, f.Name, path = FolderPath(f.Id), f.RequiresApproval, f.BreakInheritance, f.CreatedAt, f.CreatedBy }),
                files = files.Select(f => new
                {
                    f.Id, f.WorkspaceId, f.FolderId, f.Name, exportPath = exportPaths[f.Id], f.Extension, f.MimeType, mediaType = f.MediaType.ToString(), f.SizeBytes, f.ChecksumSha256,
                    f.Title, f.Description, f.Tags, approval = f.ApprovalStatus.ToString(), f.UploaderOrganizationId, f.CreatedAt, f.CreatedBy, f.UpdatedAt, f.UpdatedBy,
                    catalogItems = f.CatalogItems.Select(c => c.CatalogItemId),
                    versions = versions.Where(v => v.FileId == f.Id).OrderBy(v => v.VersionNo).Select(v => new { v.VersionNo, v.SizeBytes, v.Checksum, v.ChangeNote, v.CreatedAt, v.CreatedBy, current = v.Id == f.CurrentVersionId }),
                }),
                permissions = acl.Select(a => new { resourceType = a.ResourceType.ToString(), a.ResourceId, principalType = a.PrincipalType.ToString(), a.PrincipalId, level = a.AccessLevel.ToString(), a.Inherit, a.ExpiresAt }),
                shares = shares.Select(s => new { resourceType = s.ResourceType.ToString(), s.ResourceId, s.SharedBy, sharedWithType = s.SharedWithType.ToString(), s.SharedWithId, level = s.AccessLevel.ToString(), s.Message, s.CreatedAt }),
                comments = comments.Select(c => new { c.Id, c.FileId, c.ParentId, c.Content, c.CreatedBy, c.CreatedAt, c.AttachmentFileIds }),
            };
            await WriteTextAsync(zip, "metadata.json", JsonSerializer.Serialize(metadata, Json), ct);
            await WriteTextAsync(zip, "metadata.csv", Csv(["id", "duong_dan", "ten", "tieu_de", "mo_ta", "the", "loai", "dinh_dang", "dung_luong_byte", "sha256", "nguoi_tao", "ngay_tao", "ngay_sua", "phien_ban"],
                files.Select(f => new[]
                {
                    f.Id.ToString(), exportPaths[f.Id], f.Name, f.Title ?? "", f.Description ?? "", string.Join(";", f.Tags), f.MediaType.ToString(), f.Extension, f.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    f.ChecksumSha256 ?? "", f.CreatedBy is { } c ? userNames.GetValueOrDefault(c, "") : "", f.CreatedAt.ToString("o"), (f.UpdatedAt ?? f.CreatedAt).ToString("o"),
                    versions.Count(v => v.FileId == f.Id).ToString(),
                })), ct);
            if (includeAudit)
            {
                // Streamed row by row: the audit trail can be very large.
                await using var entry = zip.CreateEntry("audit-logs.csv").Open();
                await using var writer = new StreamWriter(entry, new UTF8Encoding(true));
                await writer.WriteLineAsync("id,thoi_gian_utc,nguoi_dung,hanh_dong,loai_tai_nguyen,ma_tai_nguyen,ten_tai_nguyen,ip,hash");
                await foreach (var a in db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
                    await writer.WriteLineAsync(string.Join(',', new[] { a.Id.ToString(), a.OccurredAt.ToString("o"), a.Username ?? "", a.Action.ToString(), a.ResourceType?.ToString() ?? "", a.ResourceId?.ToString() ?? "", a.ResourceName ?? "", a.Ip ?? "", a.Hash }.Select(CsvCell)));
            }
            await WriteTextAsync(zip, "README.md", Readme(files.Count, includeVersions, includeAudit), ct);
        }
        return (fileName, fileName, new FileInfo(path).Length);
    }

    private async Task CopyAsync(ZipArchive zip, string entryPath, FileVersion v, DateTime modified, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
        entry.LastWriteTime = modified.AddHours(7);
        await using var src = await content.OpenReadAsync(v, ct);
        await using var dst = entry.Open();
        await src.CopyToAsync(dst, 1 << 16, ct);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var s = zip.CreateEntry(name).Open();
        // BOM so Excel opens the UTF-8 CSV correctly.
        await s.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
        await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    private static string Csv(string[] header, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header));
        foreach (var r in rows) sb.AppendLine(string.Join(',', r.Select(CsvCell)));
        return sb.ToString();
    }

    private static string CsvCell(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    private static string Readme(int files, bool versions, bool audit) => $"""
        # Gói dữ liệu bàn giao BlueDrive

        Thời điểm xuất (giờ Việt Nam): {DateTime.UtcNow.AddHours(7):dd/MM/yyyy HH:mm}
        Số tệp: {files}

        ## Cấu trúc
        - `files/<Không gian lưu trữ>/<cây thư mục>/<tệp>`: bản hiện tại của mọi tệp, đã giải mã, giữ nguyên tên và cây thư mục gốc.
          Không gian cá nhân có tên dạng `Ca nhan - <tên đăng nhập>`.
        {(versions ? "- `versions/<mã tệp>/v<số>-<tên>`: các phiên bản cũ của tệp.\n" : "")}- `metadata.json`: toàn bộ dữ liệu thuộc tính (đơn vị, người dùng, nhóm, danh mục, không gian, thư mục, tệp + phiên bản,
          phân quyền, chia sẻ, bình luận). Trường `exportPath` của mỗi tệp trỏ tới vị trí trong thư mục `files/`.
        - `metadata.csv`: danh sách tệp dạng bảng (UTF-8, mở được bằng Excel).
        {(audit ? "- `audit-logs.csv`: nhật ký hoạt động đầy đủ kèm mã băm chuỗi (`hash`) để kiểm tra toàn vẹn.\n" : "")}
        Định dạng chi tiết: xem tài liệu `docs/data-export-format.md` trong bộ mã nguồn hệ thống.
        """;
}
