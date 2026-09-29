namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common.Results;

/// <summary>
/// Domain errors raised by <see cref="FreeMonthsBenefit"/> state transitions and
/// construction. Used in tandem with <c>ContractErrors</c> for aggregate-level
/// errors; scoped here so that benefit-specific validation stays separate from
/// contract-level validation.
/// </summary>
public static class FreeMonthsBenefitErrors
{
    public static Error IdRequired =>
        Error.Validation("Contract.FreeMonthsBenefit.Id_Required",
            "FreeMonthsBenefit ID is required");

    public static Error ContractIdRequired =>
        Error.Validation("Contract.FreeMonthsBenefit.ContractId_Required",
            "FreeMonthsBenefit ContractId is required");

    public static Error EntitlementMonthsRequired =>
        Error.Validation("Contract.FreeMonthsBenefit.EntitlementMonths_Invalid",
            "FreeMonthsBenefit EntitlementMonths must be a positive integer");

    public static Error EligibilityRuleRequired =>
        Error.Validation("Contract.FreeMonthsBenefit.EligibilityRule_Required",
            "FreeMonthsBenefit EligibilityRule is required");

    public static Error CurrencyRequired =>
        Error.Validation("Contract.FreeMonthsBenefit.Currency_Required",
            "FreeMonthsBenefit CurrencyCode is required and must be a 3-letter ISO-4217 code");

    public static Error CurrencyMismatch(string expectedCurrency) =>
        Error.Validation("Contract.FreeMonthsBenefit.CurrencyMismatch",
            $"FreeMonthsBenefit CurrencyCode must match contract currency '{expectedCurrency}'");

    public static Error InvalidEligibilityStatus =>
        Error.Validation("Contract.FreeMonthsBenefit.EligibilityStatus_Invalid",
            "FreeMonthsEligibilityStatus value is not defined");

    public static Error InvalidFulfillmentStatus =>
        Error.Validation("Contract.FreeMonthsBenefit.FulfillmentStatus_Invalid",
            "FreeMonthsFulfillmentStatus value is not defined");

    public static Error NotEligible =>
        Error.Validation("Contract.FreeMonthsBenefit.NotEligible",
            "FreeMonthsBenefit must be Eligible before it can be granted");

    public static Error NotGranted =>
        Error.Validation("Contract.FreeMonthsBenefit.NotGranted",
            "FreeMonthsBenefit must be Granted before it can be applied to a subscription");

    public static Error AlreadyApplied =>
        Error.Conflict("Contract.FreeMonthsBenefit.AlreadyApplied",
            "FreeMonthsBenefit has already been applied to a subscription (terminal state)");

    public static Error AlreadyGranted =>
        Error.Conflict("Contract.FreeMonthsBenefit.AlreadyGranted",
            "FreeMonthsBenefit has already been granted");

    public static Error NotFound(Guid id) =>
        Error.NotFound("Contract.FreeMonthsBenefit.NotFound",
            $"FreeMonthsBenefit with ID '{id}' was not found");

    public static Error CrossTenantFreeMonthsBenefit =>
        Error.Forbidden("Contract.FreeMonthsBenefit.CrossTenant",
            "Cannot access a FreeMonthsBenefit belonging to a different tenant");
}
