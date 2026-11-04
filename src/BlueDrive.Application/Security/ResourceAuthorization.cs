using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BlueDrive.Application.Security;

/// <summary>Effective access of a user on one resource, with the reason (for the "who has access" panel and tests).</summary>
public sealed record ResourceAccess(AccessLevel Level, bool CanUpload, bool CanApprove, bool WorkspaceLocked, string Source)
{
    public static readonly ResourceAccess None = new(AccessLevel.None, false, false, false, "none");
    public bool CanView => Level >= AccessLevel.View;
    public bool CanDownload => Level >= AccessLevel.Download;
    public bool CanEdit => Level >= AccessLevel.Edit && !WorkspaceLocked;
    public bool CanManage => Level >= AccessLevel.Manage && !WorkspaceLocked;
}

public interface IResourceAuthorizationService
{
    Task<ResourceAccess> GetAccessAsync(Guid userId, ResourceType type, Guid resourceId, CancellationToken ct = default);
    Task<bool> CanAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default);
    /// <summary>Throws 403/404 (Vietnamese) when the user lacks the level.</summary>
    Task<ResourceAccess> EnsureAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default);
    Task<ResourceAccess> EnsureCanUploadAsync(Guid userId, Guid workspaceId, Guid? folderId, CancellationToken ct = default);
    Task<PrincipalSet> GetPrincipalsAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>All principals a user acts as: self, groups, organization (and its ancestors), roles.</summary>
/// <param name="CanEditDocs">The role grants <c>doc.edit</c>: needed to edit through workspace ownership or membership.</param>
public sealed record PrincipalSet(Guid UserId, IReadOnlyList<Guid> GroupIds, IReadOnlyList<Guid> OrganizationIds, IReadOnlyList<Guid> RoleIds, bool IsDocAdmin, bool CanViewDocs,
    bool CanEditDocs)
{
    /// <summary>
    /// Edit / manage rights that come from owning or being a member of a workspace need the role's <c>doc.edit</c>;
    /// without it they stop at view + download. Explicit grants on an item (share, direct permission) are not capped:
    /// the item's manager chose to let this person edit it.
    /// </summary>
    public AccessLevel CapByRole(AccessLevel level) => CanEditDocs || IsDocAdmin || level < AccessLevel.Edit ? level : AccessLevel.Download;

    public bool Matches(PrincipalType type, Guid id) => type switch
    {
        PrincipalType.User => id == UserId,
        PrincipalType.Group => GroupIds.Contains(id),
        PrincipalType.Organization => OrganizationIds.Contains(id),
        PrincipalType.Role => RoleIds.Contains(id),
        _ => false,
    };
}

/// <summary>
/// The current model's evaluation, kept to compare it with the new one (<c>GET /admin/permission-model/verify</c>); the application
/// uses <see cref="ModelResourceAuthorizationService"/>.
/// Order of evaluation (spec §4.4): doc.admin → workspace owner → workspace member role → direct ACL on the item →
/// ACL inherited from ancestor folders (until break_inheritance) and the workspace → shares → workspace lock caps writes.
/// Items not yet approved are visible only to editors/approvers/the uploader. Grants are additive (max wins).
/// Owner / member levels are capped by the role's doc.edit (<see cref="PrincipalSet.CapByRole"/>); ACL and shares are not.
/// </summary>
public sealed class LegacyResourceAuthorizationService(IAppDbContext db, IPermissionService perms, TimeProvider clock) : IResourceAuthorizationService
{
    public static AccessLevel LevelOf(WorkspaceMemberRole role) => role switch
    {
        WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager => AccessLevel.Manage,
        WorkspaceMemberRole.Editor => AccessLevel.Edit,
        WorkspaceMemberRole.Uploader or WorkspaceMemberRole.Approver => AccessLevel.Download,
        _ => AccessLevel.View,
    };

    // Scoped service = one request: lists that check every item (recent, search hits, favorites) would otherwise reload the
    // user's groups, roles and organizations once per item (load test T1, docs/bao-cao-kiem-thu-tai.md).
    private readonly Dictionary<Guid, PrincipalSet> _principals = [];

    public async Task<PrincipalSet> GetPrincipalsAsync(Guid userId, CancellationToken ct = default)
    {
        if (_principals.TryGetValue(userId, out var known)) return known;
        var set = await LoadPrincipalsAsync(userId, ct);
        _principals[userId] = set;
        return set;
    }

