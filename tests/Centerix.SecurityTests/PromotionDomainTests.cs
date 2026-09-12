namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// CODER TASK 5 — Promotion & Offer Engine domain and calculation tests.
/// Validates:
/// 1. All four promotion types (Percentage, FixedAmount, PayForXMonths, PromotionalPrice)
/// 2. Negative/invalid discount rejection
/// 3. Discount > base rejection
/// 4. Promotional price > base rejection
/// 5. Invalid duration/date range rejection
/// 6. Draft/Disabled/Expired promotion cannot apply
/// 7. StartsAt/EndsAt boundary behavior
/// 8. Priority selection (deterministic)
/// 9. No stacking (single promotion selected)
/// 10. Critical commercial test (3 months 10% off)
/// 11. PayForXMonths semantic preservation
/// 12. Historical snapshot immutability
/// </summary>
public class PromotionDomainTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Plan CreatePlan(
        decimal monthlyPrice = 1000m,
        string currencyCode = "EGP",
        int durationMonths = 12,
        bool isActive = true)
    {
        var result = Plan.Create(
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
            isActive: isActive,
            currencyCode: currencyCode,
            durationMonths: durationMonths);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Promotion CreatePercentagePromotion(
        int id = 1,
        int planId = 1,
        int durationMonths = 3,
        decimal percentage = 10m,
        int priority = 10,
        PromotionStatus status = PromotionStatus.Active,
        DateTime? startsAt = null,
        DateTime? endsAt = null)
    {
        var start = startsAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = endsAt ?? new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = Promotion.Create(
            id: id,
            name: $"10% off {durationMonths} months",
            type: PromotionType.PercentageDiscount,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: start,
            endsAtUtc: end,
            priority: priority,
            percentage: percentage);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        if (status == PromotionStatus.Active)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
        }
        else if (status == PromotionStatus.Disabled)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
            var disableResult = promo.Deactivate();
            Assert.True(disableResult.IsSuccess);
        }

        return promo;
    }

    private static Promotion CreateFixedAmountPromotion(
        int id = 2,
        int planId = 1,
        int durationMonths = 3,
        decimal fixedAmount = 500m,
        int priority = 10,
        PromotionStatus status = PromotionStatus.Active)
    {
        var result = Promotion.Create(
            id: id,
            name: $"${fixedAmount} off {durationMonths} months",
            type: PromotionType.FixedAmountDiscount,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: priority,
            fixedAmount: fixedAmount);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        if (status == PromotionStatus.Active)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
        }
        else if (status == PromotionStatus.Disabled)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
            var disableResult = promo.Deactivate();
            Assert.True(disableResult.IsSuccess);
        }

        return promo;
    }

    private static Promotion CreatePayForXMonthsPromotion(
        int id = 3,
        int planId = 1,
        int durationMonths = 12,
        int chargedMonths = 10,
        int priority = 10,
        PromotionStatus status = PromotionStatus.Active)
    {
        var result = Promotion.Create(
            id: id,
            name: $"Pay {chargedMonths} get {durationMonths}",
            type: PromotionType.PayForXMonths,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: priority,
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        if (status == PromotionStatus.Active)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
        }
        else if (status == PromotionStatus.Disabled)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
            var disableResult = promo.Deactivate();
            Assert.True(disableResult.IsSuccess);
        }

        return promo;
    }

    private static Promotion CreatePromotionalPricePromotion(
        int id = 4,
        int planId = 1,
        int durationMonths = 6,
        decimal promotionalPrice = 4900m,
        int priority = 10,
        PromotionStatus status = PromotionStatus.Active)
    {
        var result = Promotion.Create(
            id: id,
            name: $"Special price {durationMonths} months",
            type: PromotionType.PromotionalPrice,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: priority,
            promotionalPrice: promotionalPrice);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        if (status == PromotionStatus.Active)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
        }
        else if (status == PromotionStatus.Disabled)
        {
            var activateResult = promo.Activate();
            Assert.True(activateResult.IsSuccess);
            var disableResult = promo.Deactivate();
            Assert.True(disableResult.IsSuccess);
        }

        return promo;
    }

    private static IPromotionCalculationService CreateService()
        => new PromotionCalculationService();

    private static DateTime UtcNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // ==================================================================
    // 1. PERCENTAGE DISCOUNT
    // ==================================================================

    [Fact]
    public void PercentageDiscount_10Percent_3Months_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(percentage: 10m, durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(300m, offer.DiscountAmount);
        Assert.Equal(2700m, offer.FinalAmount);
        Assert.Equal(10m, offer.DiscountPercentage);
        Assert.Equal(promo.Id, offer.PromotionId);
        Assert.Equal("PercentageDiscount", offer.PromotionType);
    }

    [Fact]
    public void PercentageDiscount_13Percent_6Months_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(percentage: 13m, durationMonths: 6);
        var service = CreateService();

        var result = service.Calculate(plan, 6, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(6000m, offer.BaseAmount);
        Assert.Equal(780m, offer.DiscountAmount);
        Assert.Equal(5220m, offer.FinalAmount);
    }

    // ==================================================================
    // 2. FIXED AMOUNT DISCOUNT
    // ==================================================================

    [Fact]
    public void FixedAmountDiscount_500_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreateFixedAmountPromotion(fixedAmount: 500m, durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(500m, offer.DiscountAmount);
        Assert.Equal(2500m, offer.FinalAmount);
        Assert.Equal("FixedAmountDiscount", offer.PromotionType);
    }

    // ==================================================================
    // 3. PAY-FOR-X-MONTHS
    // ==================================================================

    [Fact]
    public void PayForXMonths_10of12_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePayForXMonthsPromotion(durationMonths: 12, chargedMonths: 10);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(12000m, offer.BaseAmount);
        Assert.Equal(2000m, offer.DiscountAmount);
        Assert.Equal(10000m, offer.FinalAmount);
        Assert.Equal(10, offer.ChargedMonths);
        Assert.Equal("PayForXMonths", offer.PromotionType);
    }

    [Fact]
    public void PayForXMonths_PreservesSemanticDistinction()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePayForXMonthsPromotion(durationMonths: 12, chargedMonths: 10);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;

        // Duration = 12 months entitlement
        Assert.Equal(12, offer.DurationMonths);

        // Charged = 10 months
        Assert.Equal(10, offer.ChargedMonths);

        // Commercial amount = 10 × 1000 = 10,000
        Assert.Equal(10000m, offer.FinalAmount);

        // Should NOT be represented as 16.67% discount
        Assert.NotEqual(16.67m, offer.DiscountPercentage);
    }

    // ==================================================================
    // 4. PROMOTIONAL PRICE
    // ==================================================================

    [Fact]
    public void PromotionalPrice_4900_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotionalPricePromotion(durationMonths: 6, promotionalPrice: 4900m);
        var service = CreateService();

        var result = service.Calculate(plan, 6, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(6000m, offer.BaseAmount); // 1000 × 6
        Assert.Equal(1100m, offer.DiscountAmount); // 6000 - 4900 = 1100
        // Actually: normal 6-month price = 6000 (monthly × 6), promotional = 4900, discount = 1100
        Assert.Equal(4900m, offer.FinalAmount);
        Assert.Equal("PromotionalPrice", offer.PromotionType);
    }

    // ==================================================================
    // 5. NEGATIVE DISCOUNT REJECTED
    // ==================================================================

    [Fact]
    public void PercentageDiscount_NegativePercentage_Rejected()
    {
        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: -5m);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void PercentageDiscount_ZeroPercentage_Rejected()
    {
        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: 0m);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void FixedAmountDiscount_NegativeAmount_Rejected()
    {
        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.FixedAmountDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            fixedAmount: -100m);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 6. DISCOUNT > BASE REJECTED
    // ==================================================================

    [Fact]
    public void FixedAmountDiscount_ExceedsBase_Rejected()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreateFixedAmountPromotion(fixedAmount: 5000m, durationMonths: 3); // base = 3000
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void PercentageDiscount_Over100Percent_Rejected()
    {
        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: 150m);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 7. PROMOTIONAL PRICE > BASE REJECTED
    // ==================================================================

    [Fact]
    public void PromotionalPrice_ExceedsBase_Rejected()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotionalPricePromotion(durationMonths: 6, promotionalPrice: 7000m); // base = 6000
        var service = CreateService();

        var result = service.Calculate(plan, 6, UtcNow, [promo]);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 8. INVALID DURATION REJECTED
    // ==================================================================

    [Fact]
    public void CreatePromotion_NegativeDuration_Rejected()
    {
        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: -1,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: 10m);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void CalculateOffer_ZeroDuration_Rejected()
    {
        var plan = CreatePlan();
        var service = CreateService();

        var result = service.Calculate(plan, 0, UtcNow, []);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 9. INVALID DATE RANGE REJECTED
    // ==================================================================

    [Fact]
    public void CreatePromotion_StartAfterEnd_Rejected()
    {
        var start = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: start, endsAtUtc: end,
            percentage: 10m);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void CreatePromotion_StartEqualsEnd_Rejected()
    {
        var date = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = Promotion.Create(
            id: 1, name: "Bad", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: date, endsAtUtc: date,
            percentage: 10m);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 10. DRAFT PROMOTION CANNOT APPLY
    // ==================================================================

    [Fact]
    public void DraftPromotion_CannotApply()
    {
        var plan = CreatePlan();
        var promo = CreatePercentagePromotion(status: PromotionStatus.Draft);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId); // No promotion applied
        Assert.Equal("None", result.Value.PromotionType);
    }

    // ==================================================================
    // 11. DISABLED PROMOTION CANNOT APPLY
    // ==================================================================

    [Fact]
    public void DisabledPromotion_CannotApply()
    {
        var plan = CreatePlan();
        var promo = CreatePercentagePromotion(status: PromotionStatus.Disabled);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId);
        Assert.Equal("None", result.Value.PromotionType);
    }

    // ==================================================================
    // 12. EXPIRED PROMOTION CANNOT APPLY
    // ==================================================================

    [Fact]
    public void ExpiredPromotion_CannotApply()
    {
        var plan = CreatePlan();
        var promo = CreatePercentagePromotion(
            status: PromotionStatus.Active,
            startsAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // Already expired
        var service = CreateService();

        // Evaluation time is June 2026, but promotion ended Jan 2026
        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId);
        Assert.Equal("None", result.Value.PromotionType);
    }

    // ==================================================================
    // 13. STARTSAT BOUNDARY
    // ==================================================================

    [Fact]
    public void Promotion_AtStartsAt_IsActive()
    {
        var plan = CreatePlan();
        var startsAt = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var endsAt = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var promo = CreatePercentagePromotion(startsAt: startsAt, endsAt: endsAt);
        var service = CreateService();

        // Exactly at StartsAtUtc: should be active
        var result = service.Calculate(plan, 3, startsAt, [promo]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.PromotionId);
    }

    // ==================================================================
    // 14. ENDSAT BOUNDARY (exclusive)
    // ==================================================================

    [Fact]
    public void Promotion_AtEndsAt_IsInactive()
    {
        var plan = CreatePlan();
        var startsAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endsAt = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var promo = CreatePercentagePromotion(startsAt: startsAt, endsAt: endsAt);
        var service = CreateService();

        // Exactly at EndsAtUtc: should be inactive (exclusive end)
        var result = service.Calculate(plan, 3, endsAt, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId);
        Assert.Equal("None", result.Value.PromotionType);
    }

    // ==================================================================
    // 15. PRIORITY SELECTION
    // ==================================================================

    [Fact]
    public void MultiplePromotions_SelectsHighestPriority()
    {
        var plan = CreatePlan();
        var lowPriority = CreatePercentagePromotion(id: 1, percentage: 5m, priority: 1);
        var highPriority = CreatePercentagePromotion(id: 2, percentage: 15m, priority: 100);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [lowPriority, highPriority]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.PromotionId); // High priority wins
        Assert.Equal(15m, result.Value.DiscountPercentage);
    }

    [Fact]
    public void MultiplePromotions_SamePriority_SelectsByCreatedAt()
    {
        var plan = CreatePlan();
        var promo1 = CreatePercentagePromotion(id: 1, percentage: 5m, priority: 10);
        var promo2 = CreatePercentagePromotion(id: 2, percentage: 15m, priority: 10);
        var service = CreateService();

        // Both have same priority; lower Id (earlier created) wins due to ThenBy(p => p.Id)
        var result = service.Calculate(plan, 3, UtcNow, [promo2, promo1]);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.PromotionId); // Earlier created wins
    }

    // ==================================================================
    // 16. NO STACKING
    // ==================================================================

    [Fact]
    public void MultipleEligiblePromotions_NoStacking_SelectsOne()
    {
        var plan = CreatePlan();
        var promo1 = CreatePercentagePromotion(id: 1, percentage: 10m, priority: 10);
        var promo2 = CreatePercentagePromotion(id: 2, percentage: 20m, priority: 5);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo1, promo2]);

        Assert.True(result.IsSuccess);
        // Only one promotion applied (highest priority)
        Assert.Equal(10m, result.Value.DiscountPercentage);
        // Base = 3000, discount = 300 (only 10%), NOT 3000 * 30% = 900
        Assert.Equal(300m, result.Value.DiscountAmount);
        Assert.Equal(2700m, result.Value.FinalAmount);
    }

    // ==================================================================
    // 17. CRITICAL COMMERCIAL TEST
    // ==================================================================

    [Fact]
    public void CriticalCommercialTest_3Months_10Percent()
    {
        // Monthly = 1,000, 3 months = 3,000, 10% = 300, Final = 2,700
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(percentage: 10m, durationMonths: 3);
        var service = CreateService();

        var offerResult = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(offerResult.IsSuccess);
        var offer = offerResult.Value;

        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(300m, offer.DiscountAmount);
        Assert.Equal(2700m, offer.FinalAmount);
        Assert.Equal(1000m, offer.MonthlyListPrice);
        Assert.Equal("EGP", offer.CurrencyCode);
        Assert.Equal(promo.Id, offer.PromotionId);
    }

    // ==================================================================
    // 18. PAY-FOR-X-MONTHS TEST
    // ==================================================================

    [Fact]
    public void PayForXMonthsTest_12Duration_10Charged()
    {
        // Monthly = 1,000, Duration = 12, Charged = 10
        // Entitlement = 12 months, Charged = 10, Amount = 10,000
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePayForXMonthsPromotion(durationMonths: 12, chargedMonths: 10);
        var service = CreateService();

        var offerResult = service.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offerResult.IsSuccess);
        var offer = offerResult.Value;

        Assert.Equal(12, offer.DurationMonths); // 12 months entitlement
        Assert.Equal(10, offer.ChargedMonths); // 10 months charged
        Assert.Equal(12000m, offer.BaseAmount); // 12 × 1000
        Assert.Equal(10000m, offer.FinalAmount); // 10 × 1000
        Assert.Equal(2000m, offer.DiscountAmount);
    }

    // ==================================================================
    // 19. HISTORICAL SNAPSHOT TEST
    // ==================================================================

    [Fact]
    public void HistoricalSnapshot_PromotionChange_DoesNotAffectContract()
    {
        // Step 1: Create promotion
        var promo = CreatePercentagePromotion(percentage: 10m, durationMonths: 3);

        // Step 2: Calculate offer
        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();
        var offer = service.Calculate(plan, 3, UtcNow, [promo]).Value;

        // Step 3: Simulate Contract creation from offer
        var contractResult = Centerix.Domain.Platform.Contracts.Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-TEST-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths);

        Assert.True(contractResult.IsSuccess);
        var contract = contractResult.Value;

        // Step 4: Verify contract snapshot
        Assert.Equal(offer.FinalAmount, contract.ContractedAmount);
        Assert.Equal(offer.DiscountAmount, contract.DiscountAmount);
        Assert.Equal(offer.PromotionId, contract.PromotionId);
        Assert.Equal("PercentageDiscount", contract.PromotionType);

        // Step 5: Change promotion (10% -> 20%)
        var deactivateResult = promo.Deactivate();
        Assert.True(deactivateResult.IsSuccess);

        var newPromo = CreatePercentagePromotion(id: 99, percentage: 20m, durationMonths: 3);

        // Step 6: Recalculate current offer
        var newOffer = service.Calculate(plan, 3, UtcNow, [newPromo]).Value;

        // Step 7: Verify contract is UNCHANGED
        Assert.Equal(offer.FinalAmount, contract.ContractedAmount);
        Assert.Equal(offer.DiscountAmount, contract.DiscountAmount);
        Assert.Equal(offer.PromotionId, contract.PromotionId);
        Assert.Equal("PercentageDiscount", contract.PromotionType);
        Assert.Equal(2700m, contract.ContractedAmount); // Still 2700, not 2400

        // New offer would be different
        Assert.Equal(2400m, newOffer.FinalAmount); // 20% off 3000 = 2400
    }

    // ==================================================================
    // 20. PLAN SCOPE MATCHING
    // ==================================================================

    [Fact]
    public void PromotionForPlanA_DoesNotApplyToPlanB()
    {
        var planA = CreatePlan(monthlyPrice: 1000m);
        var planB = Plan.Create(
            id: 2, code: "BAS", displayName: "Basic",
            monthlyPrice: 500m, maxStudents: 50, maxUsers: 10,
            maxBranches: 2, maxTeachers: 5, storageGB: 10, smsQuota: 100,
            currencyCode: "EGP", durationMonths: 1).Value;

        var promo = CreatePercentagePromotion(planId: 1, percentage: 10m, durationMonths: 3);
        var service = CreateService();

        // Applies to Plan A
        var resultA = service.Calculate(planA, 3, UtcNow, [promo]);
        Assert.True(resultA.IsSuccess);
        Assert.NotNull(resultA.Value.PromotionId);

        // Does NOT apply to Plan B (planId mismatch)
        var resultB = service.Calculate(planB, 3, UtcNow, [promo]);
        Assert.True(resultB.IsSuccess);
        Assert.Null(resultB.Value.PromotionId);
    }

    [Fact]
    public void PromotionForDuration3_DoesNotApplyToDuration6()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(durationMonths: 3, percentage: 10m);
        var service = CreateService();

        // Applies to 3 months
        var result3 = service.Calculate(plan, 3, UtcNow, [promo]);
        Assert.True(result3.IsSuccess);
        Assert.NotNull(result3.Value.PromotionId);

        // Does NOT apply to 6 months
        var result6 = service.Calculate(plan, 6, UtcNow, [promo]);
        Assert.True(result6.IsSuccess);
        Assert.Null(result6.Value.PromotionId);
    }

    // ==================================================================
    // 21. INACTIVE PLAN REJECTED
    // ==================================================================

    [Fact]
    public void InactivePlan_CannotCalculateOffer()
    {
        var plan = CreatePlan(isActive: false);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, []);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 22. NO PROMOTION (full price)
    // ==================================================================

    [Fact]
    public void NoPromotion_ReturnsFullPrice()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, []);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(0m, offer.DiscountAmount);
        Assert.Equal(3000m, offer.FinalAmount);
        Assert.Null(offer.PromotionId);
        Assert.Equal("None", offer.PromotionType);
    }

    // ==================================================================
    // 23. PROMOTION LIFECYCLE
    // ==================================================================

    [Fact]
    public void PromotionLifecycle_Draft_CannotActivateTwice()
    {
        var promo = CreatePercentagePromotion(status: PromotionStatus.Draft);

        Assert.Equal(PromotionStatus.Draft, promo.Status);

        var activateResult = promo.Activate();
        Assert.True(activateResult.IsSuccess);
        Assert.Equal(PromotionStatus.Active, promo.Status);

        var secondActivate = promo.Activate();
        Assert.False(secondActivate.IsSuccess);
        Assert.Equal(PromotionStatus.Active, promo.Status);
    }

    [Fact]
    public void PromotionLifecycle_DraftToActiveToDisabled()
    {
        var result = Promotion.Create(
            id: 1, name: "Test", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: 10m);

        Assert.True(result.IsSuccess);
        var promo = result.Value;
        Assert.Equal(PromotionStatus.Draft, promo.Status);

        var activate = promo.Activate();
        Assert.True(activate.IsSuccess);
        Assert.Equal(PromotionStatus.Active, promo.Status);

        var disable = promo.Deactivate();
        Assert.True(disable.IsSuccess);
        Assert.Equal(PromotionStatus.Disabled, promo.Status);
    }

    [Fact]
    public void PromotionLifecycle_CannotActivateDisabled()
    {
        var promo = CreatePercentagePromotion(status: PromotionStatus.Disabled);

        var result = promo.Activate();
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void PromotionLifecycle_CannotDeactivateDraft()
    {
        var result = Promotion.Create(
            id: 1, name: "Test", type: PromotionType.PercentageDiscount,
            planId: 1, durationMonths: 3,
            startsAtUtc: DateTime.UtcNow, endsAtUtc: DateTime.UtcNow.AddDays(30),
            percentage: 10m);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        var disable = promo.Deactivate();
        Assert.False(disable.IsSuccess);
    }

    [Fact]
    public void PromotionLifecycle_MarkExpired_OnlyFromActive()
    {
        var promo = CreatePercentagePromotion(status: PromotionStatus.Active);

        var result = promo.MarkExpired();
        Assert.True(result.IsSuccess);
        Assert.Equal(PromotionStatus.Expired, promo.Status);
    }

    // ==================================================================
    // 24. GLOBAL PROMOTION (PlanId = 0)
    // ==================================================================

    [Fact]
    public void GlobalPromotion_PlanIdZero_AppliesToAnyPlan()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(planId: 0, percentage: 10m, durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.PromotionId);
    }

    // ==================================================================
    // 25. GLOBAL DURATION PROMOTION (DurationMonths = 0)
    // ==================================================================

    [Fact]
    public void GlobalDurationPromotion_DurationZero_AppliesToAnyDuration()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePercentagePromotion(durationMonths: 0, percentage: 10m);
        var service = CreateService();

        var result6 = service.Calculate(plan, 6, UtcNow, [promo]);
        Assert.True(result6.IsSuccess);
        Assert.NotNull(result6.Value.PromotionId);

        var result12 = service.Calculate(plan, 12, UtcNow, [promo]);
        Assert.True(result12.IsSuccess);
        Assert.NotNull(result12.Value.PromotionId);
    }

    // ==================================================================
    // 26. CHARGED MONTHS > DURATION REJECTED
    // ==================================================================

    [Fact]
    public void PayForXMonths_ChargedExceedsDuration_Rejected()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePayForXMonthsPromotion(durationMonths: 6, chargedMonths: 12);
        var service = CreateService();

        var result = service.Calculate(plan, 6, UtcNow, [promo]);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // 27. PROMOTIONAL PRICE = BASE (zero discount)
    // ==================================================================

    [Fact]
    public void PromotionalPrice_EqualToBase_ZeroDiscount()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotionalPricePromotion(durationMonths: 3, promotionalPrice: 3000m);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.DiscountAmount);
        Assert.Equal(3000m, result.Value.FinalAmount);
    }

    // ==================================================================
    // 28. PROMOTIONAL PRICE = ZERO
    // ==================================================================

    [Fact]
    public void PromotionalPrice_Zero_FreeOffer()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotionalPricePromotion(durationMonths: 3, promotionalPrice: 0m);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, result.Value.BaseAmount);
        Assert.Equal(3000m, result.Value.DiscountAmount);
        Assert.Equal(0m, result.Value.FinalAmount);
    }

    // ==================================================================
    // 29. Rounding determinism
    // ==================================================================

    [Fact]
    public void PercentageDiscount_Rounding_IsDeterministic()
    {
        var plan = CreatePlan(monthlyPrice: 333.33m);
        var promo = CreatePercentagePromotion(percentage: 7.5m, durationMonths: 3);
        var service = CreateService();

        var result1 = service.Calculate(plan, 3, UtcNow, [promo]);
        var result2 = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.Equal(result1.Value.FinalAmount, result2.Value.FinalAmount);
        Assert.Equal(result1.Value.DiscountAmount, result2.Value.DiscountAmount);
    }
}
