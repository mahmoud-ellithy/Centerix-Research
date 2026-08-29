namespace Centerix.Domain.Platform.Tenants.Events;

using Centerix.Domain.Common;

/// <summary>Raised when a Platform Admin rejects a tenant application.</summary>
public sealed class TenantRejectedEvent(Guid tenantId, string reason) : DomainEvent
{
    public Guid TenantId { get; } = tenantId;
    public string Reason { get; } = reason;
}
