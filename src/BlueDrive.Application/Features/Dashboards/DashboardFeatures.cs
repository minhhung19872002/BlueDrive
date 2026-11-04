using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Monitoring;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlueDrive.Application.Features.Dashboards;

public sealed record TypeSlice(MediaType MediaType, string Name, long Bytes, int Files);

public sealed record FolderUsage(Guid Id, string Name, string Path, long Bytes, int Files);

public sealed record RecentDocDto(Guid Id, string Name, MediaType MediaType, string OwnerName, string? WorkspaceName, Guid WorkspaceId, long SizeBytes, DateTime ModifiedAt);

public sealed record PersonalDashboardDto(Guid WorkspaceId, long UsedBytes, long QuotaBytes, double Percent, int MyFiles, int SharedWithMe, int Starred, int InTrash,
    IReadOnlyList<TypeSlice> ByType, IReadOnlyList<FolderUsage> TopFolders, IReadOnlyList<RecentDocDto> Recent);

public static class DashboardBuckets
{
    /// <summary>Chart buckets of #64/#69: Ảnh, Video, Âm thanh, Tài liệu, Bảng tính, Trình chiếu, PDF, Khác.</summary>
    public static MediaType Bucket(MediaType t) => t switch
    {
        MediaType.Image or MediaType.Video or MediaType.Audio or MediaType.Document or MediaType.Spreadsheet or MediaType.Presentation or MediaType.Pdf => t,
        _ => MediaType.Other,
    };

    public static List<TypeSlice> Slices(IEnumerable<(MediaType Type, long Bytes, int Files)> rows)
        => rows.GroupBy(r => Bucket(r.Type)).Select(g => new TypeSlice(g.Key, MediaTypeNames.Of(g.Key), g.Sum(x => x.Bytes), g.Sum(x => x.Files)))
            .OrderByDescending(s => s.Bytes).ToList();
}

/// <summary>#63–66: personal dashboard (cached 1 minute per user).</summary>
public sealed record GetPersonalDashboardQuery : IRequest<PersonalDashboardDto>;

public sealed class GetPersonalDashboardHandler(IAppDbContext db, ICurrentUser me, IResourceAuthorizationService auth, IMemoryCache cache) : IRequestHandler<GetPersonalDashboardQuery, PersonalDashboardDto>
{
    public async Task<PersonalDashboardDto> Handle(GetPersonalDashboardQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        return (await cache.GetOrCreateAsync($"dash:personal:{uid}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.OwnerId == uid && w.Type == WorkspaceType.Personal, ct);
            var files = db.Files.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && !f.IsDeleted);
            var byType = await files.GroupBy(f => f.MediaType).Select(g => new { g.Key, Bytes = g.Sum(f => f.SizeBytes), Count = g.Count() }).ToListAsync(ct);

            // Folder sizes roll up their whole subtree.
            var folders = await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == ws.Id && !f.IsDeleted).Select(f => new { f.Id, f.Name, f.Path }).ToListAsync(ct);
            var perFolder = await files.Where(f => f.FolderId != null).GroupBy(f => f.FolderId!.Value).Select(g => new { g.Key, Bytes = g.Sum(f => f.SizeBytes), Count = g.Count() }).ToListAsync(ct);
            var pathOf = folders.ToDictionary(f => f.Id, f => f.Path);
            var names = folders.ToDictionary(f => f.Id, f => f.Name);
            var top = folders.Select(f =>
            {
                var inside = perFolder.Where(p => pathOf.TryGetValue(p.Key, out var path) && path.StartsWith(f.Path)).ToList();
                var display = string.Join(" / ", LegacyResourceAuthorizationService.PathIds(f.Path).AsEnumerable().Reverse().Select(id => names.GetValueOrDefault(id, "")));
                return new FolderUsage(f.Id, f.Name, display, inside.Sum(i => i.Bytes), inside.Sum(i => i.Count));
            }).Where(f => f.Bytes > 0).OrderByDescending(f => f.Bytes).Take(10).ToList();

            var p = await auth.GetPrincipalsAsync(uid, ct);
            var principals = p.GroupIds.Concat(p.OrganizationIds).Concat(p.RoleIds).Append(uid).ToList();
            var shared = await db.Shares.Where(s => principals.Contains(s.SharedWithId) && s.SharedBy != uid).Select(s => s.ResourceId).Distinct().CountAsync(ct);
            var starred = await db.UserFavorites.CountAsync(s => s.UserId == uid, ct);
            var trash = await db.Files.CountAsync(f => f.WorkspaceId == ws.Id && f.IsDeleted && f.IsTrashRoot, ct) + await db.Folders.CountAsync(f => f.WorkspaceId == ws.Id && f.IsDeleted && f.IsTrashRoot, ct);

            // #66: personal documents most recently modified.
            var recent = await files.OrderByDescending(f => f.UpdatedAt ?? f.CreatedAt).Take(10)
                .Select(f => new RecentDocDto(f.Id, f.Name, f.MediaType, db.Users.Where(u => u.Id == ws.OwnerId).Select(u => u.FullName).First(), null, f.WorkspaceId, f.SizeBytes, f.UpdatedAt ?? f.CreatedAt))
                .ToListAsync(ct);
            return new PersonalDashboardDto(ws.Id, ws.UsedBytes, ws.QuotaBytes, ws.QuotaBytes > 0 ? Math.Round((double)ws.UsedBytes / ws.QuotaBytes * 100, 1) : 0,
                byType.Sum(b => b.Count), shared, starred, trash, DashboardBuckets.Slices(byType.Select(b => (b.Key, b.Bytes, b.Count))), top, recent);
        }))!;
    }
}

