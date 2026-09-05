namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Commercial lifecycle of a <see cref="Contract"/> aggregate.
/// Domain-controlled state machine; not freely mutable from API DTOs.
/// </summary>
public enum ContractStatus : byte
{
    /// <summary>Draft contract, not yet submitted for approval.</summary>
    Draft = 0,

    /// <summary>Submitted and pending platform approval.</summary>
    PendingApproval = 1,

    /// <summary>Approved and commercially active.</summary>
    Active = 2,

    /// <summary>Temporarily suspended (operational or financial hold).</summary>
    Suspended = 3,

    /// <summary>Terminated before natural expiry.</summary>
    Terminated = 4,

    /// <summary>Reached natural end of term without renewal.</summary>
    Expired = 5
}
