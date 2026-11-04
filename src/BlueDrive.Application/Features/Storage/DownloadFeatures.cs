using System.Text.Json;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

/// <summary>Short-lived signed payload behind /api/v1/content/{token} (works for &lt;video src&gt;, downloads, ONLYOFFICE).</summary>
public sealed record ContentTicket(Guid UserId, string Kind, Guid? FileId, Guid? VersionId, bool Inline, IReadOnlyList<ItemRef>? Items, string? ZipName);

public interface IContentTicketService
{
    string Issue(ContentTicket ticket, TimeSpan lifetime);
    ContentTicket? Read(string token);
}

public sealed class ContentTicketService(IDataProtectionProvider provider) : IContentTicketService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("BlueDrive.ContentTicket.v1").ToTimeLimitedDataProtector();

    public string Issue(ContentTicket ticket, TimeSpan lifetime) => _protector.Protect(JsonSerializer.Serialize(ticket, Json), lifetime);

    public ContentTicket? Read(string token)
    {
        try { return JsonSerializer.Deserialize<ContentTicket>(_protector.Unprotect(token), Json); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException) { return null; }
    }
}

public sealed record DownloadUrlDto(string Url, DateTime ExpiresAt, string FileName, string MimeType, long SizeBytes);

/// <summary>#23/#45: issue a signed URL for viewing inline (needs View) or downloading (needs Download). Audits view/download.</summary>
public sealed record GetDownloadUrlQuery(Guid FileId, Guid? VersionId, bool Inline) : IRequest<DownloadUrlDto>;

public sealed class GetDownloadUrlHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<GetDownloadUrlQuery, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetDownloadUrlQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        await auth.EnsureAsync(uid, ResourceType.File, q.FileId, q.Inline ? AccessLevel.View : AccessLevel.Download, ct);
        var f = await db.Files.AsNoTracking().FirstAsync(x => x.Id == q.FileId, ct);
        var versionId = q.VersionId ?? f.CurrentVersionId!.Value;
        await ScanGate.EnsureAsync(db, f, versionId, uid, ct);
        var v = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.FileId == f.Id, ct) ?? throw new NotFoundException("Không tìm thấy phiên bản.");
        var lifetime = TimeSpan.FromHours(q.Inline ? 4 : 1);
        var token = tickets.Issue(new ContentTicket(uid, "file", f.Id, v.Id, q.Inline, null, null), lifetime);
        var now = clock.GetUtcNow().UtcDateTime;
        await RecentTracker.TouchAsync(db, uid, f.Id, q.Inline ? "view" : "download", now, ct);
        await audit.LogAsync(new AuditEntry(q.Inline ? AuditAction.View : AuditAction.Download, ResourceType.File, f.Id, f.Name, f.WorkspaceId,
            new { version = v.VersionNo, size = v.SizeBytes }), ct);
        return new DownloadUrlDto($"/api/v1/content/{Uri.EscapeDataString(token)}", now.Add(lifetime), f.Name, f.MimeType, v.SizeBytes);
    }
}

/// <summary>#23: several files/folders → one streamed zip.</summary>
public sealed record GetZipUrlCommand(IReadOnlyList<ItemRef> Items) : IRequest<DownloadUrlDto>;

