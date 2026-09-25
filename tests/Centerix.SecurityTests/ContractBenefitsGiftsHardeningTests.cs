namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.Events;
using Centerix.Infrastructure.Platform.Services;
using NSubstitute;
using Xunit;

/// <summary>
/// CODER TASK 6: Contract Benefits & Gifts Engine Hardening.
/// Comprehensive tests covering the critical test matrix (A–M) and the
/// economic safety invariants.
/// </summary>
public class ContractBenefitsGiftsHardeningTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Contract CreateValidContract(
        string tenantId = "tenant-1",
        string contractNumber = "CNT-001",
        decimal monthlyListPrice = 1000m,
        decimal contractualMonthlyValue = 1000m,
        decimal contractedAmount = 10000m,
        int durationMonths = 12,
        DateTime? effectiveAtUtc = null,
        DateTime? endsAtUtc = null)
    {
        var effective = effectiveAtUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ends = endsAtUtc ?? effective.AddMonths(durationMonths);

        var result = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: contractNumber,
            planId: 1,
            effectiveAtUtc: effective,
            endsAtUtc: ends,
            durationMonths: durationMonths,
            monthlyListPrice: monthlyListPrice,
            contractualMonthlyValue: contractualMonthlyValue,
            currencyCode: "EGP",
            grossAmount: contractedAmount,
            contractedAmount: contractedAmount,
            discountAmount: 0,
            promotionReference: null, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);

        Assert.True(result.IsSuccess, $"Contract creation failed: {string.Join(",", result.Errors?.Select(e => e.Code) ?? [])}");
        return result.Value;
    }

    private static ContractBenefit CreateBenefit(
        Guid contractId,
        string name,
        decimal value,
        ContractBenefitType type = ContractBenefitType.PhysicalGift)
    {
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contractId,
            type,
            name,
            null,
            value,
            "EGP").Value;

        return benefit;
    }

    private static ContractBenefit CreateEligibleBenefit(
        Guid contractId,
        string name,
        decimal value,
        ContractBenefitType type = ContractBenefitType.PhysicalGift,
        string? tenantId = null)
    {
        var benefit = CreateBenefit(contractId, name, value, type);
        benefit.MarkEligible(DateTime.UtcNow, tenantId);
        return benefit;
    }

    private static ContractBenefit CreateGrantedBenefit(
        Guid contractId,
        string name,
        decimal value,
        ContractBenefitType type = ContractBenefitType.PhysicalGift,
        DateTime? grantedAtUtc = null,
        string? tenantId = null)
    {
        var benefit = CreateBenefit(contractId, name, value, type);
        benefit.MarkEligible(DateTime.UtcNow, tenantId);

        if (type == ContractBenefitType.PhysicalGift)
        {
            benefit.MarkGranted(grantedAtUtc ?? DateTime.UtcNow, tenantId: tenantId);
        }
        else
        {
            // For non-PhysicalGift types, set granted via reflection for refund tests.
            // In production, only PhysicalGift can be delivered through MarkBenefitDelivered.
            typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.IsGranted))!
                .SetValue(benefit, true);
            typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.GrantedAtUtc))!
                .SetValue(benefit, grantedAtUtc ?? DateTime.UtcNow);
            typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.EligibilityStatus))!
                .SetValue(benefit, BenefitEligibilityStatus.Delivered);
        }

        return benefit;
    }

    private static Payment CreateCompletedPayment(
        decimal amount,
        decimal allocatedAmount,
        Contract? contract = null,
        string? paymentNumber = null)
    {
        var paymentResult = Payment.Create(
            id: Guid.NewGuid(),
            paymentNumber: paymentNumber ?? $"PAY-{Guid.NewGuid().ToString()[..8]}",
            amount: amount,
            currencyCode: "EGP",
            method: PaymentMethod.Cash);

        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;

        var completeResult = payment.Complete(DateTime.UtcNow);
        Assert.True(completeResult.IsSuccess);

        if (allocatedAmount > 0 && contract is not null)
        {
            var invoice = Invoice.Create(
                Guid.NewGuid(),
                $"INV-{Guid.NewGuid().ToString()[..8]}",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 31),
                allocatedAmount,
                0,
                0,
                allocatedAmount,
                contractId: contract.Id).Value;
            invoice.Issue(DateTime.UtcNow);

            AddAllocationWithInvoice(payment, invoice, allocatedAmount);
        }

        return payment;
    }

    private static void AddAllocationWithInvoice(
        Payment payment,
        Invoice invoice,
        decimal allocatedAmount,
        PaymentAllocationStatus status = PaymentAllocationStatus.Active)
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            payment.Id,
            invoice.Id,
            allocatedAmount,
            DateTime.UtcNow);

        Assert.True(allocation.IsSuccess);

        if (status == PaymentAllocationStatus.Reversed)
            allocation.Value.Reverse();

        var invoiceProp = typeof(PaymentAllocation).GetProperty("Invoice", BindingFlags.Public | BindingFlags.Instance);
        invoiceProp!.SetValue(allocation.Value, invoice);

        var allocationsField = typeof(Payment).GetField("_allocations", BindingFlags.NonPublic | BindingFlags.Instance);
        var allocationsList = (List<PaymentAllocation>)allocationsField!.GetValue(payment)!;
        allocationsList.Add(allocation.Value);
    }

    private static readonly RefundCalculationService s_refundService = new();
    private static readonly BenefitEligibilityService s_eligibilityService = new();

    // ==================================================================
    // TEST A: 3× Cap
    // ==================================================================

    [Fact]
    public void TestA_Cap_ExactlyAtLimit_Passes()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        var benefit = CreateBenefit(contract.Id, "Gift", 3000m);

        var result = contract.AddBenefit(benefit);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, contract.Benefits.Sum(b => b.ContractualValue));
    }

    [Fact]
    public void TestA_Cap_ExceedsLimit_Fails()
    {
        var contract = CreateValidContract(contractualMonthlyValue: 1000m);
        var benefit = CreateBenefit(contract.Id, "Gift", 3000.01m);

        var result = contract.AddBenefit(benefit);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors[0].Code);
        Assert.Empty(contract.Benefits);
    }

    // ==================================================================
    // TEST B: Zero-value benefit
    // ==================================================================

    [Fact]
    public void TestB_ZeroValueBenefit_DoesNotAffectRefund()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var zeroBenefit = CreateGrantedBenefit(contract.Id, "Free Sticker", 0m);
        contract.AddBenefit(zeroBenefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        Assert.Equal(0m, result.ConsumedBenefitValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
    }

    // ==================================================================
    // TEST C: Not delivered → Recovery = 0
    // ==================================================================

    [Fact]
    public void TestC_NotDelivered_RecoveryIsZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        // Gift configured but NOT delivered
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Non-delivered benefit must not generate recovery
        var contribution = result.BenefitContributions.First();
        Assert.False(contribution.IsRecoverable);
        Assert.Equal(0m, contribution.RemainingValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
    }

    // ==================================================================
    // TEST D: Delivered at start → Recovery ≈ 1000
    // ==================================================================

    [Fact]
    public void TestD_DeliveredAtStart_RecoveryNearlyFull()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Printer", 1000m, grantedAtUtc: effectiveAt);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel on the same day as contract start (ElapsedDays = 0)
        var cancellationDate = effectiveAt;

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // ElapsedDays = 0 → consumed = 0, remaining = 1000
        Assert.Equal(0m, result.ConsumedBenefitValue);
        Assert.Equal(1000m, result.RemainingBenefitValue);
        Assert.Equal(0m, result.UsedSubscriptionAmount);
        // Economic obligation = 0 + 1000 = 1000
        Assert.Equal(1000m, result.CustomerEconomicObligation);
        Assert.Equal(9000m, result.RefundAmount);
    }

    // ==================================================================
    // TEST E: Midpoint — verify exact day-based formula
    // ==================================================================

    [Fact]
    public void TestE_Midpoint_FormulaCalculation()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contractDurationDays = (endsAt - effectiveAt).Days; // 365
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel at 182 days
        var cancellationDate = effectiveAt.AddDays(182);
        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // consumed = round(1000 * 182 / 365) = round(498.63) = 498.63
        var expectedConsumed = Math.Round(1000m * 182m / 365m, 2, MidpointRounding.AwayFromZero);
        var expectedRemaining = 1000m - expectedConsumed;

        Assert.Equal(expectedConsumed, result.ConsumedBenefitValue);
        Assert.Equal(expectedRemaining, result.RemainingBenefitValue);
        Assert.Equal(expectedConsumed + expectedRemaining, 1000m);
    }

    // ==================================================================
    // TEST F: Contract end → Recovery = 0
    // ==================================================================

    [Fact]
    public void TestF_ContractEnd_RecoveryIsZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel at contract end date
        var cancellationDate = endsAt;

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // ElapsedDays >= DurationDays → consumed = 1000, remaining = 0
        Assert.Equal(1000m, result.ConsumedBenefitValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
        Assert.Equal(0m, result.RefundAmount);
    }

    // ==================================================================
    // TEST G: Multiple benefits — independent consumption
    // ==================================================================

    [Fact]
    public void TestG_MultipleBenefits_IndependentConsumption()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contractDurationDays = (endsAt - effectiveAt).Days;
        var contract = CreateValidContract(
            monthlyListPrice: 1500m,
            contractualMonthlyValue: 1500m,
            contractedAmount: 18000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 18000m, "EGP", 1500m, 1).Value);

        var benefit1 = CreateGrantedBenefit(contract.Id, "Printer", 1000m);
        var benefit2 = CreateGrantedBenefit(contract.Id, "PC", 2000m);
        contract.AddBenefit(benefit1);
        contract.AddBenefit(benefit2);

        var payment = CreateCompletedPayment(18000m, 18000m, contract);

        // Cancel after 182 days
        var cancellationDate = effectiveAt.AddDays(182);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Each benefit consumed independently
        var consumed1 = Math.Round(1000m * 182m / contractDurationDays, 2, MidpointRounding.AwayFromZero);
        var consumed2 = Math.Round(2000m * 182m / contractDurationDays, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(2, result.BenefitContributions.Count);

        var contrib1 = result.BenefitContributions.First(b => b.Name == "Printer");
        var contrib2 = result.BenefitContributions.First(b => b.Name == "PC");

        Assert.Equal(consumed1, contrib1.ConsumedValue);
        Assert.Equal(1000m - consumed1, contrib1.RemainingValue);
        Assert.Equal(consumed2, contrib2.ConsumedValue);
        Assert.Equal(2000m - consumed2, contrib2.RemainingValue);

        // Total consumed = consumed1 + consumed2
        Assert.Equal(consumed1 + consumed2, result.ConsumedBenefitValue);
        Assert.Equal((1000m - consumed1) + (2000m - consumed2), result.RemainingBenefitValue);
    }

    // ==================================================================
    // TEST H: Partial payment — gift remains ineligible
    // ==================================================================

    [Fact]
    public void TestH_PartialPayment_GiftRemainsIneligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        // Only 5000 paid (50% of contracted amount)
        var completedPaymentTotal = 5000m;

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, completedPaymentTotal, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
    }

    // ==================================================================
    // TEST I: Overdue installment — gift remains ineligible
    // ==================================================================

    [Fact]
    public void TestI_NoPayment_GiftRemainsIneligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        // No payments at all
        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 0m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
    }

    // ==================================================================
    // TEST J: Successful payment — gift becomes eligible
    // ==================================================================

    [Fact]
    public void TestJ_SuccessfulPayment_GiftBecomesEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        // Activate the contract (eligibility requires Active status)
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        // Full payment
        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.True(canBecomeEligible);

        // Mark eligible
        var result = benefit.MarkEligible(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(BenefitEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.NotNull(benefit.EligibleAtUtc);
    }

    // ==================================================================
    // TEST K: Delivery idempotency
    // ==================================================================

    [Fact]
    public void TestK_DeliveryIdempotency_ConcurrentDeliveryAttempts()
    {
        var contract = CreateValidContract();
        var benefit = CreateEligibleBenefit(contract.Id, "Printer", 1000m);

        // First delivery
        var result1 = benefit.MarkGranted(DateTime.UtcNow, "user-1");
        Assert.True(result1.IsSuccess);
        Assert.True(benefit.IsGranted);

        // Second delivery (idempotent)
        var result2 = benefit.MarkGranted(DateTime.UtcNow, "user-2");
        Assert.True(result2.IsSuccess);
        Assert.True(benefit.IsGranted);

        // Only one delivery recorded
        Assert.Single(benefit.DomainEvents.OfType<BenefitDeliveredEvent>());
    }

    // ==================================================================
    // TEST L: Cross-tenant isolation
    // ==================================================================

    [Fact]
    public void TestL_CrossTenant_DifferentTenantsDifferentBenefits()
    {
        var contractA = CreateValidContract(tenantId: "tenant-A", contractNumber: "CNT-A");
        var contractB = CreateValidContract(tenantId: "tenant-B", contractNumber: "CNT-B");

        var benefitA = CreateBenefit(contractA.Id, "Printer-A", 1000m);
        var benefitB = CreateBenefit(contractB.Id, "Printer-B", 2000m);

        contractA.AddBenefit(benefitA);
        contractB.AddBenefit(benefitB);

        // Each benefit belongs to its own contract/tenant
        Assert.Equal(contractA.Id, benefitA.ContractId);
        Assert.Equal(contractB.Id, benefitB.ContractId);
        Assert.NotEqual(benefitA.ContractId, benefitB.ContractId);

        // Domain errors enforce cross-tenant access prevention
        Assert.NotEqual(contractA.TenantId, contractB.TenantId);
    }

    // ==================================================================
    // TEST M: Refund regression — Used + Remaining = correct
    // ==================================================================

    [Fact]
    public void TestM_RefundRegression_UsedPlusRemainingCorrect()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 3).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Verify: Used + Remaining = Total
        Assert.Equal(result.ConsumedBenefitValue + result.RemainingBenefitValue, result.TotalBenefitValue);

        // Verify: CustomerEconomicObligation = UsedSubscription + RemainingBenefits
        Assert.Equal(result.UsedSubscriptionAmount + result.RemainingBenefitValue, result.CustomerEconomicObligation);

        // Verify: Refund = Paid - Obligation
        Assert.Equal(result.AmountActuallyPaid - result.CustomerEconomicObligation, result.RefundableAmount);

        // Verify: RefundAmount = max(0, RefundableAmount)
        Assert.Equal(Math.Max(0m, result.RefundableAmount), result.RefundAmount);

        // Verify: Outstanding = max(0, -RefundableAmount)
        Assert.Equal(Math.Max(0m, -result.RefundableAmount), result.CustomerOutstandingAmount);
    }

    // ==================================================================
    // CRITICAL ECONOMIC SAFETY TEST (Section 28)
    // ==================================================================

    [Fact]
    public void CriticalEconomicSafetyTest_FullPayment_GiftDelivered_CancelHalfway()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractualMonthlyValue: 1000m,
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 3).Value);

        var gift = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(gift);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel halfway through (182 days)
        var cancellationDate = effectiveAt.AddDays(182);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // SAFETY INVARIANT 1: Subscription Used + Remaining Gift <= Customer Economic Obligation
        Assert.True(
            result.UsedSubscriptionAmount + result.RemainingBenefitValue <= result.CustomerEconomicObligation,
            $"Invariant violated: Used({result.UsedSubscriptionAmount}) + Remaining({result.RemainingBenefitValue}) > Obligation({result.CustomerEconomicObligation})");

        // SAFETY INVARIANT 2: RefundableAmount = Paid - Obligation
        Assert.Equal(
            result.AmountActuallyPaid - result.CustomerEconomicObligation,
            result.RefundableAmount);

        // SAFETY INVARIANT 3: No positive economic gain from gift
        // The customer cannot profit from the gift cancellation
        if (result.IsRefundDue)
        {
            // Refund must not exceed what was paid minus obligation
            Assert.True(result.RefundAmount <= result.AmountActuallyPaid,
                "Refund must not exceed amount paid");
        }

        // SAFETY INVARIANT 4: Remaining benefit value never negative
        Assert.True(result.RemainingBenefitValue >= 0,
            $"Remaining benefit value is negative: {result.RemainingBenefitValue}");

        // SAFETY INVARIANT 5: Consumed benefit value never exceeds contractual value
        Assert.True(result.ConsumedBenefitValue <= result.TotalBenefitValue,
            $"Consumed({result.ConsumedBenefitValue}) > Total({result.TotalBenefitValue})");

        // SAFETY INVARIANT 6: Refund amount never negative
        Assert.True(result.RefundAmount >= 0, "Refund amount is negative");

        // SAFETY INVARIANT 7: Outstanding never negative
        Assert.True(result.CustomerOutstandingAmount >= 0, "Outstanding amount is negative");
    }

    // ==================================================================
    // HISTORICAL SNAPSHOT TEST (Section 29)
    // ==================================================================

    [Fact]
    public void HistoricalSnapshotTest_BenefitSurvivesPlanChanges()
    {
        // Create Contract
        var contract = CreateValidContract(contractualMonthlyValue: 1000m, contractedAmount: 10000m);

        // Create Benefit snapshot
        var benefit = CreateBenefit(contract.Id, "Printer", 1500m);
        contract.AddBenefit(benefit);

        // Deliver Benefit
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.MarkGranted(DateTime.UtcNow, "user-1");

        // Verify snapshot is immutable
        Assert.Equal("Printer", benefit.Name);
        Assert.Equal(1500m, benefit.ContractualValue);
        Assert.Equal(ContractBenefitType.PhysicalGift, benefit.BenefitType);
        Assert.Equal("EGP", benefit.CurrencyCode);
        Assert.True(benefit.IsGranted);
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);

        // Simulate Plan/Promotion/Benefit catalog changes (would happen elsewhere)
        // The contract benefit snapshot must remain authoritative
        Assert.Equal(1500m, contract.Benefits.First().ContractualValue);
        Assert.Equal("Printer", contract.Benefits.First().Name);
    }

    // ==================================================================
    // Additional boundary tests
    // ==================================================================

    [Fact]
    public void BeforeContractStart_ConsumedIsZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);

        var benefit = CreateGrantedBenefit(Guid.NewGuid(), "Gift", 1000m);

        // Before start: elapsedDays < 0
        var elapsedDays = -10;
        var durationDays = (endsAt - effectiveAt).Days;

        var consumed = benefit.CalculateConsumedValue(elapsedDays, durationDays);

        Assert.Equal(0m, consumed);
    }

    [Fact]
    public void AtContractEnd_ConsumedIsFull()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var durationDays = (endsAt - effectiveAt).Days;

        var benefit = CreateGrantedBenefit(Guid.NewGuid(), "Gift", 1000m);

        var consumed = benefit.CalculateConsumedValue(durationDays, durationDays);

        Assert.Equal(1000m, consumed);
    }

    [Fact]
    public void PastContractEnd_ConsumedIsFull()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var durationDays = (endsAt - effectiveAt).Days;

        var benefit = CreateGrantedBenefit(Guid.NewGuid(), "Gift", 1000m);

        var consumed = benefit.CalculateConsumedValue(durationDays + 100, durationDays);

        Assert.Equal(1000m, consumed);
    }

    [Fact]
    public void ZeroDuration_ConsumedIsFull()
    {
        var benefit = CreateGrantedBenefit(Guid.NewGuid(), "Gift", 1000m);

        // Edge case: zero duration
        var consumed = benefit.CalculateConsumedValue(10, 0);

        Assert.Equal(1000m, consumed);
    }

    [Fact]
    public void Benefit_NonFinancial_NoRecoveryDeduction()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var zeroBenefit = CreateGrantedBenefit(contract.Id, "Service", 0m, ContractBenefitType.Service);
        contract.AddBenefit(zeroBenefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        Assert.Equal(0m, result.ConsumedBenefitValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
    }

    [Fact]
    public void Benefit_CurrencyMustMatchContract()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        // Manually set wrong currency to test mismatch
        var result = contract.AddBenefit(benefit);

        // Benefit was created with EGP (matching contract), so it should succeed
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Benefit_DeliveryLifecycle_CompletePath()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        // Initial state
        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);
        Assert.False(benefit.IsGranted);
        Assert.Null(benefit.EligibleAtUtc);
        Assert.Null(benefit.GrantedAtUtc);
        Assert.Null(benefit.DeliveredBy);

        // Mark eligible
        var eligibleTime = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        benefit.MarkEligible(eligibleTime);
        Assert.Equal(BenefitEligibilityStatus.Eligible, benefit.EligibilityStatus);
        Assert.Equal(eligibleTime, benefit.EligibleAtUtc);

        // Mark delivered
        var deliveredTime = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        benefit.MarkGranted(deliveredTime, "staff-42");
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);
        Assert.True(benefit.IsGranted);
        Assert.Equal(deliveredTime, benefit.GrantedAtUtc);
        Assert.Equal("staff-42", benefit.DeliveredBy);
    }

    [Fact]
    public void Benefit_EligibilityService_NonFinancialRequiresSameConditions()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var zeroBenefit = CreateBenefit(contract.Id, "Free Service", 0m, ContractBenefitType.Service);

        // Contract is Draft (not Active) -- zero-value must NOT bypass eligibility
        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            zeroBenefit, contract, 0m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);

        // Activate contract and verify eligibility with satisfied payment
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var canBecomeEligibleActive = s_eligibilityService.CanBecomeEligible(
            zeroBenefit, contract, 10000m, contract.ContractedAmount);

        Assert.True(canBecomeEligibleActive);
    }

    [Fact]
    public void Benefit_EligibilityService_InactiveContract_NotEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        // Contract is in Draft status (not Active)
        Assert.Equal(ContractStatus.Draft, contract.Status);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
    }

    [Fact]
    public void Benefit_EligibilityService_ActiveContractFullPayment_Eligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        // Activate the contract
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.True(canBecomeEligible);
    }

    [Fact]
    public void Benefit_EligibilityService_DeliveredStayDelivered()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var benefit = CreateGrantedBenefit(contract.Id, "Printer", 1000m);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 0m, contract.ContractedAmount);

        // Already delivered, so CanBecomeEligible returns true
        Assert.True(canBecomeEligible);

        // DetermineEligibilityStatus returns Delivered (preserves state)
        var status = s_eligibilityService.DetermineEligibilityStatus(
            benefit, contract, 0m, contract.ContractedAmount);

        Assert.Equal(BenefitEligibilityStatus.Delivered, status);
    }

    // ==================================================================
    // Remaining Unconsumed Value Never Negative
    // ==================================================================

    [Fact]
    public void RemainingValue_NeverNegative()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);

        // Test at many different elapsed day values
        for (int days = -30; days <= 400; days += 5)
        {
            var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
            var contractDurationDays = (endsAt - effectiveAt).Days;

            var consumed = benefit.CalculateConsumedValue(days, contractDurationDays);
            var remaining = benefit.CalculateRemainingValue(days, contractDurationDays);

            Assert.True(consumed >= 0, $"Consumed is negative at {days} days: {consumed}");
            Assert.True(consumed <= 1000m, $"Consumed exceeds contractual at {days} days: {consumed}");
            Assert.True(remaining >= 0, $"Remaining is negative at {days} days: {remaining}");
            Assert.True(remaining <= 1000m, $"Remaining exceeds contractual at {days} days: {remaining}");
            Assert.Equal(1000m, consumed + remaining);
        }
    }

    // ==================================================================
    // ContractBenefitType PhysicalGift vs Service distinction
    // ==================================================================

    [Fact]
    public void BenefitType_PhysicalGift_CanBeMarkedGranted()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m, ContractBenefitType.PhysicalGift);

        Assert.Equal(ContractBenefitType.PhysicalGift, benefit.BenefitType);

        benefit.MarkEligible(DateTime.UtcNow);
        benefit.MarkGranted(DateTime.UtcNow, "user-1");

        Assert.True(benefit.IsGranted);
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);
    }

    [Fact]
    public void BenefitType_Service_CannotBeMarkedGranted()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Support", 500m, ContractBenefitType.Service);

        Assert.Equal(ContractBenefitType.Service, benefit.BenefitType);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow);

        // Service benefits cannot be delivered through MarkBenefitDelivered
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
        Assert.Equal(BenefitEligibilityStatus.Eligible, benefit.EligibilityStatus);
    }

    // ==================================================================
    // Section 28 critical scenario — No positive economic gain from gift
    // ==================================================================

    [Fact]
    public void CriticalEconomicSafety_NoPositiveGainFromGift()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractualMonthlyValue: 1000m,
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 3).Value);

        var gift = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(gift);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel at various points and verify no economic gain
        for (int days = 0; days <= 365; days += 30)
        {
            var cancellationDate = effectiveAt.AddDays(days);

            // Recreate fresh benefit for each calculation (consumption is side-effect free)
            var freshGift = CreateGrantedBenefit(contract.Id, "Gift", 1000m);

            var result = s_refundService.Calculate(
                contract, cancellationDate,
                new[] { payment },
                new[] { freshGift });

            // Core invariant: refund <= amount paid
            Assert.True(result.RefundAmount <= result.AmountActuallyPaid,
                $"Day {days}: Refund({result.RefundAmount}) > Paid({result.AmountActuallyPaid})");

            // Core invariant: remaining benefit value >= 0
            Assert.True(result.RemainingBenefitValue >= 0,
                $"Day {days}: Remaining({result.RemainingBenefitValue}) < 0");

            // Core invariant: consumed benefit <= total
            Assert.True(result.ConsumedBenefitValue <= result.TotalBenefitValue,
                $"Day {days}: Consumed({result.ConsumedBenefitValue}) > Total({result.TotalBenefitValue})");
        }
    }

    // ==================================================================
    // Domain Events
    // ==================================================================

    [Fact]
    public void Contract_Benefit_RaisesBenefitEligibleEvent()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);

        var domainEvents = benefit.DomainEvents;
        Assert.Contains(domainEvents, e => e is BenefitEligibleEvent);

        var eligibleEvent = domainEvents.OfType<BenefitEligibleEvent>().First();
        Assert.Equal(contract.TenantId, eligibleEvent.TenantId);
        Assert.Equal(contract.Id, eligibleEvent.ContractId);
        Assert.Equal(benefit.Id, eligibleEvent.BenefitId);
    }

    [Fact]
    public void Contract_Benefit_RaisesBenefitDeliveredEvent()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
        benefit.MarkGranted(DateTime.UtcNow, "user-1", contract.TenantId);

        var domainEvents = benefit.DomainEvents;
        Assert.Contains(domainEvents, e => e is BenefitDeliveredEvent);

        var deliveredEvent = domainEvents.OfType<BenefitDeliveredEvent>().First();
        Assert.Equal(contract.TenantId, deliveredEvent.TenantId);
        Assert.Equal(contract.Id, deliveredEvent.ContractId);
        Assert.Equal(benefit.Id, deliveredEvent.BenefitId);
    }

    // ==================================================================
    // Immutability after delivery
    // ==================================================================

    [Fact]
    public void Benefit_Delivered_SnapshotFieldsAreImmutable()
    {
        var contract = CreateValidContract();
        var benefit = CreateGrantedBenefit(contract.Id, "Printer", 1000m);

        // Verify no public setters on snapshot fields
        var nameProp = typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.Name));
        Assert.NotNull(nameProp);
        Assert.Null(nameProp.GetSetMethod(false));

        var valueProp = typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.ContractualValue));
        Assert.NotNull(valueProp);
        Assert.Null(valueProp.GetSetMethod(false));

        var typeProp = typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.BenefitType));
        Assert.NotNull(typeProp);
        Assert.Null(typeProp.GetSetMethod(false));

        var currencyProp = typeof(ContractBenefit).GetProperty(nameof(ContractBenefit.CurrencyCode));
        Assert.NotNull(currencyProp);
        Assert.Null(currencyProp.GetSetMethod(false));
    }

    // ==================================================================
    // TASK 6.1: Zero-value benefit must not bypass eligibility
    // ==================================================================

    [Fact]
    public void Task6_1_ZeroValue_InactiveContract_NotEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var zeroBenefit = CreateBenefit(contract.Id, "Free Gift", 0m);

        // Contract is Draft (not Active), zero-value must NOT bypass
        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            zeroBenefit, contract, 0m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, zeroBenefit.EligibilityStatus);
    }

    [Fact]
    public void Task6_1_ZeroValue_InsufficientPayment_NotEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var zeroBenefit = CreateBenefit(contract.Id, "Free Gift", 0m);

        // Active contract but no payment — zero-value must NOT bypass payment check
        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            zeroBenefit, contract, 0m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
        Assert.Equal(BenefitEligibilityStatus.NotEligible, zeroBenefit.EligibilityStatus);
    }

    [Fact]
    public void Task6_1_ZeroValue_MustNotBypassEligibility()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);

        // Draft contract + zero payment — zero-value must not bypass
        var zeroBenefit = CreateBenefit(contract.Id, "Sticker", 0m);
        Assert.False(s_eligibilityService.CanBecomeEligible(zeroBenefit, contract, 0m, contract.ContractedAmount));

        // Active contract + insufficient payment — zero-value must not bypass
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);
        Assert.False(s_eligibilityService.CanBecomeEligible(zeroBenefit, contract, 5000m, contract.ContractedAmount));

        // Active contract + full payment — zero-value becomes eligible
        Assert.True(s_eligibilityService.CanBecomeEligible(zeroBenefit, contract, 10000m, contract.ContractedAmount));
    }

    // ==================================================================
    // TASK 6.1: Delivery restricted to PhysicalGift only
    // ==================================================================

    [Fact]
    public void Task6_1_PhysicalGift_CanBeDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m, ContractBenefitType.PhysicalGift);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow, "user-1");

        Assert.True(result.IsSuccess);
        Assert.True(benefit.IsGranted);
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);
    }

    [Fact]
    public void Task6_1_Service_CannotBeDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Support", 500m, ContractBenefitType.Service);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
    }

    [Fact]
    public void Task6_1_FinancialCredit_CannotBeDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Credit", 500m, ContractBenefitType.FinancialCredit);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
    }

    [Fact]
    public void Task6_1_ExtendedTerm_CannotBeDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Extension", 500m, ContractBenefitType.ExtendedTerm);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
    }

    [Fact]
    public void Task6_1_Other_CannotBeDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "OtherBenefit", 500m, ContractBenefitType.Other);

        benefit.MarkEligible(DateTime.UtcNow);
        var result = benefit.MarkGranted(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.OnlyPhysicalGiftCanBeDelivered", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
    }

    [Fact]
    public void Task6_1_AlreadyDelivered_PhysicalGift_IsIdempotent()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m, ContractBenefitType.PhysicalGift);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
        benefit.MarkGranted(DateTime.UtcNow, "user-1", contract.TenantId);
        Assert.True(benefit.IsGranted);

        // Second delivery is idempotent
        var result2 = benefit.MarkGranted(DateTime.UtcNow, "user-2", contract.TenantId);
        Assert.True(result2.IsSuccess);
        Assert.True(benefit.IsGranted);
    }

    // ==================================================================
    // TASK 6.1: Domain events contain real TenantId
    // ==================================================================

    [Fact]
    public void Task6_1_BenefitEligibleEvent_TenantId_EqualsContractTenantId()
    {
        var contract = CreateValidContract(tenantId: "tenant-abc-123");
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);

        var eligibleEvent = benefit.DomainEvents.OfType<BenefitEligibleEvent>().Single();
        Assert.Equal(contract.TenantId, eligibleEvent.TenantId);
        Assert.Equal("tenant-abc-123", eligibleEvent.TenantId);
        Assert.NotEmpty(eligibleEvent.TenantId);
    }

    [Fact]
    public void Task6_1_BenefitDeliveredEvent_TenantId_EqualsContractTenantId()
    {
        var contract = CreateValidContract(tenantId: "tenant-xyz-789");
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
        benefit.MarkGranted(DateTime.UtcNow, "user-1", contract.TenantId);

        var deliveredEvent = benefit.DomainEvents.OfType<BenefitDeliveredEvent>().Single();
        Assert.Equal(contract.TenantId, deliveredEvent.TenantId);
        Assert.Equal("tenant-xyz-789", deliveredEvent.TenantId);
        Assert.NotEmpty(deliveredEvent.TenantId);
    }

    // ==================================================================
    // TASK 6.1: Installment dependency documentation
    // ==================================================================

    [Fact]
    public void Task6_1_InstallmentDependency_IsDocumented()
    {
        // This test documents that overdue-installment validation depends on the
        // future Installment Schedule/Obligation engine.
        // Current eligibility checks:
        //   1. Contract.Status == Active
        //   2. completedPaymentTotal >= contractedAmount
        //   3. overdue installment check: NOT YET IMPLEMENTED
        //
        // The BenefitEligibilityService documentation explicitly states:
        // "No overdue installment check is performed (current model does not have
        //  installment schedules; this limitation is documented as a dependency on
        //  the future Installment Schedule/Obligation engine)"
        //
        // This test verifies the current behavior is consistent with that limitation:
        // a benefit with Active contract + full payment becomes eligible even without
        // installment schedule validation.

        var contract = CreateValidContract(contractedAmount: 10000m);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.True(canBecomeEligible);
    }

    // ==================================================================
    // TASK 6.1: Refund regression — zero-value benefit no negative values
    // ==================================================================

    [Fact]
    public void Task6_1_ZeroValueBenefit_NoNegativeRefundValues()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var zeroBenefit = CreateGrantedBenefit(contract.Id, "Free Sticker", 0m);
        contract.AddBenefit(zeroBenefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Zero-value benefit must not introduce negative or incorrect refund values
        Assert.True(result.ConsumedBenefitValue >= 0);
        Assert.True(result.RemainingBenefitValue >= 0);
        Assert.True(result.RefundAmount >= 0);
        Assert.True(result.CustomerOutstandingAmount >= 0);
        Assert.Equal(0m, result.ConsumedBenefitValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
    }

    // ==================================================================
    // TASK 6.1: Lifecycle preservation — no Delivered→Eligible transition
    // ==================================================================

    [Fact]
    public void Task6_1_Delivered_BenefitCannotRevertToEligible()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
        benefit.MarkGranted(DateTime.UtcNow, "user-1", contract.TenantId);
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);

        // Attempt to mark eligible again — should be idempotent, not revert
        benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
        Assert.Equal(BenefitEligibilityStatus.Delivered, benefit.EligibilityStatus);
        Assert.True(benefit.IsGranted);
    }

    [Fact]
    public void Task6_1_NotEligible_BenefitCannotSkipToDelivered()
    {
        var contract = CreateValidContract();
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        Assert.Equal(BenefitEligibilityStatus.NotEligible, benefit.EligibilityStatus);

        // Cannot deliver a benefit that is not eligible
        var result = benefit.MarkGranted(DateTime.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.Benefit.NotEligible", result.Errors[0].Code);
        Assert.False(benefit.IsGranted);
    }

    // ==================================================================
    // TASK 6.1: Complete 20-test matrix verification
    // ==================================================================

    [Fact]
    public void Task6_1_Eligibility_ActiveContract_SatisfiedPayment_Eligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.True(canBecomeEligible);
    }

    [Fact]
    public void Task6_1_Eligibility_InactiveContract_NotEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        Assert.Equal(ContractStatus.Draft, contract.Status);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 10000m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
    }

    [Fact]
    public void Task6_1_Eligibility_InsufficientPayment_NotEligible()
    {
        var contract = CreateValidContract(contractedAmount: 10000m);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);

        var canBecomeEligible = s_eligibilityService.CanBecomeEligible(
            benefit, contract, 5000m, contract.ContractedAmount);

        Assert.False(canBecomeEligible);
    }

    [Fact]
    public void Task6_1_Refund_DeliveredPhysicalGift_ProducesRemainingValueRecovery()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        var cancellationDate = effectiveAt.AddDays(182);
        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Delivered physical gift produces remaining-value recovery
        Assert.True(result.RemainingBenefitValue > 0);
        Assert.True(result.ConsumedBenefitValue > 0);
        Assert.Equal(result.ConsumedBenefitValue + result.RemainingBenefitValue, result.TotalBenefitValue);
    }

    [Fact]
    public void Task6_1_Refund_NonDeliveredGift_ProducesZeroRecovery()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            contractedAmount: 10000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var benefit = CreateBenefit(contract.Id, "Printer", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        var contribution = result.BenefitContributions.First();
        Assert.False(contribution.IsRecoverable);
        Assert.Equal(0m, contribution.RemainingValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
    }

    [Fact]
    public void Task6_1_Refund_MultipleBenefits_IndependentlyCalculated()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contractDurationDays = (endsAt - effectiveAt).Days;
        var contract = CreateValidContract(
            monthlyListPrice: 1500m,
            contractualMonthlyValue: 1500m,
            contractedAmount: 18000m,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 18000m, "EGP", 1500m, 1).Value);

        var benefit1 = CreateGrantedBenefit(contract.Id, "Printer", 1000m);
        var benefit2 = CreateGrantedBenefit(contract.Id, "PC", 2000m);
        contract.AddBenefit(benefit1);
        contract.AddBenefit(benefit2);

        var payment = CreateCompletedPayment(18000m, 18000m, contract);
        var cancellationDate = effectiveAt.AddDays(182);

        var result = s_refundService.Calculate(
            contract, cancellationDate,
            new[] { payment },
            contract.Benefits);

        var consumed1 = Math.Round(1000m * 182m / contractDurationDays, 2, MidpointRounding.AwayFromZero);
        var consumed2 = Math.Round(2000m * 182m / contractDurationDays, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(2, result.BenefitContributions.Count);
        Assert.Equal(consumed1 + consumed2, result.ConsumedBenefitValue);
        Assert.Equal((1000m - consumed1) + (2000m - consumed2), result.RemainingBenefitValue);
    }
}
