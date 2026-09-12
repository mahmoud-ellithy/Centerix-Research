namespace Centerix.Domain.Platform.Contracts.Events;

using Centerix.Domain.Common;

/// <summary>Raised when a new Contract is created.</summary>
public class ContractCreatedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }
    public int PlanId { get; }
    public string ContractNumber { get; }

    public ContractCreatedEvent(Guid contractId, string tenantId, int planId, string contractNumber)
    {
        ContractId = contractId;
        TenantId = tenantId;
        PlanId = planId;
        ContractNumber = contractNumber;
    }
}

/// <summary>Raised when a Contract is submitted for approval.</summary>
public class ContractSubmittedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }

    public ContractSubmittedEvent(Guid contractId, string tenantId)
    {
        ContractId = contractId;
        TenantId = tenantId;
    }
}

/// <summary>Raised when a Contract becomes Active.</summary>
public class ContractActivatedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }
    public DateTime EffectiveAtUtc { get; }

    public ContractActivatedEvent(Guid contractId, string tenantId, DateTime effectiveAtUtc)
    {
        ContractId = contractId;
        TenantId = tenantId;
        EffectiveAtUtc = effectiveAtUtc;
    }
}

/// <summary>Raised when a Contract is suspended.</summary>
public class ContractSuspendedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }

    public ContractSuspendedEvent(Guid contractId, string tenantId)
    {
        ContractId = contractId;
        TenantId = tenantId;
    }
}

/// <summary>Raised when a suspended Contract is reactivated.</summary>
public class ContractReactivatedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }

    public ContractReactivatedEvent(Guid contractId, string tenantId)
    {
        ContractId = contractId;
        TenantId = tenantId;
    }
}

/// <summary>Raised when a Contract is terminated.</summary>
public class ContractTerminatedEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }
    public DateTime TerminatedAtUtc { get; }

    public ContractTerminatedEvent(Guid contractId, string tenantId, DateTime terminatedAtUtc)
    {
        ContractId = contractId;
        TenantId = tenantId;
        TerminatedAtUtc = terminatedAtUtc;
    }
}

/// <summary>Raised when a Contract expires.</summary>
public class ContractExpiredEvent : DomainEvent
{
    public Guid ContractId { get; }
    public string TenantId { get; }
    public DateTime ExpiredAtUtc { get; }

    public ContractExpiredEvent(Guid contractId, string tenantId, DateTime expiredAtUtc)
    {
        ContractId = contractId;
        TenantId = tenantId;
        ExpiredAtUtc = expiredAtUtc;
    }
}

/// <summary>Raised when a Contract Benefit becomes eligible for delivery.</summary>
public class BenefitEligibleEvent : DomainEvent
{
    public Guid ContractId { get; }
    public Guid BenefitId { get; }
    public string TenantId { get; }

    public BenefitEligibleEvent(Guid contractId, Guid benefitId, string tenantId)
    {
        ContractId = contractId;
        BenefitId = benefitId;
        TenantId = tenantId;
    }
}

/// <summary>Raised when a Contract Benefit is delivered to the customer.</summary>
public class BenefitDeliveredEvent : DomainEvent
{
    public Guid ContractId { get; }
    public Guid BenefitId { get; }
    public string TenantId { get; }
    public DateTime DeliveredAtUtc { get; }
    public string? DeliveredBy { get; }

    public BenefitDeliveredEvent(Guid contractId, Guid benefitId, string tenantId, DateTime deliveredAtUtc, string? deliveredBy)
    {
        ContractId = contractId;
        BenefitId = benefitId;
        TenantId = tenantId;
        DeliveredAtUtc = deliveredAtUtc;
        DeliveredBy = deliveredBy;
    }
}
