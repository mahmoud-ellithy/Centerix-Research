namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common.Results;

public static class TenantPlanErrors
{
    public static Error TenantIdRequired =>
        Error.Validation("TenantPlan.TenantId_Required", "Tenant is required");

    public static Error PlanIdRequired =>
        Error.Validation("TenantPlan.PlanId_Required", "Plan ID is required");

    public static Error StartsAtRequired =>
        Error.Validation("TenantPlan.StartsAt_Required", "Start date is required");

    public static Error EndDateBeforeStart =>
        Error.Validation("TenantPlan.EndDate_Before_Start", "End date must be after start date");

    public static Error SnapshotPriceInvalid =>
        Error.Validation("TenantPlan.SnapshotPrice_Invalid", "Snapshot price cannot be negative");

    public static Error SnapshotCurrencyInvalid =>
        Error.Validation("TenantPlan.SnapshotCurrency_Invalid", "Snapshot currency must be a 3-letter ISO-4217 code");

    public static Error DurationInvalid =>
        Error.Validation("TenantPlan.Duration_Invalid", "Duration must be at least one month");

    public static Error BonusInvalid =>
        Error.Validation("TenantPlan.Bonus_Invalid", "Bonus months cannot be negative");

    public static Error StatusInvalid =>
        Error.Validation("TenantPlan.Status_Invalid", "Invalid subscription status");

    public static Error SnapshotLimitsInvalid =>
        Error.Validation("TenantPlan.SnapshotLimits_Invalid", "Snapshot limits cannot be negative");

    public static Error FeatureCodeRequired =>
        Error.Validation("TenantPlan.FeatureCode_Required", "Feature code is required");

    public static Error FeatureAlreadyGranted =>
        Error.Conflict("TenantPlan.FeatureAlreadyGranted", "Feature code is already granted to this subscription");

    public static Error AlreadyActive =>
        Error.Conflict("TenantPlan.AlreadyActive", "This plan subscription is already active");

    public static Error AlreadyCancelledSubscription =>
        Error.Conflict("TenantPlan.AlreadyCancelled", "This subscription is already cancelled");

    public static Error NotActive =>
        Error.Conflict("TenantPlan.NotActive", "This plan subscription is not active");

    public static Error NotYetExpired =>
        Error.Conflict("TenantPlan.NotYetExpired", "Subscription has not reached its effective end yet");

    public static Error AlreadyExpired =>
        Error.Conflict("TenantPlan.AlreadyExpired", "Subscription has already expired");

    public static Error CannotCancelExpired =>
        Error.Conflict("TenantPlan.CannotCancelExpired", "Cannot cancel an expired subscription");

    public static Error CannotRenewCancelled =>
        Error.Conflict("TenantPlan.CannotRenewCancelled", "Cannot renew a cancelled subscription");

    /// <summary>Describes an illegal transition from <paramref name="current"/> to the target action.</summary>
    public static Error InvalidStateTransition(Enums.SubscriptionStatus current, string action) =>
        Error.Conflict("TenantPlan.InvalidStateTransition",
            $"Cannot {action} a subscription in status '{current}'");
}
