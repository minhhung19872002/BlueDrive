namespace BlueDrive.Domain.Common;

/// <summary>Base entity with a time-ordered UUIDv7 primary key.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Entity carrying the standard audit columns (UTC).</summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Marker for soft-deletable resources (folders, files).</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
    Guid? DeletedBy { get; set; }
}
