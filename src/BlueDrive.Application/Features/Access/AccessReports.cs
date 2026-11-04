using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Sharing;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Access;

// Spec 8.4: access reports (administrators everywhere, Space managers in their Space) and bulk operations. Every report is a table that the
// page shows and exports (Excel / PDF) the same way.

public sealed record ReportRow(Guid? ItemId, Guid? WorkspaceId, IReadOnlyList<string?> Cells);

public sealed record AccessReportDto(string Kind, string Title, IReadOnlyList<string> Columns, IReadOnlyList<ReportRow> Rows, bool Truncated, string? Summary);

/// <param name="Kind">check, user-access, external, links, unique, expiring.</param>
public sealed record RunAccessReportQuery(string Kind, Guid? WorkspaceId = null, Guid? UserId = null, Guid? GroupId = null, Guid? ItemId = null, int Days = 30)
    : IRequest<AccessReportDto>;

public sealed record ExportAccessReportQuery(RunAccessReportQuery Report, string Format) : IRequest<ExportedFile>;

public sealed record BulkResultDto(int Count, string Message);

/// <summary>Revoke every "Bất kỳ ai" link — of a Space, or of the whole system (administrators).</summary>
public sealed record BulkRevokeAnyoneLinksCommand(Guid? WorkspaceId) : IRequest<BulkResultDto>;

/// <summary>Take away every permission given to one user or guest (their own content stays theirs).</summary>
public sealed record BulkRemovePrincipalCommand(Guid UserId) : IRequest<BulkResultDto>;

/// <summary>Delete the guests that have not signed in for N days.</summary>
public sealed record BulkDeleteInactiveGuestsCommand(int Days) : IRequest<BulkResultDto>;

