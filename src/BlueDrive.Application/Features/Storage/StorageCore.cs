using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Settings;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Storage;

/// <summary>Quota accounting. Usage counts every stored version (trash included) and is released only on purge.</summary>
public sealed class QuotaService(IAppDbContext db, ISettingsService settings, IAlertService alerts, IUserNotifier notifier)
{
    public async Task EnsureAvailableAsync(Guid workspaceId, long additionalBytes, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.QuotaBytes, w.UsedBytes, w.Name, w.Type }).FirstOrDefaultAsync(ct)
                 ?? throw new NotFoundException("Không tìm thấy nhóm tài nguyên.");
        if (ws.QuotaBytes > 0 && ws.UsedBytes + additionalBytes > ws.QuotaBytes)
        {
            var free = Math.Max(0, ws.QuotaBytes - ws.UsedBytes);
            throw new AppException(
                $"Không đủ dung lượng: {(ws.Type == WorkspaceType.Personal ? "dung lượng cá nhân" : $"nhóm \"{ws.Name}\"")} còn trống {TextUtil.FormatBytes(free)}, " +
                $"cần {TextUtil.FormatBytes(additionalBytes)}. Vui lòng dọn thùng rác hoặc liên hệ quản trị viên để tăng hạn mức.", 413);
        }
        // Spec 8.2: the system's storage above every Space and Drive.
        var tenant = await TenantQuotaBytesAsync(ct);
        if (tenant > 0)
        {
            var used = await db.Workspaces.SumAsync(w => w.UsedBytes, ct);
            if (used + additionalBytes > tenant)
                throw new AppException($"Hệ thống đã dùng hết dung lượng chung ({TextUtil.FormatBytes(used)}/{TextUtil.FormatBytes(tenant)}). Vui lòng liên hệ quản trị viên.", 413);
        }
    }

    private async Task<long> TenantQuotaBytesAsync(CancellationToken ct) => (long)(await settings.GetAsync(SettingKeys.QuotaTenantGb, 0.0, ct) * 1024L * 1024 * 1024);

    /// <summary>Spec 8.2: full — no uploads, no new versions (viewing and downloading stay).</summary>
    public async Task<bool> IsFullAsync(Guid workspaceId, CancellationToken ct)
    {
        try
        {
            await EnsureAvailableAsync(workspaceId, 1, ct);
            return false;
        }
        catch (AppException e) when (e.StatusCode == 413) { return true; }
    }

    /// <summary>The thresholds told about (the first one is set by the administrator, 80 % by default).</summary>
    private async Task<int[]> ThresholdsAsync(CancellationToken ct)
    {
        var first = await settings.GetAsync(SettingKeys.QuotaWarningPercent, 80, ct);
        return new[] { 100, 90, first }.Where(t => t is > 0 and <= 100).Distinct().OrderDescending().ToArray();
    }

    private static int? Crossed(int[] thresholds, long before, long after, long quota) =>
        thresholds.Where(t => before * 100 < t * quota && after * 100 >= t * quota).Select(t => (int?)t).FirstOrDefault();

    /// <summary>Atomically adjusts usage of the workspace (and of the owner for personal workspaces), then checks thresholds.</summary>
    public async Task AddUsageAsync(Guid workspaceId, long delta, CancellationToken ct)
    {
        if (delta == 0) return;
        await db.Workspaces.Where(w => w.Id == workspaceId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.UsedBytes, w => w.UsedBytes + delta < 0 ? 0 : w.UsedBytes + delta), ct);
        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.Type, w.OwnerId, w.UsedBytes, w.QuotaBytes, w.Name }).FirstAsync(ct);
        if (ws.Type == WorkspaceType.Personal)
            await db.Users.Where(u => u.Id == ws.OwnerId).ExecuteUpdateAsync(s => s.SetProperty(u => u.UsedBytes, ws.UsedBytes), ct);
        if (delta <= 0) return;
        var thresholds = await ThresholdsAsync(ct);
        if (ws.QuotaBytes > 0 && Crossed(thresholds, ws.UsedBytes - delta, ws.UsedBytes, ws.QuotaBytes) is { } hit)
        {
            var personal = ws.Type == WorkspaceType.Personal;
            var label = personal ? $"Dung lượng cá nhân \"{ws.Name}\"" : $"Space \"{ws.Name}\"";
            var text = $"{label} đã dùng {(double)ws.UsedBytes / ws.QuotaBytes * 100:0.#}% hạn mức ({TextUtil.FormatBytes(ws.UsedBytes)}/{TextUtil.FormatBytes(ws.QuotaBytes)})."
                       + (hit >= 100 ? " Không tải lên và tạo phiên bản mới được nữa (vẫn xem, tải xuống được)." : "");
            await alerts.RaiseAsync(hit >= 100 ? AlertType.QuotaExceeded : AlertType.QuotaWarning, hit >= 100 ? AlertSeverity.Critical : AlertSeverity.Warning,
                text, personal ? ws.OwnerId : null, workspaceId, ct: ct);
            // The owner of the Drive / of the Space is told too, with where to free space.
            await notifier.NotifyUserAsync(ws.OwnerId, "quota", hit >= 100 ? "Đã hết dung lượng" : $"Dung lượng đã dùng {hit}%", text,
                personal ? "/storage" : $"/storage?workspaceId={workspaceId}", ct);
        }
        var tenant = await TenantQuotaBytesAsync(ct);
        if (tenant > 0)
        {
            var used = await db.Workspaces.SumAsync(w => w.UsedBytes, ct);
            if (Crossed(thresholds, used - delta, used, tenant) is { } t)
                await alerts.RaiseAsync(t >= 100 ? AlertType.QuotaExceeded : AlertType.QuotaWarning, t >= 100 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"Toàn hệ thống đã dùng {(double)used / tenant * 100:0.#}% dung lượng ({TextUtil.FormatBytes(used)}/{TextUtil.FormatBytes(tenant)}).", ct: ct);
        }
    }
}

