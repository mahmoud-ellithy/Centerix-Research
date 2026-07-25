namespace Centerix.Domain.Common;

/// <summary>
/// Base class for entities that require an audit trail (who/when created, who/when last modified).
/// Fields are stamped automatically by <c>AuditableEntityInterceptor</c> at save time.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAtUtc { get; internal set; }
    public string? CreatedBy { get; internal set; }

    public DateTimeOffset LastModifiedUtc { get; internal set; }
    public string? LastModifiedBy { get; internal set; }
}

public abstract class AuditableEntity<TId> : AuditableEntity, IHasTenantId
    where TId : notnull
{
    public TId Id { get; private set; }
    public string? TenantId { get; internal set; }

    protected AuditableEntity()
    { }

    protected AuditableEntity(TId id)
    {
        Id = id;
    }
}

/// <summary>
/// Extends <see cref="AuditableEntity"/> with soft-delete columns (<c>DeletedAtUtc</c>,
/// <c>DeletedBy</c>). Entities that support tombstoning should inherit from this class
/// and configure <c>HasQueryFilter(e => e.DeletedAtUtc == null)</c> in their EF Core
/// configuration.
/// </summary>
public abstract class SoftDeletableEntity : AuditableEntity
{
    public DateTimeOffset? DeletedAtUtc { get; internal set; }
    public string? DeletedBy { get; internal set; }

    public bool IsDeleted() => DeletedAtUtc.HasValue;
}

public abstract class SoftDeletableEntity<TId> : SoftDeletableEntity, IHasTenantId
    where TId : notnull
{
    public TId Id { get; private set; }
    public string? TenantId { get; internal set; }

    protected SoftDeletableEntity()
    { }

    protected SoftDeletableEntity(TId id)
    {
        Id = id;
    }
}
