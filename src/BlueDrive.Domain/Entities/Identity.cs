using BlueDrive.Domain.Common;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Domain.Entities;

public class Organization : AuditableEntity
{
    public Guid? ParentId { get; set; }
    public Organization? Parent { get; set; }
    public List<Organization> Children { get; set; } = [];
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public OrganizationType Type { get; set; } = OrganizationType.PhongBan;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Materialized path of ids, e.g. "/{root}/{child}/".</summary>
    public string Path { get; set; } = "/";
}

public class User : AuditableEntity
{
    public string Username { get; set; } = "";
    /// <summary>Encrypted at rest via Data Protection.</summary>
    public string? Email { get; set; }
    /// <summary>Lowercase SHA-256 of the email, used for lookup since Email is encrypted.</summary>
    public string? EmailHash { get; set; }
    public string FullName { get; set; } = "";
    /// <summary>Encrypted at rest via Data Protection.</summary>
    public string? Phone { get; set; }
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string? Position { get; set; }
    public Guid? AvatarFileId { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTime? PasswordChangedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsLocked { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public int FailedLoginCount { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    /// <summary>Bumped on "revoke all access" so outstanding access tokens become invalid.</summary>
    public int SecurityStamp { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserGroupMember> GroupMemberships { get; set; } = [];
}

public class PasswordHistory : Entity
{
    public Guid UserId { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class UserGroup : AuditableEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<UserGroupMember> Members { get; set; } = [];
}

public class UserGroupMember
{
    public Guid GroupId { get; set; }
    public UserGroup? Group { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public DateTime AddedAt { get; set; }
}

public class Role : AuditableEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public class UserRole
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class Permission
{
    /// <summary>"module.action", e.g. "doc.upload".</summary>
    public string Code { get; set; } = "";
    public string Module { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public string PermissionCode { get; set; } = "";
}

public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public class UserSession : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string? Device { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
}

public class PasswordResetToken : Entity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
