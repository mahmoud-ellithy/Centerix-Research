namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;

using Xunit;

/// <summary>
/// Task 18.4.2 — domain-level regression tests for the refund-after-subscription-change policy.
///
/// Approved rule: a value already converted into a SubscriptionChange credit must not be
/// refunded again as cash. <see cref="RefundCalculationService"/> therefore subtracts the
/// already-issued SubscriptionChange credit for the contract from the refundable base.
///
/// The SQL Server integration scenarios (credit then refund, partial credit + partial refund,
/// full credit consumption) live in <c>Task18_4_2FinancialPolicySqlServerTests</c>; these fast
/// tests pin the arithmetic and boundary behaviour of the calculation itself.
/// </summary>
public class Task18_4_2FinancialPolicyTests
{
    private static readonly RefundCalculationService s_service = new();

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>
    /// Creates an Active 12-month contract (12,000 = 12 × 1,000) that started four months ago,
    /// so UsedSubscriptionAmount = 1,000 × 4 = 4,000 and the unused value is 8,000.
    /// </summary>
    private static Contract CreateActiveContract()
    {
        var startedAt = DateTime.UtcNow.AddMonths(-4);
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1842",
            contractNumber: $"CTR-{Guid.NewGuid():N}"[..16],
            planId: 1,
            effectiveAtUtc: startedAt,
            endsAtUtc: startedAt.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        contract.SubmitForApproval();
        contract.Activate(startedAt);
        return contract;
    }

    /// <summary>
    /// Creates a Completed cash payment of <paramref name="amount"/> fully allocated to an
    /// issued invoice of <paramref name="contract"/> (the genuine Payment → PaymentAllocation →
    /// Invoice chain required by the contract-scoped settlement filter).
    /// </summary>
    private static Payment CreateCompletedPayment(decimal amount, Contract contract)
    {
        var payment = Payment.Create(
            id: Guid.NewGuid(),
            paymentNumber: $"PAY-{Guid.NewGuid():N}"[..16],
            amount: amount,
            currencyCode: "EGP",
            method: PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);

        var invoice = Invoice.Create(
            id: Guid.NewGuid(),
            invoiceNumber: $"INV-{Guid.NewGuid():N}"[..16],
            periodStart: DateOnly.FromDateTime(contract.EffectiveAtUtc),
            periodEnd: DateOnly.FromDateTime(contract.EndsAtUtc),
            subtotal: 12000m,
            discountAmount: 0m,
            taxAmount: 0m,
            totalAmount: 12000m,
            contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, amount, DateTime.UtcNow).Value;

        // Set the Invoice navigation so the calculation service can scope the allocation
        // to this contract, and attach the allocation to the payment's private list.
        typeof(PaymentAllocation)
            .GetProperty("Invoice", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(allocation, invoice);
        var allocations = (List<PaymentAllocation>)typeof(Payment)
            .GetField("_allocations", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(payment)!;
        allocations.Add(allocation);

        return payment;
    }

    // ==================================================================
    // Refund-after-subscription-change policy
    // ==================================================================

    /// <summary>
    /// A credit equal to the full unused paid value leaves nothing refundable as cash —
    /// the value was already converted into the SubscriptionChange credit.
    /// </summary>
    [Fact]
    public void RefundAfterSubscriptionChange_CreditedValue_NotRefundedAgainAsCash()
    {
        var contract = CreateActiveContract();
        var payment = CreateCompletedPayment(12000m, contract);

        var result = s_service.Calculate(
            contract, DateTime.UtcNow, new[] { payment }, contract.Benefits,
            alreadyIssuedSubscriptionChangeCredit: 8000m);

        Assert.Equal(12000m, result.AmountActuallyPaid);
        Assert.Equal(4000m, result.CustomerEconomicObligation);
        Assert.Equal(8000m, result.AlreadyConvertedSubscriptionChangeCredit);
        Assert.Equal(0m, result.RefundableAmount);
        Assert.Equal(0m, result.RefundAmount);
        Assert.False(result.IsRefundDue);
        Assert.Equal(0m, result.CustomerOutstandingAmount);

        // Without the credit subtraction the same value would be paid out twice as cash.
        Assert.Equal(8000m, result.AmountActuallyPaid - result.CustomerEconomicObligation);
    }

    /// <summary>
    /// Partial credit + partial refund: only the genuinely refundable remaining value
    /// (paid 10,000 − obligation 4,000 − credit 5,000 = 1,000) is returned.
    /// </summary>
    [Fact]
    public void RefundAfterSubscriptionChange_PartialCredit_ReturnsOnlyGenuinelyRefundableRemainder()
    {
        var contract = CreateActiveContract();
        var payment = CreateCompletedPayment(10000m, contract);

        var result = s_service.Calculate(
            contract, DateTime.UtcNow, new[] { payment }, contract.Benefits,
            alreadyIssuedSubscriptionChangeCredit: 5000m);

        Assert.Equal(10000m, result.AmountActuallyPaid);
        Assert.Equal(4000m, result.CustomerEconomicObligation);
        Assert.Equal(5000m, result.AlreadyConvertedSubscriptionChangeCredit);
        Assert.Equal(1000m, result.RefundableAmount);
        Assert.Equal(1000m, result.RefundAmount);
        Assert.True(result.IsRefundDue);
        Assert.Equal(0m, result.CustomerOutstandingAmount);
    }

    /// <summary>
    /// When the customer consumed more value than they paid, the outstanding amount is
    /// computed from paid − obligation only: the already-converted credit is not customer
    /// debt and must not inflate the outstanding balance.
    /// </summary>
    [Fact]
    public void RefundAfterSubscriptionChange_UnpaidConsumedValue_OutstandingUnaffectedByCredit()
    {
        var contract = CreateActiveContract();
        var payment = CreateCompletedPayment(3000m, contract);

        var result = s_service.Calculate(
            contract, DateTime.UtcNow, new[] { payment }, contract.Benefits,
            alreadyIssuedSubscriptionChangeCredit: 3000m);

        Assert.Equal(0m, result.RefundAmount);
        Assert.False(result.IsRefundDue);

        // 4,000 consumed − 3,000 paid = 1,000 owed; the credit neither creates nor hides debt.
        Assert.Equal(1000m, result.CustomerOutstandingAmount);
        Assert.True(result.IsAmountOwed);
    }

    /// <summary>
    /// Regression guard: with no plan-change credit (the default), the calculation is
    /// byte-for-byte the pre-18.4.2 behaviour.
    /// </summary>
    [Fact]
    public void RefundCalculation_WithoutIssuedCredit_BehaviorUnchanged()
    {
        var contract = CreateActiveContract();
        var payment = CreateCompletedPayment(10000m, contract);

        var result = s_service.Calculate(
            contract, DateTime.UtcNow, new[] { payment }, contract.Benefits);

        Assert.Equal(0m, result.AlreadyConvertedSubscriptionChangeCredit);
        Assert.Equal(6000m, result.AmountActuallyPaid - result.CustomerEconomicObligation);
        Assert.Equal(6000m, result.RefundableAmount);
        Assert.Equal(6000m, result.RefundAmount);
        Assert.Equal(Math.Max(0m, result.RefundableAmount), result.RefundAmount);
        Assert.Equal(Math.Max(0m, -result.RefundableAmount), result.CustomerOutstandingAmount);
    }
}
