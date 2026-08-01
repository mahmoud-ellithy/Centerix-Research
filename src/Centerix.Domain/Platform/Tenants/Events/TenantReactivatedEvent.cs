using Centerix.Domain.Common;

namespace Centerix.Domain.Platform.Tenants.Events;

public sealed class TenantReactivatedEvent(Guid tenantId) : DomainEvent
{
    public Guid TenantId { get; } = tenantId;
}
