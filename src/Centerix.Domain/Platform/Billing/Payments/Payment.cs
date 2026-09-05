namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents a customer payment transaction. A payment is an independent financial event
/// that may settle one or more invoices through <see cref="PaymentAllocation"/> records.
/// Once completed, a payment is immutable — corrections are separate future transactions.
/// </summary>
public class Payment : AuditableEntity<Guid>
{
    public string PaymentNumber { get; private set; } = default!;
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = "EGP";
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? ExternalReference { get; private set; }
    public string? Notes { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private readonly List<PaymentAllocation> _allocations = [];
    public IReadOnlyList<PaymentAllocation> Allocations => _allocations.AsReadOnly();

    private readonly List<PaymentReceipt> _receipts = [];
    public IReadOnlyList<PaymentReceipt> Receipts => _receipts.AsReadOnly();

    private Payment() { }

    private Payment(
        Guid id,
        string paymentNumber,
        decimal amount,
        string currencyCode,
        PaymentMethod method,
        string? externalReference,
        string? notes)
        : base(id)
    {
        PaymentNumber = paymentNumber;
        Amount = amount;
        CurrencyCode = currencyCode;
        Method = method;
        Status = PaymentStatus.Pending;
        ExternalReference = externalReference;
        Notes = notes;
    }

    /// <summary>
    /// Creates a new payment in Pending status.
    /// </summary>
    public static Result<Payment> Create(
        Guid id,
        string paymentNumber,
        decimal amount,
        string currencyCode,
        PaymentMethod method,
        string? externalReference = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(paymentNumber))
            return PaymentErrors.ReceiptNumberRequired;

        if (amount <= 0)
            return PaymentErrors.AmountMustBePositive;

        if (!Enum.IsDefined(method))
            return PaymentErrors.MethodRequired;

        return new Payment(id, paymentNumber, amount, currencyCode, method, externalReference, notes);
    }

    /// <summary>
    /// Marks the payment as completed. Once completed, the payment is immutable.
    /// </summary>
    public Result<Updated> Complete(DateTime completedAtUtc, string? externalReference = null)
    {
        if (Status != PaymentStatus.Pending && Status != PaymentStatus.Processing)
            return PaymentErrors.CannotCompleteWrongStatus;

        Status = PaymentStatus.Completed;
        CompletedAtUtc = completedAtUtc;

        // Only set external reference if not already provided
        if (!string.IsNullOrWhiteSpace(externalReference) && string.IsNullOrWhiteSpace(ExternalReference))
        {
            ExternalReference = externalReference;
        }

        return Result.Updated;
    }

    /// <summary>
    /// Marks the payment as processing (submitted to provider).
    /// </summary>
    public Result<Updated> MarkProcessing()
    {
        if (Status != PaymentStatus.Pending)
            return PaymentErrors.CannotCompleteWrongStatus;

        Status = PaymentStatus.Processing;
        return Result.Updated;
    }

    /// <summary>
    /// Marks the payment as failed. Failed payments do NOT count toward settlement.
    /// </summary>
    public Result<Updated> MarkFailed()
    {
        if (Status != PaymentStatus.Pending && Status != PaymentStatus.Processing)
            return PaymentErrors.CannotFailWrongStatus;

        Status = PaymentStatus.Failed;
        return Result.Updated;
    }

    /// <summary>
    /// Gets the total amount allocated to invoices.
    /// </summary>
    public decimal GetAllocatedAmount()
    {
        return _allocations
            .Where(a => a.Status == PaymentAllocationStatus.Active)
            .Sum(a => a.AllocatedAmount);
    }

    /// <summary>
    /// Gets the unallocated amount (excess that could become customer credit).
    /// </summary>
    public decimal GetUnallocatedAmount()
    {
        return Amount - GetAllocatedAmount();
    }

    /// <summary>
    /// Returns true if the payment is completed and immutable.
    /// </summary>
    public bool IsCompleted => Status == PaymentStatus.Completed;

    /// <summary>
    /// Returns true if the payment counts toward settlement.
    /// </summary>
    public bool CountsTowardSettlement => Status == PaymentStatus.Completed;
}