public sealed record GroupLinkDto(Guid Id, string Name, WorkspaceMemberRole? Role, bool IsOwner, long UsedBytes, long QuotaBytes, bool IsLocked);

public sealed record GroupTypeBreakdown(Guid WorkspaceId, string Name, IReadOnlyList<TypeSlice> Slices);

public sealed record GroupDashboardDto(int MemberOf, int LeaderOf, IReadOnlyList<GroupLinkDto> Groups, IReadOnlyList<GroupTypeBreakdown> TypesByGroup, IReadOnlyList<RecentDocDto> Recent);

/// <summary>#67–70: group dashboard (cached 1 minute per user).</summary>
public sealed record GetGroupDashboardQuery : IRequest<GroupDashboardDto>;

public sealed class GetGroupDashboardHandler(IAppDbContext db, ICurrentUser me, IMemoryCache cache) : IRequestHandler<GetGroupDashboardQuery, GroupDashboardDto>
{
    public async Task<GroupDashboardDto> Handle(GetGroupDashboardQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        return (await cache.GetOrCreateAsync($"dash:group:{uid}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var groups = await db.Workspaces.AsNoTracking().Where(w => w.Type != WorkspaceType.Personal && (w.OwnerId == uid || w.Members.Any(m => m.UserId == uid)))
                .OrderByDescending(w => w.UsedBytes)
                .Select(w => new GroupLinkDto(w.Id, w.Name, w.Members.Where(m => m.UserId == uid).Select(m => (WorkspaceMemberRole?)m.MemberRole).FirstOrDefault(),
                    w.OwnerId == uid, w.UsedBytes, w.QuotaBytes, w.IsLocked))
                .ToListAsync(ct);
            var ids = groups.Select(g => g.Id).ToList();
            var types = await db.Files.AsNoTracking().Where(f => ids.Contains(f.WorkspaceId) && !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Approved)
                .GroupBy(f => new { f.WorkspaceId, f.MediaType }).Select(g => new { g.Key.WorkspaceId, g.Key.MediaType, Bytes = g.Sum(f => f.SizeBytes), Count = g.Count() }).ToListAsync(ct);
            var breakdown = groups.Select(g => new GroupTypeBreakdown(g.Id, g.Name,
                DashboardBuckets.Slices(types.Where(t => t.WorkspaceId == g.Id).Select(t => (t.MediaType, t.Bytes, t.Count))))).ToList();
            var recent = await db.Files.AsNoTracking().Where(f => ids.Contains(f.WorkspaceId) && !f.IsDeleted && (f.ApprovalStatus == ApprovalStatus.Approved || f.CreatedBy == uid))
                .OrderByDescending(f => f.UpdatedAt ?? f.CreatedAt).Take(10)
                .Select(f => new RecentDocDto(f.Id, f.Name, f.MediaType, db.Users.Where(u => u.Id == (f.UpdatedBy ?? f.CreatedBy)).Select(u => u.FullName).FirstOrDefault() ?? "",
                    f.Workspace!.Name, f.WorkspaceId, f.SizeBytes, f.UpdatedAt ?? f.CreatedAt)).ToListAsync(ct);
            return new GroupDashboardDto(groups.Count, groups.Count(g => g.IsOwner || g.Role == WorkspaceMemberRole.Owner), groups, breakdown, recent);
        }))!;
    }
}

