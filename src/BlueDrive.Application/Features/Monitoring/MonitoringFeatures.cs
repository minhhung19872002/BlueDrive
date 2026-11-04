using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Monitoring;

// ---------- #12 audit log search ----------
public sealed record AuditLogDto(long Id, DateTime OccurredAt, Guid? UserId, string? Username, string? FullName, Guid? OrganizationId, string? OrganizationName,
    AuditAction Action, ResourceType? ResourceType, Guid? ResourceId, string? ResourceName, Guid? WorkspaceId, string? WorkspaceName, string? Ip, string? UserAgent, string? Detail);

public sealed record AuditLogFilter
{
    public Guid? UserId { get; init; }
    public Guid? OrganizationId { get; init; }
    public bool IncludeSubOrganizations { get; init; } = true;
    public IReadOnlyList<AuditAction>? Actions { get; init; }
    public ResourceType? ResourceType { get; init; }
    public Guid? WorkspaceId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Ip { get; init; }
    /// <summary>Keyword on resource name / username.</summary>
    public string? Q { get; init; }
}

public static class AuditQueries
{
    public static async Task<IQueryable<AuditLog>> FilterAsync(IAppDbContext db, AuditLogFilter f, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking();
        if (f.UserId.HasValue) q = q.Where(a => a.UserId == f.UserId);
        if (f.OrganizationId.HasValue)
        {
            if (f.IncludeSubOrganizations)
            {
                var path = await db.Organizations.Where(o => o.Id == f.OrganizationId).Select(o => o.Path).FirstOrDefaultAsync(ct) ?? "/none/";
                var ids = db.Organizations.Where(o => o.Path.StartsWith(path)).Select(o => (Guid?)o.Id);
                q = q.Where(a => ids.Contains(a.OrganizationId));
            }
            else q = q.Where(a => a.OrganizationId == f.OrganizationId);
        }
        if (f.Actions is { Count: > 0 } actions) q = q.Where(a => actions.Contains(a.Action));
        if (f.ResourceType.HasValue) q = q.Where(a => a.ResourceType == f.ResourceType);
        if (f.WorkspaceId.HasValue) q = q.Where(a => a.WorkspaceId == f.WorkspaceId);
        if (f.From.HasValue) q = q.Where(a => a.OccurredAt >= f.From.Value.Date.AddHours(-7));
        if (f.To.HasValue) q = q.Where(a => a.OccurredAt < f.To.Value.Date.AddDays(1).AddHours(-7));
        if (!string.IsNullOrWhiteSpace(f.Ip)) q = q.Where(a => a.Ip != null && a.Ip.StartsWith(f.Ip.Trim()));
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = TextUtil.Unaccent(f.Q.Trim());
            q = q.Where(a => (a.ResourceName != null && DbFn.Unaccent(a.ResourceName).Contains(term)) || (a.Username != null && a.Username.Contains(term)));
        }
        return q;
    }

    public static IQueryable<AuditLogDto> Project(IAppDbContext db, IQueryable<AuditLog> q) => q.Select(a => new AuditLogDto(a.Id, a.OccurredAt, a.UserId, a.Username,
        db.Users.Where(u => u.Id == a.UserId).Select(u => u.FullName).FirstOrDefault(), a.OrganizationId,
        db.Organizations.Where(o => o.Id == a.OrganizationId).Select(o => o.Name).FirstOrDefault(), a.Action, a.ResourceType, a.ResourceId, a.ResourceName,
        a.WorkspaceId, db.Workspaces.Where(w => w.Id == a.WorkspaceId).Select(w => w.Name).FirstOrDefault(), a.Ip, a.UserAgent, a.Detail));

    public static readonly IReadOnlyDictionary<AuditAction, string> ActionNames = new Dictionary<AuditAction, string>
    {
        [AuditAction.Login] = "Đăng nhập", [AuditAction.Logout] = "Đăng xuất", [AuditAction.LoginFailed] = "Đăng nhập sai", [AuditAction.Upload] = "Tải lên",
        [AuditAction.Download] = "Tải xuống", [AuditAction.View] = "Xem", [AuditAction.Edit] = "Chỉnh sửa", [AuditAction.Create] = "Tạo mới", [AuditAction.Share] = "Chia sẻ",
        [AuditAction.Unshare] = "Hủy chia sẻ", [AuditAction.Rename] = "Đổi tên", [AuditAction.Move] = "Di chuyển", [AuditAction.Copy] = "Sao chép", [AuditAction.Delete] = "Xóa",
        [AuditAction.Restore] = "Khôi phục", [AuditAction.Purge] = "Xóa vĩnh viễn", [AuditAction.PermissionChange] = "Đổi quyền", [AuditAction.ConfigChange] = "Đổi cấu hình",
        [AuditAction.PasswordChange] = "Đổi mật khẩu", [AuditAction.PasswordReset] = "Khôi phục mật khẩu", [AuditAction.SessionRevoke] = "Thu hồi phiên",
        [AuditAction.UserLock] = "Khóa tài khoản", [AuditAction.UserUnlock] = "Mở khóa tài khoản", [AuditAction.Approve] = "Phê duyệt", [AuditAction.Reject] = "Từ chối",
        [AuditAction.VersionRestore] = "Khôi phục phiên bản", [AuditAction.Comment] = "Bình luận", [AuditAction.ShareLinkAccess] = "Truy cập liên kết",
        [AuditAction.Export] = "Xuất dữ liệu", [AuditAction.Backup] = "Sao lưu", [AuditAction.Import] = "Nhập dữ liệu",
    };
}

