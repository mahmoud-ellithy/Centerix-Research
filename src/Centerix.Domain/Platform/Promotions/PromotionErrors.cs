namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions.Enums;

public static class PromotionErrors
{
    public static Error InvalidId =>
        Error.Validation("Promotion.Id_Invalid", "Promotion ID must be non-negative");

    public static Error NameRequired =>
        Error.Validation("Promotion.Name_Required", "Promotion name is required");

    public static Error NameTooLong =>
        Error.Validation("Promotion.Name_TooLong", "Promotion name must not exceed 200 characters");

    public static Error InvalidPlanId =>
        Error.Validation("Promotion.PlanId_Invalid", "Plan ID must be non-negative");

    public static Error InvalidDurationMonths =>
        Error.Validation("Promotion.DurationMonths_Invalid", "Duration months must be non-negative");

    public static Error StartsAtRequired =>
        Error.Validation("Promotion.StartsAt_Required", "Start date is required");

    public static Error EndsAtRequired =>
        Error.Validation("Promotion.EndsAt_Required", "End date is required");

    public static Error InvalidDateRange =>
        Error.Validation("Promotion.DateRange_Invalid", "Start date must be before end date");

    public static Error CodeTooLong =>
        Error.Validation("Promotion.Code_TooLong", "Promotion code must not exceed 50 characters");

    public static Error InvalidPercentage =>
        Error.Validation("Promotion.Percentage_Invalid", "Percentage must be greater than 0 and at most 100");

    public static Error InvalidFixedAmount =>
        Error.Validation("Promotion.FixedAmount_Invalid", "Fixed amount must be greater than 0");

    public static Error InvalidPromotionalPrice =>
        Error.Validation("Promotion.PromotionalPrice_Invalid", "Promotional price must be non-negative");

    public static Error InvalidChargedMonths =>
        Error.Validation("Promotion.ChargedMonths_Invalid", "Charged months must be greater than 0");

    public static Error InvalidStateTransition(PromotionStatus current, string action) =>
        Error.Conflict("Promotion.InvalidStateTransition",
            $"Cannot {action} a promotion in status '{current}'");

    public static Error NotFound(int id) =>
        Error.NotFound("Promotion.NotFound", $"Promotion with ID '{id}' was not found");

    public static Error DuplicateCode(string code) =>
        Error.Conflict("Promotion.DuplicateCode",
            $"A promotion with code '{code}' already exists");

    public static Error PromotionNotApplicable =>
        Error.Validation("Promotion.NotApplicable", "No eligible promotion found for the given plan and duration");

    public static Error PlanNotFound =>
        Error.NotFound("Promotion.PlanNotFound", "The specified plan was not found");

    public static Error PlanInactive =>
        Error.Validation("Promotion.PlanInactive", "Cannot apply a promotion to an inactive plan");
}
