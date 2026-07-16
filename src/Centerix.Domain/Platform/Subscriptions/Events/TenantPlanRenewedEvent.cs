namespace Centerix.Domain.Platform.Subscriptions.Events;

using Centerix.Domain.Common;

public class TenantPlanRenewedEvent(Guid tenantPlanId, int planId) : DomainEvent
{
    public Guid TenantPlanId { get; } = tenantPlanId;
    public int PlanId { get; } = planId;
}
