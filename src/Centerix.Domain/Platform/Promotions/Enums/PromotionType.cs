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
    PromotionalPrice = 3,

    /// <summary>
    /// Grants extra free months on top of the purchased term. The number of free months is
    /// configured per promotion (<c>Promotion.FreeMonthsCount</c>) and is snapshotted onto the
    /// calculated Offer as a gated <c>OfferFreeMonthsBenefit</c>. Does not change the charged amount.
    /// </summary>
    FreeMonthsBonus = 4,

    /// <summary>
    /// Grants an additional benefit/gift (e.g. physical gift, service) on top of the purchased
    /// subscription. The benefit is configured per promotion (name, description, value, currency,
    /// type) and is snapshotted onto the calculated Offer as a gated <c>OfferBenefit</c>.
    /// Does not change the charged amount.
    /// </summary>
    AdditionalBenefits = 5
}
