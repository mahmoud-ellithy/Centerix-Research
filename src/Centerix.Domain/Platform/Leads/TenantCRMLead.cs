namespace Centerix.Domain.Platform.Leads;

using System.Text.RegularExpressions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Leads.Enums;
using Centerix.Domain.Platform.Leads.Events;

public class TenantCRMLead : AuditableEntity<Guid>
{
    public string CenterName { get; private set; } = default!;
    public string ContactName { get; private set; } = default!;
    public string Phone { get; private set; } = default!;
    public string Source { get; private set; } = default!;
    public LeadStage Stage { get; private set; }
    public string? AssignedTo { get; private set; }

    private TenantCRMLead() { }

    private TenantCRMLead(
        Guid id,
        string centerName,
        string contactName,
        string phone,
        string source,
        LeadStage stage,
        string? assignedTo)
        : base(id)
    {
        CenterName = centerName;
        ContactName = contactName;
        Phone = phone;
        Source = source;
        Stage = stage;
        AssignedTo = assignedTo;
    }

    public static Result<TenantCRMLead> Create(
        Guid id,
        string centerName,
        string contactName,
        string phone,
        string source,
        LeadStage stage,
        string? assignedTo)
    {
        if (string.IsNullOrWhiteSpace(centerName))
            return TenantCRMLeadErrors.CenterNameRequired;

        if (string.IsNullOrWhiteSpace(contactName))
            return TenantCRMLeadErrors.ContactNameRequired;

        if (string.IsNullOrWhiteSpace(phone) || !Regex.IsMatch(phone, @"^\+?\d{7,15}$"))
            return TenantCRMLeadErrors.InvalidPhoneNumber;

        if (string.IsNullOrWhiteSpace(source))
            return TenantCRMLeadErrors.SourceRequired;

        if (!Enum.IsDefined(stage))
            return TenantCRMLeadErrors.StageRequired;

        return new TenantCRMLead(id, centerName, contactName, phone, source, stage, assignedTo);
    }

    public Result<Updated> Update(
        string centerName,
        string contactName,
        string phone,
        string source,
        string stage,
        string? assignedTo)
    {
        if (string.IsNullOrWhiteSpace(centerName))
            return TenantCRMLeadErrors.CenterNameRequired;

        if (string.IsNullOrWhiteSpace(contactName))
            return TenantCRMLeadErrors.ContactNameRequired;

        if (string.IsNullOrWhiteSpace(phone) || !Regex.IsMatch(phone, @"^\+?\d{7,15}$"))
            return TenantCRMLeadErrors.InvalidPhoneNumber;

        if (string.IsNullOrWhiteSpace(source))
            return TenantCRMLeadErrors.SourceRequired;

        if (!Enum.TryParse<LeadStage>(stage, out var parsedStage))
            return TenantCRMLeadErrors.StageRequired;

        CenterName = centerName;
        ContactName = contactName;
        Phone = phone;
        Source = source;
        Stage = parsedStage;
        AssignedTo = assignedTo;

        return Result.Updated;
    }

    public Result<Updated> MoveToStage(LeadStage newStage)
    {
        if (!CanTransitionTo(newStage))
            return TenantCRMLeadErrors.InvalidStageTransition;

        var oldStage = Stage;
        Stage = newStage;

        AddDomainEvent(new LeadStageChangedEvent(Id, CenterName, oldStage, newStage));

        return Result.Updated;
    }

    private bool CanTransitionTo(LeadStage newStage)
    {
        if (newStage == Stage)
            return false;

        return (Stage, newStage) switch
        {
            (LeadStage.New, LeadStage.Contacted) => true,
            (LeadStage.New, LeadStage.Lost) => true,
            (LeadStage.Contacted, LeadStage.Qualified) => true,
            (LeadStage.Contacted, LeadStage.Lost) => true,
            (LeadStage.Qualified, LeadStage.Converted) => true,
            (LeadStage.Qualified, LeadStage.Lost) => true,
            _ => false,
        };
    }
}
