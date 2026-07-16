namespace Centerix.Domain.Platform.Plans.Events;

using Centerix.Domain.Common;

public class PlanActivatedEvent(int planId) : DomainEvent
{
    public int PlanId { get; } = planId;
}