public sealed class GetZipUrlHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IContentTicketService tickets, IAuditLogger audit, TimeProvider clock)
    : IRequestHandler<GetZipUrlCommand, DownloadUrlDto>
{
    public async Task<DownloadUrlDto> Handle(GetZipUrlCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (cmd.Items.Count == 0) throw new AppException("Vui lòng chọn mục cần tải xuống.");
        string? firstName = null;
        foreach (var i in cmd.Items)
        {
            await auth.EnsureAsync(uid, i.IsFile ? ResourceType.File : ResourceType.Folder, i.Id, AccessLevel.Download, ct);
            firstName ??= i.IsFile ? await db.Files.Where(f => f.Id == i.Id).Select(f => f.Name).FirstAsync(ct) : await db.Folders.Where(f => f.Id == i.Id).Select(f => f.Name).FirstAsync(ct);
        }
        var zipName = cmd.Items.Count == 1 && !cmd.Items[0].IsFile ? $"{firstName}.zip" : $"BlueDrive_{clock.GetUtcNow().UtcDateTime.AddHours(7):yyyyMMdd_HHmm}.zip";
        var token = tickets.Issue(new ContentTicket(uid, "zip", null, null, false, cmd.Items, zipName), TimeSpan.FromMinutes(30));
        foreach (var i in cmd.Items)
            await audit.LogAsync(new AuditEntry(AuditAction.Download, i.IsFile ? ResourceType.File : ResourceType.Folder, i.Id, i.IsFile ? null : firstName, Detail: new { zip = zipName }), ct);
        return new DownloadUrlDto($"/api/v1/content/{Uri.EscapeDataString(token)}", clock.GetUtcNow().UtcDateTime.AddMinutes(30), zipName, "application/zip", 0);
    }
}

public sealed record ZipEntrySource(string EntryPath, Guid VersionId, DateTime ModifiedAt);

/// <summary>Resolves zip entries (re-checking access at download time) for the content endpoint.</summary>
public sealed record ResolveZipEntriesQuery(Guid UserId, IReadOnlyList<ItemRef> Items) : IRequest<IReadOnlyList<ZipEntrySource>>;

public sealed class ResolveZipEntriesHandler(IAppDbContext db, IResourceAuthorizationService auth) : IRequestHandler<ResolveZipEntriesQuery, IReadOnlyList<ZipEntrySource>>
{
    public async Task<IReadOnlyList<ZipEntrySource>> Handle(ResolveZipEntriesQuery q, CancellationToken ct)
    {
        var list = new List<ZipEntrySource>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string path)
        {
            var p = path;
            for (var i = 1; !used.Add(p); i++) p = Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}").Replace('\\', '/');
            return p;
        }
        foreach (var item in q.Items)
        {
            if (item.IsFile)
            {
                if (!await auth.CanAsync(q.UserId, ResourceType.File, item.Id, AccessLevel.Download, ct)) continue;
                var f = await db.Files.AsNoTracking().FirstAsync(x => x.Id == item.Id, ct);
                if (await ScanGate.BlockReasonAsync(db, f, null, q.UserId, ct) is not null) continue;
                list.Add(new ZipEntrySource(Unique(f.Name), f.CurrentVersionId!.Value, f.UpdatedAt ?? f.CreatedAt));
                continue;
            }
            if (!await auth.CanAsync(q.UserId, ResourceType.Folder, item.Id, AccessLevel.Download, ct)) continue;
            var root = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == item.Id, ct);
            var folders = await db.Folders.AsNoTracking().Where(x => x.Path.StartsWith(root.Path) && !x.IsDeleted).ToDictionaryAsync(x => x.Id, ct);
            string RelPath(Guid folderId)
            {
                var parts = new List<string>();
                for (Guid? cur = folderId; cur is { } c && folders.TryGetValue(c, out var fo); cur = fo.Id == root.Id ? null : fo.ParentId) parts.Insert(0, fo.Name);
                return string.Join('/', parts);
            }
            var folderIds = folders.Keys.ToList();
            var files = await db.Files.AsNoTracking().Where(x => x.FolderId != null && folderIds.Contains(x.FolderId.Value) && !x.IsDeleted).ToListAsync(ct);
            foreach (var f in files)
            {
                // Quarantined files and files still waiting for their scan (uploaded by someone else) stay out of the zip.
                if (!await auth.CanAsync(q.UserId, ResourceType.File, f.Id, AccessLevel.Download, ct) || await ScanGate.BlockReasonAsync(db, f, null, q.UserId, ct) is not null) continue;
                list.Add(new ZipEntrySource(Unique($"{RelPath(f.FolderId!.Value)}/{f.Name}"), f.CurrentVersionId!.Value, f.UpdatedAt ?? f.CreatedAt));
            }
            if (files.Count == 0) used.Add(root.Name + "/");
        }
        return list;
    }
}
