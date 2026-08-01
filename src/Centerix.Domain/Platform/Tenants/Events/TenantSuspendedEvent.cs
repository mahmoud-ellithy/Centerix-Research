using Centerix.Domain.Common;

namespace Centerix.Domain.Platform.Tenants.Events;

public sealed class TenantSuspendedEvent(Guid tenantId, string reason) : DomainEvent
{
    public Guid TenantId { get; } = tenantId;
    public string Reason { get; } = reason;
}
