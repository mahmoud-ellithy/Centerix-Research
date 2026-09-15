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

    public static Error AlreadyPastDue =>
        Error.Conflict("TenantPlan.AlreadyPastDue", "This subscription is already past due");

    public static Error CannotMarkPastDue =>
        Error.Conflict("TenantPlan.CannotMarkPastDue", "Cannot mark a subscription as past due from its current state");

    public static Error CannotSuspendFromObligation =>
        Error.Conflict("TenantPlan.CannotSuspendFromObligation", "Cannot suspend a subscription for non-payment from its current state");

    public static Error CannotReactivateFromRecovery =>
        Error.Conflict("TenantPlan.CannotReactivateFromRecovery", "Cannot reactivate a subscription from financial recovery in its current state");

    /// <summary>Describes an illegal transition from <paramref name="current"/> to the target action.</summary>
    public static Error InvalidStateTransition(Enums.SubscriptionStatus current, string action) =>
        Error.Conflict("TenantPlan.InvalidStateTransition",
            $"Cannot {action} a subscription in status '{current}'");

    // ---- Renewal-specific errors (Task 9.3) ----

    public static Error CannotRenewSuspended =>
        Error.Conflict("TenantPlan.CannotRenewSuspended",
            "Cannot renew a suspended subscription. Resolve financial obligations first.");

    public static Error CannotRenewPending =>
        Error.Conflict("TenantPlan.CannotRenewPending",
            "Cannot renew a pending subscription. Activate or cancel it first.");

    public static Error OverlappingActiveSubscription =>
        Error.Conflict("TenantPlan.Overlap",
            "An active or pending subscription already exists for this tenant. Cannot create overlapping renewal.");

    public static Error PlanNotFound =>
        Error.NotFound("TenantPlan.PlanNotFound",
            "The specified plan was not found.");

    public static Error PlanInactive =>
        Error.Conflict("TenantPlan.PlanInactive",
            "Cannot renew to an inactive plan.");
}
