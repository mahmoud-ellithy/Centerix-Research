namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Contracts;

/// <summary>
/// Service interface for calculating refund amounts for early cancellations.
/// This service performs deterministic, side-effect-free calculations based on:
/// 1. Historical Contract pricing tiers (NOT prorated)
/// 2. Actual successful payments (Payment + PaymentAllocation)
/// 3. Unconsumed gift/benefit value (day-based calculation)
/// </summary>
/// <remarks>
/// The calculation follows the financial invariant:
/// RefundableAmount = AmountActuallyPaid - CustomerEconomicObligation
/// where CustomerEconomicObligation = UsedSubscriptionAmount + RemainingBenefitValue
///
/// Important rules:
/// - DO NOT prorate the discounted contract price
/// - Use historical Contract pricing tiers for used subscription amount
/// - Gift recovery is based on unconsumed economic value (not physical return)
/// - Refund is based on actual successful payments, NOT Invoice.Total
/// - Negative refundable amount means customer owes money (do NOT clamp to zero)
/// </remarks>
public interface IRefundCalculationService
{
    /// <summary>
    /// Calculates the refund for an early cancellation.
    /// </summary>
    /// <param name="contract">The contract being cancelled (historical snapshot).</param>
    /// <param name="asOfUtc">The cancellation date.</param>
    /// <param name="payments">The successful payments with their allocations.</param>
    /// <param name="benefits">The benefits granted under the contract.</param>
    /// <returns>A deterministic result containing all calculation details.</returns>
    RefundCalculationResult Calculate(
        Contract contract,
        DateTime asOfUtc,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<ContractBenefit> benefits);
}
