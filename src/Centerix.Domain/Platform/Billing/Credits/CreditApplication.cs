namespace Centerix.Domain.Platform.Billing.Credits;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Represents an immutable record of a credit being applied to an invoice.
/// Each CreditApplication is a financial movement that records:
/// - Which credit was consumed
/// - Which invoice was settled
/// - How much was applied
/// - When it occurred
///
/// A single credit can produce multiple CreditApplication records when partial
/// consumption is supported. These records are NEVER modified or deleted.
/// </summary>
public class CreditApplication : AuditableEntity<Guid>
{
    public Guid CreditId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private CreditApplication() { }

    private CreditApplication(
        Guid id,
        Guid creditId,
        Guid invoiceId,
        decimal amount,
        DateTime appliedAtUtc)
        : base(id)
    {
        CreditId = creditId;
        InvoiceId = invoiceId;
        Amount = amount;
        AppliedAtUtc = appliedAtUtc;
    }

    /// <summary>
    /// Creates a new credit application record.
    /// </summary>
    public static Result<CreditApplication> Create(
        Guid id,
        Guid creditId,
        Guid invoiceId,
        decimal amount,
        DateTime appliedAtUtc)
    {
        if (amount <= 0)
            return Error.Validation("CreditApplication.AmountMustBePositive", "Credit application amount must be greater than zero.");

        return new CreditApplication(id, creditId, invoiceId, amount, appliedAtUtc);
    }
}
