namespace Centerix.Application.Platform.Contracts.Services;

using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Domain service that determines whether a Contract Benefit can become eligible
/// based on contract status, payment state, and installment obligations.
/// </summary>
/// <remarks>
/// Eligibility rules for physical gifts:
/// - Contract must be Active
/// - Required contractual payment obligation must be satisfied
/// - No overdue required installment
/// - Only Completed payments count (Pending/Processing/Failed/Cancelled do not)
///
/// For non-physical benefits (Service, FinancialCredit, etc.), eligibility
/// may follow different rules as determined by the business policy.
/// </remarks>
public interface IBenefitEligibilityService
{
    /// <summary>
    /// Determines whether a benefit can become eligible for delivery.
    /// </summary>
    /// <param name="benefit">The benefit to check.</param>
    /// <param name="contract">The associated contract.</param>
    /// <param name="completedPaymentTotal">Total amount of completed payments for this contract.</param>
    /// <param name="contractedAmount">The contracted amount (total obligation).</param>
    /// <returns>True if the benefit can become eligible.</returns>
    bool CanBecomeEligible(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount);

    /// <summary>
    /// Determines the effective eligibility status for a benefit given the current state.
    /// </summary>
    BenefitEligibilityStatus DetermineEligibilityStatus(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount);
}
