namespace Centerix.Domain.Platform.Billing.Refunds;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Refunds.Enums;

/// <summary>
/// Represents a refund transaction. A refund is an independent financial event
/// that returns money to a customer, typically due to early cancellation.
/// Once executed, a refund is immutable — corrections are separate future transactions.
///
/// The original Payment remains immutable. The Refund is a separate financial movement.
/// </summary>
/// <remarks>
/// Lifecycle: Pending → Approved → Processing → Completed
///            Pending → Rejected
///            Pending → Cancelled
///            Processing → Failed
///
/// Financial invariant: A refund can only be executed once. The execution creates
/// an immutable ledger entry and cannot be reversed by mutating the refund.
/// </remarks>
public class Refund : AuditableEntity<Guid>
{
    /// <summary>Business-facing refund number/reference (unique per tenant).</summary>
    public string RefundNumber { get; private set; } = default!;

    /// <summary>Reference to the Contract this refund is associated with.</summary>
    public Guid ContractId { get; private set; }

    /// <summary>Reference to the Subscription being cancelled (if applicable).</summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>Reference to the Invoice this refund is associated with (if applicable).</summary>
    public Guid? InvoiceId { get; private set; }

    /// <summary>Amount to be refunded to the customer.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Currency code for monetary values.</summary>
    public string CurrencyCode { get; private set; } = "EGP";

    /// <summary>Current status of the refund.</summary>
    public RefundStatus Status { get; private set; }

    /// <summary>Reason for the refund (e.g., Early cancellation, Overpayment).</summary>
    public string Reason { get; private set; } = default!;

    /// <summary>UTC timestamp when the refund was requested.</summary>
    public DateTime RequestedAtUtc { get; private set; }

    /// <summary>UTC timestamp when the refund was approved.</summary>
    public DateTime? ApprovedAtUtc { get; private set; }

    /// <summary>UTC timestamp when the refund was executed.</summary>
    public DateTime? ExecutedAtUtc { get; private set; }

    /// <summary>ID of the user who created the refund request.</summary>
    public new string CreatedBy { get; private set; } = default!;

    /// <summary>ID of the user who approved the refund.</summary>
    public string? ApprovedBy { get; private set; }

    /// <summary>ID of the user who executed the refund.</summary>
    public string? ExecutedBy { get; private set; }

    /// <summary>Client-supplied idempotency key for the execution request (unique per tenant).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Indicates whether this refund has been executed (immutable once true).</summary>
    public bool IsExecuted => Status == RefundStatus.Completed;

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private Refund() { }

    private Refund(
        Guid id,
        string refundNumber,
        Guid contractId,
        Guid? subscriptionId,
        Guid? invoiceId,
        decimal amount,
        string currencyCode,
        string reason,
        string createdBy,
        DateTime requestedAtUtc,
        string? idempotencyKey)
        : base(id)
    {
        RefundNumber = refundNumber;
        ContractId = contractId;
        SubscriptionId = subscriptionId;
        InvoiceId = invoiceId;
        Amount = amount;
        CurrencyCode = currencyCode;
        Status = RefundStatus.Pending;
        Reason = reason;
        CreatedBy = createdBy;
        RequestedAtUtc = requestedAtUtc;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>
    /// Creates a new refund request in Pending status.
    /// </summary>
    public static Result<Refund> Create(
        Guid id,
        string refundNumber,
        Guid contractId,
        Guid? subscriptionId,
        Guid? invoiceId,
        decimal amount,
        string currencyCode,
        string reason,
        string createdBy,
        DateTime requestedAtUtc,
        string? idempotencyKey = null)
    {
        if (id == Guid.Empty)
            return RefundErrors.IdRequired;

        if (string.IsNullOrWhiteSpace(refundNumber))
            return RefundErrors.RefundNumberRequired;

        if (contractId == Guid.Empty)
            return RefundErrors.ContractIdRequired;

        if (amount <= 0)
            return RefundErrors.AmountMustBePositive;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return RefundErrors.InvalidCurrency;

        if (string.IsNullOrWhiteSpace(reason))
            return RefundErrors.ReasonRequired;

        if (string.IsNullOrWhiteSpace(createdBy))
            return RefundErrors.CreatedByRequired;

        return new Refund(
            id,
            refundNumber.Trim(),
            contractId,
            subscriptionId,
            invoiceId,
            amount,
            currencyCode.Trim().ToUpperInvariant(),
            reason.Trim(),
            createdBy,
            requestedAtUtc,
            idempotencyKey);
    }

    /// <summary>
    /// Approves the refund request.
    /// </summary>
    public Result<Updated> Approve(string approvedBy, DateTime approvedAtUtc)
    {
        if (Status != RefundStatus.Pending)
            return RefundErrors.InvalidStateTransition(Status, "approve");

        if (string.IsNullOrWhiteSpace(approvedBy))
            return RefundErrors.ApprovedByRequired;

        Status = RefundStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAtUtc = approvedAtUtc;

        return Result.Updated;
    }

    /// <summary>
    /// Rejects the refund request.
    /// </summary>
    public Result<Updated> Reject(string rejectedBy, DateTime rejectedAtUtc)
    {
        if (Status != RefundStatus.Pending)
            return RefundErrors.InvalidStateTransition(Status, "reject");

        Status = RefundStatus.Rejected;
        ApprovedBy = rejectedBy;
        ApprovedAtUtc = rejectedAtUtc;

        return Result.Updated;
    }

    /// <summary>
    /// Marks the refund as processing (execution started).
    /// Allows Pending → Processing for optional approval workflow.
    /// </summary>
    public Result<Updated> MarkProcessing()
    {
        if (Status != RefundStatus.Pending && Status != RefundStatus.Approved)
            return RefundErrors.InvalidStateTransition(Status, "start processing");

        Status = RefundStatus.Processing;
        return Result.Updated;
    }

    /// <summary>
    /// Executes the refund and marks it as completed.
    /// Allows Pending → Completed directly (optional approval workflow) per Task #4.
    /// </summary>
    public Result<Updated> Execute(string executedBy, DateTime executedAtUtc, string? idempotencyKey = null)
    {
        if (Status != RefundStatus.Pending && Status != RefundStatus.Approved && Status != RefundStatus.Processing)
            return RefundErrors.InvalidStateTransition(Status, "execute");

        if (string.IsNullOrWhiteSpace(executedBy))
            return RefundErrors.ExecutedByRequired;

        Status = RefundStatus.Completed;
        ExecutedBy = executedBy;
        ExecutedAtUtc = executedAtUtc;

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            IdempotencyKey = idempotencyKey;
        }

        return Result.Updated;
    }

    /// <summary>
    /// Marks the refund as failed during execution.
    /// </summary>
    public Result<Updated> MarkFailed()
    {
        if (Status != RefundStatus.Processing)
            return RefundErrors.InvalidStateTransition(Status, "mark as failed");

        Status = RefundStatus.Failed;
        return Result.Updated;
    }

    /// <summary>
    /// Cancels the refund request before execution.
    /// </summary>
    public Result<Updated> Cancel(string cancelledBy, DateTime cancelledAtUtc)
    {
        if (Status != RefundStatus.Pending && Status != RefundStatus.Approved)
            return RefundErrors.InvalidStateTransition(Status, "cancel");

        Status = RefundStatus.Cancelled;
        ApprovedBy = cancelledBy;
        ApprovedAtUtc = cancelledAtUtc;

        return Result.Updated;
    }
}
