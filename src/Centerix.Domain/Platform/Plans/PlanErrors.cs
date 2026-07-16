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
}
