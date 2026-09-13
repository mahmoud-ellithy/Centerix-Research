namespace Centerix.Domain.Platform.Billing.Installments;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Authoritative payment obligation for a customer. Represents a specific amount due
/// on a specific date, covering a specific service period within a Contract.
///
/// Key distinction: DueDate controls payment lateness. CoveredPeriodStart/End controls
/// the entitlement/period that the installment pays for. They are NOT the same concept.
///
/// Status is server-derived from: DueDateUtc, Amount, SettledAmount, and current time.
/// Clients cannot manufacture PaidAmount, SettledAmount, RemainingAmount, or Status.
///
/// Settlement is derived from valid completed PaymentAllocation records assigned to this
/// installment, not from client-supplied values.
/// </summary>
public class Installment : AuditableEntity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid? SubscriptionId { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public int SequenceNumber { get; private set; }

    public DateTime DueDateUtc { get; private set; }

    public DateTime CoveredPeriodStartUtc { get; private set; }
    public DateTime CoveredPeriodEndUtc { get; private set; }

    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>
    /// Derived from SUM(valid completed PaymentAllocation amounts assigned to this installment).
    /// Never set by clients — computed server-side.
    /// </summary>
    public decimal SettledAmount { get; private set; }

    /// <summary>
    /// Derived: Amount - SettledAmount. Never set by clients.
    /// </summary>
    public decimal RemainingAmount => Amount - SettledAmount;

    public InstallmentStatus Status { get; private set; }

    // Optimistic-concurrency token (SQL Server rowversion, store-generated)
    public byte[] RowVersion { get; internal set; } = [];

    private readonly List<PaymentAllocation> _paymentAllocations = [];
    public IReadOnlyList<PaymentAllocation> PaymentAllocations => _paymentAllocations.AsReadOnly();

    private Installment() { }

    private Installment(
        Guid id,
        Guid contractId,
        Guid? subscriptionId,
        Guid? invoiceId,
        int sequenceNumber,
        DateTime dueDateUtc,
        DateTime coveredPeriodStartUtc,
        DateTime coveredPeriodEndUtc,
        decimal amount,
        string currencyCode)
        : base(id)
    {
        ContractId = contractId;
        SubscriptionId = subscriptionId;
        InvoiceId = invoiceId;
        SequenceNumber = sequenceNumber;
        DueDateUtc = dueDateUtc;
        CoveredPeriodStartUtc = coveredPeriodStartUtc;
        CoveredPeriodEndUtc = coveredPeriodEndUtc;
        Amount = amount;
        CurrencyCode = currencyCode;
        SettledAmount = 0;
        Status = InstallmentStatus.Pending;
    }

    /// <summary>
    /// Creates a new installment (payment obligation).
    /// Status is derived: Pending when not yet due and unpaid.
    /// </summary>
    public static Result<Installment> Create(
        Guid id,
        Guid contractId,
        int sequenceNumber,
        DateTime dueDateUtc,
        DateTime coveredPeriodStartUtc,
        DateTime coveredPeriodEndUtc,
        decimal amount,
        string currencyCode,
        Guid? subscriptionId = null,
        Guid? invoiceId = null)
    {
        if (id == Guid.Empty)
            return InstallmentErrors.IdRequired;

        if (contractId == Guid.Empty)
            return InstallmentErrors.ContractIdRequired;

        if (sequenceNumber <= 0)
            return InstallmentErrors.SequenceNumberMustBePositive;

        if (dueDateUtc == default)
            return InstallmentErrors.DueDateRequired;

        if (coveredPeriodStartUtc == default || coveredPeriodEndUtc == default)
            return InstallmentErrors.CoveredPeriodRequired;

        if (coveredPeriodEndUtc <= coveredPeriodStartUtc)
            return InstallmentErrors.CoveredPeriodInvalid;

        if (amount <= 0)
            return InstallmentErrors.AmountMustBePositive;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return InstallmentErrors.CurrencyRequired;

        var installment = new Installment(
            id, contractId, subscriptionId, invoiceId, sequenceNumber,
            dueDateUtc, coveredPeriodStartUtc, coveredPeriodEndUtc,
            amount, currencyCode.Trim().ToUpperInvariant());

        // Calculate initial status: Overdue when due date is already past at creation
        installment.RecalculateStatus(DateTime.UtcNow);

        return installment;
    }