public sealed record SearchAuditLogsQuery(AuditLogFilter Filter, int Page = 1, int PageSize = 50) : IRequest<PagedResult<AuditLogDto>>;

public sealed class SearchAuditLogsHandler(IAppDbContext db) : IRequestHandler<SearchAuditLogsQuery, PagedResult<AuditLogDto>>
{
    public async Task<PagedResult<AuditLogDto>> Handle(SearchAuditLogsQuery q, CancellationToken ct)
        => await AuditQueries.Project(db, (await AuditQueries.FilterAsync(db, q.Filter, ct)).OrderByDescending(a => a.Id))
            .ToCappedPagedAsync(new PageQuery { Page = q.Page, PageSize = q.PageSize }, ct);
}

/// <summary>Recomputes the hash chain to detect tampering (spec §4.11).</summary>
public sealed record VerifyAuditChainQuery(long? FromId, int Max = 200_000) : IRequest<AuditChainReport>;

public sealed record AuditChainReport(bool Valid, long Checked, long? FirstInvalidId, string Message);

public interface IAuditChainVerifier
{
    Task<AuditChainReport> VerifyAsync(long? fromId, int max, CancellationToken ct);
}

public sealed class VerifyAuditChainHandler(IAuditChainVerifier verifier) : IRequestHandler<VerifyAuditChainQuery, AuditChainReport>
{
    public Task<AuditChainReport> Handle(VerifyAuditChainQuery q, CancellationToken ct) => verifier.VerifyAsync(q.FromId, q.Max, ct);
}

// ---------- #13 storage statistics ----------
public sealed record UsageRow(Guid Id, string Name, string? Code, string? Extra, long UsedBytes, long QuotaBytes, int Files)
{
    public double Percent => QuotaBytes > 0 ? Math.Round((double)UsedBytes / QuotaBytes * 100, 1) : 0;
}

public sealed record StorageStatsQuery(string By, Guid? OrganizationId, string? Q, string? Sort, int Page = 1, int PageSize = 50) : IRequest<PagedResult<UsageRow>>;

public sealed record StorageTotals(long UsedBytes, long QuotaBytes, long PersonalBytes, long GroupBytes, int Users, int Workspaces, int Files, long DiskFreeBytes);

public sealed class StorageStatsHandler(IAppDbContext db) : IRequestHandler<StorageStatsQuery, PagedResult<UsageRow>>
{
    public async Task<PagedResult<UsageRow>> Handle(StorageStatsQuery q, CancellationToken ct)
    {
        var rows = await StorageStats.RowsAsync(db, q.By, q.OrganizationId, q.Q, ct);
        rows = (q.Sort switch
        {
            "name" => rows.OrderBy(r => r.Name),
            "-percent" => rows.OrderByDescending(r => r.Percent),
            "-files" => rows.OrderByDescending(r => r.Files),
            _ => rows.OrderByDescending(r => r.UsedBytes),
        }).ToList();
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);
        return new PagedResult<UsageRow>(rows.Skip((page - 1) * size).Take(size).ToList(), rows.Count, page, size);
    }
}

