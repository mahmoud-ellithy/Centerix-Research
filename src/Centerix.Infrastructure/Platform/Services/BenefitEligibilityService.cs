namespace Centerix.Infrastructure.Platform.Services;

using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Determines whether a Contract Benefit can become eligible for delivery
/// based on the contract status, payment obligation state, and installment compliance.
/// </summary>
/// <remarks>
/// Eligibility rules:
/// 1. Contract must be Active
/// 2. Required contractual payment obligation must be satisfied
///    (completed payments >= contracted amount)
/// 3. No overdue required installment
///
/// Zero-value benefits (ContractualValue = 0) are NOT exempt from eligibility checks.
/// All benefits, regardless of value, must satisfy the same contract and payment conditions.
///
/// For physical gifts, eligibility must be established before delivery.
/// </remarks>
public class BenefitEligibilityService : IBenefitEligibilityService
{
    /// <summary>
    /// Determines whether a benefit can become eligible for delivery.
    /// </summary>
    public bool CanBecomeEligible(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount,
        bool hasOverdueInstallment = false)
    {
        if (benefit == null) return false;
        if (contract == null) return false;

        // Already delivered or eligible
        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Delivered)
            return true;

        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return true;

        // Zero-value benefits are NOT exempt from eligibility checks.
        // All benefits must satisfy the same contract and payment conditions.

        // Contract must be Active
        if (contract.Status != ContractStatus.Active)
            return false;

        // Payment obligation check: completed payments must cover the contracted amount
        if (contractedAmount > 0 && completedPaymentTotal < contractedAmount)
            return false;

        // Overdue installment check: no overdue required installment allowed
        if (hasOverdueInstallment)
            return false;

        return true;
    }

    /// <summary>
    /// Determines the effective eligibility status for a benefit given the current state.
    /// </summary>
    public BenefitEligibilityStatus DetermineEligibilityStatus(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount,
        bool hasOverdueInstallment = false)
    {
        // Already delivered - keep delivered status
        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Delivered)
            return BenefitEligibilityStatus.Delivered;

        // Already eligible - keep eligible status
        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return BenefitEligibilityStatus.Eligible;

        // Check if can become eligible now
        if (CanBecomeEligible(benefit, contract, completedPaymentTotal, contractedAmount, hasOverdueInstallment))
            return BenefitEligibilityStatus.Eligible;

        return BenefitEligibilityStatus.NotEligible;
    }
}
