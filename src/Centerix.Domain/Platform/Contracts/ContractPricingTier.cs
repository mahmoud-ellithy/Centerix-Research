namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Immutable snapshot of a pricing tier selected for a Contract.
/// Preserves the commercial terms agreed at contract creation so that future
/// Plan price changes do not alter the contractual value of existing contracts.
/// </summary>
/// <remarks>
/// Example pricing tiers:
///   1 Month  = 1,000
///   3 Months = 2,700
///   6 Months = 5,220
///   12 Months = 10,000
///
/// For a Contract whose original duration is 12 months:
///   elapsed = 1  → 1-month tier
///   elapsed = 2  → 2 × original monthly list price
///   elapsed = 3  → 3-month tier
///   elapsed = 4  → 3-month tier
///   elapsed = 5  → 3-month tier
///   elapsed = 6  → 6-month tier
///   elapsed = 7  → 6-month tier
///   ...
///   elapsed = 11 → 6-month tier
///   elapsed = 12 → 12-month tier
/// </remarks>
public class ContractPricingTier : Entity
{
    public Guid Id { get; private set; }
    public Guid ContractId { get; private set; }

    /// <summary>Duration of this tier in calendar months (e.g., 1, 3, 6, 12).</summary>
    public int DurationMonths { get; private set; }

    /// <summary>Total price for this tier duration (NOT per-month; total for the period).</summary>
    public decimal TierPrice { get; private set; }

    /// <summary>Currency code (ISO-4217, e.g., EGP, USD).</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>
    /// The original monthly list price at the time of contract creation.
    /// Used for tier calculations (e.g., 2 months = 2 × MonthlyListPrice when no 2-month tier exists).
    /// </summary>
    public decimal MonthlyListPrice { get; private set; }

    /// <summary>Display order for UI rendering.</summary>
    public int DisplayOrder { get; private set; }

    /// <summary>The Contract this pricing tier belongs to.</summary>
    public Contract Contract { get; private set; } = default!;

    private ContractPricingTier() { }

    private ContractPricingTier(
        Guid id,
        Guid contractId,
        int durationMonths,
        decimal tierPrice,
        string currencyCode,
        decimal monthlyListPrice,
        int displayOrder)
    {
        Id = id;
        ContractId = contractId;
        DurationMonths = durationMonths;
        TierPrice = tierPrice;
        CurrencyCode = currencyCode;
        MonthlyListPrice = monthlyListPrice;
        DisplayOrder = displayOrder;
    }

    /// <summary>
    /// Creates a pricing tier snapshot with validated commercial terms.
    /// </summary>
    public static Result<ContractPricingTier> Create(
        Guid id,
        Guid contractId,
        int durationMonths,
        decimal tierPrice,
        string currencyCode,
        decimal monthlyListPrice,
        int displayOrder = 0)
    {
        if (id == Guid.Empty)
            return ContractErrors.PricingTier.IdRequired;

        if (contractId == Guid.Empty)
            return ContractErrors.PricingTier.ContractIdRequired;

        if (durationMonths <= 0)
            return ContractErrors.PricingTier.InvalidDuration;

        if (tierPrice < 0)
            return ContractErrors.PricingTier.InvalidPrice;

        if (monthlyListPrice < 0)
            return ContractErrors.PricingTier.InvalidMonthlyListPrice;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.PricingTier.InvalidCurrency;

        return new ContractPricingTier(
            id,
            contractId,
            durationMonths,
            tierPrice,
            currencyCode.Trim().ToUpperInvariant(),
            monthlyListPrice,
            displayOrder);
    }
}
