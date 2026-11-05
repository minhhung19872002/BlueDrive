using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Application.Features.Access;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Features.Retention;

/// <summary>What keeps a file from being purged: a legal hold (always), a retention policy (until its date — the file is preserved).</summary>
public sealed record Protection(string? Hold, Guid? PolicyId, string? PolicyName, DateTime? RetainUntil)
{
    public bool Held => Hold is not null;
    public bool Retained => RetainUntil is not null;
}

/// <summary>
/// Spec 7.2–7.3: which retention policies and legal holds reach a file. Works on the storage tables (not the permission projection) so that
/// what is in the trash or in the preservation store is evaluated too. Scoped: policies and holds are read once per request.
/// </summary>
public sealed class RetentionService(IAppDbContext db, TimeProvider clock)
{
    private List<LegalHold>? _holds;
    private List<RetentionPolicy>? _policies;

    private async Task LoadAsync(CancellationToken ct)
    {
        _holds ??= await db.LegalHolds.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(ct);
        _policies ??= await db.RetentionPolicies.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
    }

    public static DateTime UntilOf(RetentionPolicy p, DateTime createdAt, DateTime? updatedAt) =>
        (p.Basis == RetentionBasis.Modified ? updatedAt ?? createdAt : createdAt).AddYears(p.Years);

    public async Task<Protection?> ForFileAsync(Guid fileId, CancellationToken ct) => (await ForFilesAsync([fileId], ct)).GetValueOrDefault(fileId);