public sealed class AccessReportHandlers(IAppDbContext db, ICurrentUser me, IPermissionService perms, IItemPermissionService engine, TimeProvider clock,
    ITableExporter exporter, LinkModel links, AclEditor acl, ISender mediator, IAuditLogger audit)
    : IRequestHandler<RunAccessReportQuery, AccessReportDto>, IRequestHandler<ExportAccessReportQuery, ExportedFile>,
      IRequestHandler<BulkRevokeAnyoneLinksCommand, BulkResultDto>, IRequestHandler<BulkRemovePrincipalCommand, BulkResultDto>,
      IRequestHandler<BulkDeleteInactiveGuestsCommand, BulkResultDto>
{
    private const int Max = 5000;

    /// <summary>Administrators see everything; who manages a Space sees that Space only (and must name it).</summary>
    private async Task<(Guid Uid, Guid? Space)> ScopeAsync(Guid? workspaceId, string perm, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (await perms.HasAsync(uid, perm, ct)) return (uid, workspaceId);
        if (workspaceId is { } ws && (await engine.GetEffectiveAsync(uid, ws, ct)).Has(AccessRight.ManagePermissions)) return (uid, ws);
        throw new ForbiddenException("Báo cáo này dành cho quản trị hoặc Quản lý Space (chọn Space của mình).");
    }

    private static string D(DateTime? t) => t is { } v ? v.AddHours(7).ToString("dd/MM/yyyy HH:mm") : "Không thời hạn";

    private static string KindName(SecurableKind k) => k switch
    {
        SecurableKind.Space => "Space",
        SecurableKind.Drive => "Drive",
        SecurableKind.Folder => "Thư mục",
        SecurableKind.File => "Tệp",
        _ => k.ToString(),
    };

    private sealed record Node(Guid Id, string Name, SecurableKind Kind, Guid? Root);

    private async Task<Dictionary<Guid, Node>> NodesAsync(IEnumerable<Guid> ids, Guid? space, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Securables.AsNoTracking().Where(s => list.Contains(s.Id) && !s.IsDeleted && (space == null || (s.SpaceId ?? s.PersonalDriveId) == space))
            .Select(s => new Node(s.Id, s.Name, s.Kind, s.SpaceId ?? s.PersonalDriveId)).ToDictionaryAsync(n => n.Id, ct);
    }

    private async Task<Dictionary<Guid, string>> RootNamesAsync(IEnumerable<Node> nodes, CancellationToken ct)
    {
        var roots = nodes.Where(n => n.Root != null).Select(n => n.Root!.Value).Distinct().ToList();
        return await db.Workspaces.AsNoTracking().Where(w => roots.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
    }

    private async Task<Dictionary<Guid, string>> LevelNamesAsync(CancellationToken ct) =>
        await db.PermissionLevels.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

    public async Task<AccessReportDto> Handle(RunAccessReportQuery q, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var levels = await LevelNamesAsync(ct);
        switch (q.Kind)
        {
            case "check":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                if (q.ItemId is not { } itemId) throw new AppException("Chọn tư liệu cần kiểm tra.");
                var node = (await NodesAsync([itemId], space, ct)).GetValueOrDefault(itemId) ?? throw new NotFoundException("Không tìm thấy tư liệu.");
                if (q.UserId is { } userId)
                {
                    var name = await db.Users.Where(u => u.Id == userId).Select(u => u.FullName + " (" + u.Username + ")").FirstOrDefaultAsync(ct)
                               ?? throw new NotFoundException("Không tìm thấy người dùng.");
                    var x = await engine.ExplainAsync(userId, itemId, ct);
                    var rights = AccessRights.Descriptions.Where(d => x.Effective.Has(d.Key)).Select(d => d.Value).ToList();
                    var rows = x.Sources.Select(s => new ReportRow(itemId, node.Root, [s.Kind, s.Principal, s.Level, s.Scope, D(s.ExpiresAt)])).ToList();
                    var summary = x.Effective == AccessRight.None
                        ? $"{name} không có quyền trên {KindName(node.Kind).ToLowerInvariant()} \"{node.Name}\"."
                        : $"Quyền hiệu lực của {name} trên \"{node.Name}\": {string.Join(", ", rights)}." + (x.Inherited ? $" Kế thừa từ \"{x.ScopeName}\"." : "")
                          + (x.Restrictions.Count > 0 ? " Giới hạn: " + string.Join("; ", x.Restrictions) + "." : "");
                    return new AccessReportDto(q.Kind, $"Kiểm tra quyền — {name}", ["Nguồn", "Đối tượng", "Cấp quyền", "Áp dụng tại", "Hết hạn"], rows, false, summary);
                }
                if (q.GroupId is { } groupId)
                {
                    var scope = await db.Securables.AsNoTracking().Where(s => s.Id == itemId).Select(s => s.AclScopeId).FirstAsync(ct);
                    var gname = await db.UserGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy nhóm.");
                    var entries = await db.AclEntries.AsNoTracking().Where(e => e.ScopeId == scope && e.PrincipalId == groupId && (e.ExpiresAt == null || e.ExpiresAt > now)).ToListAsync(ct);
                    var scopeName = await db.Securables.Where(s => s.Id == scope).Select(s => s.Name).FirstAsync(ct);
                    var rows = entries.Select(e => new ReportRow(itemId, node.Root, ["Cấp trực tiếp cho nhóm", gname, levels.GetValueOrDefault(e.PermissionLevelId, ""), scopeName, D(e.ExpiresAt)])).ToList();
                    return new AccessReportDto(q.Kind, $"Kiểm tra quyền — nhóm {gname}", ["Nguồn", "Đối tượng", "Cấp quyền", "Áp dụng tại", "Hết hạn"], rows, false,
                        rows.Count == 0 ? $"Nhóm \"{gname}\" không được cấp quyền trực tiếp trên \"{node.Name}\" (thành viên vẫn có thể có quyền qua Space, đơn vị)." : null);
                }
                throw new AppException("Chọn người dùng hoặc nhóm cần kiểm tra.");
            }
            case "user-access":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                if (q.UserId is not { } userId) throw new AppException("Chọn người dùng.");
                var principals = await db.PrincipalMemberships.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.PrincipalId).ToListAsync(ct);
                principals.Add(userId);
                var entries = await db.AclEntries.AsNoTracking().Where(e => principals.Contains(e.PrincipalId) && (e.ExpiresAt == null || e.ExpiresAt > now))
                    .Take(Max + 1).ToListAsync(ct);
                var nodes = await NodesAsync(entries.Select(e => e.ScopeId), space, ct);
                var roots = await RootNamesAsync(nodes.Values, ct);
                var who = await PrincipalDisplay.ResolveAsync(db, entries.Select(e => e.PrincipalId), ct);
                var rows = entries.Where(e => nodes.ContainsKey(e.ScopeId)).Select(e =>
                {
                    var n = nodes[e.ScopeId];
                    return new ReportRow(n.Id, n.Root, [n.Name, KindName(n.Kind), n.Root is { } r ? roots.GetValueOrDefault(r) : null, levels.GetValueOrDefault(e.PermissionLevelId, ""),
                        e.PrincipalId == userId ? "Cấp trực tiếp" : who.GetValueOrDefault(e.PrincipalId, ("(không xác định)", null)).Name,
                        n.Kind == SecurableKind.File ? "Tệp này" : "Mục này và mọi mục bên trong còn kế thừa", D(e.ExpiresAt)]);
                }).OrderBy(r => r.Cells[2]).ThenBy(r => r.Cells[0]).Take(Max).ToList();
                var name = await db.Users.Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "";
                return new AccessReportDto(q.Kind, $"{name} truy cập được gì", ["Tư liệu", "Loại", "Space / Drive", "Cấp quyền", "Qua", "Phạm vi", "Hết hạn"], rows,
                    entries.Count > Max, $"{rows.Count} quyền được cấp (kể cả qua nhóm, đơn vị, nhóm Space).");
            }
            case "external":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                var guestEntries = await db.AclEntries.AsNoTracking().Where(e => e.PrincipalKind == PrincipalKind.Guest && (e.ExpiresAt == null || e.ExpiresAt > now)).Take(Max).ToListAsync(ct);
                var anyone = await db.ShareLinks.AsNoTracking().Where(l => l.Scope == ShareLinkScope.Anyone && !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)).Take(Max).ToListAsync(ct);
                var nodes = await NodesAsync(guestEntries.Select(e => e.ScopeId).Concat(anyone.Select(l => l.ResourceId)), space, ct);
                var roots = await RootNamesAsync(nodes.Values, ct);
                var people = await PrincipalDisplay.ResolveAsync(db, guestEntries.Select(e => e.PrincipalId).Concat(guestEntries.Where(e => e.GrantedBy != null).Select(e => e.GrantedBy!.Value))
                    .Concat(anyone.Where(l => l.CreatedBy != null).Select(l => l.CreatedBy!.Value)), ct);
                string P(Guid? id) => id is { } v ? people.GetValueOrDefault(v, ("(không xác định)", null)).Name : "—";
                var rows = guestEntries.Where(e => nodes.ContainsKey(e.ScopeId)).Select(e => (n: nodes[e.ScopeId], row: (IReadOnlyList<string?>)
                        [nodes[e.ScopeId].Name, null, $"Khách: {P(e.PrincipalId)}", levels.GetValueOrDefault(e.PermissionLevelId, ""), P(e.GrantedBy), D(e.GrantedAt), D(e.ExpiresAt)]))
                    .Concat(anyone.Where(l => nodes.ContainsKey(l.ResourceId)).Select(l => (n: nodes[l.ResourceId], row: (IReadOnlyList<string?>)
                        [nodes[l.ResourceId].Name, null, "Liên kết \"Bất kỳ ai\"", l.LevelId is { } lv ? levels.GetValueOrDefault(lv, "") : "", P(l.CreatedBy), D(l.CreatedAt), D(l.ExpiresAt)])))
                    .Select(x => new ReportRow(x.n.Id, x.n.Root, [x.row[0], x.n.Root is { } r ? roots.GetValueOrDefault(r) : null, .. x.row.Skip(2)])).ToList();
                return new AccessReportDto(q.Kind, "Chia sẻ ra ngoài", ["Tư liệu", "Space / Drive", "Chia sẻ cho", "Cấp quyền", "Người chia sẻ", "Lúc", "Hết hạn"], rows,
                    guestEntries.Count >= Max || anyone.Count >= Max, $"{rows.Count} mục chia sẻ cho khách hoặc liên kết ẩn danh.");
            }
            case "links":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                var all = await db.ShareLinks.AsNoTracking().Where(l => !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)).OrderByDescending(l => l.CreatedAt).Take(Max + 1).ToListAsync(ct);
                var nodes = await NodesAsync(all.Select(l => l.ResourceId), space, ct);
                var roots = await RootNamesAsync(nodes.Values, ct);
                var people = await PrincipalDisplay.ResolveAsync(db, all.Where(l => l.CreatedBy != null).Select(l => l.CreatedBy!.Value), ct);
                var rows = all.Where(l => nodes.ContainsKey(l.ResourceId)).Take(Max).Select(l =>
                {
                    var n = nodes[l.ResourceId];
                    return new ReportRow(n.Id, n.Root, [n.Name, n.Root is { } r ? roots.GetValueOrDefault(r) : null, ShareLinkMapping.ScopeLabel(l.Scope),
                        l.LevelId is { } lv ? levels.GetValueOrDefault(lv, "") : "", l.CreatedBy is { } c ? people.GetValueOrDefault(c, ("", null)).Name : "—", D(l.CreatedAt), D(l.ExpiresAt),
                        l.ExpiresAt is null ? "Không thời hạn" : null]);
                }).ToList();
                return new AccessReportDto(q.Kind, "Liên kết đang hiệu lực", ["Tư liệu", "Space / Drive", "Ai dùng được", "Cấp quyền", "Người tạo", "Tạo lúc", "Hết hạn", "Lưu ý"], rows,
                    all.Count > Max, $"{rows.Count} liên kết, {rows.Count(r => r.Cells[7] is not null)} liên kết không thời hạn.");
            }
            case "unique":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                var nodes = await db.Securables.AsNoTracking().Where(s => s.HasUniqueAcl && s.ParentId != null && !s.IsDeleted && (s.Kind == SecurableKind.Folder || s.Kind == SecurableKind.File)
                                                                        && (space == null || (s.SpaceId ?? s.PersonalDriveId) == space))
                    .OrderBy(s => s.Path).Take(Max + 1).Select(s => new Node(s.Id, s.Name, s.Kind, s.SpaceId ?? s.PersonalDriveId)).ToListAsync(ct);
                var ids = nodes.Select(n => n.Id).ToList();
                var entries = await db.AclEntries.AsNoTracking().Where(e => ids.Contains(e.ScopeId)).Select(e => new { e.ScopeId, e.PrincipalId, e.Source }).ToListAsync(ct);
                var people = await PrincipalDisplay.ResolveAsync(db, entries.Where(e => e.Source == AclSource.Direct).Select(e => e.PrincipalId), ct);
                var roots = await RootNamesAsync(nodes, ct);
                var rows = nodes.Take(Max).Select(n =>
                {
                    var own = entries.Where(e => e.ScopeId == n.Id).ToList();
                    return new ReportRow(n.Id, n.Root, [n.Name, KindName(n.Kind), n.Root is { } r ? roots.GetValueOrDefault(r) : null, own.Count.ToString(),
                        string.Join(", ", own.Where(e => e.Source == AclSource.Direct).Select(e => people.GetValueOrDefault(e.PrincipalId, ("", null)).Name).Distinct())]);
                }).ToList();
                return new AccessReportDto(q.Kind, "Quyền riêng (ngắt kế thừa)", ["Tư liệu", "Loại", "Space / Drive", "Số mục quyền", "Cấp trực tiếp cho"], rows, nodes.Count > Max,
                    $"{rows.Count} thư mục / tệp có quyền riêng.");
            }
            case "expiring":
            {
                var (_, space) = await ScopeAsync(q.WorkspaceId, Perm.AccessReportView, ct);
                var days = Math.Clamp(q.Days, 1, 365);
                var from = now.AddDays(-days);
                var to = now.AddDays(days);
                var entries = await db.AclEntries.AsNoTracking().Where(e => e.ExpiresAt != null && e.ExpiresAt >= from && e.ExpiresAt <= to && e.Source != AclSource.Emergency
                                                                 && e.PrincipalKind != PrincipalKind.Link) // links are listed as links
                    .OrderBy(e => e.ExpiresAt).Take(Max).ToListAsync(ct);
                var linkRows = await db.ShareLinks.AsNoTracking().Where(l => !l.IsRevoked && l.ExpiresAt != null && l.ExpiresAt >= from && l.ExpiresAt <= to)
                    .OrderBy(l => l.ExpiresAt).Take(Max).ToListAsync(ct);
                var nodes = await NodesAsync(entries.Select(e => e.ScopeId).Concat(linkRows.Select(l => l.ResourceId)), space, ct);
                var roots = await RootNamesAsync(nodes.Values, ct);
                var people = await PrincipalDisplay.ResolveAsync(db, entries.Select(e => e.PrincipalId), ct);
                string State(DateTime at) => at <= now ? "Đã hết hạn" : "Sắp hết hạn";
                var rows = entries.Where(e => nodes.ContainsKey(e.ScopeId)).Select(e => (at: e.ExpiresAt!.Value, row: new ReportRow(e.ScopeId, nodes[e.ScopeId].Root,
                        [nodes[e.ScopeId].Name, nodes[e.ScopeId].Root is { } r ? roots.GetValueOrDefault(r) : null, people.GetValueOrDefault(e.PrincipalId, ("", null)).Name,
                         levels.GetValueOrDefault(e.PermissionLevelId, ""), D(e.ExpiresAt), State(e.ExpiresAt!.Value)])))
                    .Concat(linkRows.Where(l => nodes.ContainsKey(l.ResourceId)).Select(l => (at: l.ExpiresAt!.Value, row: new ReportRow(l.ResourceId, nodes[l.ResourceId].Root,
                        [nodes[l.ResourceId].Name, nodes[l.ResourceId].Root is { } r ? roots.GetValueOrDefault(r) : null, $"Liên kết ({ShareLinkMapping.ScopeLabel(l.Scope)})",
                         l.LevelId is { } lv ? levels.GetValueOrDefault(lv, "") : "", D(l.ExpiresAt), State(l.ExpiresAt!.Value)]))))
                    .OrderBy(x => x.at).Select(x => x.row).ToList();
                return new AccessReportDto(q.Kind, $"Quyền sắp / đã hết hạn (±{days} ngày)", ["Tư liệu", "Space / Drive", "Đối tượng", "Cấp quyền", "Hết hạn", "Trạng thái"], rows,
                    entries.Count >= Max || linkRows.Count >= Max, null);
            }
            default:
                throw new AppException("Loại báo cáo không hợp lệ.");
        }
    }

    public async Task<ExportedFile> Handle(ExportAccessReportQuery q, CancellationToken ct)
    {
        var report = await Handle(q.Report, ct);
        var by = await db.Users.Where(u => u.Id == me.RequireUserId()).Select(u => u.FullName).FirstAsync(ct);
        var rows = report.Rows.Select(r => r.Cells).ToList();
        await audit.LogAsync(new AuditEntry(AuditAction.Export, ResourceName: report.Title, Detail: new { accessReport = q.Report.Kind, rows = rows.Count, q.Report.WorkspaceId }), ct);
        return await exporter.ExportAsync(new TableExport(report.Title, report.Columns.Select(c => new ExportColumn(c)).ToList(), rows,
            report.Summary + (report.Truncated ? $" (chỉ {Max} dòng đầu)" : ""), by), ExportFormat.Normalize(q.Format), ct);
    }

    public async Task<BulkResultDto> Handle(BulkRevokeAnyoneLinksCommand cmd, CancellationToken ct)
    {
        var (_, space) = await ScopeAsync(cmd.WorkspaceId, Perm.AccessReportAdmin, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var candidates = await db.ShareLinks.Where(l => l.Scope == ShareLinkScope.Anyone && !l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)).ToListAsync(ct);
        var inScope = space is null ? null : await NodesAsync(candidates.Select(l => l.ResourceId), space, ct);
        var revoked = 0;
        foreach (var l in candidates.Where(l => inScope is null || inScope.ContainsKey(l.ResourceId)))
        {
            l.IsRevoked = true;
            await links.RevokeAsync(l, ct);
            revoked++;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.LinkRevoked, space is null ? null : ResourceType.Workspace, space, "Thu hồi hàng loạt liên kết \"Bất kỳ ai\"", space,
            new { bulk = true, revoked }), ct);
        return new BulkResultDto(revoked, $"Đã thu hồi {revoked} liên kết \"Bất kỳ ai\".");
    }

    public async Task<BulkResultDto> Handle(BulkRemovePrincipalCommand cmd, CancellationToken ct)
    {
        await ScopeAsync(null, Perm.AccessReportAdmin, ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct) ?? throw new NotFoundException("Không tìm thấy người dùng.");
        var count = await db.AclEntries.CountAsync(e => e.PrincipalId == user.Id, ct);
        await acl.ForgetPrincipalAsync(user.Id, ct);
        var memberships = await db.ShareLinkMembers.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.PermissionRevoked, ResourceName: $"Gỡ toàn bộ quyền của {user.Username}", Detail: new { bulk = true, user = user.Id, entries = count, memberships }), ct);
        return new BulkResultDto(count, $"Đã gỡ {count} quyền được cấp cho {user.FullName}.");
    }

    public async Task<BulkResultDto> Handle(BulkDeleteInactiveGuestsCommand cmd, CancellationToken ct)
    {
        await ScopeAsync(null, Perm.AccessReportAdmin, ct);
        if (cmd.Days is < 1 or > 3650) throw new AppException("Số ngày không hoạt động từ 1 đến 3650.");
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-cmd.Days);
        var ids = await (from g in db.Guests
                         join u in db.Users on g.UserId equals u.Id
                         where (u.LastLoginAt ?? g.InvitedAt) < cutoff && u.IsActive
                         select g.UserId).ToListAsync(ct);
        foreach (var id in ids) await mediator.Send(new DeleteGuestCommand(id), ct);
        await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceName: "Xóa khách không hoạt động", Detail: new { bulk = true, days = cmd.Days, guests = ids.Count }), ct);
        return new BulkResultDto(ids.Count, $"Đã xóa {ids.Count} khách không đăng nhập trong {cmd.Days} ngày.");
    }
}

