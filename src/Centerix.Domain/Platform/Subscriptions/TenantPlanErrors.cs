namespace Centerix.Domain.Platform.Subscriptions;

using Centerix.Domain.Common.Results;

public static class TenantPlanErrors
{
    public static Error PlanIdRequired =>
        Error.Validation("TenantPlan.PlanId_Required", "Plan ID is required");

    public static Error StartsAtRequired =>
        Error.Validation("TenantPlan.StartsAt_Required", "Start date is required");

    public static Error EndDateBeforeStart =>
        Error.Validation("TenantPlan.EndDate_Before_Start", "End date must be after start date");

    public static Error AlreadyActive =>
        Error.Conflict("TenantPlan.AlreadyActive", "This plan subscription is already active");

    public static Error NotActive =>
        Error.Conflict("TenantPlan.NotActive", "This plan subscription is not active");

    public static Error CannotCancelExpired =>
        Error.Conflict("TenantPlan.CannotCancelExpired", "Cannot cancel an expired subscription");

    public static Error CannotRenewCancelled =>
        Error.Conflict("TenantPlan.CannotRenewCancelled", "Cannot renew a cancelled subscription");
}
