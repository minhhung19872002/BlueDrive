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
    /// <summary>Default sensitivity label of what is in the Space (spec 7.1).</summary>
    public Guid? DefaultLabelId { get; set; }
    /// <summary>Who manages the Space may move items out of it (spec 1.1 "có thể bị cấm").</summary>
    public bool AllowMoveOut { get; set; } = true;
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
    /// <summary>A top folder that is a document library of its own (spec 1.4), with its own settings.</summary>
    public bool IsLibrary { get; set; }
    /// <summary>Sensitivity label of the folder (what is below follows unless labelled itself).</summary>
    public Guid? LabelId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public Guid? OriginalParentId { get; set; }
    /// <summary>True when the folder itself was deleted (not as a descendant of a deleted folder).</summary>
    public bool IsTrashRoot { get; set; }
    /// <summary>Purged while kept by a retention policy: in the preservation store (spec 7.2), hidden from the trash.</summary>
    public DateTime? PreservedAt { get; set; }
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
    /// <summary>Latest major (published) version: what people who may not see drafts get (spec 1.4); null = never published.</summary>
    public Guid? PublishedVersionId { get; set; }
    /// <summary>Checked out (spec 1.4): only this person changes it until it is checked in.</summary>
    public Guid? CheckedOutBy { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    /// <summary>Sensitivity label of the file itself (spec 7.1).</summary>
    public Guid? LabelId { get; set; }
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
    /// <summary>Purged while kept by a retention policy: in the preservation store (spec 7.2), hidden from the trash.</summary>
    public DateTime? PreservedAt { get; set; }
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
    /// <summary>Major.Minor label (1.0, 1.1 …); a minor version is a draft.</summary>
    public int Major { get; set; }
    public int Minor { get; set; }
    public bool IsPublished { get; set; } = true;
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

/// <summary>
/// Settings of a document library (spec 1.4). The Space's default library ("Tài liệu") has the id <c>PermissionProjection.LibraryOf(space)</c>;
/// other libraries are top folders flagged <see cref="Folder.IsLibrary"/> and use the folder's id.
/// </summary>
public class LibrarySettings
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "Tài liệu";
    public bool IsDefault { get; set; }
    public VersioningMode Versioning { get; set; }
    public int MajorLimit { get; set; } = 100;
    public int MinorLimit { get; set; } = 10;
    /// <summary>Versions older than this many days are deleted (null = kept); the current and the published version always stay.</summary>
    public int? DeleteVersionsOlderThanDays { get; set; }
    public bool RequireCheckout { get; set; }
    public bool ContentApproval { get; set; }
    /// <summary>File types allowed in this library (empty = as the system); never wider than the system.</summary>
    public List<string> AllowedExtensions { get; set; } = [];
    public int? MaxFileSizeMb { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A metadata column of a library (spec 1.4): type, required, default, unique, validation.</summary>
public class LibraryColumn : Entity
{
    public Guid LibraryId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Stable key used in views and the API (latin, lower case).</summary>
    public string Key { get; set; } = "";
    public ColumnType Type { get; set; }
    public bool Required { get; set; }
    public bool Unique { get; set; }
    /// <summary>Default value as JSON (string, number, true/false, array).</summary>
    public string? DefaultValue { get; set; }
    /// <summary>Text: a regular expression; Number: "min..max".</summary>
    public string? Validation { get; set; }
    public string? ValidationMessage { get; set; }
    public List<string> Choices { get; set; } = [];
    /// <summary>Type Catalog: the shared catalog whose items are offered.</summary>
    public Guid? CatalogId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A file's value in a column; the typed copies serve sorting and filtering.</summary>
public class FileColumnValue
{
    public Guid FileId { get; set; }
    public Guid ColumnId { get; set; }
    /// <summary>The value as JSON.</summary>
    public string Value { get; set; } = "null";
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public DateTime? DateValue { get; set; }
}

/// <summary>A column's default for what is added in a folder (and below it) — spec 1.4 "giá trị mặc định theo thư mục".</summary>
public class FolderColumnDefault
{
    public Guid FolderId { get; set; }
    public Guid ColumnId { get; set; }
    public string Value { get; set; } = "null";
}

/// <summary>A view of a library (spec 1.4): columns, sort, filters, grouping, flat; public (owner null) or personal.</summary>
public class LibraryView : Entity
{
    public Guid LibraryId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    public List<string> Columns { get; set; } = [];
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    /// <summary>JSON list of { column, op, value } (op: eq, ne, contains, gt, lt, empty, notempty).</summary>
    public string Filters { get; set; } = "[]";
    public string? GroupBy { get; set; }
    public bool Flat { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A document template offered by "Mới" in a library (spec 1.4): a file of the Space copied when used.</summary>
public class LibraryTemplate : Entity
{
    public Guid LibraryId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public Guid FileId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Sensitivity label (spec 7.1): a colour, a description and what it enforces. BlueDrive is not for information classified as state secret.
/// </summary>
public class SensitivityLabel : Entity
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#1677ff";
    public string Description { get; set; } = "";
    /// <summary>Higher = more restrictive; lowering a label needs a reason.</summary>
    public int Priority { get; set; }
    public bool BlockExternal { get; set; }
    public bool BlockAnyoneLinks { get; set; }
    public bool ViewOnlyOutsideSpace { get; set; }
    public bool Watermark { get; set; }
    public bool BlockSync { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Spec 1.1: an offer to make someone else the owner of an item of one's Drive của tôi; it takes effect when they accept.</summary>
public class OwnershipTransfer : Entity
{
    public Guid ItemId { get; set; }
    public string ItemType { get; set; } = "file";
    public string ItemName { get; set; } = "";
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public OwnershipTransferStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
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

/// <summary>Spec 7.2: keeps what it covers N years (from creation or last change); what is deleted meanwhile goes to the preservation store.</summary>
public class RetentionPolicy : Entity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public RetentionScope ScopeKind { get; set; }
    /// <summary>Space, library, label or catalog item.</summary>
    public Guid ScopeId { get; set; }
    /// <summary>The Space of a library.</summary>
    public Guid? WorkspaceId { get; set; }
    public int Years { get; set; }
    public RetentionBasis Basis { get; set; }
    public RetentionExpiry OnExpiry { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Spec 7.3: nothing it covers is purged — not even by an administrator — until it is released.</summary>
public class LegalHold : Entity
{
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public HoldScope ScopeKind { get; set; }
    public Guid ScopeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }
}
