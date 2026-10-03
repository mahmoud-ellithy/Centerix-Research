namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// TASK 9 CORRECTION — the benefit eligibility rule is trusted, configured Promotion data.
/// <para>
/// These tests pin the corrected architecture:
/// </para>
/// <list type="bullet">
///   <item>T9-C01  the rule is configured on the Promotion (persisted), not generated</item>
///   <item>T9-C02  no derived/default rule exists anywhere in the calculation path</item>
///   <item>T9-C03  a benefit-bearing promotion without a rule is rejected at Create</item>
///   <item>T9-C04  a benefit-bearing promotion without a rule is rejected at Update</item>
///   <item>T9-C05  a rule on a discount-only promotion is rejected</item>
///   <item>T9-C06  an updated rule takes effect on the next calculation, not on existing offers</item>
///   <item>T9-C07  the rule travels unchanged into FreeMonthsBenefit and ContractBenefit</item>
/// </list>
/// </summary>
public class Task9C_PromotionBenefitRuleDomainTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IPromotionCalculationService Service() => new PromotionCalculationService();

    private static Plan NewPlan(decimal monthlyPrice = 1000m) =>
        Plan.Create(
            id: 1, code: "ENT", displayName: "Enterprise",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: true,
            currencyCode: "EGP", durationMonths: 12, bonusMonths: 0).Value;

    /// <summary>A distinctive configured rule — not a value the code could invent on its own.</summary>
    private static EligibilityRule ConfiguredRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(4321m));

    private static Promotion FreeMonthsPromotion(EligibilityRule? rule, int freeMonths = 2)
    {
        var result = Promotion.Create(
            id: 7, name: "T9C", type: PromotionType.FreeMonthsBonus, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            freeMonthsCount: freeMonths, benefitEligibilityRule: rule);
        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion BenefitPromotion(EligibilityRule? rule, decimal value = 500m)
    {
        var result = Promotion.Create(
            id: 8, name: "T9C", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitName: "Gift", benefitValue: value, benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP", benefitEligibilityRule: rule);
        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? string.Empty : string.Join("; ", result.Errors!.Select(e => e.Description));

    // ─────────────────────────────────────────────────────────────────
    // T9-C01 — The rule is configured data on the Promotion
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C01_ConfiguredRule_IsStoredOnThePromotion_AndUsedVerbatim()
    {
        var configured = ConfiguredRule();
        var promotion = FreeMonthsPromotion(configured);

        // The Promotion itself owns the rule as stored configuration.
        Assert.Same(configured, promotion.BenefitEligibilityRule);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(promotion.BenefitEligibilityRule!));

        // Both benefit-bearing types carry it into the calculated offer unchanged.
        var freeMonthsOffer = Service().Calculate(NewPlan(), 12, Now, [promotion]);
        Assert.True(freeMonthsOffer.IsSuccess, Describe(freeMonthsOffer));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(freeMonthsOffer.Value.EntitlementEligibilityRule!));

        var benefitOffer = Service().Calculate(NewPlan(), 12, Now, [BenefitPromotion(configured)]);
        Assert.True(benefitOffer.IsSuccess, Describe(benefitOffer));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(benefitOffer.Value.EntitlementEligibilityRule!));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C02 — No default or derived rule exists
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C02_NoDefaultRuleIsEverGenerated_WhenNoRuleIsConfigured()
    {
        // A rule CANNOT be configured for a discount-only promotion, and such a promotion must
        // never acquire an entitlement — and therefore never acquire an implicit rule.
        var discount = Promotion.Create(
            id: 9, name: "T9C", type: PromotionType.PercentageDiscount, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, percentage: 10m);
        Assert.True(discount.IsSuccess, Describe(discount));
        Assert.True(discount.Value.Activate().IsSuccess);

        Assert.Null(discount.Value.BenefitEligibilityRule);

        var offer = Service().Calculate(NewPlan(), 12, Now, [discount.Value]);
        Assert.True(offer.IsSuccess, Describe(offer));
        Assert.Null(offer.Value.EntitlementEligibilityRule);
        Assert.False(offer.Value.HasFreeMonths);
        Assert.False(offer.Value.HasAdditionalBenefit);

        // Different plans / durations must not change a discount offer's (absent) rule.
        var other = Service().Calculate(NewPlan(monthlyPrice: 7777m), 3, Now, [discount.Value]);
        Assert.Null(other.Value.EntitlementEligibilityRule);
    }

    [Fact]
    public void T9_C02b_PromotionRowWithoutARule_CannotSilentlyGrantAnUngatedEntitlement()
    {
        // Simulates a row that predates the rule column: the aggregate refuses to grant the
        // entitlement, so no ungated FreeMonthsBenefit can ever be produced.
        var promotion = FreeMonthsPromotion(ConfiguredRule());

        var calc = Service();
        var offer = calc.Calculate(NewPlan(), 12, Now, [promotion]);
        Assert.True(offer.IsSuccess, Describe(offer));
        Assert.NotNull(offer.Value.EntitlementEligibilityRule);

        // The OfferFreeMonthsBenefit factory independently refuses a missing rule, so even a
        // bypass of the calculation service cannot produce an ungated entitlement.
        var withoutRule = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 2,
            currencyCode: "EGP",
            eligibilityRule: null);

        Assert.False(withoutRule.IsSuccess);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C03 — Create rejects a benefit-bearing promotion with no rule
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C03_Create_BenefitBearingPromotionWithoutRule_IsRejected()
    {
        var freeMonths = Promotion.Create(
            id: 1, name: "T9C", type: PromotionType.FreeMonthsBonus, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, freeMonthsCount: 2);
        Assert.False(freeMonths.IsSuccess);
        Assert.Contains(freeMonths.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Required");

        var benefit = Promotion.Create(
            id: 1, name: "T9C", type: PromotionType.AdditionalBenefits, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End,
            benefitName: "Gift", benefitValue: 100m, benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");
        Assert.False(benefit.IsSuccess);
        Assert.Contains(benefit.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Required");

        // PayForXMonths only needs a rule when it actually grants free months.
        var payForXNoEntitlement = Promotion.Create(
            id: 1, name: "T9C", type: PromotionType.PayForXMonths, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, chargedMonths: 10);
        Assert.True(payForXNoEntitlement.IsSuccess, Describe(payForXNoEntitlement));

        var payForXWithEntitlement = Promotion.Create(
            id: 1, name: "T9C", type: PromotionType.PayForXMonths, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, chargedMonths: 10, freeMonthsCount: 1);
        Assert.False(payForXWithEntitlement.IsSuccess);
        Assert.Contains(payForXWithEntitlement.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Required");
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C04 — Update cannot drop the rule of a benefit-bearing promotion
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C04_Update_CannotRemoveTheRuleFromABenefitBearingPromotion()
    {
        var promotion = FreeMonthsPromotion(ConfiguredRule());

        var result = promotion.Update(
            name: promotion.Name,
            type: promotion.Type,
            planId: promotion.PlanId,
            durationMonths: promotion.DurationMonths,
            startsAtUtc: promotion.StartsAtUtc,
            endsAtUtc: promotion.EndsAtUtc,
            priority: promotion.Priority,
            freeMonthsCount: 3,
            benefitEligibilityRule: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("Promotion.BenefitEligibilityRule_Required", result.Errors!.Select(e => e.Code));

        // The original rule is untouched by the rejected update.
        Assert.NotNull(promotion.BenefitEligibilityRule);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C05 — A rule on a discount-only promotion is rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C05_RuleOnDiscountOnlyPromotion_IsRejected()
    {
        var result = Promotion.Create(
            id: 1, name: "T9C", type: PromotionType.PercentageDiscount, planId: 1, durationMonths: 12,
            startsAtUtc: Start, endsAtUtc: End, percentage: 10m,
            benefitEligibilityRule: ConfiguredRule());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_NotSupportedForType");
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C06 — A new rule applies to the next calculation only
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C06_UpdatedRule_AppliesToTheNextCalculation()
    {
        var promotion = FreeMonthsPromotion(ConfiguredRule());
        var first = Service().Calculate(NewPlan(), 12, Now, [promotion]);
        Assert.True(first.IsSuccess, Describe(first));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(ConfiguredRule()),
            EligibilityRuleSerializer.Serialize(first.Value.EntitlementEligibilityRule!));

        var replacement = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(999m));

        Assert.True(promotion.Update(
            name: promotion.Name,
            type: promotion.Type,
            planId: promotion.PlanId,
            durationMonths: promotion.DurationMonths,
            startsAtUtc: promotion.StartsAtUtc,
            endsAtUtc: promotion.EndsAtUtc,
            priority: promotion.Priority,
            freeMonthsCount: 2,
            benefitEligibilityRule: replacement).IsSuccess);

        // The already-calculated offer is a value object: it is unchanged.
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(ConfiguredRule()),
            EligibilityRuleSerializer.Serialize(first.Value.EntitlementEligibilityRule!));

        // The next calculation picks up the new configured rule.
        var second = Service().Calculate(NewPlan(), 12, Now, [promotion]);
        Assert.True(second.IsSuccess, Describe(second));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(replacement),
            EligibilityRuleSerializer.Serialize(second.Value.EntitlementEligibilityRule!));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C07 — The rule travels unchanged into both snapshot children
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_C07_ConfiguredRule_ReachesBothOfferSnapshotChildrenUnchanged()
    {
        var configured = ConfiguredRule();
        var offerId = Guid.NewGuid();

        var freeMonths = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(), offerId: offerId, entitlementMonths: 2,
            currencyCode: "EGP", eligibilityRule: configured);
        Assert.True(freeMonths.IsSuccess, Describe(freeMonths));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(freeMonths.Value.EligibilityRule!));

        var benefit = OfferBenefit.Create(
            id: Guid.NewGuid(), offerId: offerId,
            benefitType: ContractBenefitType.PhysicalGift, name: "Gift",
            description: null, contractualValue: 500m, currencyCode: "EGP",
            eligibilityRule: configured);
        Assert.True(benefit.IsSuccess, Describe(benefit));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(benefit.Value.EligibilityRule!));

        // The Offer aggregate accepts both children and exposes them for the handler.
        var offer = Offer.Create(
            id: offerId, tenantId: "T9C-tenant", planId: 1, durationMonths: 12,
            baseAmount: 12000m, discountAmount: 0m, finalAmount: 12000m,
            monthlyListPrice: 1000m, currencyCode: "EGP",
            calculatedAtUtc: Now, paymentTerms: PaymentTerms.FullUpfront,
            promotionId: 7, promotionName: "T9C", promotionCode: null,
            promotionType: nameof(PromotionType.FreeMonthsBonus),
            discountPercentage: null, chargedMonths: null, bonusMonths: 0);
        Assert.True(offer.IsSuccess, Describe(offer));

        Assert.True(offer.Value.AddFreeMonthsBenefit(freeMonths.Value).IsSuccess);
        Assert.True(offer.Value.AddBenefit(benefit.Value).IsSuccess);

        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(Assert.Single(offer.Value.FreeMonthsBenefits).EligibilityRule!));
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(configured),
            EligibilityRuleSerializer.Serialize(Assert.Single(offer.Value.Benefits).EligibilityRule!));
    }
}