public sealed record DailyCount(DateOnly Date, int Files, long Bytes);

public sealed record AdminDashboardDto(StorageTotals Totals, IReadOnlyList<TypeSlice> FilesByType, IReadOnlyList<DailyCount> UploadsLast30Days,
    IReadOnlyList<UsageRow> TopOrganizations, int UnacknowledgedAlerts, int ActiveSessions, int PendingApprovals, DateTime GeneratedAt);

/// <summary>System-wide dashboard for administrators (cached 2 minutes).</summary>
public sealed record GetAdminDashboardQuery : IRequest<AdminDashboardDto>;

public sealed class GetAdminDashboardHandler(IAppDbContext db, ISender mediator, IMemoryCache cache, TimeProvider clock) : IRequestHandler<GetAdminDashboardQuery, AdminDashboardDto>
{
    public async Task<AdminDashboardDto> Handle(GetAdminDashboardQuery q, CancellationToken ct)
        => (await cache.GetOrCreateAsync("dash:admin", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            var now = clock.GetUtcNow().UtcDateTime;
            var totals = await mediator.Send(new StorageTotalsQuery(), ct);
            var byType = await db.Files.AsNoTracking().Where(f => !f.IsDeleted).GroupBy(f => f.MediaType)
                .Select(g => new { g.Key, Bytes = g.Sum(f => f.SizeBytes), Count = g.Count() }).ToListAsync(ct);
            var since = now.Date.AddDays(-29).AddHours(-7);
            var uploads = await db.Files.AsNoTracking().Where(f => f.CreatedAt >= since).Select(f => new { f.CreatedAt, f.SizeBytes }).ToListAsync(ct);
            var days = Enumerable.Range(0, 30).Select(i => DateOnly.FromDateTime(now.AddHours(7).Date.AddDays(-29 + i))).ToList();
            var daily = days.Select(d => new DailyCount(d, uploads.Count(u => DateOnly.FromDateTime(u.CreatedAt.AddHours(7)) == d),
                uploads.Where(u => DateOnly.FromDateTime(u.CreatedAt.AddHours(7)) == d).Sum(u => u.SizeBytes))).ToList();
            var orgs = (await StorageStats.RowsAsync(db, "organization", null, null, ct)).Where(o => o.UsedBytes > 0).OrderByDescending(o => o.UsedBytes).Take(10).ToList();
            return new AdminDashboardDto(totals, DashboardBuckets.Slices(byType.Select(b => (b.Key, b.Bytes, b.Count))), daily, orgs,
                await db.Alerts.CountAsync(a => !a.IsAcknowledged, ct), await db.UserSessions.CountAsync(s => s.RevokedAt == null && s.LastSeenAt > now.AddMinutes(-30), ct),
                await db.Files.CountAsync(f => !f.IsDeleted && f.ApprovalStatus == ApprovalStatus.Pending, ct), now);
        }))!;
}
