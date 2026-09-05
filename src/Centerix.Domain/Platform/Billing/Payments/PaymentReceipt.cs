namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents a payment receipt issued for a completed payment.
/// Receipt numbers are unique and immutable after issuance.
/// A receipt is issued per payment (not per allocation), representing the single payment transaction.
/// </summary>
public class PaymentReceipt : AuditableEntity<Guid>
{
    public string ReceiptNumber { get; private set; } = default!;
    public Guid PaymentId { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = "EGP";
    public PaymentMethod Method { get; private set; }
    public string? ExternalReference { get; private set; }
    public ReceiptStatus Status { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    public Payment Payment { get; private set; } = default!;

    private PaymentReceipt() { }

    private PaymentReceipt(
        Guid id,
        string receiptNumber,
        Guid paymentId,
        decimal amount,
        string currencyCode,
        PaymentMethod method,
        string? externalReference,
        DateTime issuedAtUtc)
        : base(id)
    {
        ReceiptNumber = receiptNumber;
        PaymentId = paymentId;
        Amount = amount;
        CurrencyCode = currencyCode;
        Method = method;
        ExternalReference = externalReference;
        Status = ReceiptStatus.Issued;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>
    /// Issues a new receipt for a completed payment.
    /// </summary>
    public static Result<PaymentReceipt> Issue(
        Guid id,
        string receiptNumber,
        Guid paymentId,
        decimal amount,
        string currencyCode,
        PaymentMethod method,
        string? externalReference,
        DateTime issuedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(receiptNumber))
            return PaymentErrors.ReceiptNumberRequired;

        if (amount <= 0)
            return PaymentErrors.AmountMustBePositive;

        return new PaymentReceipt(id, receiptNumber, paymentId, amount, currencyCode, method, externalReference, issuedAtUtc);
    }

    /// <summary>
    /// Cancels this receipt (future, if underlying payment is found invalid).
    /// </summary>
    public Result<Updated> Cancel()
    {
        if (Status != ReceiptStatus.Issued)
            return Error.Conflict("Receipt.NotIssued", "Receipt is not in Issued status.");

        Status = ReceiptStatus.Cancelled;
        return Result.Updated;
    }

    /// <summary>
    /// Returns true if this receipt is valid (issued and not cancelled).
    /// </summary>
    public bool IsValid => Status == ReceiptStatus.Issued;
}
