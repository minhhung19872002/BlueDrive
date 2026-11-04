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

public class ShareLink : AuditableEntity
{
    public string Token { get; set; } = "";
    public ResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public bool AllowDownload { get; set; }
    public bool AllowView { get; set; } = true;
    public string? PasswordHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? MaxAccessCount { get; set; }
    public int AccessCount { get; set; }
    public bool IsRevoked { get; set; }
    public bool RequireLogin { get; set; }
    /// <summary>Anyone with the link may edit Office documents in the online editor (co-editing with signed-in users).</summary>
    public bool AllowEdit { get; set; }
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
