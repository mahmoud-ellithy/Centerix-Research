namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Plans;

/// <summary>
/// Deterministic service for calculating commercial offers by applying Promotions to Plans.
/// No stacking: selects one highest-priority eligible promotion.
///
/// Authoritative pricing rule: when a Plan has an applicable PricingTier for the requested
/// duration, the tier price IS the base amount. The promotion is then applied to the tier price.
/// When no tier exists, falls back to MonthlyPrice × DurationMonths.
/// </summary>
public sealed class PromotionCalculationService : IPromotionCalculationService
{
    public Result<CalculatedOffer> Calculate(
        Plan plan,
        int durationMonths,
        DateTime evaluationTime,
        IReadOnlyList<Promotion> eligiblePromotions)
    {
        if (plan is null)
            return PromotionErrors.PlanNotFound;

        if (!plan.IsActive)
            return PromotionErrors.PlanInactive;

        if (durationMonths <= 0)
            return Error.Validation("Offer.Duration_Invalid", "Duration must be at least one month");

        // Authoritative pricing: use PlanPricingTier if available, otherwise MonthlyPrice × Duration
        var applicableTier = plan.GetPricingTierForDuration(durationMonths);
        decimal baseAmount;
        if (applicableTier is not null)
        {
            baseAmount = applicableTier.TierPrice;
        }
        else
        {
            baseAmount = plan.MonthlyPrice * durationMonths;
        }

        var now = DateTime.UtcNow;

        // Find the best eligible promotion (highest priority, then earliest created)
        var bestPromotion = eligiblePromotions
            .Where(p => p.IsApplicableAt(evaluationTime))
            .Where(p => p.PlanId == 0 || p.PlanId == plan.Id)
            .Where(p => p.DurationMonths == 0 || p.DurationMonths == durationMonths)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

        if (bestPromotion is null)
        {
            return new CalculatedOffer
            {
                PlanId = plan.Id,
                DurationMonths = durationMonths,
                BaseAmount = baseAmount,
                DiscountAmount = 0,
                FinalAmount = baseAmount,
                MonthlyListPrice = plan.MonthlyPrice,
                CurrencyCode = plan.CurrencyCode,
                PromotionType = "None",
                CalculatedAtUtc = now
            };
        }

        return ApplyPromotion(plan, durationMonths, baseAmount, bestPromotion, now);
    }

    private static Result<CalculatedOffer> ApplyPromotion(
        Plan plan,
        int durationMonths,
        decimal baseAmount,
        Promotion promotion,
        DateTime now)
    {
        decimal discountAmount;
        decimal finalAmount;
        decimal? discountPercentage = null;
        int? chargedMonths = null;

        switch (promotion.Type)
        {
            case PromotionType.PercentageDiscount:
                discountAmount = Math.Round(baseAmount * promotion.Percentage!.Value / 100m, 2, MidpointRounding.AwayFromZero);
                finalAmount = baseAmount - discountAmount;
                discountPercentage = promotion.Percentage;
                break;

            case PromotionType.FixedAmountDiscount:
                discountAmount = promotion.FixedAmount!.Value;
                if (discountAmount > baseAmount)
                    return Error.Validation("Promotion.Discount_ExceedsBase",
                        $"Fixed discount ({discountAmount}) cannot exceed base amount ({baseAmount})");
                finalAmount = baseAmount - discountAmount;
                break;

            case PromotionType.PayForXMonths:
                chargedMonths = promotion.ChargedMonths!.Value;
                if (chargedMonths > durationMonths)
                    return Error.Validation("Promotion.ChargedMonths_ExceedsDuration",
                        $"Charged months ({chargedMonths}) cannot exceed duration ({durationMonths})");
                finalAmount = plan.MonthlyPrice * chargedMonths.Value;
                discountAmount = baseAmount - finalAmount;
                break;

            case PromotionType.PromotionalPrice:
                finalAmount = promotion.PromotionalPrice!.Value;
                if (finalAmount > baseAmount)
                    return Error.Validation("Promotion.PromotionalPrice_ExceedsBase",
                        $"Promotional price ({finalAmount}) cannot exceed base amount ({baseAmount})");
                discountAmount = baseAmount - finalAmount;
                if (baseAmount > 0)
                    discountPercentage = Math.Round(discountAmount / baseAmount * 100m, 2, MidpointRounding.AwayFromZero);
                break;

            default:
                return Error.Failure("Promotion.UnknownType", $"Unknown promotion type: {promotion.Type}");
        }

        if (discountAmount < 0) discountAmount = 0;
        if (finalAmount < 0) finalAmount = 0;

        return new CalculatedOffer
        {
            PlanId = plan.Id,
            DurationMonths = durationMonths,
            BaseAmount = baseAmount,
            DiscountAmount = discountAmount,
            FinalAmount = finalAmount,
            PromotionId = promotion.Id,
            PromotionName = promotion.Name,
            PromotionCode = promotion.Code,
            PromotionType = promotion.Type.ToString(),
            DiscountPercentage = discountPercentage,
            ChargedMonths = chargedMonths,
            MonthlyListPrice = plan.MonthlyPrice,
            CurrencyCode = plan.CurrencyCode,
            CalculatedAtUtc = now
        };
    }
}
