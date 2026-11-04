using BlueDrive.Application.Abstractions;
using BlueDrive.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;

namespace BlueDrive.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext, IDataProtectionKeyContext
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Catalog> Catalogs => Set<Catalog>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<FileCatalogItem> FileCatalogItems => Set<FileCatalogItem>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();
    public DbSet<RecentItem> RecentItems => Set<RecentItem>();
    public DbSet<ResourcePermission> ResourcePermissions => Set<ResourcePermission>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<ShareLinkMember> ShareLinkMembers => Set<ShareLinkMember>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<Shortcut> Shortcuts => Set<Shortcut>();
    public DbSet<LibrarySettings> LibrarySettings => Set<LibrarySettings>();
    public DbSet<SensitivityLabel> SensitivityLabels => Set<SensitivityLabel>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();
    public DbSet<OffboardingRecord> OffboardingRecords => Set<OffboardingRecord>();
    public DbSet<EmergencyAccess> EmergencyAccesses => Set<EmergencyAccess>();
    public DbSet<LibraryColumn> LibraryColumns => Set<LibraryColumn>();
    public DbSet<FileColumnValue> FileColumnValues => Set<FileColumnValue>();
    public DbSet<FolderColumnDefault> FolderColumnDefaults => Set<FolderColumnDefault>();
    public DbSet<LibraryView> LibraryViews => Set<LibraryView>();
    public DbSet<LibraryTemplate> LibraryTemplates => Set<LibraryTemplate>();
    public DbSet<OwnershipTransfer> OwnershipTransfers => Set<OwnershipTransfer>();
    public DbSet<WorkspaceGroupMember> WorkspaceGroupMembers => Set<WorkspaceGroupMember>();
    public DbSet<GuestOtp> GuestOtps => Set<GuestOtp>();
    public DbSet<FileText> FileTexts => Set<FileText>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<PersonalDrive> PersonalDrives => Set<PersonalDrive>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<Securable> Securables => Set<Securable>();
    public DbSet<Principal> Principals => Set<Principal>();
    public DbSet<PrincipalMembership> PrincipalMemberships => Set<PrincipalMembership>();
    public DbSet<MembershipVersion> MembershipVersions => Set<MembershipVersion>();
    public DbSet<GroupNesting> GroupNestings => Set<GroupNesting>();
    public DbSet<SpaceGroup> SpaceGroups => Set<SpaceGroup>();
    public DbSet<SpaceGroupMember> SpaceGroupMembers => Set<SpaceGroupMember>();
    public DbSet<PermissionLevel> PermissionLevels => Set<PermissionLevel>();
    public DbSet<AclScope> AclScopes => Set<AclScope>();
    public DbSet<AclEntry> AclEntries => Set<AclEntry>();
    public DbSet<ScopeGrant> ScopeGrants => Set<ScopeGrant>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<ChangeLog> ChangeLogs => Set<ChangeLog>();
    public DbSet<SecurityIncident> SecurityIncidents => Set<SecurityIncident>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<SystemAnnouncement> SystemAnnouncements => Set<SystemAnnouncement>();
    public DbSet<ReportJob> ReportJobs => Set<ReportJob>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<ChangeJournalEntry> ChangeJournal => Set<ChangeJournalEntry>();
    public DbSet<SyncDevice> Devices => Set<SyncDevice>();
    public DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes => Set<OAuthAuthorizationCode>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    Task<int> IAppDbContext.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Persist every domain enum as a snake_case string ("PhongBan" -> "phong_ban").
        var enumTypes = typeof(Domain.Enums.AccessLevel).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == "BlueDrive.Domain.Enums");
        foreach (var t in enumTypes)
        {
            if (t == typeof(Domain.Enums.AccessLevel)) continue; // ordered enum: keep as int for comparisons
            builder.Properties(t).HaveConversion(typeof(SnakeCaseEnumConverter<>).MakeGenericType(t)).HaveMaxLength(40);
        }
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("unaccent");
        b.HasPostgresExtension("ltree");
        b.HasPostgresExtension("pg_trgm");
        b.HasDbFunction(typeof(Application.Common.DbFn).GetMethod(nameof(Application.Common.DbFn.Unaccent))!).HasName("f_unaccent").HasSchema(null);

        b.Entity<Organization>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Path);
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(255);
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.EmailHash);
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.FullName).HasMaxLength(255);
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<PasswordHistory>().HasIndex(x => new { x.UserId, x.CreatedAt });

        b.Entity<UserGroup>().HasIndex(x => x.Name).IsUnique();
        b.Entity<UserGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => x.GroupId);
            e.HasOne(x => x.User).WithMany(x => x.GroupMemberships).HasForeignKey(x => x.UserId);
        });

        b.Entity<Role>().HasIndex(x => x.Code).IsUnique();
        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        b.Entity<Permission>(e => { e.HasKey(x => x.Code); e.Property(x => x.Code).HasMaxLength(100); });
        b.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.PermissionCode });
            e.HasOne(x => x.Role).WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId);
            e.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionCode);
        });

        b.Entity<RefreshToken>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.SessionId); e.HasIndex(x => x.UserId); });
        b.Entity<UserSession>(e => { e.HasIndex(x => new { x.UserId, x.RevokedAt }); e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId); });
        b.Entity<ChangeJournalEntry>(e =>
        {
            e.ToTable("change_journal");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(x => new { x.WorkspaceId, x.Seq });
            e.HasIndex(x => x.ItemId);
            e.HasIndex(x => x.OccurredAt);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.ContentHash).HasMaxLength(64);
        });
        b.Entity<SyncDevice>(e =>
        {
            e.ToTable("devices");
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Os).HasMaxLength(200);
            e.Property(x => x.AppVersion).HasMaxLength(50);
            e.Property(x => x.MachineIdHash).HasMaxLength(64);
            e.Property(x => x.LastIp).HasMaxLength(64);
        });
        b.Entity<OAuthAuthorizationCode>(e =>
        {
            e.ToTable("oauth_authorization_codes");
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.Property(x => x.RedirectUri).HasMaxLength(300);
            e.Property(x => x.CodeChallenge).HasMaxLength(128);
        });
        b.Entity<PasswordResetToken>().HasIndex(x => x.TokenHash).IsUnique();

        b.Entity<Catalog>().HasIndex(x => x.Code).IsUnique();
        b.Entity<CatalogItem>(e =>
        {
            e.HasIndex(x => new { x.CatalogId, x.Code }).IsUnique();
            e.HasOne(x => x.Catalog).WithMany(x => x.Items).HasForeignKey(x => x.CatalogId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Workspace>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.Type });
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<WorkspaceMember>(e =>
        {
            e.HasKey(x => new { x.WorkspaceId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Workspace).WithMany(x => x.Members).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<Folder>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.ParentId, x.IsDeleted });
            e.HasIndex(x => x.Path);
            e.Property(x => x.Name).HasMaxLength(255);
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<StoredFile>(e =>
        {
            e.ToTable("files");
            e.HasIndex(x => new { x.WorkspaceId, x.FolderId, x.IsDeleted });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.CreatedBy);
            e.HasIndex(x => x.UploaderOrganizationId);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Extension).HasMaxLength(20);
            e.Property(x => x.MimeType).HasMaxLength(150);
            if (Database.IsNpgsql())
            {
                e.Property<NpgsqlTsVector>("SearchVector");
                e.HasIndex("SearchVector").HasMethod("GIN");
            }
            e.HasIndex(x => x.Name).HasMethod("GIN").HasOperators("gin_trgm_ops");
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<FileVersion>(e =>
        {
            e.HasIndex(x => new { x.FileId, x.VersionNo }).IsUnique();
            e.HasOne(x => x.File).WithMany(x => x.Versions).HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FileCatalogItem>(e =>
        {
            e.HasKey(x => new { x.FileId, x.CatalogItemId });
            e.HasIndex(x => x.CatalogItemId);
            e.HasOne<StoredFile>().WithMany(x => x.CatalogItems).HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CatalogItem).WithMany().HasForeignKey(x => x.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserFavorite>().HasIndex(x => new { x.UserId, x.ItemType, x.ItemId }).IsUnique();
        b.Entity<RecentItem>(e => { e.HasIndex(x => new { x.UserId, x.ItemId }).IsUnique(); e.HasIndex(x => new { x.UserId, x.AccessedAt }); });

        b.Entity<ResourcePermission>(e =>
        {
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
            e.HasIndex(x => new { x.PrincipalType, x.PrincipalId });
        });
        b.Entity<Share>(e =>
        {
            e.HasIndex(x => new { x.SharedWithType, x.SharedWithId });
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
        });
        b.Entity<ShareLink>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.Property(x => x.TokenHash).HasMaxLength(64); e.HasIndex(x => new { x.ResourceType, x.ResourceId }); });
        b.Entity<ShareLinkMember>(e => { e.HasKey(x => new { x.LinkId, x.UserId }); e.HasIndex(x => x.UserId); });
        b.Entity<Guest>(e => { e.HasKey(x => x.UserId); e.HasIndex(x => x.ExpiresAt); });
        b.Entity<LibrarySettings>(e =>
        {
            e.ToTable("library_settings");
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Name).HasMaxLength(255);
        });
        b.Entity<SensitivityLabel>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(16);
            e.Property(x => x.Description).HasMaxLength(1000);
        });
        b.Entity<RetentionPolicy>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => new { x.ScopeKind, x.ScopeId });
        });
        b.Entity<LegalHold>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ReleaseReason).HasMaxLength(1000);
            e.HasIndex(x => x.ReleasedAt);
        });
        b.Entity<OffboardingRecord>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.ReportJson).HasColumnType("jsonb");
        });
        b.Entity<EmergencyAccess>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });
        b.Entity<LibraryColumn>(e =>
        {
            e.HasIndex(x => new { x.LibraryId, x.Key }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Validation).HasMaxLength(500);
            e.Property(x => x.ValidationMessage).HasMaxLength(500);
        });
        b.Entity<FileColumnValue>(e =>
        {
            e.HasKey(x => new { x.FileId, x.ColumnId });
            e.HasIndex(x => new { x.ColumnId, x.TextValue });
            e.Property(x => x.Value).HasColumnType("jsonb");
            e.Property(x => x.TextValue).HasMaxLength(2000);
        });
        b.Entity<FolderColumnDefault>(e =>
        {
            e.HasKey(x => new { x.FolderId, x.ColumnId });
            e.Property(x => x.Value).HasColumnType("jsonb");
        });
        b.Entity<LibraryView>(e =>
        {
            e.HasIndex(x => new { x.LibraryId, x.OwnerUserId });
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Filters).HasColumnType("jsonb");
        });
        b.Entity<LibraryTemplate>(e =>
        {
            e.HasIndex(x => x.LibraryId);
            e.Property(x => x.Name).HasMaxLength(255);
        });
        b.Entity<OwnershipTransfer>(e =>
        {
            e.HasIndex(x => new { x.ToUserId, x.Status });
            e.HasIndex(x => new { x.ItemId, x.Status });
            e.Property(x => x.ItemName).HasMaxLength(255);
            e.Property(x => x.ItemType).HasMaxLength(16);
        });
        b.Entity<Shortcut>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.FolderId });
            e.HasIndex(x => x.TargetId);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.TargetType).HasMaxLength(16);
        });
        b.Entity<WorkspaceGroupMember>(e => { e.HasKey(x => new { x.WorkspaceId, x.PrincipalId }); e.HasIndex(x => x.PrincipalId); });
        b.Entity<GuestOtp>(e => { e.HasIndex(x => new { x.UserId, x.CreatedAt }); e.Property(x => x.CodeHash).HasMaxLength(64); });
        ConfigureAccessModel(b);
        b.Entity<FileText>(e =>
        {
            e.HasKey(x => x.FileId);
            e.HasOne<StoredFile>().WithOne().HasForeignKey<FileText>(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            if (Database.IsNpgsql())
            {
                // Accent-insensitive like files.search_vector; weight D: a match in the name or title ranks first.
                e.Property<NpgsqlTsVector>("ContentVector").HasComputedColumnSql("setweight(to_tsvector('simple', f_unaccent(text)), 'D')", stored: true);
                e.HasIndex("ContentVector").HasMethod("GIN");
            }
        });

        b.Entity<Comment>().HasIndex(x => new { x.FileId, x.CreatedAt });
        b.Entity<Notification>().HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
        b.Entity<SavedSearch>().HasIndex(x => x.UserId);

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => new { x.Action, x.OccurredAt });
            e.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
            e.HasIndex(x => x.ResourceId);
            e.Property(x => x.Detail).HasColumnType("jsonb");
        });

        b.Entity<Alert>().HasIndex(x => new { x.IsAcknowledged, x.CreatedAt });
        b.Entity<SystemSetting>(e => { e.HasKey(x => x.Key); e.Property(x => x.ValueJson).HasColumnType("jsonb"); });
        b.Entity<BackupJob>().HasIndex(x => x.CreatedAt);
        b.Entity<SupportTicket>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.Status, x.DueAt });
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ChangeLog>().HasIndex(x => x.AppliedAt);
        b.Entity<SecurityIncident>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.Status, x.ReportedAt });
            e.HasIndex(x => x.AlertId);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SystemAnnouncement>().HasIndex(x => new { x.IsActive, x.StartsAt });
        b.Entity<ReportJob>().HasIndex(x => new { x.RequestedBy, x.CreatedAt });
        b.Entity<SystemLog>(e => { e.HasIndex(x => x.Timestamp); e.Property(x => x.Properties).HasColumnType("jsonb"); });
    }

    /// <summary>Storage and permission model like Google Drive / SharePoint (docs/migration-permissions.md).</summary>
    private void ConfigureAccessModel(ModelBuilder b)
    {
        var npgsql = Database.IsNpgsql();
        b.Entity<Space>(e =>
        {
            e.Property(x => x.Policy).HasColumnType(npgsql ? "jsonb" : null);
            e.HasIndex(x => x.OrgUnitId);
        });
        b.Entity<PersonalDrive>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<Library>(e =>
        {
            e.Property(x => x.Settings).HasColumnType(npgsql ? "jsonb" : null);
            e.HasIndex(x => x.SpaceId);
            e.HasIndex(x => x.PersonalDriveId);
        });
        b.Entity<Securable>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(255);
            if (npgsql)
            {
                e.Property(x => x.Path).HasColumnType("ltree");
                e.HasIndex(x => x.Path).HasMethod("gist");
            }
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.AclScopeId);
            e.HasIndex(x => x.SpaceId);
            e.HasIndex(x => x.PersonalDriveId);
        });
        b.Entity<Principal>(e => { e.HasIndex(x => new { x.Kind, x.RefId }); e.Property(x => x.DisplayName).HasMaxLength(255); });
        b.Entity<PrincipalMembership>(e => { e.HasKey(x => new { x.UserId, x.PrincipalId }); e.HasIndex(x => x.PrincipalId); });
        b.Entity<MembershipVersion>().HasKey(x => x.UserId);
        b.Entity<GroupNesting>(e => { e.HasKey(x => new { x.ParentGroupId, x.ChildGroupId }); e.HasIndex(x => x.ChildGroupId); });
        b.Entity<SpaceGroup>(e => { e.HasIndex(x => new { x.RootId, x.Kind }); e.Property(x => x.Name).HasMaxLength(255); });
        b.Entity<SpaceGroupMember>(e => { e.HasKey(x => new { x.SpaceGroupId, x.PrincipalId }); e.HasIndex(x => x.PrincipalId); });
        b.Entity<PermissionLevel>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Permissions).HasConversion<long>();
        });
        b.Entity<AclScope>().HasKey(x => x.ScopeId);
        b.Entity<AclEntry>(e =>
        {
            e.Property(x => x.AppliesToChildren).HasDefaultValue(true);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => x.ScopeId);
            e.HasIndex(x => x.PrincipalId);
            e.HasIndex(x => x.ExpiresAt);
        });
        b.Entity<AccessRequest>(e =>
        {
            e.HasIndex(x => new { x.SecurableId, x.Status });
            e.HasIndex(x => new { x.RequesterId, x.CreatedAt });
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
        });
        b.Entity<ScopeGrant>(e =>
        {
            e.HasKey(x => new { x.ScopeId, x.PrincipalId });
            e.HasIndex(x => new { x.PrincipalId, x.ScopeId });
            e.Property(x => x.Permissions).HasConversion<long>();
        });
    }
}

public sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => EnumNames.ToSnake(v.ToString()),
    v => (TEnum)Enum.Parse(typeof(TEnum), EnumNames.FromSnake(v), true)) where TEnum : struct, Enum;

public static class EnumNames
{
    public static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static string FromSnake(string snake) => snake.Replace("_", "");

    public static string ToSnake<TEnum>(TEnum value) where TEnum : struct, Enum => ToSnake(value.ToString());
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    // Unspecified values are UTC by convention (never the server's local time zone).
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
