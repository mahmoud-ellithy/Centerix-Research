namespace Centerix.Domain.Platform.Billing.Invoicing;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Invoicing.Events;
using Centerix.Domain.Platform.Billing.Payments;

public class Invoice : AuditableEntity<Guid>
{
    public string InvoiceNumber { get; private set; } = default!;
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public DateTime? IssuedAt { get; private set; }
    public DateTime? DueAt { get; private set; }

    // Commercial traceability (optional for legacy invoices, required for new invoices)
    public Guid? ContractId { get; private set; }
    public Guid? SubscriptionId { get; private set; }
    public Guid? BillingCycleId { get; private set; }

    private readonly List<InvoiceLine> _invoiceLines = [];
    public IReadOnlyList<InvoiceLine> InvoiceLines => _invoiceLines.AsReadOnly();

    private readonly List<PlatformPayment> _platformPayments = [];
    public IReadOnlyList<PlatformPayment> PlatformPayments => _platformPayments.AsReadOnly();

    private readonly List<PaymentAllocation> _paymentAllocations = [];
    public IReadOnlyList<PaymentAllocation> PaymentAllocations => _paymentAllocations.AsReadOnly();

    private Invoice() { }

    private Invoice(
        Guid id,
        string invoiceNumber,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal subtotal,
        decimal discountAmount,
        decimal taxAmount,
        decimal totalAmount,
        InvoiceStatus status,
        Guid? contractId = null,
        Guid? subscriptionId = null,
        Guid? billingCycleId = null)
        : base(id)
    {
        InvoiceNumber = invoiceNumber;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Subtotal = subtotal;
        DiscountAmount = discountAmount;
        TaxAmount = taxAmount;
        TotalAmount = totalAmount;
        Status = status;
        ContractId = contractId;
        SubscriptionId = subscriptionId;
        BillingCycleId = billingCycleId;
    }

    public static Result<Invoice> Create(
        Guid id,
        string invoiceNumber,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal subtotal,
        decimal discountAmount,
        decimal taxAmount,
        decimal totalAmount,
        Guid? contractId = null,
        Guid? subscriptionId = null,
        Guid? billingCycleId = null)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            return InvoiceErrors.InvoiceNumberRequired;

        if (periodEnd < periodStart)
            return InvoiceErrors.InvalidPeriod;

        if (subtotal < 0 || discountAmount < 0 || taxAmount < 0)
            return InvoiceErrors.InvalidAmount;

        if (totalAmount < 0)
            return InvoiceErrors.InvalidTotalAmount;

        return new Invoice(
            id, invoiceNumber, periodStart, periodEnd, subtotal, discountAmount, taxAmount, totalAmount,
            InvoiceStatus.Draft, contractId, subscriptionId, billingCycleId);
    }

    public Result<Updated> Issue(DateTime utcNow, DateTime? dueAt = null)
    {
        if (Status != InvoiceStatus.Draft)
            return InvoiceErrors.CannotIssueDraftOnly;

        Status = InvoiceStatus.Issued;
        IssuedAt = utcNow;
        DueAt = dueAt;

        return Result.Updated;
    }

    /// <summary>
    /// Updates the invoice status based on current allocations.
    /// Called after payment allocations change.
    /// </summary>
    public Result<Updated> UpdatePaymentStatus()
    {
        if (Status != InvoiceStatus.Issued && Status != InvoiceStatus.Sent && Status != InvoiceStatus.PartiallyPaid)
            return InvoiceErrors.CannotPayNotIssued;

        var paidAmount = GetPaidAmount();

        if (paidAmount >= TotalAmount)
        {
            Status = InvoiceStatus.Paid;
            AddDomainEvent(new InvoicePaidEvent(Id));
        }
        else if (paidAmount > 0)
        {
            Status = InvoiceStatus.PartiallyPaid;
        }

        return Result.Updated;
    }

    /// <summary>
    /// Gets the total amount paid through completed payment allocations.
    /// This is the source of truth for paid amount.
    /// </summary>
    public decimal GetPaidAmount()
    {
        return _paymentAllocations
            .Where(a => a.IsActive)
            .Sum(a => a.AllocatedAmount);
    }

    /// <summary>
    /// Gets the remaining amount to be paid.
    /// Calculated as TotalAmount - PaidAmount.
    /// </summary>
    public decimal GetRemainingAmount()
    {
        return TotalAmount - GetPaidAmount();
    }

    public Result<Updated> Cancel()
    {
        if (Status == InvoiceStatus.Cancelled)
            return InvoiceErrors.AlreadyCancelled;

        if (Status != InvoiceStatus.Draft)
            return InvoiceErrors.CannotCancelNonDraft;

        Status = InvoiceStatus.Cancelled;

        return Result.Updated;
    }
}