    private async Task<PrincipalSet> LoadPrincipalsAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.OrganizationId, OrgPath = x.Organization != null ? x.Organization.Path : null }).FirstOrDefaultAsync(ct);
        var groups = await db.UserGroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(ct);
        var roles = await db.UserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct);
        var orgs = u?.OrgPath?.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList() ?? [];
        var p = await perms.GetPermissionsAsync(userId, ct);
        return new PrincipalSet(userId, groups, orgs, roles, p.Contains(Perm.DocAdmin), p.Contains(Perm.DocView), p.Contains(Perm.DocEdit));
    }

    public async Task<bool> CanAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default)
    {
        var a = await GetAccessAsync(userId, type, resourceId, ct);
        return level >= AccessLevel.Edit ? (level == AccessLevel.Manage ? a.CanManage : a.CanEdit) : a.Level >= level;
    }

    public async Task<ResourceAccess> EnsureAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default)
    {
        var a = await GetAccessAsync(userId, type, resourceId, ct);
        if (!a.CanView) throw new NotFoundException(type == ResourceType.Folder ? "Không tìm thấy thư mục hoặc bạn không có quyền truy cập." : "Không tìm thấy tài liệu hoặc bạn không có quyền truy cập.");
        if (level >= AccessLevel.Edit && a.WorkspaceLocked && a.Level >= level)
            throw new AppException("Nhóm tài nguyên đang bị khóa, chỉ cho phép xem.", 423);
        var ok = level switch { AccessLevel.Manage => a.CanManage, AccessLevel.Edit => a.CanEdit, _ => a.Level >= level };
        if (!ok) throw new ForbiddenException(level switch
        {
            AccessLevel.Download => "Bạn không có quyền tải xuống tài liệu này.",
            AccessLevel.Edit => "Bạn không có quyền chỉnh sửa tài liệu này.",
            AccessLevel.Manage => "Bạn không có quyền quản lý tài liệu này.",
            _ => "Bạn không có quyền xem tài liệu này.",
        });
        return a;
    }

    public async Task<ResourceAccess> EnsureCanUploadAsync(Guid userId, Guid workspaceId, Guid? folderId, CancellationToken ct = default)
    {
        var a = folderId.HasValue ? await GetAccessAsync(userId, ResourceType.Folder, folderId.Value, ct) : await GetAccessAsync(userId, ResourceType.Workspace, workspaceId, ct);
        if (!a.CanView) throw new NotFoundException("Không tìm thấy thư mục đích hoặc bạn không có quyền truy cập.");
        if (a.WorkspaceLocked) throw new AppException("Nhóm tài nguyên đang bị khóa, không thể tải lên hoặc tạo mới.", 423);
        if (!a.CanUpload) throw new ForbiddenException("Bạn không có quyền tải lên/tạo mới tại vị trí này.");
        return a;
    }

    public async Task<ResourceAccess> GetAccessAsync(Guid userId, ResourceType type, Guid resourceId, CancellationToken ct = default)
    {
        var p = await GetPrincipalsAsync(userId, ct);
        if (!p.CanViewDocs && !p.IsDocAdmin) return ResourceAccess.None;

        Guid workspaceId;
        List<Guid> folderChain; // nearest first: the item's own folder (or itself) then ancestors
        Guid? fileId = null;
        Guid? createdBy = null;
        var approved = true;
        var fileBreaks = false;
        switch (type)
        {
            case ResourceType.File:
            {
                var f = await db.Files.AsNoTracking().Where(x => x.Id == resourceId && !x.IsDeleted)
                    .Select(x => new { x.WorkspaceId, x.FolderId, FolderPath = x.Folder != null ? x.Folder.Path : null, x.CreatedBy, x.ApprovalStatus, x.BreakInheritance })
                    .FirstOrDefaultAsync(ct);
                if (f is null) return ResourceAccess.None;
                workspaceId = f.WorkspaceId;
                folderChain = PathIds(f.FolderPath);
                fileId = resourceId;
                createdBy = f.CreatedBy;
                approved = f.ApprovalStatus == ApprovalStatus.Approved;
                fileBreaks = f.BreakInheritance;
                break;
            }
            case ResourceType.Folder:
            {
                var fo = await db.Folders.AsNoTracking().Where(x => x.Id == resourceId && !x.IsDeleted)
                    .Select(x => new { x.WorkspaceId, x.Path, x.CreatedBy }).FirstOrDefaultAsync(ct);
                if (fo is null) return ResourceAccess.None;
                workspaceId = fo.WorkspaceId;
                folderChain = PathIds(fo.Path);
                createdBy = fo.CreatedBy;
                break;
            }
            default:
                workspaceId = resourceId;
                folderChain = [];
                break;
        }

        var ws = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => new { w.OwnerId, w.IsLocked, w.Type }).FirstOrDefaultAsync(ct);
        if (ws is null) return ResourceAccess.None;
        var locked = ws.IsLocked;

        if (p.IsDocAdmin) return new ResourceAccess(AccessLevel.Manage, true, true, locked, "admin");
        var isOwner = ws.OwnerId == userId;
        if (isOwner && p.CanEditDocs) return new ResourceAccess(AccessLevel.Manage, true, true, locked, "owner");

        var level = AccessLevel.None;
        var source = "none";
        var canUpload = false;
        var canApprove = false;
        var uncapped = AccessLevel.None; // before the role cap: decides where the user may upload (doc.upload is checked by the upload itself)
        void Grant(AccessLevel l, string why, bool viaMembership = false)
        {
            if (l > uncapped) uncapped = l;
            var effective = viaMembership ? p.CapByRole(l) : l;
            if (effective > level) { level = effective; source = why; }
        }
        if (isOwner)
        {
            // Owner whose role has no doc.edit: sees and downloads everything, edits only what was granted explicitly.
            Grant(AccessLevel.Manage, "owner", viaMembership: true);
            canUpload = true;
            canApprove = true;
        }

        // Break inheritance: the nearest folder (self included) flagged break_inheritance stops workspace-level and higher grants.
        var folderFlags = folderChain.Count == 0 ? new Dictionary<Guid, bool>()
            : await db.Folders.AsNoTracking().Where(f => folderChain.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.BreakInheritance, ct);
        var effectiveChain = new List<Guid>();
        var broken = fileBreaks; // a file with unique permissions inherits nothing from its folders
        foreach (var id in fileBreaks ? new List<Guid>() : folderChain)
        {
            effectiveChain.Add(id);
            if (folderFlags.GetValueOrDefault(id)) { broken = true; break; }
        }

        var member = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.UserId == userId).Select(m => (WorkspaceMemberRole?)m.MemberRole).FirstOrDefaultAsync(ct);
        if (member is { } role)
        {
            var managerial = role is WorkspaceMemberRole.Manager or WorkspaceMemberRole.Owner;
            if (managerial || !broken)
            {
                Grant(LevelOf(role), $"member:{EnumName(role)}", viaMembership: true);
                canUpload |= role is WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager or WorkspaceMemberRole.Editor or WorkspaceMemberRole.Uploader;
                canApprove |= role is WorkspaceMemberRole.Owner or WorkspaceMemberRole.Manager or WorkspaceMemberRole.Approver;
            }
            // Uploaders may edit what they uploaded themselves.
            if (role == WorkspaceMemberRole.Uploader && createdBy == userId) Grant(AccessLevel.Edit, "member:uploader_own", viaMembership: true);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var acl = await db.ResourcePermissions.AsNoTracking()
            .Where(a => (a.ExpiresAt == null || a.ExpiresAt > now) &&
                        ((fileId != null && a.ResourceType == ResourceType.File && a.ResourceId == fileId) ||
                         (a.ResourceType == ResourceType.Folder && effectiveChain.Contains(a.ResourceId)) ||
                         (!broken && a.ResourceType == ResourceType.Workspace && a.ResourceId == workspaceId)))
            .ToListAsync(ct);
        foreach (var a in acl.Where(a => p.Matches(a.PrincipalType, a.PrincipalId)))
        {
            var direct = (a.ResourceType == ResourceType.File) || (a.ResourceType == type && a.ResourceId == resourceId);
            if (!direct && !a.Inherit) continue;
            Grant(a.AccessLevel, direct ? "acl" : "acl_inherited");
            if (a.AccessLevel >= AccessLevel.Edit) canUpload = true;
        }

        // A folder share is inherited by sub-folders and files, down to a folder that breaks inheritance.
        var shares = await db.Shares.AsNoTracking()
            .Where(s => (fileId != null && s.ResourceType == ResourceType.File && s.ResourceId == fileId) ||
                        (s.ResourceType == ResourceType.Folder && effectiveChain.Contains(s.ResourceId)))
            .ToListAsync(ct);
        foreach (var s in shares.Where(s => p.Matches(s.SharedWithType, s.SharedWithId)))
        {
            Grant(s.AccessLevel, "share");
            if (s.AccessLevel >= AccessLevel.Edit) canUpload = true;
        }

        if (uncapped < AccessLevel.Edit) canUpload = false;
        if (member == WorkspaceMemberRole.Uploader && (!broken)) canUpload = true;

        // Approval visibility: only approved items are visible to plain viewers.
        if (type == ResourceType.File && !approved && createdBy != userId && level < AccessLevel.Edit && !canApprove)
            return ResourceAccess.None;

        return level == AccessLevel.None ? ResourceAccess.None : new ResourceAccess(level, canUpload, canApprove, locked, source);
    }

    public static List<Guid> PathIds(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var ids = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        ids.Reverse();
        return ids;
    }

    private static string EnumName(WorkspaceMemberRole r) => r.ToString().ToLowerInvariant();
}