    /// <summary>
    /// Factory method that creates an installment with deterministic initial status.
    /// When the due date is already in the past at creation time, the installment
    /// starts as Overdue rather than Pending.
    /// </summary>
    public static Result<Installment> CreateWithStatus(
        Guid id,
        Guid contractId,
        int sequenceNumber,
        DateTime dueDateUtc,
        DateTime coveredPeriodStartUtc,
        DateTime coveredPeriodEndUtc,
        decimal amount,
        string currencyCode,
        DateTime utcNow,
        Guid? subscriptionId = null,
        Guid? invoiceId = null)
    {
        var result = Create(id, contractId, sequenceNumber, dueDateUtc,
            coveredPeriodStartUtc, coveredPeriodEndUtc, amount, currencyCode,
            subscriptionId, invoiceId);

        if (!result.IsSuccess)
            return result;

        result.Value.RecalculateStatus(utcNow);
        return result;
    }

    /// <summary>
    /// Updates mutable fields of an installment. Only allowed when the installment
    /// is in a Pending status (not yet financially active).
    /// After an installment becomes PartiallyPaid, Paid, or Cancelled, financial
    /// fields become immutable.
    /// </summary>
    public Result<Updated> Update(
        DateTime dueDateUtc,
        DateTime coveredPeriodStartUtc,
        DateTime coveredPeriodEndUtc,
        decimal amount)
    {
        if (Status != InstallmentStatus.Pending)
            return InstallmentErrors.CannotUpdateNonPending;

        if (dueDateUtc == default)
            return InstallmentErrors.DueDateRequired;

        if (coveredPeriodStartUtc == default || coveredPeriodEndUtc == default)
            return InstallmentErrors.CoveredPeriodRequired;

        if (coveredPeriodEndUtc <= coveredPeriodStartUtc)
            return InstallmentErrors.CoveredPeriodInvalid;

        if (amount <= 0)
            return InstallmentErrors.AmountMustBePositive;

        DueDateUtc = dueDateUtc;
        CoveredPeriodStartUtc = coveredPeriodStartUtc;
        CoveredPeriodEndUtc = coveredPeriodEndUtc;
        Amount = amount;

        RecalculateStatus(DateTime.UtcNow);

        return Result.Updated;
    }

    /// <summary>
    /// Cancels an installment through an explicit domain operation.
    /// Only allowed when the installment has no payment allocations.
    /// </summary>
    public Result<Updated> Cancel(DateTime utcNow)
    {
        if (Status is InstallmentStatus.Paid or InstallmentStatus.Cancelled)
            return InstallmentErrors.CannotCancelPaidOrCancelled;

        if (_paymentAllocations.Any(a => a.IsActive))
            return InstallmentErrors.CannotCancelHasAllocations;

        Status = InstallmentStatus.Cancelled;
        return Result.Updated;
    }