public static class StorageStats
{
    /// <summary>by = "user" | "organization" | "workspace".</summary>
    public static async Task<List<UsageRow>> RowsAsync(IAppDbContext db, string by, Guid? organizationId, string? keyword, CancellationToken ct)
    {
        var term = TextUtil.Unaccent(keyword);
        string? orgPath = organizationId is { } oid ? await db.Organizations.Where(o => o.Id == oid).Select(o => o.Path).FirstOrDefaultAsync(ct) : null;
        switch (by)
        {
            case "organization":
            {
                var orgs = await db.Organizations.AsNoTracking().Where(o => orgPath == null || o.Path.StartsWith(orgPath)).Select(o => new { o.Id, o.Code, o.Name, o.Path }).ToListAsync(ct);
                var users = await db.Users.AsNoTracking().Where(u => u.OrganizationId != null).Select(u => new { u.OrganizationId, u.UsedBytes, u.QuotaBytes }).ToListAsync(ct);
                var groupWs = await db.Workspaces.AsNoTracking().Where(w => w.Type != WorkspaceType.Personal && w.OrganizationId != null).Select(w => new { w.OrganizationId, w.UsedBytes, w.QuotaBytes }).ToListAsync(ct);
                var files = await db.Files.AsNoTracking().Where(f => !f.IsDeleted && f.UploaderOrganizationId != null).GroupBy(f => f.UploaderOrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                // A unit's usage includes its sub-units (tree roll-up).
                return orgs.Where(o => term.Length == 0 || TextUtil.Unaccent(o.Name).Contains(term) || o.Code.ToLowerInvariant().Contains(term)).Select(o =>
                {
                    var sub = orgs.Where(x => x.Path.StartsWith(o.Path)).Select(x => x.Id).ToHashSet();
                    var used = users.Where(u => sub.Contains(u.OrganizationId!.Value)).Sum(u => u.UsedBytes) + groupWs.Where(w => sub.Contains(w.OrganizationId!.Value)).Sum(w => w.UsedBytes);
                    var quota = users.Where(u => sub.Contains(u.OrganizationId!.Value)).Sum(u => u.QuotaBytes) + groupWs.Where(w => sub.Contains(w.OrganizationId!.Value)).Sum(w => w.QuotaBytes);
                    var count = files.Where(f => sub.Contains(f.Key!.Value)).Sum(f => f.Count);
                    return new UsageRow(o.Id, o.Name, o.Code, $"{users.Count(u => sub.Contains(u.OrganizationId!.Value))} người dùng", used, quota, count);
                }).ToList();
            }
            case "workspace":
            {
                var q = db.Workspaces.AsNoTracking().Where(w => w.Type != WorkspaceType.Personal);
                if (orgPath is not null) q = q.Where(w => w.Organization != null && w.Organization.Path.StartsWith(orgPath));
                var rows = await q.Select(w => new { w.Id, w.Name, Owner = w.Owner!.FullName, w.UsedBytes, w.QuotaBytes, Files = db.Files.Count(f => f.WorkspaceId == w.Id && !f.IsDeleted) }).ToListAsync(ct);
                return rows.Where(r => term.Length == 0 || TextUtil.Unaccent(r.Name).Contains(term)).Select(r => new UsageRow(r.Id, r.Name, null, $"Trưởng nhóm: {r.Owner}", r.UsedBytes, r.QuotaBytes, r.Files)).ToList();
            }
            default:
            {
                var q = db.Users.AsNoTracking().AsQueryable();
                if (orgPath is not null) q = q.Where(u => u.Organization != null && u.Organization.Path.StartsWith(orgPath));
                if (term.Length > 0) q = q.Where(u => u.Username.Contains(term) || DbFn.Unaccent(u.FullName).Contains(term));
                var rows = await q.Select(u => new
                {
                    u.Id, u.FullName, u.Username, Org = u.Organization != null ? u.Organization.Name : null, u.UsedBytes, u.QuotaBytes,
                    Files = db.Files.Count(f => f.CreatedBy == u.Id && !f.IsDeleted),
                }).ToListAsync(ct);
                return rows.Select(r => new UsageRow(r.Id, r.FullName, r.Username, r.Org, r.UsedBytes, r.QuotaBytes, r.Files)).ToList();
            }
        }
    }
}

public sealed record StorageTotalsQuery : IRequest<StorageTotals>;

public sealed class StorageTotalsHandler(IAppDbContext db, IDiskInfo disk) : IRequestHandler<StorageTotalsQuery, StorageTotals>
{
    public async Task<StorageTotals> Handle(StorageTotalsQuery q, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().GroupBy(w => w.Type == WorkspaceType.Personal)
            .Select(g => new { Personal = g.Key, Used = g.Sum(w => w.UsedBytes), Quota = g.Sum(w => w.QuotaBytes), Count = g.Count() }).ToListAsync(ct);
        var personal = ws.FirstOrDefault(x => x.Personal);
        var group = ws.FirstOrDefault(x => !x.Personal);
        return new StorageTotals((personal?.Used ?? 0) + (group?.Used ?? 0), (personal?.Quota ?? 0) + (group?.Quota ?? 0), personal?.Used ?? 0, group?.Used ?? 0,
            await db.Users.CountAsync(ct), group?.Count ?? 0, await db.Files.CountAsync(f => !f.IsDeleted, ct), disk.FreeBytes());
    }
}

/// <summary>Free space on the storage volume (health page, totals).</summary>
public interface IDiskInfo
{
    long FreeBytes();
    long TotalBytes();
}

// ---------- #14 alerts ----------
public sealed record AlertDto(Guid Id, AlertType Type, string TypeName, AlertSeverity Severity, string Message, Guid? RelatedUserId, string? RelatedUserName,
    Guid? RelatedWorkspaceId, string? RelatedWorkspaceName, bool IsAcknowledged, string? AcknowledgedByName, DateTime? AcknowledgedAt, DateTime CreatedAt);

public sealed record AlertFilter
{
    public AlertType? Type { get; init; }
    public AlertSeverity? Severity { get; init; }
    public bool? Acknowledged { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public static class AlertQueries
{
    public static IQueryable<Alert> Filter(IAppDbContext db, AlertFilter f)
    {
        var q = db.Alerts.AsNoTracking();
        if (f.Type.HasValue) q = q.Where(a => a.Type == f.Type);
        if (f.Severity.HasValue) q = q.Where(a => a.Severity == f.Severity);
        if (f.Acknowledged.HasValue) q = q.Where(a => a.IsAcknowledged == f.Acknowledged);
        if (f.From.HasValue) q = q.Where(a => a.CreatedAt >= f.From.Value.Date.AddHours(-7));
        if (f.To.HasValue) q = q.Where(a => a.CreatedAt < f.To.Value.Date.AddDays(1).AddHours(-7));
        return q.OrderByDescending(a => a.CreatedAt);
    }

    public static IQueryable<AlertDto> Project(IAppDbContext db, IQueryable<Alert> q) => q.Select(a => new AlertDto(a.Id, a.Type, "", a.Severity, a.Message,
        a.RelatedUserId, db.Users.Where(u => u.Id == a.RelatedUserId).Select(u => u.FullName).FirstOrDefault(), a.RelatedWorkspaceId,
        db.Workspaces.Where(w => w.Id == a.RelatedWorkspaceId).Select(w => w.Name).FirstOrDefault(), a.IsAcknowledged,
        db.Users.Where(u => u.Id == a.AcknowledgedBy).Select(u => u.FullName).FirstOrDefault(), a.AcknowledgedAt, a.CreatedAt));

    public static AlertDto Named(AlertDto a) => a with { TypeName = AlertService.TypeNames.GetValueOrDefault(a.Type, a.Type.ToString()) };
}

public sealed record ListAlertsQuery(AlertFilter Filter, int Page = 1, int PageSize = 50) : IRequest<PagedResult<AlertDto>>;

public sealed class ListAlertsHandler(IAppDbContext db) : IRequestHandler<ListAlertsQuery, PagedResult<AlertDto>>
{
    public async Task<PagedResult<AlertDto>> Handle(ListAlertsQuery q, CancellationToken ct)
    {
        var page = await AlertQueries.Project(db, AlertQueries.Filter(db, q.Filter)).ToPagedAsync(new PageQuery { Page = q.Page, PageSize = q.PageSize }, ct);
        return page with { Items = page.Items.Select(AlertQueries.Named).ToList() };
    }
}

public sealed record AcknowledgeAlertsCommand(IReadOnlyList<Guid> Ids) : IRequest<int>;

public sealed class AcknowledgeAlertsHandler(IAppDbContext db, ICurrentUser me, IAuditLogger audit, TimeProvider clock) : IRequestHandler<AcknowledgeAlertsCommand, int>
{
    public async Task<int> Handle(AcknowledgeAlertsCommand cmd, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var n = await db.Alerts.Where(a => cmd.Ids.Contains(a.Id) && !a.IsAcknowledged)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsAcknowledged, true).SetProperty(a => a.AcknowledgedBy, uid).SetProperty(a => a.AcknowledgedAt, now), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Edit, ResourceName: "Xác nhận cảnh báo", Detail: new { alerts = cmd.Ids, count = n }), ct);
        return n;
    }
}

public sealed record UnacknowledgedAlertCountQuery : IRequest<int>;

public sealed class UnacknowledgedAlertCountHandler(IAppDbContext db) : IRequestHandler<UnacknowledgedAlertCountQuery, int>
{
    public Task<int> Handle(UnacknowledgedAlertCountQuery q, CancellationToken ct) => db.Alerts.CountAsync(a => !a.IsAcknowledged, ct);
}
