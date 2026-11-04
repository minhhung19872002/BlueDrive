using BlueDrive.Domain.Common;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Domain.Entities;

public class Catalog : AuditableEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public int SortOrder { get; set; }
    public List<CatalogItem> Items { get; set; } = [];
}

public class CatalogItem : AuditableEntity
{
    public Guid CatalogId { get; set; }
    public Catalog? Catalog { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Workspace : AuditableEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public bool IsLocked { get; set; }
    public WorkspaceType Type { get; set; }
    public List<WorkspaceMember> Members { get; set; } = [];
    /// <summary>Sharing outside the organization for this Space / drive (null = as the system allows; can only be stricter).</summary>
    public ExternalSharing? ExternalSharing { get; set; }
    /// <summary>Members may share with people who are not in the Space.</summary>
    public bool MembersShareOutside { get; set; } = true;
    /// <summary>Single files and folders may be shared (given their own permissions).</summary>
    public bool AllowItemSharing { get; set; } = true;
    public AccessRequestDeciders AccessRequestDeciders { get; set; }
    /// <summary>Spec 6: active → archived (read only, hidden from the default list, still found by search) → deleted (system trash, 93 days).</summary>
    public SpaceStatus Status { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    /// <summary>Cover image: an image file of the Space.</summary>
    public Guid? CoverFileId { get; set; }
    /// <summary>Template the Space was created from (blank, department, project, campaign).</summary>
    public string? Template { get; set; }
    /// <summary>People who do not manage the Space may download / print / copy.</summary>
    public bool AllowDownload { get; set; } = true;
    /// <summary>The Space may be synced to computers (BlueDrive Desktop).</summary>
    public bool AllowSync { get; set; } = true;
    /// <summary>Days items stay in the Space's trash (null = as the system).</summary>
    public int? TrashRetentionDays { get; set; }
    /// <summary>Members see the Activity tab (otherwise only who manages the Space).</summary>
    public bool ActivityForMembers { get; set; } = true;
}

/// <summary>A user group or a unit (with its sub-units) in one of the Space's member groups (spec 6 "Thành viên").</summary>
public class WorkspaceGroupMember
{
    public Guid WorkspaceId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public Guid PrincipalId { get; set; }
    public WorkspaceMemberRole MemberRole { get; set; }
    public DateTime AddedAt { get; set; }
}

public class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public WorkspaceMemberRole MemberRole { get; set; }
    public DateTime AddedAt { get; set; }
}

public class Folder : AuditableEntity, ISoftDeletable
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid? ParentId { get; set; }
    public Folder? Parent { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Materialized path of ancestor ids incl. self: "/{a}/{b}/{self}/".</summary>
    public string Path { get; set; } = "/";
    public bool RequiresApproval { get; set; }
    public bool BreakInheritance { get; set; }
    /// <summary>Sharing outside the organization for this item and what is below it (null = as above; can only be stricter).</summary>
    public ExternalSharing? ExternalSharing { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public Guid? OriginalParentId { get; set; }
    /// <summary>True when the folder itself was deleted (not as a descendant of a deleted folder).</summary>
    public bool IsTrashRoot { get; set; }
    /// <summary>Incremented on every change recorded in the change journal; the sync ETag.</summary>
    public long Revision { get; set; } = 1;
}

public class StoredFile : AuditableEntity, ISoftDeletable
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid? FolderId { get; set; }
    public Folder? Folder { get; set; }
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public MediaType MediaType { get; set; }
    public long SizeBytes { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionReason { get; set; }
    public ScanStatus ScanStatus { get; set; } = ScanStatus.Pending;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailKey { get; set; }
    public string? PreviewKey { get; set; }
    /// <summary>Organization of the uploader at upload time ("đơn vị đăng tải").</summary>
    public Guid? UploaderOrganizationId { get; set; }
    /// <summary>Unique permissions (like SharePoint "Stop inheriting permissions"): folder grants no longer reach this file.</summary>
    public bool BreakInheritance { get; set; }
    /// <summary>Sharing outside the organization for this item and what is below it (null = as above; can only be stricter).</summary>
    public ExternalSharing? ExternalSharing { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public Guid? OriginalFolderId { get; set; }
    public bool IsTrashRoot { get; set; }
    /// <summary>Incremented on every change recorded in the change journal; the sync ETag.</summary>
    public long Revision { get; set; } = 1;
    public List<FileVersion> Versions { get; set; } = [];
    public List<FileCatalogItem> CatalogItems { get; set; } = [];
}

public class FileVersion : Entity
{
    public Guid FileId { get; set; }
    public StoredFile? File { get; set; }
    public int VersionNo { get; set; }
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Checksum { get; set; }
    public string? ChangeNote { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsEncrypted { get; set; }
    /// <summary>Per-file data key wrapped by the master key (envelope encryption), base64.</summary>
    public string? WrappedKey { get; set; }
}

public class FileCatalogItem
{
    public Guid FileId { get; set; }
    public Guid CatalogItemId { get; set; }
    public CatalogItem? CatalogItem { get; set; }
}

public class UserFavorite : Entity
{
    public Guid UserId { get; set; }
    public FavoriteItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RecentItem : Entity
{
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public string Action { get; set; } = "view";
    public DateTime AccessedAt { get; set; }
}

public class ResourcePermission : AuditableEntity
{
    public ResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public PrincipalType PrincipalType { get; set; }
    public Guid PrincipalId { get; set; }
    public AccessLevel AccessLevel { get; set; }
    public bool Inherit { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
}

public class Share : Entity
{
    public ResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public Guid SharedBy { get; set; }
    public PrincipalType SharedWithType { get; set; }
    public Guid SharedWithId { get; set; }
    public AccessLevel AccessLevel { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Text of the current version (Word, Excel, PowerPoint, OpenDocument, PDF, plain text), extracted after the malware scan for
/// searching inside documents. Kept apart from <see cref="StoredFile"/> so listings never load it.
/// </summary>
public class FileText
{
    public Guid FileId { get; set; }
    public Guid VersionId { get; set; }
    public string Text { get; set; } = "";
    public DateTime ExtractedAt { get; set; }
}

public class ShareLink : AuditableEntity
{
    /// <summary>SHA-256 (hex) of the token; the token itself is never stored (new links: derived from the id with a server key).</summary>
    public string TokenHash { get; set; } = "";
    public ShareLinkScope Scope { get; set; }
    /// <summary>Level the link gives (Organization / Specific: through the model, the link is a principal; Anyone: the visitor's rights).</summary>
    public Guid? LevelId { get; set; }
    /// <summary>When "sắp hết hạn" was announced to who made it (spec 5.5); cleared when it is extended.</summary>
    public DateTime? ReminderSentAt { get; set; }
    public ResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public bool AllowDownload { get; set; }
    public bool AllowView { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public int? MaxAccessCount { get; set; }
    public int AccessCount { get; set; }
    public bool IsRevoked { get; set; }
    public bool RequireLogin { get; set; }
    /// <summary>Anyone with the link may edit Office documents in the online editor (co-editing with signed-in users).</summary>
    public bool AllowEdit { get; set; }
}

/// <summary>Spec 1.2: a shortcut to a file or folder in a drive or a Space folder. It carries no rights; deleting it leaves the target.</summary>
public class Shortcut : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? FolderId { get; set; }
    public Guid TargetId { get; set; }
    /// <summary>file or folder.</summary>
    public string TargetType { get; set; } = "file";
    /// <summary>Name of the target when the shortcut was made (shown to who may no longer see the target).</summary>
    public string Name { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A person a link works for: listed ("Người cụ thể") or who opened an organization link — member of the link principal.</summary>
public class ShareLinkMember
{
    public Guid LinkId { get; set; }
    public Guid UserId { get; set; }
    public DateTime AddedAt { get; set; }
}

public class Comment : AuditableEntity
{
    public Guid FileId { get; set; }
    public Guid? ParentId { get; set; }
    public string Content { get; set; } = "";
    public List<Guid> Mentions { get; set; } = [];
    /// <summary>Attached files (stored as regular files in the commenter's personal workspace).</summary>
    public List<Guid> AttachmentFileIds { get; set; } = [];
    public bool IsEdited { get; set; }
    public bool IsDeleted { get; set; }
}

public class Notification : Entity
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SavedSearch : Entity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string QueryJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
