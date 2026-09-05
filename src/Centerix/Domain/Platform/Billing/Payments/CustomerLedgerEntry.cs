namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents an immutable entry in the customer ledger.
/// Each financial movement (invoice charge, payment settlement, credit creation, etc.)
/// is recorded as a separate ledger entry. The customer balance is reconstructed
/// from these entries, never stored as a single mutable field.
/// </summary>
public class CustomerLedgerEntry : AuditableEntity<Guid>
{
    /// <summary>The type of financial movement.</summary>
    public LedgerEntryType EntryType { get; private set; }

    /// <summary>
    /// The amount of the movement. Always positive — direction is determined by <see cref="EntryType"/>.
    /// </summary>
    public decimal Amount { get; private set; }

    public string CurrencyCode { get; private set; } = "EGP";

    /// <summary>
    /// The running balance after this entry is applied. Stored for audit/reconciliation,
    /// but the balance can always be reconstructed from all entries.
    /// </summary>
    public decimal RunningBalance { get; private set; }

    /// <summary>Reference to the related invoice (for InvoiceCharge entries).</summary>
    public Guid? InvoiceId { get; private set; }

    /// <summary>Reference to the related payment (for PaymentSettlement entries).</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>Reference to the related credit (for CreditCreation/CreditUsage entries).</summary>
    public Guid? CreditId { get; private set; }

    /// <summary>Human-readable description of the entry.</summary>
    public string Description { get; private set; } = default!;

    /// <summary>The date/time the entry was recorded.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    private CustomerLedgerEntry() { }

    private CustomerLedgerEntry(
        Guid id,
        LedgerEntryType entryType,
        decimal amount,
        string currencyCode,
        decimal runningBalance,
        Guid? invoiceId,
        Guid? paymentId,
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
        CreditId = creditId;
        Description = description;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>
    /// Creates a new ledger entry for an invoice charge (increases balance).
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
            description ?? $"Invoice charge: {invoiceAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a new ledger entry for a payment settlement (decreases balance).
    /// </summary>
    public static Result<CustomerLedgerEntry> CreatePaymentSettlement(
        Guid id,
        Guid paymentId,
        decimal paymentAmount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        string? description = null)
    {
        if (paymentAmount <= 0)
            return PaymentErrors.AmountMustBePositive;

        var newBalance = previousBalance - paymentAmount;

        return new CustomerLedgerEntry(
            id,
            LedgerEntryType.PaymentSettlement,
            paymentAmount,
            currencyCode,
            newBalance,
            null,
            paymentId,
            null,
            description ?? $"Payment settlement: {paymentAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a new ledger entry with a custom entry type (for future credit module).
    /// </summary>
    public static Result<CustomerLedgerEntry> Create(
        Guid id,
        LedgerEntryType entryType,
        decimal amount,
        string currencyCode,
        decimal previousBalance,
        DateTime recordedAtUtc,
        Guid? invoiceId = null,
        Guid? paymentId = null,
        Guid? creditId = null,
        string? description = null)
    {
        if (amount <= 0)
            return PaymentErrors.AmountMustBePositive;

        var newBalance = entryType switch
        {
            LedgerEntryType.InvoiceCharge => previousBalance + amount,
            LedgerEntryType.PaymentSettlement => previousBalance - amount,
            LedgerEntryType.CreditCreation => previousBalance - amount, // Credit reduces what customer owes
            LedgerEntryType.CreditUsage => previousBalance + amount,   // Using credit increases balance
            LedgerEntryType.CreditExpiry => previousBalance + amount,  // Expired credit increases balance
            LedgerEntryType.Adjustment => previousBalance + amount,    // Adjustments can be +/- (handled by caller)
            _ => previousBalance
        };

        return new CustomerLedgerEntry(
            id,
            entryType,
            amount,
            currencyCode,
            newBalance,
            invoiceId,
            paymentId,
            creditId,
            description ?? $"{entryType}: {amount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Returns true if this entry increases the customer balance.
    /// </summary>
    public bool IsDebit => EntryType switch
    {
        LedgerEntryType.InvoiceCharge => true,
        LedgerEntryType.CreditUsage => true,
        LedgerEntryType.CreditExpiry => true,
        _ => false
    };

    /// <summary>
    /// Returns true if this entry decreases the customer balance.
    /// </summary>
    public bool IsCredit => EntryType switch
    {
        LedgerEntryType.PaymentSettlement => true,
        LedgerEntryType.CreditCreation => true,
        _ => false
    };
}
