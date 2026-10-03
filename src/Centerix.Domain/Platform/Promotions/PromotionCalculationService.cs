namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
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

            case PromotionType.FreeMonthsBonus:
            case PromotionType.AdditionalBenefits:
                // Entitlement-only promotions: the customer pays the full amount and receives an
                // additional entitlement. They never reduce the charged amount.
                finalAmount = baseAmount;
                discountAmount = 0;
                break;

            default:
                return Error.Failure("Promotion.UnknownType", $"Unknown promotion type: {promotion.Type}");
        }

        if (discountAmount < 0) discountAmount = 0;
        if (finalAmount < 0) finalAmount = 0;

        // ---- Entitlement resolution (granted on top of the charged amount) ----
        // The granted entitlement is a pure function of the promotion configuration and the
        // calculated offer. It is NEVER derived from the Plan's BonusMonths, the current Plan
        // catalog, or any runtime environment value.
        var entitlement = ResolveEntitlement(plan, durationMonths, promotion, finalAmount);
        if (!entitlement.IsSuccess)
            return entitlement.Errors!;

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
            CalculatedAtUtc = now,
            FreeMonths = entitlement.Value.FreeMonths,
            BenefitName = entitlement.Value.BenefitName,
            BenefitDescription = entitlement.Value.BenefitDescription,
            BenefitValue = entitlement.Value.BenefitValue,
            BenefitType = entitlement.Value.BenefitType,
            BenefitCurrencyCode = entitlement.Value.BenefitCurrencyCode,
            EntitlementEligibilityRule = entitlement.Value.EligibilityRule
        };
    }

    /// <summary>
    /// Resolves the entitlement granted by a promotion, independently of the charged amount.
    /// </summary>
    private static Result<EntitlementResolution> ResolveEntitlement(
        Plan plan,
        int durationMonths,
        Promotion promotion,
        decimal finalAmount)
    {
        switch (promotion.Type)
        {
            case PromotionType.FreeMonthsBonus:
            {
                var freeMonths = promotion.FreeMonthsCount!.Value;
                return ValidateFreeMonths(freeMonths, durationMonths);
            }

            case PromotionType.PayForXMonths:
            {
                // Opt-in: PayForXMonths only grants free months when the promotion is explicitly
                // configured to do so (FreeMonthsCount set). The granted amount is the real
                // difference (DurationMonths - ChargedMonths), never the configured counter, so the
                // entitlement can never contradict the "pay X get Y" commercial promise.
                if (!promotion.FreeMonthsCount.HasValue)
                    return EntitlementResolution.Empty();

                var extraMonths = durationMonths - promotion.ChargedMonths!.Value;
                if (extraMonths <= 0)
                    return EntitlementResolution.Empty();

                return ValidateFreeMonths(extraMonths, durationMonths);
            }

            case PromotionType.AdditionalBenefits:
            {
                var benefitValue = promotion.BenefitValue!.Value;

                // The benefit value must fit the Contract gift invariant: total benefits may never
                // exceed three months of the subscription value. Enforced here because this is the
                // only place the Plan's monthly price is known.
                var threeMonthsValue = plan.MonthlyPrice * 3m;
                if (benefitValue > threeMonthsValue)
                    return Error.Validation("Promotion.BenefitValue_ExceedsMaximum",
                        $"Promotion benefit value ({benefitValue}) cannot exceed three months of the " +
                        $"plan's monthly value ({threeMonthsValue})");

                return new EntitlementResolution
                {
                    BenefitName = promotion.BenefitName,
                    BenefitDescription = promotion.BenefitDescription,
                    BenefitValue = benefitValue,
                    BenefitType = promotion.BenefitType,
                    BenefitCurrencyCode = promotion.BenefitCurrencyCode,
                    EligibilityRule = BuildEntitlementRule(finalAmount)
                };
            }

            default:
                // Discount-only promotions grant no entitlement.
                return EntitlementResolution.Empty();
        }
    }

    /// <summary>
    /// A free months entitlement may never exceed the purchased term it is attached to.
    /// A "5 free months" benefit on a 3-month contract is a configuration error, not a discount.
    /// </summary>
    private static Result<EntitlementResolution> ValidateFreeMonths(int freeMonths, int durationMonths)
    {
        if (freeMonths <= 0)
            return Error.Validation("Promotion.FreeMonths_Invalid", "Free months must be greater than 0");

        if (freeMonths > durationMonths)
            return Error.Validation("Promotion.FreeMonths_ExceedDuration",
                $"Free months ({freeMonths}) cannot exceed the contract duration ({durationMonths})");

        return new EntitlementResolution
        {
            FreeMonths = freeMonths,
            EligibilityRule = BuildEntitlementRule(0m)
        };
    }

    /// <summary>
    /// Builds the immutable commercial eligibility rule for a granted entitlement.
    /// Derived purely from the calculated offer — the current Plan catalog is never consulted, so
    /// the rule stays correct even after the Plan changes.
    /// </summary>
    private static EligibilityRule BuildEntitlementRule(decimal requiredPaidAmount) =>
        requiredPaidAmount > 0
            ? EligibilityRule.AllOf(
                EligibilityRule.ContractActive(),
                EligibilityRule.AmountPaidAtLeast(requiredPaidAmount))
            : EligibilityRule.ContractActive();

    private sealed record EntitlementResolution
    {
        public int? FreeMonths { get; init; }
        public string? BenefitName { get; init; }
        public string? BenefitDescription { get; init; }
        public decimal? BenefitValue { get; init; }
        public ContractBenefitType? BenefitType { get; init; }
        public string? BenefitCurrencyCode { get; init; }
        public EligibilityRule? EligibilityRule { get; init; }

        public static EntitlementResolution Empty() => new();
    }
}
