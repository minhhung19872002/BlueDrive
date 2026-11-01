using BlueDrive.Domain.Common;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Domain.Entities;

/// <summary>Append-only audit trail with a hash chain (Hash = SHA256(PrevHash + canonical record)).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public Guid? OrganizationId { get; set; }
    public AuditAction Action { get; set; }
    public ResourceType? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public string? ResourceName { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? PrevHash { get; set; }
    public string Hash { get; set; } = "";
}

public class Alert : Entity
{
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public Guid? RelatedUserId { get; set; }
    public Guid? RelatedWorkspaceId { get; set; }
    public bool IsAcknowledged { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SystemSetting
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class BackupJob : Entity
{
    public BackupType Type { get; set; }
    public JobStatus Status { get; set; }
    /// <summary>"full" or "incremental" for the file archive part.</summary>
    public string Mode { get; set; } = "full";
    public DateTime CreatedAt { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? DbDumpKey { get; set; }
    public string? FilesArchiveKey { get; set; }
    public string? ManifestKey { get; set; }
    public long SizeBytes { get; set; }
    public string? Checksum { get; set; }
    public string? Error { get; set; }
}

public class SupportTicket : Entity
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public TicketPriority Priority { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Moi;
    public Guid ReporterId { get; set; }
    public Guid? AssigneeId { get; set; }
    public List<Guid> AttachmentFileIds { get; set; } = [];
    public DateTime ReceivedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public List<TicketEvent> Events { get; set; } = [];
}

public class TicketEvent : Entity
{
    public Guid TicketId { get; set; }
    public Guid? UserId { get; set; }
    public string EventType { get; set; } = "";
    public string? Note { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class ChangeLog : Entity
{
    public string Version { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid? AppliedBy { get; set; }
    public DateTime AppliedAt { get; set; }
}

/// <summary>Background report/export job (#5 non-functional: large exports run async).</summary>
public class ReportJob : Entity
{
    public Guid RequestedBy { get; set; }
    public string ReportType { get; set; } = "";
    public string Format { get; set; } = "xlsx";
    public string ParametersJson { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int Progress { get; set; }
    public string? ResultKey { get; set; }
    public string? ResultFileName { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public class SystemLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? Source { get; set; }
    public string? Properties { get; set; }
}