    public async Task<Dictionary<Guid, Protection>> ForFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Protection>();
        if (fileIds.Count == 0) return result;
        await LoadAsync(ct);
        if (_holds!.Count == 0 && _policies!.Count == 0) return result;
        var files = await db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.WorkspaceId, f.CreatedAt, f.UpdatedAt, f.CreatedBy, f.LabelId, FolderPath = f.Folder != null ? f.Folder.Path : null })
            .ToListAsync(ct);
        var wsIds = files.Select(f => f.WorkspaceId).Distinct().ToList();
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => wsIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Type, w.OwnerId, w.DefaultLabelId }).ToDictionaryAsync(w => w.Id, ct);
        var folderIds = files.SelectMany(f => FolderIds(f.FolderPath)).Distinct().ToList();
        var folders = await db.Folders.AsNoTracking().Where(f => folderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.IsLibrary, f.ParentId, f.LabelId }).ToDictionaryAsync(f => f.Id, ct);
        var catalogs = _policies!.Any(p => p.ScopeKind == RetentionScope.Catalog)
            ? (await db.FileCatalogItems.AsNoTracking().Where(c => fileIds.Contains(c.FileId)).Select(c => new { c.FileId, c.CatalogItemId }).ToListAsync(ct))
                .ToLookup(c => c.FileId, c => c.CatalogItemId)
            : null;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var f in files)
        {
            var ws = workspaces[f.WorkspaceId];
            var chain = FolderIds(f.FolderPath);
            var hold = _holds!.FirstOrDefault(h => h.ScopeKind switch
            {
                HoldScope.Workspace => h.ScopeId == f.WorkspaceId,
                HoldScope.Folder => chain.Contains(h.ScopeId),
                HoldScope.User => f.CreatedBy == h.ScopeId || (ws.Type == WorkspaceType.Personal && ws.OwnerId == h.ScopeId),
                _ => false,
            });
            var top = chain.FirstOrDefault();
            var library = top != Guid.Empty && folders.TryGetValue(top, out var t) && t.IsLibrary ? top : PermissionProjection.LibraryOf(f.WorkspaceId);
            var label = f.LabelId ?? Enumerable.Reverse(chain).Select(id => folders.GetValueOrDefault(id)?.LabelId).FirstOrDefault(l => l is not null) ?? ws.DefaultLabelId;
            RetentionPolicy? best = null;
            DateTime bestUntil = default;
            foreach (var p in _policies!)
            {
                var applies = p.ScopeKind switch
                {
                    RetentionScope.Workspace => p.ScopeId == f.WorkspaceId,
                    RetentionScope.Library => p.ScopeId == library,
                    RetentionScope.Label => p.ScopeId == label,
                    RetentionScope.Catalog => catalogs?[f.Id].Contains(p.ScopeId) == true,
                    _ => false,
                };
                if (!applies) continue;
                var until = UntilOf(p, f.CreatedAt, f.UpdatedAt);
                if (until > now && until > bestUntil) (best, bestUntil) = (p, until);
            }
            if (hold is not null || best is not null)
                result[f.Id] = new Protection(hold?.Name, best?.Id, best?.Name, best is null ? null : bestUntil);
        }
        return result;
    }

    /// <summary>Spec 6: a Space under a hold or a retention policy cannot be deleted (nor purged).</summary>
    public async Task<string?> WorkspaceBlockAsync(Guid workspaceId, CancellationToken ct)
    {
        await LoadAsync(ct);
        if (_holds!.FirstOrDefault(h => h.ScopeKind == HoldScope.Workspace && h.ScopeId == workspaceId) is { } h) return $"giữ pháp lý \"{h.Name}\"";
        var folderHolds = _holds!.Where(x => x.ScopeKind == HoldScope.Folder).Select(x => x.ScopeId).ToList();
        if (folderHolds.Count > 0 && await db.Folders.AnyAsync(f => f.WorkspaceId == workspaceId && folderHolds.Contains(f.Id), ct)) return "giữ pháp lý trên thư mục trong Space";
        if (_policies!.FirstOrDefault(p => (p.ScopeKind == RetentionScope.Workspace && p.ScopeId == workspaceId) || (p.ScopeKind == RetentionScope.Library && p.WorkspaceId == workspaceId)) is { } p)
            return $"chính sách lưu giữ \"{p.Name}\"";
        if (await db.Files.AnyAsync(f => f.WorkspaceId == workspaceId && f.PreservedAt != null, ct)) return "tư liệu trong kho lưu giữ";
        return null;
    }

    /// <summary>"/a/b/c/" → [a, b, c] (top first).</summary>
    public static List<Guid> FolderIds(string? path) =>
        path is null ? [] : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    /// <summary>Live files a policy reaches whose time has come (spec 7.2 "hành động khi hết hạn"), not held and not kept by another policy.</summary>
    public async Task<List<Guid>> ExpiredLiveFilesAsync(RetentionPolicy p, int max, CancellationToken ct)
    {
        var live = db.Files.AsNoTracking().Where(f => !f.IsDeleted);
        var candidates = p.ScopeKind switch
        {
            RetentionScope.Workspace => live.Where(f => f.WorkspaceId == p.ScopeId),
            RetentionScope.Library => p.WorkspaceId is { } ws && p.ScopeId == PermissionProjection.LibraryOf(ws)
                ? live.Where(f => f.WorkspaceId == ws && (f.Folder == null || !db.Folders.Any(l => l.IsLibrary && f.Folder.Path.StartsWith("/" + l.Id + "/"))))
                : live.Where(f => f.Folder != null && f.Folder.Path.StartsWith("/" + p.ScopeId + "/")),
            RetentionScope.Catalog => live.Where(f => db.FileCatalogItems.Any(c => c.FileId == f.Id && c.CatalogItemId == p.ScopeId)),
            RetentionScope.Label => live.Where(f => f.LabelId == p.ScopeId
                || (f.LabelId == null && (db.Folders.Any(x => x.LabelId == p.ScopeId && f.Folder != null && f.Folder.Path.Contains("/" + x.Id + "/"))
                    || db.Workspaces.Any(w => w.Id == f.WorkspaceId && w.DefaultLabelId == p.ScopeId)))),
            _ => live.Where(_ => false),
        };
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddYears(-p.Years);
        candidates = p.Basis == RetentionBasis.Modified
            ? candidates.Where(f => (f.UpdatedAt ?? f.CreatedAt) <= cutoff)
            : candidates.Where(f => f.CreatedAt <= cutoff);
        var ids = await candidates.OrderBy(f => f.CreatedAt).Select(f => f.Id).Take(max * 2).ToListAsync(ct);
        var protections = await ForFilesAsync(ids, ct);
        var result = new List<Guid>();
        foreach (var id in ids)
        {
            if (protections.TryGetValue(id, out var pr) && (pr.Held || pr.Retained)) continue;
            // The label or library may be another one than the coarse filter assumed.
            if (p.ScopeKind is RetentionScope.Label or RetentionScope.Library && !await ReachesAsync(p, id, ct)) continue;
            result.Add(id);
            if (result.Count >= max) break;
        }
        return result;
    }

    private async Task<bool> ReachesAsync(RetentionPolicy p, Guid fileId, CancellationToken ct)
    {
        var f = await db.Files.AsNoTracking().Where(x => x.Id == fileId)
            .Select(x => new { x.WorkspaceId, x.LabelId, Path = x.Folder != null ? x.Folder.Path : null, Default = x.Workspace!.DefaultLabelId }).FirstAsync(ct);
        var chain = FolderIds(f.Path);
        var folders = await db.Folders.AsNoTracking().Where(x => chain.Contains(x.Id)).Select(x => new { x.Id, x.IsLibrary, x.LabelId }).ToDictionaryAsync(x => x.Id, ct);
        if (p.ScopeKind == RetentionScope.Library)
        {
            var top = chain.FirstOrDefault();
            var library = top != Guid.Empty && folders.TryGetValue(top, out var t) && t.IsLibrary ? top : PermissionProjection.LibraryOf(f.WorkspaceId);
            return library == p.ScopeId;
        }
        var label = f.LabelId ?? Enumerable.Reverse(chain).Select(id => folders.GetValueOrDefault(id)?.LabelId).FirstOrDefault(l => l is not null) ?? f.Default;
        return label == p.ScopeId;
    }
}