public static class StorageNames
{
    /// <summary>Returns a name unique among non-deleted siblings: "Báo cáo.docx" → "Báo cáo (1).docx".</summary>
    public static async Task<string> UniqueFileNameAsync(IAppDbContext db, Guid workspaceId, Guid? folderId, string name, Guid? exceptId, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var existing = (await db.Files.Where(f => f.WorkspaceId == workspaceId && f.FolderId == folderId && !f.IsDeleted && f.Id != exceptId && f.Name.StartsWith(baseName))
            .Select(f => f.Name).ToListAsync(ct)).Select(n => n.ToLowerInvariant()).ToHashSet();
        return Next(name, baseName, ext, existing);
    }

    public static async Task<string> UniqueFolderNameAsync(IAppDbContext db, Guid workspaceId, Guid? parentId, string name, Guid? exceptId, CancellationToken ct)
    {
        var existing = (await db.Folders.Where(f => f.WorkspaceId == workspaceId && f.ParentId == parentId && !f.IsDeleted && f.Id != exceptId && f.Name.StartsWith(name))
            .Select(f => f.Name).ToListAsync(ct)).Select(n => n.ToLowerInvariant()).ToHashSet();
        return Next(name, name, "", existing);
    }

    private static string Next(string name, string baseName, string ext, HashSet<string> existing)
    {
        if (!existing.Contains(name.ToLowerInvariant())) return name;
        for (var i = 1; ; i++)
        {
            var candidate = $"{baseName} ({i}){ext}";
            if (!existing.Contains(candidate.ToLowerInvariant())) return candidate;
        }
    }

    public static string FolderPath(Folder? parent, Guid id) => $"{parent?.Path ?? "/"}{id}/";
}

/// <summary>Upserts "recent" entries (#18 "Gần đây").</summary>
public static class RecentTracker
{
    public static async Task TouchAsync(IAppDbContext db, Guid userId, Guid fileId, string action, DateTime now, CancellationToken ct)
    {
        var updated = await db.RecentItems.Where(r => r.UserId == userId && r.ItemId == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AccessedAt, now).SetProperty(r => r.Action, action), ct);
        if (updated == 0)
        {
            db.RecentItems.Add(new RecentItem { UserId = userId, ItemId = fileId, Action = action, AccessedAt = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* concurrent insert of the same pair: the other one won */ }
        }
    }
}

public static class MediaTypeNames
{
    public static string Of(MediaType t) => t switch
    {
        MediaType.Image => "Hình ảnh", MediaType.Video => "Video", MediaType.Audio => "Âm thanh", MediaType.Document => "Tài liệu",
        MediaType.Spreadsheet => "Bảng tính", MediaType.Presentation => "Trình chiếu", MediaType.Pdf => "PDF", MediaType.Infographic => "Infographic",
        MediaType.Epub => "Ấn phẩm điện tử", MediaType.Archive => "Tệp nén", MediaType.Design => "Thiết kế", _ => "Khác",
    };
}
