namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;

public static class ContractErrors
{
    public static Error ContractNumberRequired =>
        Error.Validation("Contract.ContractNumber_Required", "Contract number is required");

    public static Error TenantIdRequired =>
        Error.Validation("Contract.TenantId_Required", "Tenant is required");

    public static Error TenantNotResolved =>
        Error.Validation("Contract.TenantNotResolved", "No authorized tenant context found. Cannot create contract.");

    public static Error PlanIdRequired =>
        Error.Validation("Contract.PlanId_Required", "Plan ID is required");

    public static Error EffectiveAtRequired =>
        Error.Validation("Contract.EffectiveAt_Required", "Effective date is required");

    public static Error DurationInvalid =>
        Error.Validation("Contract.Duration_Invalid", "Duration must be at least one month");

    public static Error MonthlyListPriceInvalid =>
        Error.Validation("Contract.MonthlyListPrice_Invalid", "Monthly list price cannot be negative");

    public static Error ContractualMonthlyValueInvalid =>
        Error.Validation("Contract.ContractualMonthlyValue_Invalid", "Contractual monthly value cannot be negative");

    public static Error CurrencyInvalid =>
        Error.Validation("Contract.Currency_Invalid", "Currency must be a 3-letter ISO-4217 code");

    public static Error StatusInvalid =>
        Error.Validation("Contract.Status_Invalid", "Invalid contract status");

    public static Error ContractedAmountInvalid =>
        Error.Validation("Contract.ContractedAmount_Invalid", "Contracted amount cannot be negative");

    public static Error DiscountAmountInvalid =>
        Error.Validation("Contract.DiscountAmount_Invalid", "Discount amount cannot be negative");

    public static Error DiscountExceedsGrossValue =>
        Error.Validation("Contract.Discount_Exceeds_GrossValue", "Discount amount cannot exceed the gross commercial value");

    public static Error ContractedAmountExceedsGrossValue =>
        Error.Validation("Contract.ContractedAmount_Exceeds_GrossValue", "Contracted amount cannot exceed the gross commercial value");

    public static Error EndsAtBeforeEffectiveAt =>
        Error.Validation("Contract.EndsAt_Before_EffectiveAt", "End date must be on or after effective date");

    public static Error InvalidStateTransition(ContractStatus current, string action) =>
        Error.Conflict("Contract.InvalidStateTransition",
            $"Cannot {action} a contract in status '{current}'");

    public static Error ContractNotFound(Guid id) =>
        Error.NotFound("Contract.NotFound", $"Contract with ID '{id}' was not found");

    public static Error DuplicateContractNumber(string contractNumber) =>
        Error.Conflict("Contract.DuplicateContractNumber",
            $"A contract with number '{contractNumber}' already exists");

    public static Error BenefitExceedsLimit =>
        Error.Validation("Contract.BenefitExceedsLimit",
            "The total value of benefits exceeds three months of the contract's contractual monthly value");

    public static class PricingTier
    {
        public static Error IdRequired =>
            Error.Validation("Contract.PricingTier.Id_Required", "Pricing tier ID is required");

        public static Error ContractIdRequired =>
            Error.Validation("Contract.PricingTier.ContractId_Required", "Contract ID is required");

        public static Error InvalidDuration =>
            Error.Validation("Contract.PricingTier.Duration_Invalid", "Duration must be at least one month");

        public static Error InvalidPrice =>
            Error.Validation("Contract.PricingTier.Price_Invalid", "Tier price cannot be negative");

        public static Error InvalidMonthlyListPrice =>
            Error.Validation("Contract.PricingTier.MonthlyListPrice_Invalid", "Monthly list price cannot be negative");

        public static Error InvalidCurrency =>
            Error.Validation("Contract.PricingTier.Currency_Invalid", "Currency must be a 3-letter ISO-4217 code");

        public static Error DuplicateDuration(int duration) =>
            Error.Validation("Contract.PricingTier.DuplicateDuration",
                $"A pricing tier with duration {duration} months already exists in this contract");

        public static Error CurrencyMismatch(string expectedCurrency) =>
            Error.Validation("Contract.PricingTier.CurrencyMismatch",
                $"Pricing tier currency must match contract currency '{expectedCurrency}'");
    }

    public static class Benefit
    {
        public static Error IdRequired =>
            Error.Validation("Contract.Benefit.Id_Required", "Benefit ID is required");

        public static Error ContractIdRequired =>
            Error.Validation("Contract.Benefit.ContractId_Required", "Contract ID is required");

        public static Error NameRequired =>
            Error.Validation("Contract.Benefit.Name_Required", "Benefit name is required");

        public static Error InvalidValue =>
            Error.Validation("Contract.Benefit.Value_Invalid", "Benefit value cannot be negative");

        public static Error InvalidCurrency =>
            Error.Validation("Contract.Benefit.Currency_Invalid", "Currency must be a 3-letter ISO-4217 code");

        public static Error CurrencyMismatch(string expectedCurrency) =>
            Error.Validation("Contract.Benefit.CurrencyMismatch",
                $"Benefit currency must match contract currency '{expectedCurrency}'");
    }
}
