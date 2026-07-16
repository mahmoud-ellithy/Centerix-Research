namespace Centerix.Domain.Common;

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
