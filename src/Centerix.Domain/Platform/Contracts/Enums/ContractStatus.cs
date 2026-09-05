namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Commercial contract lifecycle status. Domain-controlled: transitions are enforced
/// by the Contract aggregate and cannot be mutated arbitrarily from API DTOs.
/// </summary>
public enum ContractStatus
{
    Draft = 0,
    PendingApproval = 1,
    Active = 2,
    Suspended = 3,
    Terminated = 4,
    Expired = 5
}