// ---------------------------------------------------------------- policies

public sealed record RetentionPolicyDto(Guid Id, string Name, string Description, RetentionScope ScopeKind, Guid ScopeId, Guid? WorkspaceId, string ScopeName,
    int Years, RetentionBasis Basis, RetentionExpiry OnExpiry, bool IsActive, DateTime CreatedAt, string? CreatedByName);

public sealed record ListRetentionPoliciesQuery : IRequest<IReadOnlyList<RetentionPolicyDto>>;

public sealed record SaveRetentionPolicyCommand(Guid? Id, string Name, string? Description, RetentionScope ScopeKind, Guid ScopeId, Guid? WorkspaceId,
    int Years, RetentionBasis Basis, RetentionExpiry OnExpiry, bool IsActive = true) : IRequest<Guid>;

public sealed record DeleteRetentionPolicyCommand(Guid Id) : IRequest<Unit>;

// ---------------------------------------------------------------- holds

public sealed record LegalHoldDto(Guid Id, string Name, string Reason, HoldScope ScopeKind, Guid ScopeId, string ScopeName, DateTime CreatedAt, string? CreatedByName,
    DateTime? ReleasedAt, string? ReleasedByName, string? ReleaseReason);

public sealed record ListLegalHoldsQuery(bool IncludeReleased = false) : IRequest<IReadOnlyList<LegalHoldDto>>;

public sealed record CreateLegalHoldCommand(string Name, string Reason, HoldScope ScopeKind, Guid ScopeId) : IRequest<Guid>;

public sealed record ReleaseLegalHoldCommand(Guid Id, string Reason) : IRequest<Unit>;

// ---------------------------------------------------------------- preservation store and review

public sealed record PreservedItemDto(Guid Id, string Type, string Name, Guid WorkspaceId, string WorkspaceName, long SizeBytes, DateTime? DeletedAt, string? DeletedByName,
    DateTime PreservedAt, string? PolicyName, DateTime? RetainUntil, string? Hold);

public sealed record ListPreservedQuery : PageQuery, IRequest<PagedResult<PreservedItemDto>>;

/// <summary>"Trả về thùng rác": the item goes back to the (first stage) trash, from where it can be restored.</summary>
public sealed record ReleasePreservedCommand(IReadOnlyList<ItemRef> Items) : IRequest<int>;

/// <summary>"Xóa vĩnh viễn" from the store: only once the retention has passed and no hold applies.</summary>
public sealed record PurgePreservedCommand(IReadOnlyList<ItemRef> Items) : IRequest<int>;

public sealed record ReviewItemDto(Guid FileId, string Name, Guid WorkspaceId, string WorkspaceName, Guid PolicyId, string PolicyName, DateTime ExpiredAt, long SizeBytes);

/// <summary>Files whose retention passed under a policy "đưa vào danh sách chờ xóa có duyệt".</summary>
public sealed record ListRetentionReviewQuery : IRequest<IReadOnlyList<ReviewItemDto>>;

