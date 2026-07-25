namespace Centerix.Domain.Common;

/// <summary>
/// Base class for tenant-scoped entities that require full audit trail (who/when created,
/// who/when last modified, and who/when soft-deleted). The fields are stamped automatically
/// by <c>AuditableEntityInterceptor</c> at save time. Derived entities expose soft-delete
/// semantics through <c>IsDeleted()</c> and configure <c>DeletedAtUtc</c> as a query filter
/// to hide tombstoned rows.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAtUtc { get; internal set; }
    public string? CreatedBy { get; internal set; }

    public DateTimeOffset LastModifiedUtc { get; internal set; }
    public string? LastModifiedBy { get; internal set; }

    public DateTimeOffset? DeletedAtUtc { get; internal set; }
    public string? DeletedBy { get; internal set; }

    public bool IsDeleted() => DeletedAtUtc.HasValue;
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
