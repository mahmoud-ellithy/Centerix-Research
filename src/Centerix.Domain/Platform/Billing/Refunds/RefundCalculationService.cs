namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;

/// <summary>
/// Deterministic service for calculating refund amounts for early cancellations.
/// Implements the approved business rules for pricing tiers, gift recovery, and payment basis.
/// </summary>
/// <remarks>
/// Calculation formulas:
///
/// 1. Elapsed Duration:
///    - ElapsedMonths = whole months from EffectiveAtUtc to asOfUtc
///    - ElapsedDays = total days from EffectiveAtUtc to asOfUtc
///
/// 2. Used Subscription Amount (pricing tiers):
///    - Find the largest pricing tier duration <= ElapsedMonths
///    - EXCEPTION: If ElapsedMonths == 2 and smallest tier is 1, use MonthlyListPrice * 2
///    - Otherwise use MonthlyListPrice * ElapsedMonths as fallback
///
/// 3. Gift/Benefit Consumption (day-based):
///    - ConsumedBenefitValue = ContractualValue * (ElapsedDays / ContractDurationDays)
///    - RemainingBenefitValue = ContractualValue - ConsumedBenefitValue
///    - Only benefits that are IsGranted are recoverable
///
/// 4. Amount Actually Paid:
///    - Sum of Completed Payments' active allocations
///    - Only payments with Status == Completed count
///
/// 5. Refund Calculation:
///    - CustomerEconomicObligation = UsedSubscriptionAmount + RemainingBenefitValue
///    - RefundableAmount = AmountActuallyPaid - CustomerEconomicObligation
///                         - AlreadyConvertedSubscriptionChangeCredit
///    - RefundAmount = max(0, RefundableAmount)
///    - CustomerOutstandingAmount = max(0, -(AmountActuallyPaid - CustomerEconomicObligation))
///
/// 6. Already-converted value (Task 18.4.2):
///    - A plan change converts the old contract's eligible unused paid value into
///      a SubscriptionChange credit. That value has already been returned to the
///      customer (as an available credit balance and/or as settlement of the new
///      invoice) and MUST NOT be refunded again as cash.
/// </remarks>
public sealed class RefundCalculationService : IRefundCalculationService
{
    public RefundCalculationResult Calculate(
        Contract contract,
        DateTime asOfUtc,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<ContractBenefit> benefits,
        decimal alreadyIssuedSubscriptionChangeCredit = 0m)
    {
        // Calculate elapsed duration
        var elapsedMonths = contract.GetElapsedMonths(asOfUtc);
        var elapsedDays = (asOfUtc - contract.EffectiveAtUtc).Days;
        var contractDurationDays = (contract.EndsAtUtc - contract.EffectiveAtUtc).Days;

        // Calculate used subscription amount using pricing tiers
        var usedSubscriptionAmount = contract.CalculateValueForElapsedMonths(elapsedMonths);

        // Calculate benefit consumption (day-based)
        var benefitContributions = new List<BenefitContribution>();
        decimal totalBenefitValue = 0;
        decimal consumedBenefitValue = 0;
        decimal remainingBenefitValue = 0;

        foreach (var benefit in benefits)
        {
            totalBenefitValue += benefit.ContractualValue;

            // Only granted benefits are recoverable
            if (!benefit.IsGranted)
            {
                benefitContributions.Add(new BenefitContribution
                {
                    BenefitId = benefit.Id,
                    Name = benefit.Name,
                    ContractualValue = benefit.ContractualValue,
                    ConsumedValue = 0,
                    RemainingValue = 0,
                    IsRecoverable = false
                });
                continue;
            }

            // Day-based consumption calculation
            decimal consumed;
            if (contractDurationDays <= 0)
            {
                consumed = benefit.ContractualValue;
            }
            else if (elapsedDays >= contractDurationDays)
            {
                consumed = benefit.ContractualValue;
            }
            else if (elapsedDays <= 0)
            {
                consumed = 0;
            }
            else
            {
                // Day-based consumption calculation: ConsumedValue = ContractualValue * (ElapsedDays / ContractDurationDays)
                var consumptionRatio = (decimal)elapsedDays / (decimal)contractDurationDays;
                consumed = Math.Round(benefit.ContractualValue * consumptionRatio, 2, MidpointRounding.AwayFromZero);
            }

            var remaining = benefit.ContractualValue - consumed;

            consumedBenefitValue += consumed;
            remainingBenefitValue += remaining;

            benefitContributions.Add(new BenefitContribution
            {
                BenefitId = benefit.Id,
                Name = benefit.Name,
                ContractualValue = benefit.ContractualValue,
                ConsumedValue = consumed,
                RemainingValue = remaining,
                IsRecoverable = true
            });
        }

        // Calculate amount actually paid (contract-scoped: only allocations belonging to this contract's invoices)
        var paymentContributions = new List<PaymentContribution>();
        decimal amountActuallyPaid = 0;

        foreach (var payment in payments)
        {
            // Only completed payments count toward settlement
            if (payment.Status != PaymentStatus.Completed)
                continue;

            // Only count active allocations where the invoice belongs to this specific contract.
            // This prevents cross-contract payment contamination: a payment allocated to invoices
            // from multiple contracts must only contribute the portion allocated to THIS contract.
            var contractAllocatedAmount = payment.Allocations
                .Where(a => a.Status == PaymentAllocationStatus.Active
                    && a.Invoice?.ContractId == contract.Id)
                .Sum(a => a.AllocatedAmount);

            amountActuallyPaid += contractAllocatedAmount;

            paymentContributions.Add(new PaymentContribution
            {
                PaymentId = payment.Id,
                PaymentNumber = payment.PaymentNumber,
                Amount = payment.Amount,
                AllocatedAmount = contractAllocatedAmount,
                Method = payment.Method
            });
        }

        // Calculate final refund
        var customerEconomicObligation = usedSubscriptionAmount + remainingBenefitValue;
        var paidMinusObligation = amountActuallyPaid - customerEconomicObligation;

        // Task 18.4.2 — value already converted into SubscriptionChange credit:
        // the plan-change credit for this contract was issued from exactly this paid value
        // (D-02: credit = MIN(unused value, eligible paid settlement)), so the credited amount
        // is subtracted from the refundable base. Subtracting the FULL issued amount (not just
        // the unconsumed remainder) is deliberate: the consumed portion already settled the
        // new contract's invoice and must not reappear as cash either.
        var alreadyConvertedCredit = alreadyIssuedSubscriptionChangeCredit > 0m
            ? alreadyIssuedSubscriptionChangeCredit
            : 0m;

        var refundableAmount = paidMinusObligation - alreadyConvertedCredit;

        // Refund amount is the positive part (what customer receives back)
        var refundAmount = refundableAmount > 0 ? refundableAmount : 0;

        // Customer outstanding is the negative part of paid - obligation (what the customer owes
        // for consumed value). The already-converted credit is NOT customer debt: it was already
        // delivered to the customer, so it must not create a phantom outstanding balance.
        var customerOutstandingAmount = paidMinusObligation < 0 ? -paidMinusObligation : 0;

        return new RefundCalculationResult
        {
            ContractId = contract.Id,
            ElapsedMonths = elapsedMonths,
            ElapsedDays = elapsedDays,
            ContractDurationMonths = contract.DurationMonths,
            ContractDurationDays = contractDurationDays,
            UsedSubscriptionAmount = usedSubscriptionAmount,
            TotalBenefitValue = totalBenefitValue,
            ConsumedBenefitValue = consumedBenefitValue,
            RemainingBenefitValue = remainingBenefitValue,
            CustomerEconomicObligation = customerEconomicObligation,
            AmountActuallyPaid = amountActuallyPaid,
            AlreadyConvertedSubscriptionChangeCredit = alreadyConvertedCredit,
            RefundableAmount = refundableAmount,
            RefundAmount = refundAmount,
            CustomerOutstandingAmount = customerOutstandingAmount,
            CurrencyCode = contract.CurrencyCode,
            PaymentContributions = paymentContributions,
            BenefitContributions = benefitContributions
        };
    }
}
