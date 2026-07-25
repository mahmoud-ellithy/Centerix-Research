namespace Centerix.Domain.Auditing;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Tenant-scoped audit trail entry for actions performed by tenant users on tenant data.
/// Linked to <c>AspNetUsers</c> via <see cref="UserId"/> (nullable for system/anonymous actions).
/// </summary>
public class AuditLog : AuditableEntity<long>, IHasTenantId
{
    public string Action { get; private set; } = default!;
    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }
    public string? UserId { get; private set; }
    public string? IPAddress { get; private set; }
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public DateTime PerformedAt { get; private set; }

    private AuditLog() { }

    private AuditLog(
        long id,
        string action,
        string? entityType,
        string? entityId,
        string? userId,
        string? ipAddress,
        string? oldValue,
        string? newValue,
        DateTime performedAt)
        : base(id)
    {
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        UserId = userId;
        IPAddress = ipAddress;
        OldValue = oldValue;
        NewValue = newValue;
        PerformedAt = performedAt;
    }

    public static Result<AuditLog> Create(
        long id,
        string action,
        string? entityType,
        string? entityId,
        string? userId,
        string? ipAddress,
        string? oldValue,
        string? newValue,
        DateTime performedAt)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return AuditLogErrors.ActionRequired;
        }

        return new AuditLog(id, action.Trim(), entityType, entityId, userId, ipAddress, oldValue, newValue, performedAt);
    }
}
