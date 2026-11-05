namespace BlueDrive.Domain.Enums;

// Enum values are persisted as snake_case strings (see SnakeCaseEnumConverter).

public enum OrganizationType { DonVi, PhongBan }

public enum WorkspaceType { Personal, Group, Organization }

public enum WorkspaceMemberRole { Owner, Manager, Editor, Uploader, Approver, Viewer }

public enum MediaType { Image, Video, Audio, Document, Spreadsheet, Presentation, Pdf, Infographic, Epub, Archive, Design, Other }

public enum ApprovalStatus { Draft, Pending, Approved, Rejected }

/// <summary>Skipped: no scanner configured. TooLarge: above the scanner stream limit. Failed: the scanner kept failing (retries exhausted).</summary>
public enum ScanStatus { Pending, Scanning, Clean, Infected, Skipped, TooLarge, Failed }

public enum ResourceType { Workspace, Folder, File }

public enum PrincipalType { User, Group, Organization, Role }

/// <summary>Ordered: a higher level implies all lower levels.</summary>
public enum AccessLevel { None = 0, View = 1, Download = 2, Edit = 3, Manage = 4 }

public enum AuditAction
{
    Login, Logout, LoginFailed, Upload, Download, View, Edit, Create, Share, Unshare, Rename, Move, Copy,
    Delete, Restore, Purge, PermissionChange, ConfigChange, PasswordChange, PasswordReset, SessionRevoke,
    UserLock, UserUnlock, Approve, Reject, VersionRestore, Comment, ShareLinkAccess, Export, Backup, Import,
    // Storage and permission model (PROMPT_BlueDrive_Storage_Permissions.md, section 9)
    PermissionGranted, PermissionRevoked, PermissionLevelChanged, InheritanceBroken, InheritanceRestored, LinkCreated, LinkUsed, LinkRevoked,
    AccessRequested, AccessRequestDecided, OwnershipTransferred, LabelChanged, HoldApplied, HoldRemoved, EmergencyAccess, GuestInvited, GuestLogin
}

public enum AlertType { BruteForce, QuotaExceeded, QuotaWarning, UnusualDownload, BackupFailed, MalwareDetected, MalwareScanFailed }

public enum AlertSeverity { Info, Warning, Critical }

public enum BackupType { Scheduled, Manual }

public enum JobStatus { Queued, Running, Succeeded, Failed, Expired }

public enum TicketPriority { KhanCap, KhongKhanCap }

public enum TicketStatus { Moi, DangXuLy, ChoPhanHoi, DaXuLy, Dong }

public enum FavoriteItemType { Folder, File }

/// <summary>Operation recorded in the change journal (delta sync for BlueDrive Desktop).</summary>
public enum ChangeOp { Create, UpdateContent, UpdateMeta, Rename, Move, Delete, Restore, Purge, PermissionChanged }

/// <summary>Item kind in the change journal: a workspace entry means "access to the whole root changed".</summary>
public enum ChangeItemType { File, Folder, Workspace }

public enum DeviceStatus { Active, Revoked, WipePending, Wiped }

/// <summary>Information-security incident category (HSMT 3.4, TT 20/2017/TT-BTTTT).</summary>
public enum IncidentCategory { Malware, Intrusion, Vulnerability, DataLeak, DenialOfService, AccountCompromise, Other }

public enum IncidentSeverity { Low, Medium, High, Critical }

/// <summary>Incident handling workflow: report → investigate → contain → recover → resolved → closed.</summary>
public enum IncidentStatus { Reported, Investigating, Containing, Recovering, Resolved, Closed }

/// <summary>System announcement banner style (5.2).</summary>
public enum AnnouncementLevel { Info, Warning, Maintenance }

/// <summary>Spec 7.2: what a retention policy covers.</summary>
public enum RetentionScope { Workspace, Library, Label, Catalog }

/// <summary>Spec 7.2: retention counted from the creation or the last change.</summary>
public enum RetentionBasis { Created, Modified }

/// <summary>Spec 7.2: when retention ends — nothing, a review list, or deletion.</summary>
public enum RetentionExpiry { None, Review, Delete }

/// <summary>Spec 7.3: what a legal hold covers.</summary>
public enum HoldScope { Workspace, Folder, User }
