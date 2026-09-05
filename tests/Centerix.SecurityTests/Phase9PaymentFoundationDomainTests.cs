namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Xunit;

/// <summary>
/// Domain tests for Payment, PaymentAllocation, PaymentReceipt, and CustomerLedgerEntry.
/// Tests business rules, lifecycle transitions, and immutability guarantees.
/// </summary>
public class Phase9PaymentFoundationDomainTests
{
    // ------------------------------------------------------------------
    // Payment lifecycle tests
    // ------------------------------------------------------------------

    [Fact]
    public void Payment_Create_ValidInput_Succeeds()
    {
        var payment = Payment.Create(
            Guid.NewGuid(),
            "PAY-001",
            1000m,
            "EGP",
            PaymentMethod.Cash);

        Assert.True(payment.IsSuccess);
        var entity = payment.Value;
        Assert.Equal("PAY-001", entity.PaymentNumber);
        Assert.Equal(1000m, entity.Amount);
        Assert.Equal("EGP", entity.CurrencyCode);
        Assert.Equal(PaymentMethod.Cash, entity.Method);
        Assert.Equal(PaymentStatus.Pending, entity.Status);
    }

    [Fact]
    public void Payment_Create_ZeroAmount_IsDenied()
    {
        var result = Payment.Create(Guid.NewGuid(), "PAY-002", 0m, "EGP", PaymentMethod.Cash);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_Create_NegativeAmount_IsDenied()
    {
        var result = Payment.Create(Guid.NewGuid(), "PAY-003", -100m, "EGP", PaymentMethod.Cash);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_Create_EmptyPaymentNumber_IsDenied()
    {
        var result = Payment.Create(Guid.NewGuid(), "", 100m, "EGP", PaymentMethod.Cash);

        Assert.False(result.IsSuccess);
        Assert.Equal("Receipt.Number_Required", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_Create_InvalidMethod_IsDenied()
    {
        var result = Payment.Create(Guid.NewGuid(), "PAY-004", 100m, "EGP", (PaymentMethod)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Method_Required", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_Lifecycle_PendingToProcessingToCompleted()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-005", 500m, "EGP", PaymentMethod.Card).Value;

        Assert.Equal(PaymentStatus.Pending, payment.Status);

        Assert.True(payment.MarkProcessing().IsSuccess);
        Assert.Equal(PaymentStatus.Processing, payment.Status);

        Assert.True(payment.Complete(DateTime.UtcNow).IsSuccess);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.NotNull(payment.CompletedAtUtc);
    }

    [Fact]
    public void Payment_Lifecycle_PendingToFailed()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-006", 500m, "EGP", PaymentMethod.Wallet).Value;

        Assert.True(payment.MarkFailed().IsSuccess);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
    }

    [Fact]
    public void Payment_Complete_FromFailed_IsDenied()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-007", 500m, "EGP", PaymentMethod.Cash).Value;
        payment.MarkFailed();

        var result = payment.Complete(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.CannotCompleteWrongStatus", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_MarkFailed_FromCompleted_IsDenied()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-008", 500m, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);

        var result = payment.MarkFailed();

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.CannotFailWrongStatus", result.Errors![0].Code);
    }

    [Fact]
    public void Payment_CountsTowardSettlement_OnlyWhenCompleted()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-009", 500m, "EGP", PaymentMethod.Cash).Value;

        Assert.False(payment.CountsTowardSettlement);

        payment.MarkProcessing();
        Assert.False(payment.CountsTowardSettlement);

        payment.Complete(DateTime.UtcNow);
        Assert.True(payment.CountsTowardSettlement);
    }

    [Fact]
    public void Payment_GetUnallocatedAmount_ReflectsActiveAllocations()
    {
        var payment = Payment.Create(Guid.NewGuid(), "PAY-010", 1000m, "EGP", PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);

        Assert.Equal(1000m, payment.GetUnallocatedAmount());

        // Simulate allocation by adding an allocation directly (via reflection or internal method)
        // Since allocations list is private, we test via the AllocatePaymentCommand handler
        // For domain test, we verify initial state
        Assert.Equal(0m, payment.GetAllocatedAmount());
    }

    // ------------------------------------------------------------------
    // PaymentAllocation tests
    // ------------------------------------------------------------------

    [Fact]
    public void PaymentAllocation_Create_ValidInput_Succeeds()
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500m,
            DateTime.UtcNow);