public sealed record ReportItemDto(Guid Id, string Name, SecurableKind Kind, Guid? WorkspaceId, string? WorkspaceName);

/// <summary>Items by name (to pick the one "Kiểm tra quyền" looks at): names only, for administrators or the Space's managers.</summary>
public sealed record SearchReportItemsQuery(Guid? WorkspaceId, string? Query) : IRequest<IReadOnlyList<ReportItemDto>>;

public sealed class SearchReportItemsHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms, IItemPermissionService engine)
    : IRequestHandler<SearchReportItemsQuery, IReadOnlyList<ReportItemDto>>
{
    public async Task<IReadOnlyList<ReportItemDto>> Handle(SearchReportItemsQuery q, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, Perm.AccessReportView, ct)
            && !(q.WorkspaceId is { } ws && (await engine.GetEffectiveAsync(uid, ws, ct)).Has(AccessRight.ManagePermissions)))
            throw new ForbiddenException("Báo cáo này dành cho quản trị hoặc Quản lý Space (chọn Space của mình).");
        var nodes = db.Securables.AsNoTracking().Where(s => !s.IsDeleted && (q.WorkspaceId == null || (s.SpaceId ?? s.PersonalDriveId) == q.WorkspaceId));
        if (!string.IsNullOrWhiteSpace(q.Query))
        {
            var term = TextUtil.Unaccent(q.Query.Trim());
            nodes = nodes.Where(s => DbFn.Unaccent(s.Name).Contains(term));
        }
        var rows = await nodes.Where(s => s.Kind != SecurableKind.Library).OrderBy(s => s.Name).Take(30)
            .Select(s => new { s.Id, s.Name, s.Kind, Root = s.SpaceId ?? s.PersonalDriveId }).ToListAsync(ct);
        var roots = rows.Where(r => r.Root != null).Select(r => r.Root!.Value).Distinct().ToList();
        var names = await db.Workspaces.AsNoTracking().Where(w => roots.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        return rows.Select(r => new ReportItemDto(r.Id, r.Name, r.Kind, r.Root, r.Root is { } x ? names.GetValueOrDefault(x) : null)).ToList();
    }
}