/// <summary>Approves deleting (to the trash) the files of the review list.</summary>
public sealed record ApproveRetentionDeleteCommand(IReadOnlyList<Guid> FileIds) : IRequest<int>;

/// <summary>Daily job: policies "tự xóa" move their expired files to the trash; preserved items whose time passed under such a policy are purged.</summary>
public sealed record ApplyRetentionExpiryCommand : IRequest<int>;

public sealed class RetentionHandlers(IAppDbContext db, ICurrentUser me, IPermissionService perms, IAuditLogger audit, TimeProvider clock, RetentionService retention,
    TrashPurger purger)
    : IRequestHandler<ListRetentionPoliciesQuery, IReadOnlyList<RetentionPolicyDto>>, IRequestHandler<SaveRetentionPolicyCommand, Guid>,
      IRequestHandler<DeleteRetentionPolicyCommand, Unit>, IRequestHandler<ListLegalHoldsQuery, IReadOnlyList<LegalHoldDto>>,
      IRequestHandler<CreateLegalHoldCommand, Guid>, IRequestHandler<ReleaseLegalHoldCommand, Unit>,
      IRequestHandler<ListPreservedQuery, PagedResult<PreservedItemDto>>, IRequestHandler<ReleasePreservedCommand, int>, IRequestHandler<PurgePreservedCommand, int>,
      IRequestHandler<ListRetentionReviewQuery, IReadOnlyList<ReviewItemDto>>, IRequestHandler<ApproveRetentionDeleteCommand, int>,
      IRequestHandler<ApplyRetentionExpiryCommand, int>
{
    private const int ReviewLimit = 500;

    private async Task<Guid> RequireAsync(string perm, CancellationToken ct)
    {
        var uid = me.RequireUserId();
        if (!await perms.HasAsync(uid, perm, ct)) throw new ForbiddenException("Bạn không có quyền quản trị lưu giữ.");
        return uid;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private async Task<string> ScopeNameAsync(RetentionScope kind, Guid id, Guid? workspaceId, CancellationToken ct) => kind switch
    {
        RetentionScope.Workspace => await db.Workspaces.Where(w => w.Id == id).Select(w => "Space \"" + w.Name + "\"").FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        RetentionScope.Library => workspaceId is { } ws && id == PermissionProjection.LibraryOf(ws)
            ? await db.Workspaces.Where(w => w.Id == ws).Select(w => "Thư viện \"Tài liệu\" — " + w.Name).FirstOrDefaultAsync(ct) ?? "(đã xóa)"
            : await db.Folders.Where(f => f.Id == id).Select(f => "Thư viện \"" + f.Name + "\" — " + f.Workspace!.Name).FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        RetentionScope.Label => await db.SensitivityLabels.Where(l => l.Id == id).Select(l => "Nhãn \"" + l.Name + "\"").FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        RetentionScope.Catalog => await db.CatalogItems.Where(c => c.Id == id).Select(c => "Danh mục \"" + c.Name + "\"").FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        _ => "",
    };

    private async Task<string> HoldScopeNameAsync(HoldScope kind, Guid id, CancellationToken ct) => kind switch
    {
        HoldScope.Workspace => await db.Workspaces.Where(w => w.Id == id).Select(w => (w.Type == WorkspaceType.Personal ? "Drive \"" : "Space \"") + w.Name + "\"").FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        HoldScope.Folder => await db.Folders.Where(f => f.Id == id).Select(f => "Thư mục \"" + f.Name + "\" — " + f.Workspace!.Name).FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        HoldScope.User => await db.Users.Where(u => u.Id == id).Select(u => "Toàn bộ tệp của " + u.FullName + " (" + u.Username + ")").FirstOrDefaultAsync(ct) ?? "(đã xóa)",
        _ => "",
    };

    public async Task<IReadOnlyList<RetentionPolicyDto>> Handle(ListRetentionPoliciesQuery q, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionView, ct);
        var rows = await db.RetentionPolicies.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.CreatedBy), ct);
        var list = new List<RetentionPolicyDto>();
        foreach (var p in rows)
            list.Add(new RetentionPolicyDto(p.Id, p.Name, p.Description, p.ScopeKind, p.ScopeId, p.WorkspaceId, await ScopeNameAsync(p.ScopeKind, p.ScopeId, p.WorkspaceId, ct),
                p.Years, p.Basis, p.OnExpiry, p.IsActive, p.CreatedAt, p.CreatedBy is { } c ? names.GetValueOrDefault(c) : null));
        return list;
    }

    public async Task<Guid> Handle(SaveRetentionPolicyCommand cmd, CancellationToken ct)
    {
        var uid = await RequireAsync(Perm.RetentionAdmin, ct);
        var name = cmd.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) throw new AppException("Tên chính sách từ 1 đến 200 ký tự.");
        if (cmd.Years is < 1 or > 100) throw new AppException("Thời gian lưu giữ từ 1 đến 100 năm.");
        var workspaceId = cmd.ScopeKind == RetentionScope.Library ? cmd.WorkspaceId : null;
        var valid = cmd.ScopeKind switch
        {
            RetentionScope.Workspace => await db.Workspaces.AnyAsync(w => w.Id == cmd.ScopeId && w.Status != SpaceStatus.Deleted, ct),
            RetentionScope.Library => workspaceId is { } ws && (cmd.ScopeId == PermissionProjection.LibraryOf(ws)
                ? await db.Workspaces.AnyAsync(w => w.Id == ws, ct)
                : await db.Folders.AnyAsync(f => f.Id == cmd.ScopeId && f.IsLibrary && f.WorkspaceId == ws && !f.IsDeleted, ct)),
            RetentionScope.Label => await db.SensitivityLabels.AnyAsync(l => l.Id == cmd.ScopeId, ct),
            RetentionScope.Catalog => await db.CatalogItems.AnyAsync(c => c.Id == cmd.ScopeId, ct),
            _ => false,
        };
        if (!valid) throw new AppException("Phạm vi áp dụng không hợp lệ.");
        RetentionPolicy p;
        if (cmd.Id is { } id) p = await db.RetentionPolicies.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Không tìm thấy chính sách lưu giữ.");
        else db.RetentionPolicies.Add(p = new RetentionPolicy { CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = uid });
        p.Name = name;
        p.Description = cmd.Description?.Trim() ?? "";
        p.ScopeKind = cmd.ScopeKind;
        p.ScopeId = cmd.ScopeId;
        p.WorkspaceId = workspaceId;
        p.Years = cmd.Years;
        p.Basis = cmd.Basis;
        p.OnExpiry = cmd.OnExpiry;
        p.IsActive = cmd.IsActive;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, null, p.Id, $"Chính sách lưu giữ \"{p.Name}\"", null,
            new { retentionPolicy = new { p.ScopeKind, p.ScopeId, p.Years, p.Basis, p.OnExpiry, p.IsActive } }), ct);
        return p.Id;
    }

    public async Task<Unit> Handle(DeleteRetentionPolicyCommand cmd, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionAdmin, ct);
        var p = await db.RetentionPolicies.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy chính sách lưu giữ.");
        db.RetentionPolicies.Remove(p);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.ConfigChange, null, p.Id, $"Chính sách lưu giữ \"{p.Name}\"", null, new { deleted = true }), ct);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<LegalHoldDto>> Handle(ListLegalHoldsQuery q, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionView, ct);
        var rows = await db.LegalHolds.AsNoTracking().Where(h => q.IncludeReleased || h.ReleasedAt == null).OrderByDescending(h => h.CreatedAt).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.CreatedBy).Concat(rows.Select(r => r.ReleasedBy)), ct);
        var list = new List<LegalHoldDto>();
        foreach (var h in rows)
            list.Add(new LegalHoldDto(h.Id, h.Name, h.Reason, h.ScopeKind, h.ScopeId, await HoldScopeNameAsync(h.ScopeKind, h.ScopeId, ct), h.CreatedAt,
                h.CreatedBy is { } c ? names.GetValueOrDefault(c) : null, h.ReleasedAt, h.ReleasedBy is { } r ? names.GetValueOrDefault(r) : null, h.ReleaseReason));
        return list;
    }

    public async Task<Guid> Handle(CreateLegalHoldCommand cmd, CancellationToken ct)
    {
        var uid = await RequireAsync(Perm.RetentionAdmin, ct);
        var name = cmd.Name?.Trim() ?? "";
        var reason = cmd.Reason?.Trim() ?? "";
        if (name.Length is 0 or > 200) throw new AppException("Tên giữ pháp lý từ 1 đến 200 ký tự.");
        if (reason.Length is 0 or > 1000) throw new AppException("Bắt buộc nhập lý do (tối đa 1000 ký tự).");
        var exists = cmd.ScopeKind switch
        {
            HoldScope.Workspace => await db.Workspaces.AnyAsync(w => w.Id == cmd.ScopeId, ct),
            HoldScope.Folder => await db.Folders.AnyAsync(f => f.Id == cmd.ScopeId, ct),
            HoldScope.User => await db.Users.AnyAsync(u => u.Id == cmd.ScopeId, ct),
            _ => false,
        };
        if (!exists) throw new AppException("Phạm vi giữ pháp lý không hợp lệ.");
        var h = new LegalHold { Name = name, Reason = reason, ScopeKind = cmd.ScopeKind, ScopeId = cmd.ScopeId, CreatedAt = clock.GetUtcNow().UtcDateTime, CreatedBy = uid };
        db.LegalHolds.Add(h);
        await db.SaveChangesAsync(ct);
        var wsId = cmd.ScopeKind switch
        {
            HoldScope.Workspace => cmd.ScopeId,
            HoldScope.Folder => await db.Folders.Where(f => f.Id == cmd.ScopeId).Select(f => (Guid?)f.WorkspaceId).FirstOrDefaultAsync(ct),
            _ => null,
        };
        await audit.LogAsync(new AuditEntry(AuditAction.HoldApplied, cmd.ScopeKind switch { HoldScope.Workspace => ResourceType.Workspace, HoldScope.Folder => ResourceType.Folder, _ => null },
            cmd.ScopeId, $"Giữ pháp lý \"{name}\" — {await HoldScopeNameAsync(cmd.ScopeKind, cmd.ScopeId, ct)}", wsId, new { holdId = h.Id, reason, scope = cmd.ScopeKind }), ct);
        return h.Id;
    }

    public async Task<Unit> Handle(ReleaseLegalHoldCommand cmd, CancellationToken ct)
    {
        var uid = await RequireAsync(Perm.RetentionAdmin, ct);
        var reason = cmd.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > 1000) throw new AppException("Bắt buộc nhập lý do gỡ giữ pháp lý.");
        var h = await db.LegalHolds.FirstOrDefaultAsync(x => x.Id == cmd.Id, ct) ?? throw new NotFoundException("Không tìm thấy giữ pháp lý.");
        if (h.ReleasedAt is not null) throw new ConflictException("Giữ pháp lý này đã được gỡ.");
        h.ReleasedAt = clock.GetUtcNow().UtcDateTime;
        h.ReleasedBy = uid;
        h.ReleaseReason = reason;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(new AuditEntry(AuditAction.HoldRemoved, null, h.ScopeId, $"Giữ pháp lý \"{h.Name}\"", null, new { holdId = h.Id, reason }), ct);
        return Unit.Value;
    }

    public async Task<PagedResult<PreservedItemDto>> Handle(ListPreservedQuery q, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionView, ct);
        var files = db.Files.AsNoTracking().Where(f => f.PreservedAt != null);
        var folders = db.Folders.AsNoTracking().Where(f => f.PreservedAt != null);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = TextUtil.Unaccent(q.Q.Trim());
            files = files.Where(f => DbFn.Unaccent(f.Name).Contains(term));
            folders = folders.Where(f => DbFn.Unaccent(f.Name).Contains(term));
        }
        var fileRows = await files.Select(f => new { f.Id, Type = "file", f.Name, f.WorkspaceId, Ws = f.Workspace!.Name, Size = f.SizeBytes, f.DeletedAt, f.DeletedBy, At = f.PreservedAt!.Value })
            .ToListAsync(ct);
        var folderRows = await folders.Select(f => new { f.Id, Type = "folder", f.Name, f.WorkspaceId, Ws = f.Workspace!.Name, Size = 0L, f.DeletedAt, f.DeletedBy, At = f.PreservedAt!.Value })
            .ToListAsync(ct);
        var rows = fileRows.Concat(folderRows).OrderByDescending(r => r.At).ToList();
        var page = rows.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize).ToList();
        var protections = await retention.ForFilesAsync(page.Where(r => r.Type == "file").Select(r => r.Id).ToList(), ct);
        var names = await NamesAsync(page.Select(r => r.DeletedBy), ct);
        var items = page.Select(r =>
        {
            var p = protections.GetValueOrDefault(r.Id);
            return new PreservedItemDto(r.Id, r.Type, r.Name, r.WorkspaceId, r.Ws, r.Size, r.DeletedAt, r.DeletedBy is { } d ? names.GetValueOrDefault(d) : null, r.At,
                p?.PolicyName, p?.RetainUntil, p?.Hold);
        }).ToList();
        return new PagedResult<PreservedItemDto>(items, rows.Count, q.SafePage, q.SafePageSize);
    }

    public async Task<int> Handle(ReleasePreservedCommand cmd, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionAdmin, ct);
        var count = 0;
        foreach (var item in cmd.Items)
        {
            if (item.IsFile)
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.Id == item.Id && x.PreservedAt != null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong kho lưu giữ.");
                f.PreservedAt = null;
                f.SecondStageAt = null;
                // Back to the trash as an item of its own (its folder may be gone).
                if (!f.IsTrashRoot) { f.IsTrashRoot = true; f.OriginalFolderId = f.FolderId; }
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { from = "preservation" }), ct);
            }
            else
            {
                var folder = await db.Folders.FirstOrDefaultAsync(x => x.Id == item.Id && x.PreservedAt != null, ct) ?? throw new NotFoundException("Không tìm thấy mục trong kho lưu giữ.");
                folder.PreservedAt = null;
                folder.SecondStageAt = null;
                await db.SaveChangesAsync(ct);
                var path = folder.Path;
                await db.Files.Where(x => x.Folder != null && x.Folder.Path.StartsWith(path) && x.PreservedAt != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PreservedAt, (DateTime?)null), ct);
                await audit.LogAsync(new AuditEntry(AuditAction.Restore, ResourceType.Folder, folder.Id, folder.Name, folder.WorkspaceId, new { from = "preservation" }), ct);
            }
            count++;
        }
        return count;
    }

    public async Task<int> Handle(PurgePreservedCommand cmd, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionAdmin, ct);
        var count = 0;
        foreach (var item in cmd.Items)
        {
            var exists = item.IsFile
                ? await db.Files.AnyAsync(x => x.Id == item.Id && x.PreservedAt != null, ct)
                : await db.Folders.AnyAsync(x => x.Id == item.Id && x.PreservedAt != null, ct);
            if (!exists) throw new NotFoundException("Không tìm thấy mục trong kho lưu giữ.");
            if (await purger.PurgeAsync(item, "preservation", ct, PurgeMode.Manual) != PurgeOutcome.Purged)
                throw new ConflictException("Mục vẫn còn trong thời gian lưu giữ hoặc đang bị giữ pháp lý, chưa xóa vĩnh viễn được.");
            count++;
        }
        return count;
    }

    public async Task<IReadOnlyList<ReviewItemDto>> Handle(ListRetentionReviewQuery q, CancellationToken ct)
    {
        await RequireAsync(Perm.RetentionView, ct);
        var list = new List<ReviewItemDto>();
        foreach (var p in await db.RetentionPolicies.AsNoTracking().Where(p => p.IsActive && p.OnExpiry == RetentionExpiry.Review).ToListAsync(ct))
        {
            var ids = await retention.ExpiredLiveFilesAsync(p, ReviewLimit, ct);
            var rows = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id))
                .Select(f => new { f.Id, f.Name, f.WorkspaceId, Ws = f.Workspace!.Name, f.CreatedAt, f.UpdatedAt, f.SizeBytes }).ToListAsync(ct);
            list.AddRange(rows.Where(r => list.All(x => x.FileId != r.Id))
                .Select(r => new ReviewItemDto(r.Id, r.Name, r.WorkspaceId, r.Ws, p.Id, p.Name, RetentionService.UntilOf(p, r.CreatedAt, r.UpdatedAt), r.SizeBytes)));
        }
        return list.OrderBy(r => r.ExpiredAt).ToList();
    }

    public async Task<int> Handle(ApproveRetentionDeleteCommand cmd, CancellationToken ct)
    {
        var uid = await RequireAsync(Perm.RetentionAdmin, ct);
        var protections = await retention.ForFilesAsync(cmd.FileIds, ct);
        var ids = cmd.FileIds.Where(id => !protections.ContainsKey(id)).ToList();
        return await TrashAsync(ids, uid, "retention_review", ct);
    }

    public async Task<int> Handle(ApplyRetentionExpiryCommand cmd, CancellationToken ct)
    {
        var count = 0;
        foreach (var p in await db.RetentionPolicies.AsNoTracking().Where(p => p.IsActive && p.OnExpiry == RetentionExpiry.Delete).ToListAsync(ct))
            count += await TrashAsync(await retention.ExpiredLiveFilesAsync(p, ReviewLimit, ct), null, $"retention_policy_{p.Id}", ct);
        // Preserved items no longer kept (time passed, no hold) go when what kept them asked for deletion, or when nothing applies any more.
        var preserved = await db.Files.AsNoTracking().Where(f => f.PreservedAt != null && f.IsTrashRoot).Select(f => f.Id).Take(ReviewLimit).ToListAsync(ct);
        var still = await retention.ForFilesAsync(preserved, ct);
        foreach (var id in preserved.Where(id => !still.ContainsKey(id)))
            if (await purger.PurgeAsync(new ItemRef("file", id), "retention_expired", ct, PurgeMode.Auto) == PurgeOutcome.Purged) count++;
        foreach (var id in await db.Folders.AsNoTracking().Where(f => f.PreservedAt != null).Select(f => f.Id).Take(ReviewLimit).ToListAsync(ct))
            if (await purger.PurgeAsync(new ItemRef("folder", id), "retention_expired", ct, PurgeMode.Auto) == PurgeOutcome.Purged) count++;
        return count;
    }

    /// <summary>To the trash, like a user's deletion (the trash and its time then apply).</summary>
    private async Task<int> TrashAsync(IReadOnlyList<Guid> ids, Guid? by, string reason, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var files = await db.Files.Include(f => f.Folder).Where(f => ids.Contains(f.Id) && !f.IsDeleted).ToListAsync(ct);
        foreach (var f in files)
        {
            f.IsDeleted = true; f.IsTrashRoot = true; f.DeletedAt = now; f.DeletedBy = by; f.OriginalFolderId = f.FolderId;
            f.OriginalPath = await TrashAccess.OriginalPathAsync(db, f.Folder?.Path, ct);
        }
        await db.SaveChangesAsync(ct);
        foreach (var f in files)
            await audit.LogAsync(new AuditEntry(AuditAction.Delete, ResourceType.File, f.Id, f.Name, f.WorkspaceId, new { reason }), ct);
        return files.Count;
    }
}

