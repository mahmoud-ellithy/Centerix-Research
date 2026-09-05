namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents the allocation of a payment to a specific invoice.
/// A single payment can be allocated to multiple invoices.
/// Allocation amounts are immutable once created.
/// </summary>
public class PaymentAllocation : AuditableEntity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal AllocatedAmount { get; private set; }
    public PaymentAllocationStatus Status { get; private set; }
    public DateTime AllocatedAtUtc { get; private set; }

    public Payment Payment { get; private set; } = default!;
    public Invoice Invoice { get; private set; } = default!;

    private PaymentAllocation() { }

    private PaymentAllocation(
        Guid id,
        Guid paymentId,
        Guid invoiceId,
        decimal allocatedAmount,
        DateTime allocatedAtUtc)
        : base(id)
    {
        PaymentId = paymentId;
        InvoiceId = invoiceId;
        AllocatedAmount = allocatedAmount;
        Status = PaymentAllocationStatus.Active;
        AllocatedAtUtc = allocatedAtUtc;
    }

    /// <summary>
    /// Creates a new payment allocation.
    /// </summary>
    public static Result<PaymentAllocation> Create(
        Guid id,
        Guid paymentId,
        Guid invoiceId,
        decimal allocatedAmount,
        DateTime allocatedAtUtc)
    {
        if (allocatedAmount <= 0)
            return PaymentErrors.AllocationAmountMustBePositive;

        return new PaymentAllocation(id, paymentId, invoiceId, allocatedAmount, allocatedAtUtc);
    }

    /// <summary>
    /// Reverses this allocation (future refund module).
    /// </summary>
    public Result<Updated> Reverse()
    {
        if (Status != PaymentAllocationStatus.Active)
            return Error.Conflict("PaymentAllocation.NotActive", "Allocation is not active.");

        Status = PaymentAllocationStatus.Reversed;
        return Result.Updated;
    }

    /// <summary>
    /// Returns true if this allocation is active and counts toward invoice settlement.
    /// </summary>
    public bool IsActive => Status == PaymentAllocationStatus.Active;
}
