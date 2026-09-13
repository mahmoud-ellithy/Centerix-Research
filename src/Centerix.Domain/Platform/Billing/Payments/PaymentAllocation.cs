namespace Centerix.Domain.Platform.Billing.Payments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Represents the allocation of a payment to a specific invoice, and optionally
/// to a specific installment (payment obligation). A single payment can be
/// allocated to multiple invoices and/or installments.
/// Allocation amounts are immutable once created.
/// </summary>
public class PaymentAllocation : AuditableEntity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid? InstallmentId { get; private set; }
    public decimal AllocatedAmount { get; private set; }
    public PaymentAllocationStatus Status { get; private set; }
    public DateTime AllocatedAtUtc { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    public Payment Payment { get; private set; } = default!;
    public Invoice Invoice { get; private set; } = default!;
    public Installment? Installment { get; private set; }

    private PaymentAllocation() { }

    private PaymentAllocation(
        Guid id,
        Guid paymentId,
        Guid invoiceId,
        decimal allocatedAmount,
        DateTime allocatedAtUtc,
        Guid? installmentId = null)
        : base(id)
    {
        PaymentId = paymentId;
        InvoiceId = invoiceId;
        InstallmentId = installmentId;
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
        DateTime allocatedAtUtc,
        Guid? installmentId = null)
    {
        if (allocatedAmount <= 0)
            return PaymentErrors.AllocationAmountMustBePositive;

        return new PaymentAllocation(id, paymentId, invoiceId, allocatedAmount, allocatedAtUtc, installmentId);
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
