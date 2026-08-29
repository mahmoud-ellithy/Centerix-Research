namespace Centerix.Domain.Platform.Plans;

using Centerix.Domain.Common.Results;

public static class PlanErrors
{
    public static Error CodeRequired =>
        Error.Validation("Plan.Code_Required", "Plan code is required");

    public static Error DisplayNameRequired =>
        Error.Validation("Plan.DisplayName_Required", "Display name is required");

    public static Error InvalidPrice =>
        Error.Validation("Plan.InvalidPrice", "Monthly price must be greater than or equal to zero");

    public static Error InvalidLimits =>
        Error.Validation("Plan.InvalidLimits", "Limits must be greater than or equal to zero");

    public static Error AlreadyDeactivated =>
        Error.Conflict("Plan.AlreadyDeactivated", "Plan is already deactivated");

    public static Error AlreadyActive =>
        Error.Conflict("Plan.AlreadyActive", "Plan is already active");

    public static Error InvalidCurrency =>
        Error.Validation("Plan.InvalidCurrency", "Currency must be a 3-letter ISO-4217 code");

    public static Error InvalidDuration =>
        Error.Validation("Plan.InvalidDuration", "Duration must be at least one month");

    public static Error InvalidBonus =>
        Error.Validation("Plan.InvalidBonus", "Bonus months cannot be negative");

    public static Error InUseBySubscriptions =>
        Error.Conflict("Plan.InUseBySubscriptions",
            "Plan has subscriptions and cannot be deleted; deactivate it instead");
}