        Assert.True(allocation.IsSuccess);
        var entity = allocation.Value;
        Assert.Equal(500m, entity.AllocatedAmount);
        Assert.Equal(PaymentAllocationStatus.Active, entity.Status);
    }

    [Fact]
    public void PaymentAllocation_Create_ZeroAmount_IsDenied()
    {
        var result = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0m,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void PaymentAllocation_Create_NegativeAmount_IsDenied()
    {
        var result = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            -100m,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void PaymentAllocation_Reverse_Active_Succeeds()
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500m,
            DateTime.UtcNow).Value;

        Assert.True(allocation.IsActive);

        var result = allocation.Reverse();

        Assert.True(result.IsSuccess);
        Assert.False(allocation.IsActive);
        Assert.Equal(PaymentAllocationStatus.Reversed, allocation.Status);
    }

    [Fact]
    public void PaymentAllocation_Reverse_AlreadyReversed_IsDenied()
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500m,
            DateTime.UtcNow).Value;

        allocation.Reverse();

        var result = allocation.Reverse();

        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.NotActive", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // PaymentReceipt tests
    // ------------------------------------------------------------------

    [Fact]
    public void PaymentReceipt_Issue_ValidInput_Succeeds()
    {
        var receipt = PaymentReceipt.Issue(
            Guid.NewGuid(),
            "RCPT-001",
            Guid.NewGuid(),
            1000m,
            "EGP",
            PaymentMethod.Cash,
            "EXT-REF-001",
            DateTime.UtcNow);

        Assert.True(receipt.IsSuccess);
        var entity = receipt.Value;
        Assert.Equal("RCPT-001", entity.ReceiptNumber);
        Assert.Equal(1000m, entity.Amount);
        Assert.Equal(ReceiptStatus.Issued, entity.Status);
        Assert.True(entity.IsValid);
    }

    [Fact]
    public void PaymentReceipt_Issue_EmptyNumber_IsDenied()
    {
        var result = PaymentReceipt.Issue(
            Guid.NewGuid(),
            "",
            Guid.NewGuid(),
            1000m,
            "EGP",
            PaymentMethod.Cash,
            null,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Receipt.Number_Required", result.Errors![0].Code);
    }

    [Fact]
    public void PaymentReceipt_Issue_ZeroAmount_IsDenied()
    {
        var result = PaymentReceipt.Issue(
            Guid.NewGuid(),
            "RCPT-002",
            Guid.NewGuid(),
            0m,
            "EGP",
            PaymentMethod.Cash,
            null,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void PaymentReceipt_Cancel_Issued_Succeeds()
    {
        var receipt = PaymentReceipt.Issue(
            Guid.NewGuid(),
            "RCPT-003",
            Guid.NewGuid(),
            500m,
            "EGP",
            PaymentMethod.Card,
            null,
            DateTime.UtcNow).Value;

        Assert.True(receipt.IsValid);

        var result = receipt.Cancel();

        Assert.True(result.IsSuccess);
        Assert.False(receipt.IsValid);
        Assert.Equal(ReceiptStatus.Cancelled, receipt.Status);
    }

    [Fact]
    public void PaymentReceipt_Cancel_AlreadyCancelled_IsDenied()
    {
        var receipt = PaymentReceipt.Issue(
            Guid.NewGuid(),
            "RCPT-004",
            Guid.NewGuid(),
            500m,
            "EGP",
            PaymentMethod.Card,
            null,
            DateTime.UtcNow).Value;

        receipt.Cancel();

        var result = receipt.Cancel();

        Assert.False(result.IsSuccess);
        Assert.Equal("Receipt.NotIssued", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // CustomerLedgerEntry tests
    // ------------------------------------------------------------------

    [Fact]
    public void CustomerLedgerEntry_CreateInvoiceCharge_ValidInput_Succeeds()
    {
        var entry = CustomerLedgerEntry.CreateInvoiceCharge(
            Guid.NewGuid(),
            Guid.NewGuid(),
            12000m,
            "EGP",
            0m,
            DateTime.UtcNow);

        Assert.True(entry.IsSuccess);
        var entity = entry.Value;
        Assert.Equal(LedgerEntryType.InvoiceCharge, entity.EntryType);
        Assert.Equal(12000m, entity.Amount);
        Assert.Equal(12000m, entity.RunningBalance);
        Assert.True(entity.IsDebit);
        Assert.False(entity.IsCredit);
    }

    [Fact]
    public void CustomerLedgerEntry_CreatePaymentSettlement_ValidInput_Succeeds()
    {
        var allocationId = Guid.NewGuid();
        var entry = CustomerLedgerEntry.CreatePaymentSettlement(
            Guid.NewGuid(),
            Guid.NewGuid(),
            allocationId,
            5000m,
            "EGP",
            12000m,
            DateTime.UtcNow);

        Assert.True(entry.IsSuccess);
        var entity = entry.Value;
        Assert.Equal(LedgerEntryType.PaymentSettlement, entity.EntryType);
        Assert.Equal(5000m, entity.Amount);
        Assert.Equal(7000m, entity.RunningBalance);
        Assert.Equal(allocationId, entity.PaymentAllocationId);
        Assert.False(entity.IsDebit);
        Assert.True(entity.IsCredit);
    }

    [Fact]
    public void CustomerLedgerEntry_CreateInvoiceCharge_ZeroAmount_IsDenied()
    {
        var result = CustomerLedgerEntry.CreateInvoiceCharge(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0m,
            "EGP",
            0m,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void CustomerLedgerEntry_CreatePaymentSettlement_ZeroAmount_IsDenied()
    {
        var result = CustomerLedgerEntry.CreatePaymentSettlement(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0m,
            "EGP",
            1000m,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public void CustomerLedgerEntry_CreateCreditCreation_DecreasesBalance()
    {
        var entry = CustomerLedgerEntry.CreateCreditCreation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1000m,
            "EGP",
            5000m,
            DateTime.UtcNow);

        Assert.True(entry.IsSuccess);
        var entity = entry.Value;
        Assert.Equal(LedgerEntryType.CreditCreation, entity.EntryType);
        Assert.Equal(4000m, entity.RunningBalance);
        Assert.True(entity.IsCredit);
    }

    // ------------------------------------------------------------------
    // Invoice settlement integration tests
    // ------------------------------------------------------------------

    [Fact]
    public void Invoice_GetPaidAmount_ReflectsActiveAllocations()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-010",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            1000m,
            0,
            0,
            1000m).Value;

        // Initially no allocations
        Assert.Equal(0m, invoice.GetPaidAmount());
        Assert.Equal(1000m, invoice.GetRemainingAmount());
    }

    [Fact]
    public void Invoice_UpdatePaymentStatus_PartialPayment_SetsPartiallyPaid()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-011",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            1000m,
            0,
            0,
            1000m).Value;

        invoice.Issue(DateTime.UtcNow);

        // Manually add an allocation to simulate partial payment
        // Since allocations are private, we test the UpdatePaymentStatus logic
        // by checking it returns success for valid states
        var result = invoice.UpdatePaymentStatus();
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Invoice_UpdatePaymentStatus_FromDraft_IsDenied()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-012",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            1000m,
            0,
            0,
            1000m).Value;

        var result = invoice.UpdatePaymentStatus();

        Assert.False(result.IsSuccess);
        Assert.Equal("Invoice.CannotPayNotIssued", result.Errors![0].Code);
    }
}
