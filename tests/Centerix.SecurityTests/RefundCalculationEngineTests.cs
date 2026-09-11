namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Domain.Common;
using Centerix.Domain.Platform.Billing.Invoicing;
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

        // Complete the payment
        var completeResult = payment.Complete(DateTime.UtcNow);
        Assert.True(completeResult.IsSuccess);

        // Add allocation if specified
        if (allocatedAmount > 0)
        {
            if (contract is not null)
            {
                // Create an Invoice linked to the contract, then allocate to it
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
            else
            {
                // Legacy: no contract linkage (for backward compat with tests that don't need scoping)
                var allocation = PaymentAllocation.Create(
                    Guid.NewGuid(),
                    payment.Id,
                    Guid.NewGuid(),
                    allocatedAmount,
                    DateTime.UtcNow);

                Assert.True(allocation.IsSuccess);

                var allocationsField = typeof(Payment).GetField("_allocations", BindingFlags.NonPublic | BindingFlags.Instance);
                var allocationsList = (List<PaymentAllocation>)allocationsField!.GetValue(payment)!;
                allocationsList.Add(allocation.Value);
            }
        }

        return payment;
    }

    /// <summary>
    /// Creates a completed payment with allocations linked to specific invoices.
    /// Each allocation has its Invoice navigation property set, which is required
    /// for contract-scoped payment isolation in RefundCalculationService.
    /// </summary>
    private static Payment CreateCompletedPaymentWithInvoices(
        decimal amount,
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

        return payment;
    }

    /// <summary>
    /// Adds an allocation to a payment with its Invoice navigation property set.
    /// The Invoice's ContractId determines which contract the allocation belongs to.
    /// </summary>
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
        {
            allocation.Value.Reverse();
        }

        // Set Invoice navigation property via reflection
        var invoiceProp = typeof(PaymentAllocation).GetProperty("Invoice", BindingFlags.Public | BindingFlags.Instance);
        invoiceProp!.SetValue(allocation.Value, invoice);

        // Add to payment's private _allocations field
        var allocationsField = typeof(Payment).GetField("_allocations", BindingFlags.NonPublic | BindingFlags.Instance);
        var allocationsList = (List<PaymentAllocation>)allocationsField!.GetValue(payment)!;
        allocationsList.Add(allocation.Value);
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
        var payment = CreateCompletedPayment(10000m, 10000m, contract);

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
        var payment = CreateCompletedPayment(4000m, 4000m, contract);

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

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

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

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

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

        var payment = CreateCompletedPayment(10000m, 10000m, contract);
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

        var payment1 = CreateCompletedPayment(3000m, 3000m, contract);
        var payment2 = CreateCompletedPayment(2000m, 2000m, contract);
        var payment3 = CreateCompletedPayment(5000m, 5000m, contract);

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
        var completedPayment = CreateCompletedPayment(3000m, 3000m, contract);

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

    // ------------------------------------------------------------------
    // Critical Test Case C — Cross-contract payment isolation
    // One tenant, two contracts, each with its own payment.
    // Cancelling Contract A must only count Contract A's payment.
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_CrossContractPaymentIsolation_ContractBDoesNotAffectA()
    {
        // Arrange: Two contracts under the same tenant
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var contractA = CreateValidContract(
            contractNumber: "CNT-A",
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 6, 5220m, "EGP", 1000m, 2).Value);
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 12, 10000m, "EGP", 1000m, 3).Value);

        var contractB = CreateValidContract(
            contractNumber: "CNT-B",
            monthlyListPrice: 500m,
            contractedAmount: 5000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractB.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractB.Id, 1, 500m, "EGP", 500m, 1).Value);
        contractB.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractB.Id, 6, 2610m, "EGP", 500m, 2).Value);
        contractB.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractB.Id, 12, 5000m, "EGP", 500m, 3).Value);

        // Invoice A belongs to Contract A
        var invoiceA = Invoice.Create(
            Guid.NewGuid(), "INV-A",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contractA.Id).Value;
        invoiceA.Issue(DateTime.UtcNow);

        // Invoice B belongs to Contract B
        var invoiceB = Invoice.Create(
            Guid.NewGuid(), "INV-B",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            5000m, 0, 0, 5000m,
            contractId: contractB.Id).Value;
        invoiceB.Issue(DateTime.UtcNow);

        // Payment A = 10,000 allocated to Invoice A (Contract A)
        var paymentA = CreateCompletedPaymentWithInvoices(10000m, "PAY-A");
        AddAllocationWithInvoice(paymentA, invoiceA, 10000m);

        // Payment B = 5,000 allocated to Invoice B (Contract B)
        var paymentB = CreateCompletedPaymentWithInvoices(5000m, "PAY-B");
        AddAllocationWithInvoice(paymentB, invoiceB, 5000m);

        // Cancel Contract A after 6 months
        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = s_service.Calculate(
            contractA,
            cancellationDate,
            new[] { paymentA, paymentB },
            Array.Empty<ContractBenefit>());

        // Assert: AmountActuallyPaid = 10,000 (only Payment A), NOT 15,000
        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(4780m, result.RefundAmount); // 10000 - 5220 = 4780
    }

    // ------------------------------------------------------------------
    // Critical Test Case D — Unallocated payment
    // Payment Amount = 10,000, Allocated to cancelled contract = 6,000
    // Unallocated = 4,000. Amount actually paid: 6,000, not 10,000.
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_UnallocatedPayment_OnlyAllocatedPortionCounts()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        // Invoice belongs to this contract
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-UNALLOC",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);

        // Payment = 10,000 but only 6,000 allocated to this contract's invoice
        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-UNALLOC");
        AddAllocationWithInvoice(payment, invoice, 6000m);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // AmountActuallyPaid = 6,000 (only the allocated portion), NOT 10,000
        Assert.Equal(6000m, result.AmountActuallyPaid);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(780m, result.RefundAmount); // 6000 - 5220 = 780
    }

    // ------------------------------------------------------------------
    // Critical Test Case E — Reversed allocation
    // A completed payment has Allocation = 6,000, Status = Reversed
    // It must contribute 0 to AmountActuallyPaid.
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_ReversedAllocation_ContributesZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-REV",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);

        // Payment with a REVERSED allocation
        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-REV");
        AddAllocationWithInvoice(payment, invoice, 6000m, PaymentAllocationStatus.Reversed);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Reversed allocation must contribute 0
        Assert.Equal(0m, result.AmountActuallyPaid);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(5220m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Critical Test Case F — Pending/failed payment
    // Pending or Failed payment must contribute 0 even if an allocation exists.
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_PendingPayment_ContributesZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        // Pending payment (NOT completed) with an allocation
        var pendingPayment = Payment.Create(
            Guid.NewGuid(), "PAY-PENDING", 5000m, "EGP", PaymentMethod.Cash).Value;
        // Don't complete it

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { pendingPayment },
            Array.Empty<ContractBenefit>());

        // Pending payment must contribute 0
        Assert.Equal(0m, result.AmountActuallyPaid);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(5220m, result.CustomerOutstandingAmount);
    }

    [Fact]
    public void Refund_Calculation_FailedPayment_ContributesZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        // Failed payment with an allocation
        var failedPayment = Payment.Create(
            Guid.NewGuid(), "PAY-FAILED", 5000m, "EGP", PaymentMethod.Cash).Value;
        failedPayment.MarkFailed();

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { failedPayment },
            Array.Empty<ContractBenefit>());

        // Failed payment must contribute 0
        Assert.Equal(0m, result.AmountActuallyPaid);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(5220m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Test Case: Mixed allocations — some to contract, some to other contracts
    // Payment has 2 allocations: 6,000 to this contract, 4,000 to another
    // AmountActuallyPaid must be 6,000 (not 10,000)
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_MixedAllocations_OnlyContractAllocationsCount()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contractA = CreateValidContract(
            contractNumber: "CNT-A",
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var contractB = CreateValidContract(
            contractNumber: "CNT-B",
            monthlyListPrice: 1000m,
            contractedAmount: 5000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));

        // Invoice for Contract A
        var invoiceA = Invoice.Create(
            Guid.NewGuid(), "INV-A",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contractA.Id).Value;
        invoiceA.Issue(DateTime.UtcNow);

        // Invoice for Contract B
        var invoiceB = Invoice.Create(
            Guid.NewGuid(), "INV-B",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            5000m, 0, 0, 5000m,
            contractId: contractB.Id).Value;
        invoiceB.Issue(DateTime.UtcNow);

        // One payment split across two invoices (different contracts)
        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-MIXED");
        AddAllocationWithInvoice(payment, invoiceA, 6000m);  // 6,000 to Contract A
        AddAllocationWithInvoice(payment, invoiceB, 4000m);  // 4,000 to Contract B

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Act: Cancel Contract A
        var resultA = s_service.Calculate(
            contractA,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Assert: Only 6,000 counts (the allocation to Contract A's invoice), NOT 10,000
        Assert.Equal(6000m, resultA.AmountActuallyPaid);
        Assert.Equal(5220m, resultA.UsedSubscriptionAmount);
        Assert.Equal(780m, resultA.RefundAmount); // 6000 - 5220 = 780

        // Act: Cancel Contract B
        var resultB = s_service.Calculate(
            contractB,
            cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Assert: Only 4,000 counts (the allocation to Contract B's invoice)
        Assert.Equal(4000m, resultB.AmountActuallyPaid);
    }

    // ------------------------------------------------------------------
    // P0 REGRESSION TEST — Shared Payment Across Multiple Contracts
    // This is the exact bug scenario from the spec.
    //
    // Setup:
    //   Payment P = 10,000
    //   ├── Invoice A → Contract A = 6,000
    //   └── Invoice B → Contract B = 4,000
    //
    // Contract A refund: AmountActuallyPaid must be 6,000 (NOT 10,000)
    // Contract B refund: AmountActuallyPaid must be 4,000 (NOT 10,000)
    //
    // Additional assertions:
    //   - Unallocated amount (1,000 if allocations = 6,000 + 3,000) must not count
    //   - If allocation to A is reversed, A gets 0
    //   - Failed/Pending/Processing payments contribute 0
    // ------------------------------------------------------------------

    [Fact]
    public void P0_Regression_SharedPayment_CrossContract_NoContamination()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var contractA = CreateValidContract(
            contractNumber: "CNT-A-P0",
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var contractB = CreateValidContract(
            contractNumber: "CNT-B-P0",
            monthlyListPrice: 500m,
            contractedAmount: 5000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractB.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractB.Id, 6, 2610m, "EGP", 500m, 1).Value);

        var invoiceA = Invoice.Create(
            Guid.NewGuid(), "INV-A-P0",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contractA.Id).Value;
        invoiceA.Issue(DateTime.UtcNow);

        var invoiceB = Invoice.Create(
            Guid.NewGuid(), "INV-B-P0",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            5000m, 0, 0, 5000m,
            contractId: contractB.Id).Value;
        invoiceB.Issue(DateTime.UtcNow);

        // Single payment of 10,000 shared across two contracts
        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-P0-SHARED");
        AddAllocationWithInvoice(payment, invoiceA, 6000m);  // 6,000 to Contract A
        AddAllocationWithInvoice(payment, invoiceB, 4000m);  // 4,000 to Contract B

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Cancel Contract A
        var resultA = s_service.Calculate(
            contractA, cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Contract A must only see 6,000 — NOT 10,000
        Assert.Equal(6000m, resultA.AmountActuallyPaid);
        Assert.Equal(5220m, resultA.UsedSubscriptionAmount);
        Assert.Equal(780m, resultA.RefundAmount);

        // Cancel Contract B
        var resultB = s_service.Calculate(
            contractB, cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Contract B must only see 4,000 — NOT 10,000
        Assert.Equal(4000m, resultB.AmountActuallyPaid);
        Assert.Equal(2610m, resultB.UsedSubscriptionAmount);
        Assert.Equal(1390m, resultB.RefundAmount);

        // Unallocated amount: Payment = 10,000, allocated = 6,000 + 4,000 = 10,000.
        // No unallocated remainder in this case.
        // Now test with unallocated remainder:
        var paymentWithRemainder = CreateCompletedPaymentWithInvoices(10000m, "PAY-P0-UNALLOC");
        AddAllocationWithInvoice(paymentWithRemainder, invoiceA, 6000m);
        AddAllocationWithInvoice(paymentWithRemainder, invoiceB, 3000m);
        // Unallocated = 10,000 - 6,000 - 3,000 = 1,000

        var resultA2 = s_service.Calculate(
            contractA, cancellationDate,
            new[] { paymentWithRemainder },
            Array.Empty<ContractBenefit>());

        // Unallocated 1,000 must NOT count toward Contract A
        Assert.Equal(6000m, resultA2.AmountActuallyPaid);

        var resultB2 = s_service.Calculate(
            contractB, cancellationDate,
            new[] { paymentWithRemainder },
            Array.Empty<ContractBenefit>());

        // Unallocated 1,000 must NOT count toward Contract B
        Assert.Equal(3000m, resultB2.AmountActuallyPaid);
    }

    // ------------------------------------------------------------------
    // P0 REGRESSION — Reversed allocation: Contract A allocation is reversed
    // Payment = 10,000, A allocation = 6,000 (Reversed), B allocation = 4,000 (Active)
    // Contract A AmountActuallyPaid = 0
    // Contract B AmountActuallyPaid = 4,000
    // ------------------------------------------------------------------

    [Fact]
    public void P0_Regression_ReversedAllocation_AGetZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var contractA = CreateValidContract(
            contractNumber: "CNT-A-REV",
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractA.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractA.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var contractB = CreateValidContract(
            contractNumber: "CNT-B-REV",
            monthlyListPrice: 500m,
            contractedAmount: 5000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contractB.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contractB.Id, 6, 2610m, "EGP", 500m, 1).Value);

        var invoiceA = Invoice.Create(
            Guid.NewGuid(), "INV-A-REV",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contractA.Id).Value;
        invoiceA.Issue(DateTime.UtcNow);

        var invoiceB = Invoice.Create(
            Guid.NewGuid(), "INV-B-REV",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            5000m, 0, 0, 5000m,
            contractId: contractB.Id).Value;
        invoiceB.Issue(DateTime.UtcNow);

        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-REV-SHARED");
        AddAllocationWithInvoice(payment, invoiceA, 6000m, PaymentAllocationStatus.Reversed); // REVERSED
        AddAllocationWithInvoice(payment, invoiceB, 4000m);  // Active

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Contract A: reversed allocation → 0
        var resultA = s_service.Calculate(
            contractA, cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());
        Assert.Equal(0m, resultA.AmountActuallyPaid);
        Assert.Equal(0m, resultA.RefundAmount);
        Assert.Equal(5220m, resultA.CustomerOutstandingAmount);

        // Contract B: active allocation → 4,000
        var resultB = s_service.Calculate(
            contractB, cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());
        Assert.Equal(4000m, resultB.AmountActuallyPaid);
        Assert.Equal(2610m, resultB.UsedSubscriptionAmount);
        Assert.Equal(1390m, resultB.RefundAmount);
    }

    // ------------------------------------------------------------------
    // Processing payment status — must contribute 0
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_ProcessingPayment_ContributesZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        // Processing payment (NOT completed) with an allocation
        var processingPayment = Payment.Create(
            Guid.NewGuid(), "PAY-PROCESSING", 5000m, "EGP", PaymentMethod.Cash).Value;
        processingPayment.MarkProcessing();

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { processingPayment },
            Array.Empty<ContractBenefit>());

        // Processing payment must contribute 0
        Assert.Equal(0m, result.AmountActuallyPaid);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(5220m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Unallocated amount with shared payment — 1,000 remainder must not count
    // ------------------------------------------------------------------

    [Fact]
    public void P0_Regression_UnallocatedRemainder_DoesNotCount()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 1).Value);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-UNALLOC-P0",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);

        // Payment = 10,000, only 6,000 allocated to this contract's invoice
        // Unallocated = 4,000 — must NOT count
        var payment = CreateCompletedPaymentWithInvoices(10000m, "PAY-UNALLOC-P0");
        AddAllocationWithInvoice(payment, invoice, 6000m);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract, cancellationDate,
            new[] { payment },
            Array.Empty<ContractBenefit>());

        // Only 6,000 counts, not 10,000
        Assert.Equal(6000m, result.AmountActuallyPaid);
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.Equal(780m, result.RefundAmount);
    }

    // ------------------------------------------------------------------
    // Test Case: Day-based gift consumption boundary — before contract starts
    // ElapsedDays <= 0: Consumed = 0, Remaining = ContractualValue
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_BeforeContractStart_ConsumedBenefitZero()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel BEFORE contract effective date
        var cancellationDate = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Before contract: consumed = 0, remaining = 1000
        Assert.Equal(0, result.ElapsedMonths);
        Assert.Equal(0m, result.UsedSubscriptionAmount);
        Assert.Equal(1000m, result.TotalBenefitValue);
        Assert.Equal(0m, result.ConsumedBenefitValue);
        Assert.Equal(1000m, result.RemainingBenefitValue);
        Assert.Equal(10000m, result.AmountActuallyPaid);
        // Economic obligation = 0 + 1000 = 1000
        Assert.Equal(1000m, result.CustomerEconomicObligation);
        Assert.Equal(9000m, result.RefundAmount);
    }

    // ------------------------------------------------------------------
    // Test Case: Day-based gift consumption boundary — at/after contract end
    // ElapsedDays >= ContractDurationDays: Consumed = ContractualValue, Remaining = 0
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_AtContractEnd_ConsumedBenefitFull()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = effectiveAt.AddMonths(12);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 1).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        var payment = CreateCompletedPayment(10000m, 10000m, contract);

        // Cancel AT contract end date
        var cancellationDate = endsAt;

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // At contract end: consumed = 1000 (full), remaining = 0
        Assert.Equal(12, result.ElapsedMonths);
        Assert.Equal(10000m, result.UsedSubscriptionAmount);
        Assert.Equal(1000m, result.ConsumedBenefitValue);
        Assert.Equal(0m, result.RemainingBenefitValue);
        Assert.Equal(10000m, result.AmountActuallyPaid);
        // Economic obligation = 10000 + 0 = 10000
        Assert.Equal(10000m, result.CustomerEconomicObligation);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(0m, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Test Case: Negative refundable amount preserves customer outstanding
    // Paid = 4,000, Used subscription = 5,220, Remaining gift = 504.11
    // RefundableAmount = -1,724.11
    // RefundAmount = 0, CustomerOutstandingAmount = 1,724.11
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_NegativeRefundableAmount_PreservesOutstanding()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var contract = CreateValidContract(
            monthlyListPrice: 1000m,
            contractedAmount: 10000m,
            durationMonths: 12,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: effectiveAt.AddMonths(12));
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value);

        var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
        contract.AddBenefit(benefit);

        // Partial payment: only 4,000 paid
        var payment = CreateCompletedPayment(4000m, 4000m, contract);

        var cancellationDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = s_service.Calculate(
            contract,
            cancellationDate,
            new[] { payment },
            contract.Benefits);

        // Obligation > paid → negative refundable → customer owes
        Assert.Equal(5220m, result.UsedSubscriptionAmount);
        Assert.True(result.RemainingBenefitValue > 0);
        Assert.Equal(4000m, result.AmountActuallyPaid);
        Assert.True(result.RefundableAmount < 0);
        Assert.Equal(0m, result.RefundAmount);
        Assert.True(result.CustomerOutstandingAmount > 0);
        Assert.True(result.IsAmountOwed);
        Assert.False(result.IsRefundDue);

        // Verify the exact outstanding = -(RefundableAmount)
        Assert.Equal(-result.RefundableAmount, result.CustomerOutstandingAmount);
    }

    // ------------------------------------------------------------------
    // Test Case: Deterministic calculation
    // Same inputs must produce identical outputs every time.
    // ------------------------------------------------------------------

    [Fact]
    public void Refund_Calculation_IsDeterministic_SameInputsSameOutputs()
    {
        var effectiveAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        RefundCalculationResult CalculateOnce()
        {
            var contract = CreateValidContract(
                monthlyListPrice: 1000m,
                contractedAmount: 10000m,
                durationMonths: 12,
                effectiveAtUtc: effectiveAt,
                endsAtUtc: effectiveAt.AddMonths(12));
            contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
            contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
            contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);
            contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value);

            var benefit = CreateGrantedBenefit(contract.Id, "Gift", 1000m);
            contract.AddBenefit(benefit);

            var payment = CreateCompletedPayment(10000m, 10000m, contract);

            return s_service.Calculate(
                contract,
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new[] { payment },
                contract.Benefits);
        }

        var result1 = CalculateOnce();
        var result2 = CalculateOnce();

        // All monetary values must be identical
        Assert.Equal(result1.UsedSubscriptionAmount, result2.UsedSubscriptionAmount);
        Assert.Equal(result1.TotalBenefitValue, result2.TotalBenefitValue);
        Assert.Equal(result1.ConsumedBenefitValue, result2.ConsumedBenefitValue);
        Assert.Equal(result1.RemainingBenefitValue, result2.RemainingBenefitValue);
        Assert.Equal(result1.AmountActuallyPaid, result2.AmountActuallyPaid);
        Assert.Equal(result1.RefundableAmount, result2.RefundableAmount);
        Assert.Equal(result1.RefundAmount, result2.RefundAmount);
        Assert.Equal(result1.CustomerOutstandingAmount, result2.CustomerOutstandingAmount);
        Assert.Equal(result1.CustomerEconomicObligation, result2.CustomerEconomicObligation);
    }
}
