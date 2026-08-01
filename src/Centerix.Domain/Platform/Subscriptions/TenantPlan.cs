namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.Events;

public class TenantPlan : AuditableEntity<Guid>
{
    public int PlanId { get; private set; }
    public decimal SnapshotPrice { get; private set; }
    public DateTime StartsAt { get; private set; }
    public DateTime? EndsAt { get; private set; }
    public bool AutoRenew { get; private set; }
    public SubscriptionStatus Status { get; private set; }

    public Plans.Plan Plan { get; set; } = default!;

    private TenantPlan() { }

    private TenantPlan(
        Guid id,
        int planId,
        decimal snapshotPrice,
        DateTime startsAt,
        DateTime? endsAt,
        bool autoRenew,
        SubscriptionStatus status)
        : base(id)
    {
        PlanId = planId;
        SnapshotPrice = snapshotPrice;
        StartsAt = startsAt;
        EndsAt = endsAt;
        AutoRenew = autoRenew;
        Status = status;
    }

    public static Result<TenantPlan> Create(
        Guid id,
        int planId,
        decimal snapshotPrice,
        DateTime startsAt,
        bool autoRenew,
        SubscriptionStatus status)
    {
        if (planId <= 0)
            return TenantPlanErrors.PlanIdRequired;

        if (snapshotPrice <= 0)
            return Error.Validation("TenantPlan.SnapshotPrice_Invalid", "Snapshot price must be greater than zero");

        if (startsAt == default)
            return TenantPlanErrors.StartsAtRequired;

        if (!Enum.IsDefined(status))
            return Error.Validation("TenantPlan.Status_Invalid", "Invalid subscription status");

        return new TenantPlan(id, planId, snapshotPrice, startsAt, null, autoRenew, status);
    }

    public Result<Updated> Update(DateTime? endsAt, bool autoRenew)
    {
        if (endsAt.HasValue && endsAt.Value <= StartsAt)
            return TenantPlanErrors.EndDateBeforeStart;

        EndsAt = endsAt;
        AutoRenew = autoRenew;

        return Result.Updated;
    }

    public Result<Updated> Renew(DateTime utcNow, DateTime newEndsAt)
    {
        if (Status == SubscriptionStatus.Cancelled)
            return TenantPlanErrors.CannotRenewCancelled;

        if (newEndsAt <= StartsAt)
            return TenantPlanErrors.EndDateBeforeStart;

        EndsAt = newEndsAt;

        if (Status != SubscriptionStatus.Active)
        {
            Status = SubscriptionStatus.Active;
        }

        AddDomainEvent(new TenantPlanRenewedEvent(Id, PlanId));

        return Result.Updated;
    }

    public Result<Updated> Cancel(DateTime utcNow)
    {
        if (Status != SubscriptionStatus.Active)
            return TenantPlanErrors.NotActive;

        if (EndsAt.HasValue && utcNow >= EndsAt.Value)
            return TenantPlanErrors.CannotCancelExpired;

        Status = SubscriptionStatus.Cancelled;

        AddDomainEvent(new Events.TenantPlanCancelledEvent(Id, PlanId));

        return Result.Updated;
    }

    public Result<Updated> MarkExpired()
    {
        if (Status != SubscriptionStatus.Active)
            return TenantPlanErrors.NotActive;

        Status = SubscriptionStatus.Expired;

        return Result.Updated;
    }

    public Result<Updated> Suspend()
    {
        if (Status != SubscriptionStatus.Active)
            return TenantPlanErrors.NotActive;

        Status = SubscriptionStatus.Suspended;

        return Result.Updated;
    }

    public Result<Updated> Reactivate()
    {
        if (Status == SubscriptionStatus.Active)
            return TenantPlanErrors.AlreadyActive;

        Status = SubscriptionStatus.Active;

        return Result.Updated;
    }
}
