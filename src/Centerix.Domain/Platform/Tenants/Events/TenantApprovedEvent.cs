namespace Centerix.Domain.Platform.Tenants.Events;

using Centerix.Domain.Common;

/// <summary>Raised when a Platform Admin approves a tenant application.</summary>
public sealed class TenantApprovedEvent(Guid tenantId) : DomainEvent
{
    public Guid TenantId { get; } = tenantId;
}
