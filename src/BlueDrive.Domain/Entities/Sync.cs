using BlueDrive.Domain.Common;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Domain.Entities;

/// <summary>
/// Append-only change journal used by the desktop sync delta API (/sync/changes). Rows are written in the same
/// SaveChanges (transaction) as the business change. Columns describe the item state AFTER the change.
/// </summary>
public class ChangeJournalEntry
{
    public long Seq { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ItemId { get; set; }
    public ChangeItemType ItemType { get; set; }
    public ChangeOp Op { get; set; }
    public Guid? ParentId { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public Guid? VersionId { get; set; }
    public long Revision { get; set; }
    public long? SizeBytes { get; set; }
    public string? ContentHash { get; set; }
    public DateTime ModifiedAt { get; set; }
    public Guid? ModifiedBy { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTime OccurredAt { get; set; }
    /// <summary>Permission changes: who was granted / lost access (user, group, organisation or role), so that only they are told.</summary>
    public Guid? PrincipalId { get; set; }
}

/// <summary>A computer running BlueDrive Desktop (one per installation and account).</summary>
public class SyncDevice : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>SHA-256 of the machine id sent by the client (never the raw value).</summary>
    public string? MachineIdHash { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? LastIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTime? WipeRequestedAt { get; set; }
    public Guid? WipeRequestedBy { get; set; }
    public DateTime? WipedAt { get; set; }
    /// <summary>Per-device secret (encrypted at rest) used to sign server commands such as remote wipe (HMAC-SHA256).</summary>
    public string? SecretProtected { get; set; }
}

/// <summary>OAuth 2.0 authorization code (PKCE) for the desktop app loopback login. Single use, short-lived.</summary>
public class OAuthAuthorizationCode : Entity
{
    public string CodeHash { get; set; } = "";
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
