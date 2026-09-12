namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Delivery lifecycle for a Contract Benefit. Tracks the progression from
/// configuration through eligibility to physical delivery.
/// </summary>
/// <remarks>
/// Lifecycle: NotEligible → Eligible → Delivered
///
/// - NotEligible: Benefit is configured on the contract but conditions are not yet satisfied.
/// - Eligible: Contractual conditions (active status, payment obligations) are met.
/// - Delivered: Physical gift has been handed to the customer.
///
/// A benefit that remains NotEligible or Eligible (but not Delivered) must NOT
/// generate a gift recovery deduction during refund/cancellation calculation.
/// </remarks>
public enum BenefitEligibilityStatus
{
    /// <summary>Benefit is configured but contractual eligibility conditions are not yet met.</summary>
    NotEligible = 0,

    /// <summary>Contractual conditions are satisfied; benefit is ready for delivery.</summary>
    Eligible = 1,

    /// <summary>Physical gift has been delivered to the customer.</summary>
    Delivered = 2
}
