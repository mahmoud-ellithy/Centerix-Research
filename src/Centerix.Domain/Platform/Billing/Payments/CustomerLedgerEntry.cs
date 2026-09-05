namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents an immutable customer ledger entry. Each entry is a financial movement
/// (invoice charge, payment settlement, credit creation/usage/expiry, adjustment)
/// that contributes to the customer's running balance. Entries are NEVER modified
/// or deleted — corrections are separate offsetting entries.
/// </summary>
public class CustomerLedgerEntry : AuditableEntity<Guid>
{
    public LedgerEntryType EntryType { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = "EGP";
    public decimal RunningBalance { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public Guid? PaymentId { get; private set; }
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
            description ?? $"Invoice charge: {invoiceAmount} {currencyCode}",
            recordedAtUtc);
    }

    /// <summary>
    /// Creates a ledger entry for a payment settlement (decreases customer balance).
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
            creditId,
            description ?? $"Credit creation: {creditAmount} {currencyCode}",
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
        _ => false
    };
}
