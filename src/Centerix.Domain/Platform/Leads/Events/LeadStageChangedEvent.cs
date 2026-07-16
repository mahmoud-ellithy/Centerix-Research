namespace Centerix.Domain.Platform.Leads.Events;

using Centerix.Domain.Common;
using Centerix.Domain.Platform.Leads.Enums;

public class LeadStageChangedEvent(Guid leadId, string centerName, LeadStage oldStage, LeadStage newStage) : DomainEvent
{
    public Guid LeadId { get; } = leadId;
    public string CenterName { get; } = centerName;
    public LeadStage OldStage { get; } = oldStage;
    public LeadStage NewStage { get; } = newStage;
}
