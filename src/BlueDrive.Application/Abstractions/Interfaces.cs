using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueDrive.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<FileText> FileTexts { get; }
    DbSet<Space> Spaces { get; }
    DbSet<PersonalDrive> PersonalDrives { get; }
    DbSet<Library> Libraries { get; }
    DbSet<Securable> Securables { get; }
    DbSet<Principal> Principals { get; }
    DbSet<PrincipalMembership> PrincipalMemberships { get; }
    DbSet<MembershipVersion> MembershipVersions { get; }
    DbSet<GroupNesting> GroupNestings { get; }
    DbSet<SpaceGroup> SpaceGroups { get; }
    DbSet<SpaceGroupMember> SpaceGroupMembers { get; }
    DbSet<PermissionLevel> PermissionLevels { get; }
    DbSet<AclScope> AclScopes { get; }
    DbSet<AclEntry> AclEntries { get; }
    DbSet<ScopeGrant> ScopeGrants { get; }
    DbSet<AccessRequest> AccessRequests { get; }
    DbSet<User> Users { get; }
    DbSet<PasswordHistory> PasswordHistories { get; }
    DbSet<UserGroup> UserGroups { get; }
    DbSet<UserGroupMember> UserGroupMembers { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<Catalog> Catalogs { get; }
    DbSet<CatalogItem> CatalogItems { get; }
    DbSet<Workspace> Workspaces { get; }
    DbSet<WorkspaceMember> WorkspaceMembers { get; }
    DbSet<Folder> Folders { get; }
    DbSet<StoredFile> Files { get; }
    DbSet<FileVersion> FileVersions { get; }
    DbSet<FileCatalogItem> FileCatalogItems { get; }
    DbSet<UserFavorite> UserFavorites { get; }
    DbSet<RecentItem> RecentItems { get; }
    DbSet<ResourcePermission> ResourcePermissions { get; }
    DbSet<Share> Shares { get; }
    DbSet<ShareLink> ShareLinks { get; }
    DbSet<ShareLinkMember> ShareLinkMembers { get; }
    DbSet<Comment> Comments { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<SavedSearch> SavedSearches { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Alert> Alerts { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<BackupJob> BackupJobs { get; }
    DbSet<SupportTicket> SupportTickets { get; }
    DbSet<TicketEvent> TicketEvents { get; }
    DbSet<ChangeLog> ChangeLogs { get; }
    DbSet<SecurityIncident> SecurityIncidents { get; }
    DbSet<IncidentEvent> IncidentEvents { get; }
    DbSet<SystemAnnouncement> SystemAnnouncements { get; }
    DbSet<ReportJob> ReportJobs { get; }
    DbSet<SystemLog> SystemLogs { get; }
    DbSet<ChangeJournalEntry> ChangeJournal { get; }
    DbSet<SyncDevice> Devices { get; }
    DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes { get; }
    DatabaseFacade Database { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The authenticated caller of the current request (or a system principal in jobs).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Username { get; }
    Guid? SessionId { get; }
    /// <summary>Desktop sync device of the session (claim "did"); null for web/mobile sessions and jobs.</summary>
    Guid? DeviceId => null;
    string? Ip { get; }
    string? UserAgent { get; }
    bool IsAuthenticated { get; }
    Guid RequireUserId();
}

public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record AuditEntry(
    AuditAction Action,
    ResourceType? ResourceType = null,
    Guid? ResourceId = null,
    string? ResourceName = null,
    Guid? WorkspaceId = null,
    object? Detail = null,
    Guid? UserId = null,
    string? Username = null);

/// <summary>Marker for MediatR requests that should be audited automatically by the pipeline.</summary>
public interface IAuditableRequest
{
    AuditEntry? ToAuditEntry(object? response);
}

/// <summary>Blob storage abstraction. Keys are opaque; the real path is never exposed to clients.</summary>
public interface IFileStorage
{
    Task WriteAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    /// <summary>Opens the blob positioned at <paramref name="offset"/> (ranged GET for S3, seek for local files).</summary>
    Task<Stream> OpenReadAtAsync(string key, long offset, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<long> GetSizeAsync(string key, CancellationToken ct = default);
    /// <summary>Every blob under <paramref name="prefix"/> ("" = all), with size and last write time (incremental backups).</summary>
    IAsyncEnumerable<StoredObject> ListAsync(string prefix, CancellationToken ct = default);
}

public sealed record StoredObject(string Key, long Size, DateTime LastModifiedUtc);

public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasAsync(Guid userId, string permission, CancellationToken ct = default);
    void Invalidate(Guid? userId = null);
}

public interface ISettingsService
{
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

public interface IUserNotifier
{
    Task NotifyUserAsync(Guid userId, string type, string title, string? body, string? link, CancellationToken ct = default);
    Task NotifyPermissionAsync(string permission, string type, string title, string? body, string? link, CancellationToken ct = default);
    Task BroadcastToGroupAsync(string group, string method, object payload, CancellationToken ct = default);
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

public interface ISensitiveDataProtector
{
    string? Protect(string? plain);
    string? Unprotect(string? cipher);
    string? Hash(string? plain);
}

/// <summary>Minimal file visibility check used by realtime subscriptions.</summary>
public interface IResourceAccessChecker
{
    Task<bool> CanViewFileAsync(Guid userId, Guid fileId, CancellationToken ct = default);
}
