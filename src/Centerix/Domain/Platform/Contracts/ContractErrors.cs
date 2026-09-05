namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common.Results;

/// <strong>Domain errors for the Contract aggregate.</strong>
public static class ContractErrors
{
    public static Error IdRequired =>
        Error.Validation("Contract.Id_Required", "Contract ID is required.");

    public static Error TenantIdRequired =>
        Error.Validation("Contract.TenantId_Required", "Tenant ID is required.");

    public static Error ContractNumberRequired =>
        Error.Validation("Contract.ContractNumber_Required", "Contract number is required.");

    public static Error PlanIdRequired =>
        Error.Validation("Contract.PlanId_Required", "Plan ID is required.");

    public static Error InvalidDuration =>
        Error.Validation("Contract.Duration_Invalid", "Contract duration must be greater than zero.");

    public static Error InvalidMonthlyListPrice =>
        Error.Validation("Contract.MonthlyListPrice_Invalid", "Monthly list price cannot be negative.");

    public static Error InvalidCurrency =>
        Error.Validation("Contract.Currency_Invalid", "Currency code must be a valid ISO-4217 code (3 characters).");

    public static Error InvalidContractedAmount =>
        Error.Validation("Contract.ContractedAmount_Invalid", "Contracted amount cannot be negative.");

    public static Error InvalidDiscountAmount =>
        Error.Validation("Contract.DiscountAmount_Invalid", "Discount amount cannot be negative.");

    public static Error InvalidDateRange =>
        Error.Validation("Contract.DateRange_Invalid", "End date must be after effective date.");

    public static Error ReasonRequired =>
        Error.Validation("Contract.Reason_Required", "A reason is required for this action.");

    public static Error NotYetExpired =>
        Error.Validation("Contract.NotYetExpired", "The contract has not yet reached its expiry date.");

    public static Error CannotModifyAfterDraft =>
        Error.Validation("Contract.CannotModify_AfterDraft", "Cannot modify pricing tiers or benefits after the contract leaves Draft status.");

    public static Error InvalidStateTransition(Enums.ContractStatus currentStatus, string action) =>
        Error.Conflict("Contract.InvalidStateTransition",
            $"Cannot {action} from status '{currentStatus}'.");

    public static class PricingTier
    {
        public static Error IdRequired =>
            Error.Validation("Contract.PricingTier.Id_Required", "Pricing tier ID is required.");

        public static Error ContractIdRequired =>
            Error.Validation("Contract.PricingTier.ContractId_Required", "Contract ID is required.");

        public static Error InvalidDuration =>
            Error.Validation("Contract.PricingTier.Duration_Invalid", "Pricing tier duration must be greater than zero.");

        public static Error InvalidPrice =>
            Error.Validation("Contract.PricingTier.Price_Invalid", "Pricing tier price cannot be negative.");

        public static Error InvalidMonthlyListPrice =>
            Error.Validation("Contract.PricingTier.MonthlyListPrice_Invalid", "Monthly list price cannot be negative.");

        public static Error InvalidCurrency =>
            Error.Validation("Contract.PricingTier.Currency_Invalid", "Currency code must be a valid ISO-4217 code (3 characters).");

        public static Error DuplicateDuration =>
            Error.Conflict("Contract.PricingTier.DuplicateDuration", "A pricing tier with this duration already exists.");
    }

    public static class Benefit
    {
        public static Error IdRequired =>
            Error.Validation("Contract.Benefit.Id_Required", "Benefit ID is required.");

        public static Error ContractIdRequired =>
            Error.Validation("Contract.Benefit.ContractId_Required", "Contract ID is required.");

        public static Error InvalidType =>
            Error.Validation("Contract.Benefit.Type_Invalid", "Invalid benefit type.");

        public static Error NameRequired =>
            Error.Validation("Contract.Benefit.Name_Required", "Benefit name is required.");

        public static Error InvalidValue =>
            Error.Validation("Contract.Benefit.Value_Invalid", "Contractual value cannot be negative.");

        public static Error InvalidCurrency =>
            Error.Validation("Contract.Benefit.Currency_Invalid", "Currency code must be a valid ISO-4217 code (3 characters).");

        public static Error AlreadyGranted =>
            Error.Conflict("Contract.Benefit.AlreadyGranted", "This benefit has already been granted.");

        public static Error ExceedsMaximumValue(decimal totalValue, decimal maxValue) =>
            Error.Validation("Contract.Benefit.ExceedsMaximumValue",
                $"Total benefit value ({totalValue:C}) exceeds the maximum allowed ({maxValue:C}, 3 months of subscription value).");
    }
}
