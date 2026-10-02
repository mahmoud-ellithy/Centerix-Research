namespace Centerix.Infrastructure.Platform.Services;

using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Determines whether a Contract Benefit can become eligible for grant / application
/// based on the contract status, payment obligation state, and installment compliance.
/// </summary>
/// <remarks>
/// <para>
/// Eligibility rules:
/// </para>
/// <list type="number">
///   <item><description>Contract must be Active.</description></item>
///   <item><description>Required contractual payment obligation is satisfied (completed payments ≥ contracted amount).</description></item>
///   <item><description>No overdue required installment.</description></item>
/// </list>
///
/// <para>
/// This service ONLY mutates <see cref="BenefitEligibilityStatus"/>. It MUST NEVER
/// affect <see cref="FulfillmentStatus"/>. Historical fulfillment state
/// (Granted / Delivered / AppliedToSubscription) is the authoritative record and
/// is preserved across eligibility re-evaluations.
/// </para>
/// </remarks>
public class BenefitEligibilityService : IBenefitEligibilityService
{
    public bool CanBecomeEligible(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount,
        bool hasOverdueInstallment = false)
    {
        if (benefit == null) return false;
        if (contract == null) return false;

        // Already eligible — preserve state.
        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return true;

        // Contract must be Active.
        if (contract.Status != ContractStatus.Active)
            return false;

        // Payment obligation check: completed payments must cover the contracted amount.
        if (contractedAmount > 0 && completedPaymentTotal < contractedAmount)
            return false;

        // Overdue installment check: no overdue required installment allowed.
        if (hasOverdueInstallment)
            return false;

        return true;
    }

    public BenefitEligibilityStatus DetermineEligibilityStatus(
        ContractBenefit benefit,
        Contract contract,
        decimal completedPaymentTotal,
        decimal contractedAmount,
        bool hasOverdueInstallment = false)
    {
        // Already eligible — preserve state. Eligibility is reversible, so this is a
        // no-op recommendation: it does NOT lock fulfillment to Granted or Delivered.
        if (benefit.EligibilityStatus == BenefitEligibilityStatus.Eligible)
            return BenefitEligibilityStatus.Eligible;

        if (CanBecomeEligible(benefit, contract, completedPaymentTotal, contractedAmount, hasOverdueInstallment))
            return BenefitEligibilityStatus.Eligible;

        return BenefitEligibilityStatus.NotEligible;
    }
}