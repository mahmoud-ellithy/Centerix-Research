namespace Centerix.Domain.Platform.Billing.Invoicing;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Invoicing.Events;

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

    private readonly List<InvoiceLine> _invoiceLines = [];
    public IReadOnlyList<InvoiceLine> InvoiceLines => _invoiceLines.AsReadOnly();

    private readonly List<PlatformPayment> _platformPayments = [];
    public IReadOnlyList<PlatformPayment> PlatformPayments => _platformPayments.AsReadOnly();

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
        InvoiceStatus status)
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
    }

    public static Result<Invoice> Create(
        Guid id,
        string invoiceNumber,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal subtotal,
        decimal discountAmount,
        decimal taxAmount,
        decimal totalAmount)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            return InvoiceErrors.InvoiceNumberRequired;

        if (periodEnd < periodStart)
            return InvoiceErrors.InvalidPeriod;

        if (subtotal < 0 || discountAmount < 0 || taxAmount < 0)
            return InvoiceErrors.InvalidAmount;

        if (totalAmount < 0)
            return InvoiceErrors.InvalidTotalAmount;

        return new Invoice(id, invoiceNumber, periodStart, periodEnd, subtotal, discountAmount, taxAmount, totalAmount, InvoiceStatus.Draft);
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

    public Result<Updated> MarkPaid()
    {
        if (Status != InvoiceStatus.Issued && Status != InvoiceStatus.Sent)
            return InvoiceErrors.CannotPayNotIssued;

        Status = InvoiceStatus.Paid;

        AddDomainEvent(new InvoicePaidEvent(Id));

        return Result.Updated;
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
