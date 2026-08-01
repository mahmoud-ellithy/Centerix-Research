using Centerix.Domain.Common;

namespace Centerix.Domain.Platform.Tenants.Events;

public sealed class TenantCreatedEvent(Guid tenantId, string slug) : DomainEvent
{
    public Guid TenantId { get; } = tenantId;
    public string Slug { get; } = slug;
}
