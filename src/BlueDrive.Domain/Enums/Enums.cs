namespace BlueDrive.Domain.Enums;

// Enum values are persisted as snake_case strings (see SnakeCaseEnumConverter).

public enum OrganizationType { DonVi, PhongBan }

public enum WorkspaceType { Personal, Group, Organization }

public enum WorkspaceMemberRole { Owner, Manager, Editor, Uploader, Approver, Viewer }

public enum MediaType { Image, Video, Audio, Document, Spreadsheet, Presentation, Pdf, Infographic, Epub, Archive, Design, Other }

public enum ApprovalStatus { Draft, Pending, Approved, Rejected }

public enum ScanStatus { Pending, Scanning, Clean, Infected, Skipped }

public enum ResourceType { Workspace, Folder, File }

public enum PrincipalType { User, Group, Organization, Role }

/// <summary>Ordered: a higher level implies all lower levels.</summary>
public enum AccessLevel { None = 0, View = 1, Download = 2, Edit = 3, Manage = 4 }

public enum AuditAction
{
    Login, Logout, LoginFailed, Upload, Download, View, Edit, Create, Share, Unshare, Rename, Move, Copy,
    Delete, Restore, Purge, PermissionChange, ConfigChange, PasswordChange, PasswordReset, SessionRevoke,
    UserLock, UserUnlock, Approve, Reject, VersionRestore, Comment, ShareLinkAccess, Export, Backup, Import
}

public enum AlertType { BruteForce, QuotaExceeded, QuotaWarning, UnusualDownload, BackupFailed, MalwareDetected }

public enum AlertSeverity { Info, Warning, Critical }

public enum BackupType { Scheduled, Manual }

public enum JobStatus { Queued, Running, Succeeded, Failed }

public enum TicketPriority { KhanCap, KhongKhanCap }

public enum TicketStatus { Moi, DangXuLy, ChoPhanHoi, DaXuLy, Dong }

public enum FavoriteItemType { Folder, File }
