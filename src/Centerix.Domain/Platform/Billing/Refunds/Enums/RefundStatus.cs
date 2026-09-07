namespace Centerix.Domain.Platform.Billing.Refunds.Enums;

/// <summary>
/// Refund lifecycle status. Domain-controlled: transitions are enforced
/// by the Refund aggregate and cannot be mutated arbitrarily from API DTOs.
/// </summary>
public enum RefundStatus : byte
{
    /// <summary>Refund request created, pending review.</summary>
    Pending = 0,

    /// <summary>Refund approved, ready for execution.</summary>
    Approved = 1,

    /// <summary>Refund rejected.</summary>
    Rejected = 2,

    /// <summary>Refund execution in progress.</summary>
    Processing = 3,

    /// <summary>Refund successfully executed.</summary>
    Completed = 4,

    /// <summary>Refund execution failed.</summary>
    Failed = 5,

    /// <summary>Refund cancelled before execution.</summary>
    Cancelled = 6
}
