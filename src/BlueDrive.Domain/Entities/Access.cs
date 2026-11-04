using BlueDrive.Domain.Common;
using BlueDrive.Domain.Enums;
using BlueDrive.Domain.Permissions;

namespace BlueDrive.Domain.Entities;

// Storage and permission model like Google Drive / SharePoint (PROMPT_BlueDrive_Storage_Permissions.md, docs/migration-permissions.md).

/// <summary>Không gian dùng chung: owned by the organisation, not by a person. Same id as its workspace.</summary>
public class Space
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? CoverFileId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public SpaceStatus Status { get; set; }
    /// <summary>Sharing and other policies of the Space (jsonb).</summary>
    public string Policy { get; set; } = "{}";
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public Guid? DefaultLabelId { get; set; }
    public string? Template { get; set; }
}

/// <summary>Drive của tôi: one per user. Same id as their personal workspace.</summary>
public class PersonalDrive
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
}

/// <summary>Thư viện tài liệu of a Space or of a Drive của tôi, with its own settings (versioning, check-out, approval…).</summary>
public class Library
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? SpaceId { get; set; }
    public Guid? PersonalDriveId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>jsonb: versioning, require_checkout, content_approval, allowed_types, max_size, template_ids.</summary>
    public string Settings { get; set; } = "{}";
    public bool IsDefault { get; set; }
}

/// <summary>
/// A node of the permission tree. Spaces and drives are roots; a Library sits between the root and its top folders. Folder and file
/// rows keep their own tables and share their id with their securable.
/// </summary>
public class Securable
{
    public Guid Id { get; set; }
    public SecurableKind Kind { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? LibraryId { get; set; }
    public Guid? SpaceId { get; set; }
    public Guid? PersonalDriveId { get; set; }
    /// <summary>Owner in Drive của tôi (full rights on their own items).</summary>
    public Guid? OwnerUserId { get; set; }
    /// <summary>ltree of the ids (32 hex characters) from the root down to this node.</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasUniqueAcl { get; set; }
    /// <summary>Nearest node (itself included) with <see cref="HasUniqueAcl"/>: its ACL decides.</summary>
    public Guid AclScopeId { get; set; }
    public Guid? LabelId { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Who can be granted access. Users, groups and roles keep their own id; an organisation unit has two principals: the unit alone
/// (its id) and the unit with its sub-units (<see cref="PrincipalKind.OrgUnitTree"/>, own id).
/// </summary>
public class Principal
{
    public Guid Id { get; set; }
    public PrincipalKind Kind { get; set; }
    public Guid? RefId { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>Flattened membership: every principal a user counts as (themselves, nested groups, units, Space groups, everyone…).</summary>
public class PrincipalMembership
{
    public Guid UserId { get; set; }
    public Guid PrincipalId { get; set; }
}

/// <summary>Bumped when a user's memberships change: permission caches keyed on it go stale.</summary>
public class MembershipVersion
{
    public Guid UserId { get; set; }
    public long Version { get; set; }
}

/// <summary>A group nested in another group (no cycles).</summary>
public class GroupNesting
{
    public Guid ParentGroupId { get; set; }
    public Guid ChildGroupId { get; set; }
}

/// <summary>Default groups of a Space (Chủ sở hữu / Thành viên / Khách xem…) or custom ones. Also a principal (same id).</summary>
public class SpaceGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The Space or Drive của tôi (workspace id).</summary>
    public Guid RootId { get; set; }
    public SpaceGroupKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Guid PermissionLevelId { get; set; }
}

public class SpaceGroupMember
{
    public Guid SpaceGroupId { get; set; }
    /// <summary>A user, a group or an organisation unit principal.</summary>
    public Guid PrincipalId { get; set; }
}

/// <summary>Cấp quyền: a named set of atomic rights. System levels cannot be deleted (copy them to customise).</summary>
public class PermissionLevel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public AccessRight Permissions { get; set; }
    public bool IsSystem { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A node with its own ACL; <see cref="AclVersion"/> is bumped on every change (cache key).</summary>
public class AclScope
{
    public Guid ScopeId { get; set; }
    public long AclVersion { get; set; }
}

/// <summary>One grant of a permission level to a principal on a scope.</summary>
public class AclEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ScopeId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid PermissionLevelId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; }
    public AclSource Source { get; set; }
    /// <summary>"google_additive" sharing mode: an extra grant on <see cref="AdditiveTargetId"/> without breaking inheritance.</summary>
    public bool IsAdditive { get; set; }
    public Guid? AdditiveTargetId { get; set; }
    /// <summary>False: "chỉ mục này" — the grant is for the scope's own node, not for the nodes below that inherit from it.</summary>
    public bool AppliesToChildren { get; set; } = true;
    /// <summary>Note sent with the grant ("Gửi thông báo kèm lời nhắn"), shown in "Được chia sẻ với tôi".</summary>
    public string? Message { get; set; }
    /// <summary>When "sắp hết hạn" was announced (spec 5.5); cleared when the access is extended.</summary>
    public DateTime? ReminderSentAt { get; set; }
    public DateTime? ExpiredNoticeAt { get; set; }
}

/// <summary>"Yêu cầu quyền truy cập" (spec 5.2): someone asks for a level on an item; who manages it approves (maybe another level) or refuses.</summary>
public class AccessRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SecurableId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid RequestedLevelId { get; set; }
    public string? Message { get; set; }
    public AccessRequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public Guid? DecidedLevelId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>Flattened ACL used to trim queries in SQL: per scope and principal, the OR of the rights of its live entries.</summary>
public class ScopeGrant
{
    public Guid ScopeId { get; set; }
    public Guid PrincipalId { get; set; }
    public AccessRight Permissions { get; set; }
}
