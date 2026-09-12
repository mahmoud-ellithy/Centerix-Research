namespace Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Types of commercial promotions that can be applied to a Plan/Contract.
/// Each type has specific calculation semantics and required fields.
/// </summary>
public enum PromotionType : byte
{
    /// <summary>Percentage discount on the base amount (e.g., 10% off).</summary>
    PercentageDiscount = 0,

    /// <summary>Fixed monetary discount on the base amount (e.g., 500 off).</summary>
    FixedAmountDiscount = 1,

    /// <summary>Customer pays for fewer months than the contract duration (e.g., pay 10 get 12).</summary>
    PayForXMonths = 2,

    /// <summary>Explicit promotional final price (e.g., normal 5220 → promotional 4900).</summary>
    PromotionalPrice = 3
}
