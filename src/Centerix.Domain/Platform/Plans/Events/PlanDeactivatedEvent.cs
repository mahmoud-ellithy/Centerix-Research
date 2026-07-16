namespace Centerix.Domain.Platform.Plans.Events;

using Centerix.Domain.Common;

public class PlanDeactivatedEvent(int planId) : DomainEvent
{
    public int PlanId { get; } = planId;
}
