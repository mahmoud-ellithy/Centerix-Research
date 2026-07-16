namespace Centerix.Domain.Platform.Auditing;

using Centerix.Domain.Common;

public class PlatformAuditLog : AuditableEntity<long>
{
    public string Action { get; private set; } = default!;
    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public string? IPAddress { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private PlatformAuditLog() { }

    private PlatformAuditLog(
        long id,
        string action,
        string? entityType,
        string? entityId,
        string? oldValue,
        string? newValue,
        string? ipAddress,
        DateTime createdAt)
        : base(id)
    {
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        OldValue = oldValue;
        NewValue = newValue;
        IPAddress = ipAddress;
        CreatedAt = createdAt;
    }

    public static PlatformAuditLog Create(
        long id,
        string action,
        string? entityType,
        string? entityId,
        string? oldValue,
        string? newValue,
        string? ipAddress,
        DateTime createdAt)
    {
        return new PlatformAuditLog(id, action, entityType, entityId, oldValue, newValue, ipAddress, createdAt);
    }
}
