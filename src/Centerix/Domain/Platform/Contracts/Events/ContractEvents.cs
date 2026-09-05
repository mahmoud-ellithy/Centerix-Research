namespace Centerix.Domain.Platform.Contracts.Events;

using Centerix.Domain.Common;

/// <summary>Raised when a new Contract is created.</summary>
public sealed record ContractCreatedEvent(
    Guid ContractId,
    string TenantId,
    int PlanId,
    string ContractNumber) : DomainEvent;

/// <summary>Raised when a Contract is submitted for approval.</summary>
public sealed record ContractSubmittedEvent(
    Guid ContractId,
    string TenantId) : DomainEvent;

/// <summary>Raised when a Contract becomes Active.</summary>
public sealed record ContractActivatedEvent(
    Guid ContractId,
    string TenantId,
    DateTime EffectiveAtUtc) : DomainEvent;

/// <summary>Raised when a Contract is suspended.</summary>
public sealed record ContractSuspendedEvent(
    Guid ContractId,
    string TenantId) : DomainEvent;

/// <summary>Raised when a suspended Contract is reactivated.</summary>
public sealed record ContractReactivatedEvent(
    Guid ContractId,
    string TenantId) : DomainEvent;

/// <summary>Raised when a Contract is terminated.</summary>
public sealed record ContractTerminatedEvent(
    Guid ContractId,
    string TenantId,
    DateTime TerminatedAtUtc) : DomainEvent;

/// <summary>Raised when a Contract expires.</summary>
public sealed record ContractExpiredEvent(
    Guid ContractId,
    string TenantId,
    DateTime ExpiredAtUtc) : DomainEvent;