    /// <summary>
    /// Records a payment allocation against this installment.
    /// SettledAmount is always derived from the sum of active allocations —
    /// never independently incremented. This guarantees a single source of truth.
    /// </summary>
    public Result<Updated> ApplyAllocation(PaymentAllocation allocation, DateTime utcNow)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));

        if (!allocation.IsActive)
            return Error.Conflict("Installment.InactiveAllocation", "Only active allocations can settle an installment.");

        if (allocation.AllocatedAmount <= 0)
            return InstallmentErrors.AmountMustBePositive;

        // Guard against EF Core relationship fixup: when the handler calls
        // dbContext.PaymentAllocations.Add(allocation) before this method,
        // EF Core eagerly adds the allocation to _paymentAllocations via
        // the configured navigation backing field. We must detect that
        // and avoid double-adding while still validating capacity.
        var alreadyTracked = _paymentAllocations.Contains(allocation);

        if (!alreadyTracked)
        {
            // Validate BEFORE adding: projected settled = current sum + new amount
            var projectedSettled = GetSettledAmount() + allocation.AllocatedAmount;
            if (projectedSettled > Amount)
                return InstallmentErrors.AllocationExceedsInstallment;

            _paymentAllocations.Add(allocation);
        }
        else
        {
            // EF relationship fixup already placed this allocation in the collection.
            // Validate that the total (which now includes this allocation) is within capacity.
            var totalSettled = GetSettledAmount();
            if (totalSettled > Amount)
                return InstallmentErrors.AllocationExceedsInstallment;
        }

        // Derive SettledAmount from the authoritative allocation sum — single source of truth
        SynchronizeSettledAmount();
        RecalculateStatus(utcNow);

        return Result.Updated;
    }

    /// <summary>
    /// Synchronizes the persisted SettledAmount to match the sum of active allocations.
    /// Must be called after any change to the allocation collection.
    /// </summary>
    private void SynchronizeSettledAmount()
    {
        SettledAmount = GetSettledAmount();
    }

    /// <summary>
    /// Reverses an existing allocation on this installment and synchronizes settlement.
    /// After reversal, the allocation is excluded from the active sum, and
    /// SettledAmount/RemainingAmount/Status are recalculated from active allocations only.
    /// </summary>
    public Result<Updated> ReverseAllocation(PaymentAllocation allocation, DateTime utcNow)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));

        if (!_paymentAllocations.Contains(allocation))
            return Error.Conflict("Installment.AllocationNotAssociated",
                "Allocation is not associated with this installment.");

        if (!allocation.IsActive)
            return Error.Conflict("Installment.AllocationAlreadyReversed",
                "Allocation is already reversed.");

        var reverseResult = allocation.Reverse();
        if (!reverseResult.IsSuccess)
            return reverseResult.Errors!;

        SynchronizeSettledAmount();
        RecalculateStatus(utcNow);

        return Result.Updated;
    }

    /// <summary>
    /// Recalculates the installment status based on current financial state.
    /// This is deterministic: Pending when unpaid and not yet due,
    /// PartiallyPaid when partially settled, Paid when fully settled,
    /// Overdue when still outstanding and past due date.
    /// </summary>
    public void RecalculateStatus(DateTime utcNow)
    {
        if (Status == InstallmentStatus.Cancelled)
            return;

        if (SettledAmount >= Amount)
        {
            Status = InstallmentStatus.Paid;
        }
        else if (SettledAmount > 0)
        {
            Status = DueDateUtc < utcNow ? InstallmentStatus.Overdue : InstallmentStatus.PartiallyPaid;
        }
        else
        {
            Status = DueDateUtc < utcNow ? InstallmentStatus.Overdue : InstallmentStatus.Pending;
        }
    }

    /// <summary>
    /// Returns true if this installment is overdue: RemainingAmount > 0 AND DueDateUtc < now.
    /// </summary>
    public bool IsOverdue(DateTime utcNow) => Status != InstallmentStatus.Cancelled && RemainingAmount > 0 && DueDateUtc < utcNow;

    /// <summary>
    /// Returns the total settled amount from active allocations.
    /// </summary>
    public decimal GetSettledAmount()
    {
        return _paymentAllocations
            .Where(a => a.IsActive)
            .Sum(a => a.AllocatedAmount);
    }

    /// <summary>EF navigation mutator for rehydration of payment allocations.</summary>
    internal void LoadPaymentAllocations(IEnumerable<PaymentAllocation> allocations)
        => _paymentAllocations.AddRange(allocations);
}
