namespace Centerix.Domain.Platform.Plans.Events;

using Centerix.Domain.Common;

public class PlanCreatedEvent(Plan plan) : DomainEvent
{
    public Plan Plan { get; } = plan;
}
