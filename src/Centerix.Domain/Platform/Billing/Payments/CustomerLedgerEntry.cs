namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents an immutable customer ledger entry. Each entry is a financial movement
/// (invoice charge, payment settlement, credit creation/usage/expiry, adjustment)
/// that contributes to the customer's running balance. Entries are NEVER modified
/// or deleted — corrections are separate offsetting entries.
///
/// <b>Financial invariants:</b>
/// <list type="bullet">
///   <item><description><see cref="RunningBalance"/> is a derived/denormalized value whose correctness
///   is guaranteed by the transactional write process, not an independent source of truth. The ledger
///   movements (<see cref="Amount"/>/<see cref="EntryType"/>) are sufficient to reconstruct the balance.</description></item>
///   <item><description>A <see cref="LedgerEntryType.PaymentSettlement"/> entry is tied to exactly one
///   <see cref="PaymentAllocationId"/>; this allows the system to guarantee that total payment settlement
///   equals total active allocations and prevents duplicate settlement entries.</description></item>
/// </list>
/// </summary>
public class CustomerLedgerEntry : AuditableEntity<Guid>
{
    public LedgerEntryType EntryType { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = "EGP";

    /// <summary>
    /// Derived/denormalized running balance at the moment this entry was recorded.
    /// Reconstruable from the immutable ledger movements; correctness guaranteed by the
    /// transactional write process.
    /// </summary>
    public decimal RunningBalance { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public Guid? PaymentId { get; private set; }

    /// <summary>
    /// The payment allocation that caused this settlement. Only populated for
    /// <see cref="LedgerEntryType.PaymentSettlement"/> entries and used to guarantee
    /// one active allocation = one settlement.
    /// </summary>
    public Guid? PaymentAllocationId { get; private set; }
    public Guid? RefundId { get; private set; }
    public Guid? CreditId { get; private set; }
    public string Description { get; private set; } = default!;
    public DateTime RecordedAtUtc { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private CustomerLedgerEntry() { }

    private CustomerLedgerEntry(
        Guid id,
        LedgerEntryType entryType,
        decimal amount,
        string currencyCode,
        decimal runningBalance,
        Guid? invoiceId,
        Guid? paymentId,
        Guid? paymentAllocationId,
        Guid? refundId,
        Guid? creditId,
        string description,
        DateTime recordedAtUtc)
        : base(id)
    {
        EntryType = entryType;
        Amount = amount;
        CurrencyCode = currencyCode;
        RunningBalance = runningBalance;
        InvoiceId = invoiceId;
        PaymentId = paymentId;
        PaymentAllocationId = paymentAllocationId;
        RefundId = refundId;
        CreditId = creditId;
        Description = description;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>
    /// Creates a ledger entry for an invoice charge (increases customer balance).
    /// </summary>
    public static Result<CustomerLedgerEntry> CreateInvoiceCharge(
        Guid id,
        Guid invoiceId,
        decimal invoiceAmount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        string? description = null)
    {
        if (invoiceAmount <= 0)
            return PaymentErrors.AmountMustBePositive;

        var newBalance = previousBalance + invoiceAmount;

        return new CustomerLedgerEntry(
            id,
            LedgerEntryType.InvoiceCharge,
            invoiceAmount,
            currencyCode,
            newBalance,
            invoiceId,
            null,
            null,
            null,
            null,
            description ?? $"Invoice charge: {invoiceAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a ledger entry for a payment settlement (decreases customer balance).
    /// The settlement is bound to the specific <paramref name="paymentAllocationId"/> to guarantee
    /// that settlement never exceeds the actual applied allocation and cannot be duplicated.
    /// </summary>
    public static Result<CustomerLedgerEntry> CreatePaymentSettlement(
        Guid id,
        Guid paymentId,
        Guid paymentAllocationId,
        decimal allocatedAmount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        string? description = null)
    {
        if (allocatedAmount <= 0)
            return PaymentErrors.AmountMustBePositive;

        var newBalance = previousBalance - allocatedAmount;

        return new CustomerLedgerEntry(
            id,
            LedgerEntryType.PaymentSettlement,
            allocatedAmount,
            currencyCode,
            newBalance,
            null,
            paymentId,
            paymentAllocationId,
            null,
            null,
            description ?? $"Payment settlement: {allocatedAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a ledger entry for credit creation (future credit module).
    /// </summary>
    public static Result<CustomerLedgerEntry> CreateCreditCreation(
        Guid id,
        Guid creditId,
        decimal creditAmount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        string? description = null)
    {
        if (creditAmount <= 0)
            return PaymentErrors.AmountMustBePositive;

        var newBalance = previousBalance - creditAmount;

        return new CustomerLedgerEntry(
            id,
            LedgerEntryType.CreditCreation,
            creditAmount,
            currencyCode,
            newBalance,
            null,
            null,
            null,
            null,
            creditId,
            description ?? $"Credit creation: {creditAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a ledger entry for a refund settlement (money returned to customer).
    /// Refund settlement decreases customer balance (credit movement).
    /// </summary>
    public static Result<CustomerLedgerEntry> CreateRefundSettlement(
        Guid id,
        Guid refundId,
        decimal refundAmount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        string? description = null)
    {
        if (refundAmount <= 0)
            return PaymentErrors.AmountMustBePositive;

        // Refund settlement decreases customer balance (money returned to customer)
        // This is a credit movement
        var newBalance = previousBalance - refundAmount;

        return new CustomerLedgerEntry(
            id,
            LedgerEntryType.RefundSettlement,
            refundAmount,
            currencyCode,
            newBalance,
            null,
            null,
            null,
            refundId,
            null,
            description ?? $"Refund settlement: {refundAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Returns true if this entry increases the customer balance (debit).
    /// </summary>
    public bool IsDebit => EntryType switch
    {
        LedgerEntryType.InvoiceCharge => true,
        _ => false
    };

    /// <summary>
    /// Returns true if this entry decreases the customer balance (credit).
    /// </summary>
    public bool IsCredit => EntryType switch
    {
        LedgerEntryType.PaymentSettlement => true,
        LedgerEntryType.CreditCreation => true,
        LedgerEntryType.RefundSettlement => true,
        _ => false
    };
}
