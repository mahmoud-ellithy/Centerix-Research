namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// TASK 9 — Promotion free months &amp; additional benefits (Domain layer).
///
/// Pure domain tests — no database, no EF, no HTTP. Every assertion runs against
/// <see cref="Promotion"/>, <see cref="PromotionCalculationService"/> and
/// <see cref="CalculatedOffer"/> only.
///
/// Guarantees proven here:
///   * T9-D01..D03  the new types grant an entitlement without changing the charged amount
///   * T9-D04..D09  required-field and mixed-configuration validation
///   * T9-D10..D11  the Contract gift invariant (3 x monthly) and the term bound on free months
///   * T9-D12..D14  the entitlement is never derived from Plan.BonusMonths, discount or runtime state
///   * T9-D15..D16  PayForXMonths difference handling (opt-in) and unchanged default behaviour
///   * T9-D17       a non-applicable promotion grants nothing
/// </summary>
public class Task9_PromotionBenefitsDomainTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IPromotionCalculationService Service() => new PromotionCalculationService();

    private static Plan NewPlan(
        decimal monthlyPrice = 1000m,
        string currencyCode = "EGP",
        int durationMonths = 12,
        int bonusMonths = 0) =>
        Plan.Create(
            id: 1,
            code: "ENT",
            displayName: "Enterprise",
            monthlyPrice: monthlyPrice,
            maxStudents: 100,
            maxUsers: 50,
            maxBranches: 10,
            maxTeachers: 20,
            storageGB: 100,
            smsQuota: 1000,
            isActive: true,
            currencyCode: currencyCode,
            durationMonths: durationMonths,
            bonusMonths: bonusMonths).Value;

    /// <summary>
    /// A concrete, explicitly configured benefit rule. Benefit-bearing promotions must carry one;
    /// there is no generated default.
    /// </summary>
    private static EligibilityRule DefaultBenefitRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m));

    private static Promotion NewPromotion(
        PromotionType type,
        int durationMonths = 12,
        int planId = 1,
        decimal? percentage = null,
        decimal? fixedAmount = null,
        decimal? promotionalPrice = null,
        int? chargedMonths = null,
        int? freeMonthsCount = null,
        string? benefitName = null,
        string? benefitDescription = null,
        decimal? benefitValue = null,
        ContractBenefitType? benefitType = null,
        string? benefitCurrencyCode = null,
        EligibilityRule? benefitEligibilityRule = null,
        bool activate = true)
    {
        var result = Promotion.Create(
            id: 7,
            name: "T9 promotion",
            type: type,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: Start,
            endsAtUtc: End,
            freeMonthsCount: freeMonthsCount,
            benefitName: benefitName,
            benefitDescription: benefitDescription,
            benefitValue: benefitValue,
            benefitType: benefitType,
            benefitCurrencyCode: benefitCurrencyCode,
            benefitEligibilityRule: benefitEligibilityRule,
            percentage: percentage,
            fixedAmount: fixedAmount,
            promotionalPrice: promotionalPrice,
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess, result.Errors is null ? string.Empty : string.Join(";", result.Errors.Select(e => e.Description)));
        var promotion = result.Value;
        if (activate)
            Assert.True(promotion.Activate().IsSuccess);
        return promotion;
    }

    private static Promotion FreeMonthsPromotion(int freeMonths = 2, int durationMonths = 12) =>
        NewPromotion(
            PromotionType.FreeMonthsBonus,
            durationMonths: durationMonths,
            freeMonthsCount: freeMonths,
            benefitEligibilityRule: DefaultBenefitRule());

    private static Promotion AdditionalBenefitPromotion(
        decimal value = 500m,
        string currency = "EGP",
        int durationMonths = 12) =>
        NewPromotion(
            PromotionType.AdditionalBenefits,
            durationMonths: durationMonths,
            benefitName: "Barcode Printer",
            benefitDescription: "Free barcode printer",
            benefitValue: value,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: currency,
            benefitEligibilityRule: DefaultBenefitRule());

    // ─────────────────────────────────────────────────────────────────
    // T9-D01 — Free months are granted and never change the charged amount
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D01_FreeMonthsBonus_GrantsMonths_AndLeavesAmountsUntouched()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        var promotion = FreeMonthsPromotion(freeMonths: 2);

        var result = Service().Calculate(plan, 12, Now, [promotion]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;

        // Entitlement granted
        Assert.Equal(2, offer.FreeMonths);
        Assert.True(offer.HasFreeMonths);
        Assert.False(offer.HasAdditionalBenefit);

        // The charged amount is untouched — a free month is not a discount.
        Assert.Equal(12000m, offer.BaseAmount);
        Assert.Equal(0m, offer.DiscountAmount);
        Assert.Equal(12000m, offer.FinalAmount);
        Assert.Equal("FreeMonthsBonus", offer.PromotionType);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D02 — Additional benefit carries the full gift data
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D02_AdditionalBenefits_CarriesBenefitData_AndLeavesAmountsUntouched()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        var promotion = AdditionalBenefitPromotion(value: 750.50m);

        var result = Service().Calculate(plan, 12, Now, [promotion]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;

        Assert.Equal("Barcode Printer", offer.BenefitName);
        Assert.Equal("Free barcode printer", offer.BenefitDescription);
        Assert.Equal(750.50m, offer.BenefitValue);
        Assert.Equal(ContractBenefitType.PhysicalGift, offer.BenefitType);
        Assert.Equal("EGP", offer.BenefitCurrencyCode);
        Assert.True(offer.HasAdditionalBenefit);
        Assert.False(offer.HasFreeMonths);

        Assert.Equal(12000m, offer.BaseAmount);
        Assert.Equal(0m, offer.DiscountAmount);
        Assert.Equal(12000m, offer.FinalAmount);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D03 — The four original promotion types keep working and grant nothing
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D03_ExistingPromotionTypes_StillCalculate_AndGrantNoEntitlement()
    {
        var plan = NewPlan(monthlyPrice: 1000m);

        var percentage = Service().Calculate(plan, 3, Now, [NewPromotion(PromotionType.PercentageDiscount, durationMonths: 3, percentage: 10m)]);
        var fixedAmount = Service().Calculate(plan, 3, Now, [NewPromotion(PromotionType.FixedAmountDiscount, durationMonths: 3, fixedAmount: 500m)]);
        var payForX = Service().Calculate(plan, 12, Now, [NewPromotion(PromotionType.PayForXMonths, durationMonths: 12, chargedMonths: 10)]);
        var price = Service().Calculate(plan, 6, Now, [NewPromotion(PromotionType.PromotionalPrice, durationMonths: 6, promotionalPrice: 4900m)]);

        Assert.True(percentage.IsSuccess);
        Assert.Equal(2700m, percentage.Value.FinalAmount);
        Assert.Null(percentage.Value.FreeMonths);
        Assert.Null(percentage.Value.BenefitName);

        Assert.True(fixedAmount.IsSuccess);
        Assert.Equal(2500m, fixedAmount.Value.FinalAmount);
        Assert.Null(fixedAmount.Value.FreeMonths);

        Assert.True(payForX.IsSuccess);
        Assert.Equal(10000m, payForX.Value.FinalAmount);
        Assert.Equal(10, payForX.Value.ChargedMonths);

        Assert.True(price.IsSuccess);
        Assert.Equal(4900m, price.Value.FinalAmount);
        Assert.Null(price.Value.BenefitValue);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D04..D09 — Configuration validation
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D04_FreeMonthsBonus_WithoutFreeMonthsCount_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.FreeMonthsBonus, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void T9_D05_AdditionalBenefits_WithoutName_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitValue: 100m, benefitType: ContractBenefitType.PhysicalGift, benefitCurrencyCode: "EGP");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void T9_D06_AdditionalBenefits_WithoutValue_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitName: "Gift", benefitType: ContractBenefitType.PhysicalGift, benefitCurrencyCode: "EGP");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void T9_D07_AdditionalBenefits_NegativeValue_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitName: "Gift", benefitValue: -1m, benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void T9_D08_AdditionalBenefits_InvalidCurrency_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitName: "Gift", benefitValue: 100m, benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EG");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void T9_D09_MixedFreeMonthsAndBenefit_Rejected()
    {
        var result = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            freeMonthsCount: 2,
            benefitName: "Gift", benefitValue: 100m, benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        Assert.False(result.IsSuccess);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D10 — A benefit above 3 x monthly value is rejected at calculation time
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D10_BenefitValueAboveThreeMonthlyValue_Rejected()
    {
        // Monthly 1000 => cap is 3000. 3000 is allowed, 3000.01 is not.
        var atCap = Service().Calculate(
            NewPlan(monthlyPrice: 1000m), 12, Now, [AdditionalBenefitPromotion(value: 3000m)]);
        Assert.True(atCap.IsSuccess);

        var overCap = Service().Calculate(
            NewPlan(monthlyPrice: 1000m), 12, Now, [AdditionalBenefitPromotion(value: 3000.01m)]);

        Assert.False(overCap.IsSuccess);
        Assert.Contains(overCap.Errors!, e => e.Code == "Promotion.BenefitValue_ExceedsMaximum");
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D11 — Free months may never exceed the purchased term
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D11_FiveFreeMonthsOnThreeMonthContract_Rejected()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        var promotion = FreeMonthsPromotion(freeMonths: 5, durationMonths: 3);

        var result = Service().Calculate(plan, 3, Now, [promotion]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.FreeMonths_ExceedDuration");
    }

    [Fact]
    public void T9_D11b_FreeMonthsEqualToDuration_IsAllowed()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        var promotion = FreeMonthsPromotion(freeMonths: 3, durationMonths: 3);

        var result = Service().Calculate(plan, 3, Now, [promotion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.FreeMonths);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D12 — The entitlement is NOT derived from Plan.BonusMonths
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D12_PlanBonusMonths_DoNotLeakIntoPromotionFreeMonths()
    {
        var plan = NewPlan(monthlyPrice: 1000m, bonusMonths: 3);

        // A discount-only promotion on a plan that grants 3 bonus months must NOT
        // produce a promotion-granted free months entitlement.
        var discount = Service().Calculate(
            plan, 12, Now, [NewPromotion(PromotionType.PercentageDiscount, durationMonths: 12, percentage: 10m)]);

        Assert.True(discount.IsSuccess);
        Assert.Null(discount.Value.FreeMonths);
        Assert.False(discount.Value.HasFreeMonths);

        // No promotion at all — still nothing.
        var noPromotion = Service().Calculate(plan, 12, Now, []);
        Assert.True(noPromotion.IsSuccess);
        Assert.Null(noPromotion.Value.FreeMonths);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D13 — The grant is independent of the discount that accompanies it
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D13_DiscountAndFreeMonthsAreIndependentAxes()
    {
        // A 10% discount promotion that also tries to configure 2 free months.
        var plan = NewPlan(monthlyPrice: 1000m);

        // PercentageDiscount does not carry the benefit config path, so the configuration is
        // rejected outright rather than silently half-applied.
        var creation = Promotion.Create(
            id: 7, name: "T9", type: PromotionType.PercentageDiscount, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, percentage: 10m, freeMonthsCount: 2);
        Assert.False(creation.IsSuccess);
        Assert.Contains(creation.Errors!, e => e.Code == "Promotion.BenefitConfig_NotSupportedForType");

        // The two axes are still independent in the calculated offer: an AdditionalBenefits
        // promotion never changes the amount, and a discount never grants an entitlement.
        var benefit = Service().Calculate(plan, 12, Now, [AdditionalBenefitPromotion(value: 200m)]);
        Assert.Equal(12000m, benefit.Value.FinalAmount);
        Assert.Equal(200m, benefit.Value.BenefitValue);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D14 — The entitlement carries a rule built from the offer, not the Plan
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D14_EntitlementCarriesExactlyTheConfiguredPromotionRule()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        // A deliberately distinctive rule so the assertion cannot pass by coincidence.
        var configured = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(7777m));
        var promotion = NewPromotion(
            PromotionType.FreeMonthsBonus, freeMonthsCount: 2, benefitEligibilityRule: configured);

        var result = Service().Calculate(plan, 12, Now, [promotion]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.EntitlementEligibilityRule);

        // The carried rule is the configured one, unmodified — not a regenerated default.
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(result.Value.EntitlementEligibilityRule!));

        // The rule is deterministic: the same inputs always produce the same rule snapshot.
        var again = Service().Calculate(plan, 12, Now, [promotion]);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(result.Value.EntitlementEligibilityRule!),
            EligibilityRuleSerializer.Serialize(again.Value.EntitlementEligibilityRule!));

        // Changing the Plan does not change the rule the promotion carries.
        var changedPlan = NewPlan(monthlyPrice: 5000m, durationMonths: 24);
        var afterPlanChange = Service().Calculate(changedPlan, 12, Now, [promotion]);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(afterPlanChange.Value.EntitlementEligibilityRule!));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D15..D16 — PayForXMonths difference handling
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D15_PayForXMonths_WithOptIn_GrantsTheDifferenceAsFreeMonths()
    {
        var plan = NewPlan(monthlyPrice: 1000m);

        // Pay 10 get 12 => 2 extra months. Opted in, so a rule is configured.
        var promotion = NewPromotion(
            PromotionType.PayForXMonths, durationMonths: 12, chargedMonths: 10, freeMonthsCount: 1,
            benefitEligibilityRule: DefaultBenefitRule());

        var result = Service().Calculate(plan, 12, Now, [promotion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value.ChargedMonths);
        Assert.Equal(10000m, result.Value.FinalAmount);

        // The granted months are the real difference, NOT the configured counter.
        Assert.Equal(2, result.Value.FreeMonths);
    }

    [Fact]
    public void T9_D16_PayForXMonths_WithoutOptIn_GrantsNothing()
    {
        var plan = NewPlan(monthlyPrice: 1000m);
        var promotion = NewPromotion(PromotionType.PayForXMonths, durationMonths: 12, chargedMonths: 10);

        var result = Service().Calculate(plan, 12, Now, [promotion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(10000m, result.Value.FinalAmount);
        Assert.Null(result.Value.FreeMonths);
        Assert.False(result.Value.HasFreeMonths);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-D17 — A promotion that is not applicable grants nothing
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_D17_DraftAndExpiredPromotions_GrantNoEntitlement()
    {
        var plan = NewPlan(monthlyPrice: 1000m);

        var draft = NewPromotion(
            PromotionType.FreeMonthsBonus, freeMonthsCount: 2,
            benefitEligibilityRule: DefaultBenefitRule(), activate: false);
        var draftResult = Service().Calculate(plan, 12, Now, [draft]);
        Assert.True(draftResult.IsSuccess);
        Assert.Null(draftResult.Value.PromotionId);
        Assert.Null(draftResult.Value.FreeMonths);
        Assert.Equal("None", draftResult.Value.PromotionType);

        var expired = Promotion.Create(
            id: 8, name: "Expired", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            benefitName: "Gift", benefitValue: 100m, benefitType: ContractBenefitType.Service,
            benefitCurrencyCode: "EGP",
            benefitEligibilityRule: DefaultBenefitRule()).Value;
        Assert.True(expired.Activate().IsSuccess);

        var expiredResult = Service().Calculate(plan, 12, Now, [expired]);
        Assert.True(expiredResult.IsSuccess);
        Assert.Null(expiredResult.Value.BenefitName);
        Assert.Null(expiredResult.Value.BenefitValue);
    }
}