/// <summary>Adapter used by the SignalR hub to authorize file subscriptions.</summary>
public sealed class ResourceAccessChecker(IResourceAuthorizationService auth) : IResourceAccessChecker
{
    public Task<bool> CanViewFileAsync(Guid userId, Guid fileId, CancellationToken ct = default) => auth.CanAsync(userId, ResourceType.File, fileId, AccessLevel.View, ct);
}

/// <summary>
/// Perm-Phase 3: the access checks of the whole application on the new permission model (<see cref="Features.Access.IItemPermissionService"/>),
/// mapped to the levels the features ask for. Upload = "Thêm nội dung", approve = "Phê duyệt"; a locked Space keeps its level but refuses
/// writes with 423 (as before). The document administrator no longer sees other people's content on its own (spec 3.3).
/// </summary>
public sealed class ModelResourceAuthorizationService(Features.Access.IItemPermissionService engine, LegacyResourceAuthorizationService legacy) : IResourceAuthorizationService
{
    public async Task<ResourceAccess> GetAccessAsync(Guid userId, ResourceType type, Guid resourceId, CancellationToken ct = default)
    {
        var (rights, locked) = await engine.GetWithLockAsync(userId, resourceId, ct);
        var level = Features.Access.ReferencePermissionEvaluator.ToLevel(rights);
        if (level == AccessLevel.None) return ResourceAccess.None;
        return new ResourceAccess(level, !locked && (rights & Domain.Permissions.AccessRight.AddItems) != 0,
            (rights & Domain.Permissions.AccessRight.ApproveItems) != 0, locked, "model");
    }