/// <summary>Folders of a Space or Drive by name (to place a hold on one) — names only, not their content.</summary>
public sealed record ListHoldFoldersQuery(Guid WorkspaceId) : IRequest<IReadOnlyList<HoldFolderDto>>;

public sealed record HoldFolderDto(Guid Id, string Path);

public sealed class ListHoldFoldersHandler(IAppDbContext db, ICurrentUser me, IPermissionService perms) : IRequestHandler<ListHoldFoldersQuery, IReadOnlyList<HoldFolderDto>>
{
    public async Task<IReadOnlyList<HoldFolderDto>> Handle(ListHoldFoldersQuery q, CancellationToken ct)
    {
        if (!await perms.HasAsync(me.RequireUserId(), Perm.RetentionAdmin, ct)) throw new ForbiddenException("Bạn không có quyền quản trị lưu giữ.");
        var folders = await db.Folders.AsNoTracking().Where(f => f.WorkspaceId == q.WorkspaceId && !f.IsDeleted).Select(f => new { f.Id, f.Name, f.Path }).ToListAsync(ct);
        var names = folders.ToDictionary(f => f.Id, f => f.Name);
        return folders.Select(f => new HoldFolderDto(f.Id, string.Join(" / ", RetentionService.FolderIds(f.Path).Select(id => names.GetValueOrDefault(id, "…")))))
            .OrderBy(f => f.Path, StringComparer.CurrentCulture).Take(2000).ToList();
    }
}
