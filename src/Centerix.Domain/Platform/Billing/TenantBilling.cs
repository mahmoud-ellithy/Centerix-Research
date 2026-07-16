namespace Centerix.Domain.Platform.Billing;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Enums;
using Centerix.Domain.Platform.Billing.Events;

public class TenantBilling : AuditableEntity<Guid>
{
    public int PlanId { get; private set; }
    public decimal AmountEGP { get; private set; }
    public string Method { get; private set; } = default!;
    public BillingStatus Status { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public string? InvoiceRef { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public Plans.Plan Plan { get; private set; } = default!;

    private TenantBilling() { }

    private TenantBilling(
        Guid id,
        int planId,
        decimal amountEGP,
        string method,
        BillingStatus status,
        DateTime createdAt,
        DateTime? paidAt,
        string? invoiceRef)
        : base(id)
    {
        PlanId = planId;
        AmountEGP = amountEGP;
        Method = method;
        Status = status;
        CreatedAt = createdAt;
        PaidAt = paidAt;
        InvoiceRef = invoiceRef;
    }

    public static Result<TenantBilling> Create(
        Guid id,
        int planId,
        decimal amountEGP,
        string method,
        BillingStatus status,
        DateTime createdAt,
        DateTime? paidAt = null,
        string? invoiceRef = null)
    {
        if (planId <= 0)
            return TenantBillingErrors.PlanIdRequired;

        if (amountEGP <= 0)
            return TenantBillingErrors.InvalidAmount;

        if (string.IsNullOrWhiteSpace(method))
            return TenantBillingErrors.MethodRequired;

        if (!Enum.IsDefined(status))
            return Error.Validation("Billing.Status_Invalid", "Invalid billing status");

        return new TenantBilling(id, planId, amountEGP, method, status, createdAt, paidAt, invoiceRef);
    }

    public Result<Updated> MarkPaid(DateTime paidAt, string? invoiceRef = null)
    {
        if (Status != BillingStatus.Unpaid)
            return TenantBillingErrors.AlreadyPaid;

        Status = BillingStatus.Paid;
        PaidAt = paidAt;
        InvoiceRef = invoiceRef;

        AddDomainEvent(new BillingPaidEvent(Id, PlanId, AmountEGP));

        return Result.Updated;
    }
}
