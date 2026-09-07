namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;

using Xunit;

/// <summary>
/// Domain rules for the Refund calculation engine: pricing tiers, gift recovery,
/// payment basis, and the critical test cases from the spec.
/// </summary>
public class RefundCalculationEngineTests
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
            contractedAmount: contractedAmount,
            discountAmount: 0,
            promotionReference: null);

        Assert.True(result.IsSuccess, $"Contract creation failed: {string.Join(",", result.Errors?.Select(e => e.Code) ?? [])}");
        return result.Value;
    }

    private static Payment CreateCompletedPayment(
        decimal amount,
        decimal allocatedAmount,
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

        // Complete the payment
        var completeResult = payment.Complete(DateTime.UtcNow);
        Assert.True(completeResult.IsSuccess);

        // Add allocation if specified (using reflection since _allocations is private)
        if (allocatedAmount > 0)
        {
            var allocation = PaymentAllocation.Create(
                Guid.NewGuid(),
                payment.Id,
                Guid.NewGuid(), // invoice ID
                allocatedAmount,
                DateTime.UtcNow);

            Assert.True(allocation.IsSuccess);

            // Use reflection to add allocation to the private _allocations field
            var allocationsField = typeof(Payment).GetField("_allocations", BindingFlags.NonPublic | BindingFlags.Instance);
            if (allocationsField != null)
            {
                var allocationsList = (List<PaymentAllocation>)allocationsField.GetValue(payment)!;
                allocationsList.Add(allocation.Value);
            }
            else
            {
                // Try property if field not found
                var allocationsProp = typeof(Payment).GetProperty("Allocations", BindingFlags.NonPublic | BindingFlags.Instance);
                if (allocationsProp != null)
                {
                    // Allocations is IReadOnlyList, so we need to find the underlying list
                    throw new InvalidOperationException("Cannot add allocation via reflection - field not found");
                }
            }
        }

        return payment;
    }

    private static ContractBenefit CreateGrantedBenefit(
        Guid contractId,
        string name,
        decimal value,
        DateTime? grantedAtUtc = null)
    {
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contractId,
            ContractBenefitType.PhysicalGift,
            name,
            null,
            value,
            "EGP").Value;

        var grantResult = benefit.MarkGranted(grantedAtUtc ?? DateTime.UtcNow);
        Assert.True(grantResult.IsSuccess);

        return benefit;
    }

    private static readonly RefundCalculationService s_service = new();

    // ------------------------------------------------------------------
    // Critical Test Case 1: 12-month contract, paid 10,000, gift 1,000, cancel after 6 months
    // Expected: refund = 4,275.89 (day-based gift consumption)
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_PaidInFull_WithGift_CancelAfter6Months_Returns4275_89()
    {
        // Arrange: 12-month contract (10,000), paid 10,000, gift 1,000
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        // Add pricing tiers: 1=1000, 3=2700, 6=5220, 12=10000
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value);

        // Add gift benefit: 1,000
        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        // Payment: paid 10,000
        var payment = CreateCompletedPayment(10000m, 10000m);

        // Cancel after 6 months (July 1, 2026)
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Assert
        Assert.Equal(6, result.ElapsedMonths);
        Assert.Equal(5220m, result.UsedSubscriptionAmount); // 6-month tier
        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(1000m, result.TotalBenefitValue);

        // Gift consumption (day-based): 1000 * (181 / 365) = 495.89 → remaining = 504.11
        // Refund = 10000 - (5220 + 504.11) = 4275.89
        Assert.Equal(4275.89m, result.RefundAmount);
        Assert.Equal(0m, result.CustomerOutstandingAmount);
        Assert.True(result.IsRefundDue);
        Assert.False(result.IsAmountOwed);
    }

    // ------------------------------------------------------------------
    // Critical Test Case 2: 12-month contract, paid 4,000, cancel after 6 months
    // Expected: customer owes 1,724.11 (day-based gift consumption)
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_PartialPayment_CancelAfter6Months_CustomerOwes1720()
    {
        // Arrange: 12-month contract (10,000), paid 4,000, gift 1,000
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        // Add pricing tiers
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value);

        // Add gift benefit: 1,000
        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        // Payment: paid 4,000
        var payment = CreateCompletedPayment(4000m, 4000m);

        // Cancel after 6 months
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Assert
        Assert.Equal(6, result.ElapsedMonths);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(4000m, result.AmountActuallyPaid);

        // Gift consumption (day-based): 1000 * (181 / 365) = 495.89 → remaining = 504.11
        // Customer owes = (5220 + 504.11) - 4000 = 1724.11
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(1724.11m, result.CustomerOutstandingAmount);
        Assert.False(result.IsRefundDue);
        Assert.True(result.IsAmountOwed);
    }

    // ------------------------------------------------------------------
    // Edge case: No payments made
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_NoPayments_CustomerOwesFullObligation()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            Array.Empty<Payment>(),
            Array.Empty<ContractBenefit>());

        Assert.Equal(0m, result.AmountActuallyPaid);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(5220m, result.CustomerOutstandingAmount);
        Assert.Equal(0m, result.RefundAmount);
    }

    // ------------------------------------------------------------------
    // Edge case: Full contract duration elapsed
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_FullDurationElapsed_NoRefund()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var payment = CreateCompletedPayment(10000m, 10000m);

        // Cancel after full 12 months
        var cancellationDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        Assert.Equal(12, result.ElapsedMonths);
        Assert.Equal(10000m, result.UsedSubscriptionAmount);
        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(0m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Edge case: Cancel immediately (0 months elapsed)
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_ZeroMonthsElapsed_FullRefund()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        var payment = CreateCompletedPayment(10000m, 10000m);

        // Cancel on effective date
        var cancellationDate = effectiveAt;

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        Assert.Equal(0, result.ElapsedMonths);
        Assert.Equal(0m, result.UsedSubscriptionAmount);
        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(10000m, result.RefundAmount);
        Assert.Equal(0m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Edge case: Non-granted benefits are not recoverable
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_NonGrantedBenefits_NotRecoverable()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        // Add a benefit but don't grant it
        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contract.Id,
            ContractBenefitType.PhysicalGift,
            "Pending Gift",
            null,
            1000m,
            "EGP").Value;

        // Don't mark as granted
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m);
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Non-granted benefit should not be recoverable
        var benefitContribution = result.BenefitContributions.First();
        Assert.False(benefitContribution.IsRecoverable);
        Assert.Equal(0m, benefitContribution.RemainingValue);
    }

    // ------------------------------------------------------------------
    // Edge case: Multiple payments
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_MultiplePayments_SumsAllocatedAmounts()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        var payment1 = CreateCompletedPayment(3000m, 3000m);
        var payment2 = CreateCompletedPayment(2000m, 2000m);
        var payment3 = CreateCompletedPayment(5000m, 5000m);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment1, payment2, payment3 },
            Array.Empty<ContractBenefit>());

        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(3, result.PaymentContributions.Count);
    }

    // ------------------------------------------------------------------
    // Edge case: Pending payments don't count
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_PendingPayments_DoNotCount()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        // Create a pending payment (not completed)
        var pendingPayment = Payment.Create(
            Guid.NewGuid(),
            "PAY-PENDING",
            5000m,
            "EGP",
            PaymentMethod.Cash).Value;

        // Don't complete it
        var completedPayment = CreateCompletedPayment(3000m, 3000m);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { pendingPayment, completedPayment },
            Array.Empty<ContractBenefit>());

        // Only completed payment counts
        Assert.Equal(3000m, result.AmountActuallyPaid);
        Assert.Single(result.PaymentContributions);
    }

    // ------------------------------------------------------------------
    // Refund entity lifecycle tests
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Create_ValidInput_Succeeds()
    {
        var result = Refund.Create(
            Guid.NewGuid(),
            "REF-001",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            4280m,
            "EGP",
            "Early cancellation",
            "user-1",
            DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(RefundStatus.Pending, result.Value.Status);
    }

    [Fact]
    public void Refund_Create_InvalidInput_Fails()
    {
        Assert.False(Refund.Create(Guid.Empty, "REF-001", Guid.NewGuid(), null, null, 100m, "EGP", "Reason", "user-1", DateTime.UtcNow).IsSuccess);
        Assert.False(Refund.Create(Guid.NewGuid(), "", Guid.NewGuid(), null, null, 100m, "EGP", "Reason", "user-1", DateTime.UtcNow).IsSuccess);
        Assert.False(Refund.Create(Guid.NewGuid(), "REF-001", Guid.Empty, null, null, 100m, "EGP", "Reason", "user-1", DateTime.UtcNow).IsSuccess);
        Assert.False(Refund.Create(Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null, 0m, "EGP", "Reason", "user-1", DateTime.UtcNow).IsSuccess);
        Assert.False(Refund.Create(Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null, 100m, "US", "Reason", "user-1", DateTime.UtcNow).IsSuccess);
        Assert.False(Refund.Create(Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null, 100m, "EGP", "", "user-1", DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Refund_Lifecycle_PendingToApprovedToCompleted()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.Equal(RefundStatus.Pending, refund.Status);

        // Approve
        Assert.True(refund.Approve("approver-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Approved, refund.Status);
        Assert.Equal("approver-1", refund.ApprovedBy);

        // Execute
        Assert.True(refund.Execute("executor-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Completed, refund.Status);
        Assert.Equal("executor-1", refund.ExecutedBy);
        Assert.True(refund.IsExecuted);
    }

    [Fact]
    public void Refund_Lifecycle_InvalidTransitions_AreDenied()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        // With optional approval workflow (Task #4), MarkProcessing is allowed from Pending.
        // Verify it succeeds and transitions to Processing.
        Assert.True(refund.MarkProcessing().IsSuccess);
        Assert.Equal(RefundStatus.Processing, refund.Status);

        // Approve
        refund.Approve("approver-1", DateTime.UtcNow);

        // Cannot approve again
        Assert.False(refund.Approve("approver-2", DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Refund_Lifecycle_PendingToExecute_Directly_Allowed()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        // Can execute directly from Pending (optional approval workflow)
        Assert.True(refund.Execute("executor-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Completed, refund.Status);
        Assert.True(refund.IsExecuted);
    }

    [Fact]
    public void Refund_Lifecycle_CanBeCancelled()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.True(refund.Cancel("canceller-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Cancelled, refund.Status);
    }

    [Fact]
    public void Refund_Lifecycle_CanBeRejected()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-001", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.True(refund.Reject("rejecter-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Rejected, refund.Status);
    }
}
