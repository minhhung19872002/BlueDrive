using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Settings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlueDrive.Application.Features.Sync;

/// <summary>
/// Desktop sync policy (system_settings "sync.*"), sent to the client and enforced by the server. The client also gets the upload rules
/// (allowed extensions, maximum size) to refuse a file before sending it, and the web address used by "Mở trên web" links.
/// </summary>
public sealed record SyncPolicyDto(
    bool Enabled, long MaxFileSizeBytes, IReadOnlyList<string> BlockedExtensions, IReadOnlyList<Guid> OnlineOnlyWorkspaceIds,
    string MinClientVersion, bool ViewOnlyAsLink, int PollSeconds, int JournalRetentionDays,
    IReadOnlyList<string>? UploadAllowedExtensions = null, long UploadMaxSizeBytes = 0, string? WebBaseUrl = null);

public sealed class SyncPolicyService(IAppDbContext db, ISettingsService settings, IConfiguration config)
{
    public async Task<SyncPolicyDto> GetAsync(CancellationToken ct)
    {
        var maxMb = await settings.GetAsync(SettingKeys.SyncMaxFileSizeMb, 0, ct);
        if (maxMb <= 0) maxMb = await settings.GetAsync(SettingKeys.UploadMaxSizeMb, 5120, ct);
        var onlineOnlyNames = await settings.GetAsync(SettingKeys.SyncOnlineOnlyWorkspaces, Array.Empty<string>(), ct);
        var lowered = onlineOnlyNames.Select(n => n.Trim().ToLower()).Where(n => n.Length > 0).ToList();
        var onlineOnly = lowered.Count == 0 ? []
            : await db.Workspaces.AsNoTracking().Where(w => lowered.Contains(w.Name.ToLower())).Select(w => w.Id).ToListAsync(ct);
        return new SyncPolicyDto(
            await settings.GetAsync(SettingKeys.SyncEnabled, true, ct),
            maxMb * 1024L * 1024L,
            (await settings.GetAsync(SettingKeys.SyncBlockedExtensions, Array.Empty<string>(), ct)).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToList(),
            onlineOnly,
            await settings.GetAsync(SettingKeys.SyncMinClientVersion, "1.0.0", ct),
            await settings.GetAsync(SettingKeys.SyncViewOnlyAsLink, true, ct),
            Math.Max(5, await settings.GetAsync(SettingKeys.SyncPollSeconds, 30, ct)),
            await settings.GetAsync(SettingKeys.SyncJournalRetentionDays, 90, ct),
            (await settings.GetAsync(SettingKeys.UploadAllowedExtensions, SettingKeys.DefaultAllowedExtensions, ct))
                .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToList(),
            Math.Max(0, await settings.GetAsync(SettingKeys.UploadMaxSizeMb, 5120, ct)) * 1024L * 1024L,
            string.IsNullOrWhiteSpace(config["App:PublicBaseUrl"]) ? null : config["App:PublicBaseUrl"]!.TrimEnd('/'));
    }

    /// <summary>Throws 403 when desktop sync is disabled or the user's role / organization is not allowed.</summary>
    public async Task EnsureAllowedAsync(Guid userId, CancellationToken ct)
    {
        if (!await settings.GetAsync(SettingKeys.SyncEnabled, true, ct))
            throw new ForbiddenException("Đồng bộ bằng BlueDrive Desktop đang bị tắt. Vui lòng liên hệ quản trị viên.");
        var roles = await settings.GetAsync(SettingKeys.SyncAllowedRoles, Array.Empty<string>(), ct);
        if (roles.Length > 0)
        {
            var mine = await db.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role!.Code).ToListAsync(ct);
            if (!mine.Any(c => roles.Contains(c, StringComparer.OrdinalIgnoreCase)))
                throw new ForbiddenException("Vai trò của bạn chưa được phép đồng bộ bằng BlueDrive Desktop.");
        }
        var orgs = await settings.GetAsync(SettingKeys.SyncAllowedOrganizations, Array.Empty<string>(), ct);
        if (orgs.Length > 0)
        {
            var path = await db.Users.Where(u => u.Id == userId).Select(u => u.Organization != null ? u.Organization.Path : null).FirstOrDefaultAsync(ct);
            var ids = path?.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList() ?? [];
            var codes = await db.Organizations.Where(o => ids.Contains(o.Id)).Select(o => o.Code).ToListAsync(ct);
            if (!codes.Any(c => orgs.Contains(c, StringComparer.OrdinalIgnoreCase)))
                throw new ForbiddenException("Đơn vị của bạn chưa được phép đồng bộ bằng BlueDrive Desktop.");
        }
    }

    /// <summary>Client versions are compared numerically ("1.10.0" > "1.9.3").</summary>
    public static bool IsOlder(string version, string minimum)
    {
        static int[] Parts(string v) => v.Split('.', '-', '+').Take(3).Select(p => int.TryParse(p, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray();
        var a = Parts(version);
        var b = Parts(minimum);
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] < b[i];
        return false;
    }
}

public sealed record GetSyncPolicyQuery : IRequest<SyncPolicyDto>;

public sealed class GetSyncPolicyHandler(SyncPolicyService policy) : IRequestHandler<GetSyncPolicyQuery, SyncPolicyDto>
{
    public Task<SyncPolicyDto> Handle(GetSyncPolicyQuery q, CancellationToken ct) => policy.GetAsync(ct);
}

/// <summary>Pushes to BlueDrive Desktop clients connected to /hubs/sync (implemented by the API host).</summary>
public interface ISyncNotifier
{
    Task DeviceStatusChangedAsync(Guid deviceId, string status, CancellationToken ct = default);
}

/// <summary>Nightly cleanup: journal rows older than sync.journal_retention_days (clients behind get 410 → full resync) and stale OAuth codes.</summary>
public sealed record PurgeSyncJournalCommand : IRequest<int>;

public sealed class PurgeSyncJournalHandler(IAppDbContext db, ISettingsService settings, TimeProvider clock) : IRequestHandler<PurgeSyncJournalCommand, int>
{
    public async Task<int> Handle(PurgeSyncJournalCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var days = await settings.GetAsync(SettingKeys.SyncJournalRetentionDays, 90, ct);
        var removed = days <= 0 ? 0 : await db.ChangeJournal.Where(c => c.OccurredAt < now.AddDays(-days)).ExecuteDeleteAsync(ct);
        await db.OAuthAuthorizationCodes.Where(c => c.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);
        return removed;
    }
}
