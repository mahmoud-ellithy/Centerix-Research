namespace Centerix.Domain.Platform.Billing.BillingCycles;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Subscriptions;

/// <summary>
/// A billable service period tied to a Subscription.
/// A Subscription may have one or more BillingCycles over its lifetime.
/// </summary>
public class BillingCycle : AuditableEntity<Guid>
{
    public Guid SubscriptionId { get; private set; }
    public Subscriptions.TenantPlan Subscription { get; private set; } = default!;

    public DateTime PeriodStart { get; private set; }
    public DateTime PeriodEnd { get; private set; }

    public BillingCycleStatus Status { get; private set; }

    private BillingCycle() { }

    private BillingCycle(
        Guid id,
        string tenantId,
        Guid subscriptionId,
        DateTime periodStart,
        DateTime periodEnd,
        BillingCycleStatus status)
        : base(id)
    {
        TenantId = tenantId;
        SubscriptionId = subscriptionId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Status = status;
    }

    public static Result<BillingCycle> Create(
        Guid id,
        string tenantId,
        Guid subscriptionId,
        DateTime periodStart,
        DateTime periodEnd)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BillingCycleErrors.TenantIdRequired;

        if (subscriptionId == Guid.Empty)
            return BillingCycleErrors.SubscriptionIdRequired;

        if (periodStart == default)
            return BillingCycleErrors.PeriodStartRequired;

        if (periodEnd == default)
            return BillingCycleErrors.PeriodEndRequired;

        if (periodEnd <= periodStart)
            return BillingCycleErrors.InvalidPeriod;

        return new BillingCycle(id, tenantId.Trim(), subscriptionId, periodStart, periodEnd, BillingCycleStatus.Draft);
    }

    public Result<Updated> MarkInvoiced()
    {
        if (Status != BillingCycleStatus.Draft)
            return BillingCycleErrors.InvalidStateTransition(Status, "mark as invoiced");

        Status = BillingCycleStatus.Invoiced;
        return Result.Updated;
    }

    public Result<Updated> MarkPaid()
    {
        if (Status != BillingCycleStatus.Invoiced)
            return BillingCycleErrors.InvalidStateTransition(Status, "mark as paid");

        Status = BillingCycleStatus.Paid;
        return Result.Updated;
    }

    public Result<Updated> Cancel()
    {
        if (Status is BillingCycleStatus.Paid or BillingCycleStatus.Cancelled)
            return BillingCycleErrors.InvalidStateTransition(Status, "cancel");

        Status = BillingCycleStatus.Cancelled;
        return Result.Updated;
    }
}