    public async Task<bool> CanAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default)
    {
        var a = await GetAccessAsync(userId, type, resourceId, ct);
        return level >= AccessLevel.Edit ? (level == AccessLevel.Manage ? a.CanManage : a.CanEdit) : a.Level >= level;
    }

    public async Task<ResourceAccess> EnsureAsync(Guid userId, ResourceType type, Guid resourceId, AccessLevel level, CancellationToken ct = default)
    {
        var a = await GetAccessAsync(userId, type, resourceId, ct);
        if (!a.CanView) throw new NotFoundException(type == ResourceType.Folder ? "Không tìm thấy thư mục hoặc bạn không có quyền truy cập." : "Không tìm thấy tài liệu hoặc bạn không có quyền truy cập.");
        if (level >= AccessLevel.Edit && a.WorkspaceLocked && a.Level >= level)
            throw new AppException("Nhóm tài nguyên đang bị khóa, chỉ cho phép xem.", 423);
        var ok = level switch { AccessLevel.Manage => a.CanManage, AccessLevel.Edit => a.CanEdit, _ => a.Level >= level };
        if (!ok) throw new ForbiddenException(level switch
        {
            AccessLevel.Download => "Bạn không có quyền tải xuống tài liệu này.",
            AccessLevel.Edit => "Bạn không có quyền chỉnh sửa tài liệu này.",
            AccessLevel.Manage => "Bạn không có quyền quản lý tài liệu này.",
            _ => "Bạn không có quyền xem tài liệu này.",
        });
        return a;
    }

    public async Task<ResourceAccess> EnsureCanUploadAsync(Guid userId, Guid workspaceId, Guid? folderId, CancellationToken ct = default)
    {
        var a = folderId.HasValue ? await GetAccessAsync(userId, ResourceType.Folder, folderId.Value, ct) : await GetAccessAsync(userId, ResourceType.Workspace, workspaceId, ct);
        if (!a.CanView) throw new NotFoundException("Không tìm thấy thư mục đích hoặc bạn không có quyền truy cập.");
        if (a.WorkspaceLocked) throw new AppException("Nhóm tài nguyên đang bị khóa, không thể tải lên hoặc tạo mới.", 423);
        if (!a.CanUpload) throw new ForbiddenException("Bạn không có quyền tải lên/tạo mới tại vị trí này.");
        return a;
    }

    /// <summary>Principals for the SQL filters that still read the current model (replaced channel by channel in Perm-Phase 3).</summary>
    public Task<PrincipalSet> GetPrincipalsAsync(Guid userId, CancellationToken ct = default) => legacy.GetPrincipalsAsync(userId, ct);
}
