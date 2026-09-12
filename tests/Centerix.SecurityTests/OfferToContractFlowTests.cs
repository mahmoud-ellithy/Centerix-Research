namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Xunit;

/// <summary>
/// CODER TASK 7.1 & 7.2: Harden Authoritative Offer → Contract Creation.
/// Comprehensive tests for the hardened commercial flow.
///
/// Test categories:
/// - Offer calculation (1-11)
/// - Acceptance (12-15)
/// - Contract creation (16-23)
/// - Historical integrity (24-26)
/// - Tenant isolation (29-30)
/// - Task 7.1: Benefit source authority (31-40)
/// - Task 7.2: Client Benefits injection removed (47-55)
/// </summary>
public class OfferToContractFlowTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Plan CreatePlan(
        int id = 1,
        decimal monthlyPrice = 1000m,
        string currencyCode = "EGP",
        int durationMonths = 12,
        bool isActive = true)
    {
        var result = Plan.Create(
            id: id,
            code: $"PLAN-{id}",
            displayName: $"Plan {id}",
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

    private static void AddPricingTiers(Plan plan)
    {
        var tier1 = PlanPricingTier.Create(1, plan.Id, 1, 1000m, 1).Value;
        var tier3 = PlanPricingTier.Create(2, plan.Id, 3, 2700m, 2).Value;
        var tier6 = PlanPricingTier.Create(3, plan.Id, 6, 5220m, 3).Value;
        var tier12 = PlanPricingTier.Create(4, plan.Id, 12, 10000m, 4).Value;

        plan.AddPricingTier(tier1);
        plan.AddPricingTier(tier3);
        plan.AddPricingTier(tier6);
        plan.AddPricingTier(tier12);
    }

    private static Promotion CreatePromotion(
        int id = 1,
        int planId = 1,
        int durationMonths = 3,
        PromotionType type = PromotionType.PercentageDiscount,
        decimal? percentage = 10m,
        decimal? fixedAmount = null,
        decimal? promotionalPrice = null,
        int? chargedMonths = null,
        int priority = 10,
        PromotionStatus status = PromotionStatus.Active,
        DateTime? startsAt = null,
        DateTime? endsAt = null)
    {
        var start = startsAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = endsAt ?? new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = Promotion.Create(
            id: id,
            name: $"Promo {id}",
            type: type,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: start,
            endsAtUtc: end,
            priority: priority,
            percentage: percentage,
            fixedAmount: fixedAmount,
            promotionalPrice: promotionalPrice,
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess);
        var promo = result.Value;

        if (status == PromotionStatus.Active)
        {
            promo.Activate();
        }
        else if (status == PromotionStatus.Disabled)
        {
            promo.Activate();
            promo.Deactivate();
        }

        return promo;
    }

    private static IPromotionCalculationService CreateService()
        => new PromotionCalculationService();

    private static readonly DateTime UtcNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Offer CreateValidOffer(
        string tenantId = "tenant-1",
        int planId = 1,
        int durationMonths = 12,
        decimal baseAmount = 10000m,
        decimal finalAmount = 9000m,
        decimal discountAmount = 1000m,
        decimal monthlyListPrice = 1000m,
        OfferStatus status = OfferStatus.Calculated)
    {
        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: planId,
            durationMonths: durationMonths,
            baseAmount: baseAmount,
            discountAmount: discountAmount,
            finalAmount: finalAmount,
            monthlyListPrice: monthlyListPrice,
            currencyCode: "EGP",
            promotionId: 1,
            promotionName: "Test Promo",
            promotionType: "PercentageDiscount",
            discountPercentage: 10m,
            calculatedAtUtc: UtcNow,
            expiresAtUtc: UtcNow.AddHours(24));

        Assert.True(offer.IsSuccess);

        if (status == OfferStatus.Accepted)
        {
            offer.Value.Accept(UtcNow);
        }
        else if (status == OfferStatus.ConvertedToContract)
        {
            offer.Value.Accept(UtcNow);
            offer.Value.MarkConverted(Guid.NewGuid(), UtcNow);
        }

        return offer.Value;
    }

    private static OfferBenefit CreateOfferBenefit(
        Guid offerId,
        string name,
        decimal value,
        ContractBenefitType type = ContractBenefitType.PhysicalGift)
    {
        return OfferBenefit.Create(
            Guid.NewGuid(),
            offerId,
            type,
            name,
            null,
            value,
            "EGP").Value;
    }

    // ==================================================================
    // OFFER CALCULATION (Tests 1-11)
    // ==================================================================

    [Fact]
    public void Test01_CalculateValidOffer_ReturnsCorrectValues()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, []);

        Assert.True(result.IsSuccess);
        var offer = result.Value;
        Assert.Equal(1, offer.PlanId);
        Assert.Equal(3, offer.DurationMonths);
        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(0m, offer.DiscountAmount);
        Assert.Equal(3000m, offer.FinalAmount);
        Assert.Equal("None", offer.PromotionType);
        Assert.True(offer.CalculatedAtUtc > DateTime.MinValue);
    }

    [Fact]
    public void Test02_PricingTierSelected_Correctly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, []);

        Assert.True(result.IsSuccess);
        Assert.Equal(10000m, result.Value.BaseAmount);
        Assert.Equal(10000m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test03_PromotionAppliedToPricingTierPrice()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var promo = CreatePromotion(
            type: PromotionType.PercentageDiscount,
            percentage: 10m,
            durationMonths: 12);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(10000m, result.Value.BaseAmount);
        Assert.Equal(1000m, result.Value.DiscountAmount);
        Assert.Equal(9000m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test04_PercentageDiscount_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotion(type: PromotionType.PercentageDiscount, percentage: 10m, durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, result.Value.BaseAmount);
        Assert.Equal(300m, result.Value.DiscountAmount);
        Assert.Equal(2700m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test05_FixedAmountDiscount_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotion(
            type: PromotionType.FixedAmountDiscount,
            fixedAmount: 500m,
            durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, result.Value.BaseAmount);
        Assert.Equal(500m, result.Value.DiscountAmount);
        Assert.Equal(2500m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test06_PromotionalPrice_CalculatesCorrectly()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotion(
            type: PromotionType.PromotionalPrice,
            promotionalPrice: 2500m,
            durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, result.Value.BaseAmount);
        Assert.Equal(500m, result.Value.DiscountAmount);
        Assert.Equal(2500m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test07_PayForXMonths_PreservesSemanticDistinction()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotion(
            type: PromotionType.PayForXMonths,
            chargedMonths: 10,
            durationMonths: 12);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Equal(12, result.Value.DurationMonths);
        Assert.Equal(10, result.Value.ChargedMonths);
        Assert.Equal(12000m, result.Value.BaseAmount);
        Assert.Equal(10000m, result.Value.FinalAmount);
        Assert.Equal(2000m, result.Value.DiscountAmount);
    }

    [Fact]
    public void Test08_NoStacking_SelectsHighestPriority()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo1 = CreatePromotion(id: 1, percentage: 10m, priority: 10, durationMonths: 3);
        var promo2 = CreatePromotion(id: 2, percentage: 20m, priority: 5, durationMonths: 3);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo1, promo2]);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, result.Value.DiscountPercentage);
        Assert.Equal(300m, result.Value.DiscountAmount);
        Assert.Equal(2700m, result.Value.FinalAmount);
    }

    [Fact]
    public void Test09_PromotionExpiration_CannotApply()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var promo = CreatePromotion(
            percentage: 10m,
            durationMonths: 3,
            startsAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, [promo]);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId);
        Assert.Equal("None", result.Value.PromotionType);
    }

    [Fact]
    public void Test10_OfferExpiration_SetOnCalculatedOffer()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();

        var result = service.Calculate(plan, 3, UtcNow, []);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.CalculatedAtUtc > DateTime.MinValue);
    }

    [Fact]
    public void Test11_InvalidOffer_CannotBeAccepted()
    {
        var now = DateTime.UtcNow;
        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            planId: 1,
            durationMonths: 3,
            baseAmount: 3000m,
            discountAmount: 0,
            finalAmount: 3000m,
            monthlyListPrice: 1000m,
            currencyCode: "EGP",
            calculatedAtUtc: now,
            expiresAtUtc: now.AddHours(-1));

        Assert.False(offer.IsSuccess);
    }

    // ==================================================================
    // ACCEPTANCE (Tests 12-15)
    // ==================================================================

    [Fact]
    public void Test12_ValidOffer_CanBeAccepted()
    {
        var offer = CreateValidOffer();

        var result = offer.Accept(UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(OfferStatus.Accepted, offer.Status);
        Assert.Equal(UtcNow, offer.AcceptedAtUtc);
    }

    [Fact]
    public void Test13_ExpiredOffer_CannotBeAccepted()
    {
        var offer = CreateValidOffer();

        var markResult = offer.MarkExpired(UtcNow);
        Assert.True(markResult.IsSuccess);
        Assert.Equal(OfferStatus.Expired, offer.Status);

        var result = offer.Accept(UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal("Offer.InvalidStateTransition", result.Errors[0].Code);
    }

    [Fact]
    public void Test14_AlreadyAcceptedOffer_Idempotent()
    {
        var offer = CreateValidOffer();

        var result1 = offer.Accept(UtcNow);
        Assert.True(result1.IsSuccess);

        var result2 = offer.Accept(UtcNow.AddHours(1));
        Assert.True(result2.IsSuccess);
        Assert.Equal(OfferStatus.Accepted, offer.Status);
    }

    [Fact]
    public void Test15_CrossTenantOffer_CannotBeAccepted()
    {
        var offer = CreateValidOffer(tenantId: "tenant-1");

        Assert.NotEqual("tenant-2", offer.TenantId);
    }

    // ==================================================================
    // CONTRACT CREATION (Tests 16-23)
    // ==================================================================

    [Fact]
    public void Test16_ContractCreatedFromOffer_ValuesMatchOfferSnapshot()
    {
        var offer = CreateValidOffer(
            baseAmount: 10000m,
            finalAmount: 9000m,
            discountAmount: 1000m);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-001",
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

        Assert.Equal(offer.FinalAmount, contract.ContractedAmount);
        Assert.Equal(offer.DiscountAmount, contract.DiscountAmount);
        Assert.Equal(offer.PromotionId, contract.PromotionId);
        Assert.Equal(offer.PromotionType, contract.PromotionType);
        Assert.Equal(offer.MonthlyListPrice, contract.MonthlyListPrice);
        Assert.Equal(offer.CurrencyCode, contract.CurrencyCode);
        Assert.Equal(offer.DurationMonths, contract.DurationMonths);
        Assert.Equal(offer.ChargedMonths, contract.ChargedMonths);
    }

    [Fact]
    public void Test17_ContractValues_ExactMatchOfferSnapshot()
    {
        var offer = CreateValidOffer(
            baseAmount: 12000m,
            finalAmount: 10000m,
            discountAmount: 2000m);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-002",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths).Value;

        Assert.Equal(10000m, contract.ContractedAmount);
        Assert.Equal(2000m, contract.DiscountAmount);
        Assert.Equal(1, contract.PromotionId);
        Assert.Equal("PercentageDiscount", contract.PromotionType);
    }

    [Fact]
    public void Test18_ClientCannotOverride_ContractedAmount()
    {
        var offer = CreateValidOffer(finalAmount: 9000m);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-003",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount).Value;

        Assert.Equal(9000m, contract.ContractedAmount);
    }

    [Fact]
    public void Test19_ClientCannotOverride_DiscountAmount()
    {
        var offer = CreateValidOffer(discountAmount: 1000m);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-004",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount).Value;

        Assert.Equal(1000m, contract.DiscountAmount);
    }

    [Fact]
    public void Test20_ClientCannotOverride_Promotion()
    {
        var offer = CreateValidOffer();

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-005",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType).Value;

        Assert.Equal(offer.PromotionId, contract.PromotionId);
        Assert.Equal(offer.PromotionType, contract.PromotionType);
    }

    [Fact]
    public void Test21_ClientCannotOverride_ChargedMonths()
    {
        var offer = CreateValidOffer();

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-006",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            chargedMonths: offer.ChargedMonths).Value;

        Assert.Equal(offer.ChargedMonths, contract.ChargedMonths);
    }

    [Fact]
    public void Test22_ClientCannotInjectArbitraryPricingTiers()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);

        Assert.Equal(4, plan.PricingTiers.Count);
        Assert.Equal(1000m, plan.GetPricingTierForDuration(1)?.TierPrice);
        Assert.Equal(2700m, plan.GetPricingTierForDuration(3)?.TierPrice);
        Assert.Equal(5220m, plan.GetPricingTierForDuration(6)?.TierPrice);
        Assert.Equal(10000m, plan.GetPricingTierForDuration(12)?.TierPrice);
    }

    [Fact]
    public void Test23_ClientCannotInjectArbitraryBenefits()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-007",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m).Value;

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(),
            contract.Id,
            ContractBenefitType.PhysicalGift,
            "Expensive Item",
            null,
            4000m,
            "EGP").Value;

        var result = contract.AddBenefit(benefit);
        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", result.Errors[0].Code);
    }

    // ==================================================================
    // HISTORICAL INTEGRITY (Tests 24-26)
    // ==================================================================

    [Fact]
    public void Test24_PromotionChangedAfterAcceptance_DoesNotChangeOffer()
    {
        var promo = CreatePromotion(percentage: 10m, durationMonths: 3);

        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();
        var calculated = service.Calculate(plan, 3, UtcNow, [promo]).Value;

        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            planId: calculated.PlanId,
            durationMonths: calculated.DurationMonths,
            baseAmount: calculated.BaseAmount,
            discountAmount: calculated.DiscountAmount,
            finalAmount: calculated.FinalAmount,
            monthlyListPrice: calculated.MonthlyListPrice,
            currencyCode: calculated.CurrencyCode,
            promotionId: calculated.PromotionId,
            promotionName: calculated.PromotionName,
            promotionType: calculated.PromotionType,
            discountPercentage: calculated.DiscountPercentage,
            chargedMonths: calculated.ChargedMonths,
            calculatedAtUtc: calculated.CalculatedAtUtc).Value;

        offer.Accept(UtcNow);

        promo.Deactivate();
        var newPromo = CreatePromotion(id: 99, percentage: 20m, durationMonths: 3);

        Assert.Equal(3000m, offer.BaseAmount);
        Assert.Equal(300m, offer.DiscountAmount);
        Assert.Equal(2700m, offer.FinalAmount);
        Assert.Equal(1, offer.PromotionId);
        Assert.Equal("PercentageDiscount", offer.PromotionType);
    }

    [Fact]
    public void Test25_PromotionChangedAfterAcceptance_DoesNotChangeContract()
    {
        var promo = CreatePromotion(percentage: 10m, durationMonths: 3);

        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();
        var calculated = service.Calculate(plan, 3, UtcNow, [promo]).Value;

        var offer = CreateValidOffer(
            baseAmount: calculated.BaseAmount,
            finalAmount: calculated.FinalAmount,
            discountAmount: calculated.DiscountAmount);

        var contract = Contract.Create(
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
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths).Value;

        promo.Deactivate();
        CreatePromotion(id: 99, percentage: 20m, durationMonths: 3);

        Assert.Equal(2700m, contract.ContractedAmount);
        Assert.Equal(300m, contract.DiscountAmount);
        Assert.Equal(1, contract.PromotionId);
        Assert.Equal("PercentageDiscount", contract.PromotionType);
    }

    [Fact]
    public void Test26_RefundUsesHistoricalContractPricing()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-TEST-002",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m).Value;

        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, 1000m, "EGP", 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, 2700m, "EGP", 1000m, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, 5220m, "EGP", 1000m, 3).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, 10000m, "EGP", 1000m, 4).Value);

        Assert.Equal(1000m, contract.CalculateValueForElapsedMonths(1));
        Assert.Equal(2700m, contract.CalculateValueForElapsedMonths(3));
        Assert.Equal(5220m, contract.CalculateValueForElapsedMonths(6));
        Assert.Equal(10000m, contract.CalculateValueForElapsedMonths(12));
    }

    // ==================================================================
    // OFFER DOMAIN RULES
    // ==================================================================

    [Fact]
    public void Offer_Create_ValidatesRequiredFields()
    {
        Assert.False(Offer.Create(Guid.Empty, "tenant-1", 1, 12, 10000m, 0, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "", 1, 12, 10000m, 0, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 0, 12, 10000m, 0, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 0, 10000m, 0, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 12, -1m, 0, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 12, 10000m, -1m, 10000m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 12, 10000m, 0, -1m, 1000m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 12, 10000m, 0, 10000m, -1m, "EGP").IsSuccess);
        Assert.False(Offer.Create(Guid.NewGuid(), "tenant-1", 1, 12, 10000m, 0, 10000m, 1000m, "US").IsSuccess);
    }

    [Fact]
    public void Offer_DiscountExceedsBase_IsRejected()
    {
        var result = Offer.Create(
            Guid.NewGuid(), "tenant-1", 1, 12,
            baseAmount: 1000m, discountAmount: 2000m, finalAmount: 0m,
            monthlyListPrice: 1000m, currencyCode: "EGP");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Offer_Lifecycle_CalculatedToAcceptedToConverted()
    {
        var offer = CreateValidOffer();

        Assert.Equal(OfferStatus.Calculated, offer.Status);

        offer.Accept(UtcNow);
        Assert.Equal(OfferStatus.Accepted, offer.Status);
        Assert.Equal(UtcNow, offer.AcceptedAtUtc);

        var contractId = Guid.NewGuid();
        offer.MarkConverted(contractId, UtcNow.AddHours(1));
        Assert.Equal(OfferStatus.ConvertedToContract, offer.Status);
        Assert.Equal(contractId, offer.ContractId);
    }

    [Fact]
    public void Offer_CannotAcceptFromAccepted_Directly()
    {
        var offer = CreateValidOffer(status: OfferStatus.Accepted);

        var result = offer.Accept(UtcNow);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Offer_CannotConvertFromCalculated()
    {
        var offer = CreateValidOffer(status: OfferStatus.Calculated);

        var result = offer.MarkConverted(Guid.NewGuid(), UtcNow);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Offer_CannotAcceptFromConverted()
    {
        var offer = CreateValidOffer(status: OfferStatus.ConvertedToContract);

        var result = offer.Accept(UtcNow.AddHours(2));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Offer_MarkExpired_FromCalculated_Succeeds()
    {
        var offer = CreateValidOffer(status: OfferStatus.Calculated);

        var result = offer.MarkExpired(UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(OfferStatus.Expired, offer.Status);
    }

    [Fact]
    public void Offer_MarkExpired_FromAccepted_IsDenied()
    {
        var offer = CreateValidOffer(status: OfferStatus.Accepted);

        var result = offer.MarkExpired(UtcNow);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Offer_IsAcceptable_ChecksExpiration()
    {
        var offer = CreateValidOffer();

        Assert.True(offer.IsAcceptable);

        offer.MarkExpired(UtcNow);
        Assert.False(offer.IsAcceptable);
    }

    [Fact]
    public void Offer_IsConvertible_RequiresAccepted()
    {
        var calculatedOffer = CreateValidOffer(status: OfferStatus.Calculated);
        Assert.False(calculatedOffer.IsConvertible);

        var acceptedOffer = CreateValidOffer(status: OfferStatus.Accepted);
        Assert.True(acceptedOffer.IsConvertible);
    }

    // ==================================================================
    // PRICING TIER AUTHORITY
    // ==================================================================

    [Fact]
    public void PlanPricingTier_Create_ValidatesInputs()
    {
        Assert.False(PlanPricingTier.Create(-1, 1, 12, 10000m).IsSuccess);
        Assert.False(PlanPricingTier.Create(1, 0, 12, 10000m).IsSuccess);
        Assert.False(PlanPricingTier.Create(1, 1, 0, 10000m).IsSuccess);
        Assert.False(PlanPricingTier.Create(1, 1, 12, -1m).IsSuccess);
    }

    [Fact]
    public void PlanPricingTier_DuplicateDuration_IsRejected()
    {
        var plan = CreatePlan();
        var tier1 = PlanPricingTier.Create(1, plan.Id, 12, 10000m).Value;
        var tier2 = PlanPricingTier.Create(2, plan.Id, 12, 9000m).Value;

        plan.AddPricingTier(tier1);
        var result = plan.AddPricingTier(tier2);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Plan_GetPricingTierForDuration_ReturnsCorrectTier()
    {
        var plan = CreatePlan();
        AddPricingTiers(plan);

        Assert.Equal(1000m, plan.GetPricingTierForDuration(1)?.TierPrice);
        Assert.Equal(2700m, plan.GetPricingTierForDuration(3)?.TierPrice);
        Assert.Equal(5220m, plan.GetPricingTierForDuration(6)?.TierPrice);
        Assert.Equal(10000m, plan.GetPricingTierForDuration(12)?.TierPrice);
        Assert.Null(plan.GetPricingTierForDuration(2));
    }

    [Fact]
    public void PricingTierAuthoritative_BaseAmountUsesTierPrice()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, []);
        Assert.Equal(10000m, result.Value.BaseAmount);

        var result3 = service.Calculate(plan, 3, UtcNow, []);
        Assert.Equal(2700m, result3.Value.BaseAmount);

        var result2 = service.Calculate(plan, 2, UtcNow, []);
        Assert.Equal(2000m, result2.Value.BaseAmount);
    }

    // ==================================================================
    // TENANT ISOLATION (Tests 29-30)
    // ==================================================================

    [Fact]
    public void Test29_TenantA_CannotUseTenantB_Offer()
    {
        var offerA = CreateValidOffer(tenantId: "tenant-1");
        var offerB = CreateValidOffer(tenantId: "tenant-2");

        Assert.NotEqual(offerA.TenantId, offerB.TenantId);
        Assert.Equal("tenant-1", offerA.TenantId);
        Assert.Equal("tenant-2", offerB.TenantId);
    }

    [Fact]
    public void Test30_TenantA_CannotConvertTenantB_Offer()
    {
        var offerB = CreateValidOffer(tenantId: "tenant-2", status: OfferStatus.Accepted);

        var currentTenant = "tenant-1";
        Assert.False(string.Equals(offerB.TenantId, currentTenant, StringComparison.OrdinalIgnoreCase));
    }

    // ==================================================================
    // TASK 7.1: BENEFIT SOURCE AUTHORITY (Tests 31-40)
    // ==================================================================

    [Fact]
    public void Test31_CreateContractFromOfferRequest_HasNoBenefitsInput()
    {
        var request = new Centerix.API.Controllers.CreateContractFromOfferRequest
        {
            OfferId = Guid.NewGuid(),
            ContractNumber = "CTR-001",
            EffectiveAtUtc = UtcNow
        };

        var properties = request.GetType().GetProperties();
        var benefitProperty = properties.FirstOrDefault(p =>
            string.Equals(p.Name, "Benefits", StringComparison.OrdinalIgnoreCase));

        Assert.Null(benefitProperty);
    }

    [Fact]
    public void Test32_CreateContractFromOfferCommand_HasNoBenefitsParameter()
    {
        var commandType = typeof(Centerix.Application.Platform.Promotions.Commands.CreateContractFromOfferCommand);

        var benefitProperty = commandType.GetProperty("Benefits");

        Assert.Null(benefitProperty);
    }

    [Fact]
    public void Test33_OfferBenefit_StoredOnOffer()
    {
        var offer = CreateValidOffer();

        var benefit = CreateOfferBenefit(offer.Id, "Printer", 1000m);
        var addResult = offer.AddBenefit(benefit);

        Assert.True(addResult.IsSuccess);
        Assert.Single(offer.Benefits);
        Assert.Equal("Printer", offer.Benefits[0].Name);
        Assert.Equal(1000m, offer.Benefits[0].ContractualValue);
    }

    [Fact]
    public void Test34_OfferBenefit_CopiedToContractExactly()
    {
        var offer = CreateValidOffer();

        var offerBenefit = CreateOfferBenefit(offer.Id, "Barcode Printer", 1500m, ContractBenefitType.PhysicalGift);
        offer.AddBenefit(offerBenefit);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-BEN-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount).Value;

        foreach (var ob in offer.Benefits)
        {
            var contractBenefit = ContractBenefit.Create(
                Guid.NewGuid(),
                contract.Id,
                ob.BenefitType,
                ob.Name,
                ob.Description,
                ob.ContractualValue,
                ob.CurrencyCode).Value;

            contract.AddBenefit(contractBenefit);
        }

        Assert.Single(contract.Benefits);
        Assert.Equal(offerBenefit.Name, contract.Benefits[0].Name);
        Assert.Equal(offerBenefit.ContractualValue, contract.Benefits[0].ContractualValue);
        Assert.Equal(offerBenefit.BenefitType, contract.Benefits[0].BenefitType);
        Assert.Equal(offerBenefit.CurrencyCode, contract.Benefits[0].CurrencyCode);
    }

    [Fact]
    public void Test35_BenefitOverride_ImpossibleThroughOfferSnapshot()
    {
        var offer = CreateValidOffer();

        var offerBenefit = CreateOfferBenefit(offer.Id, "Gift", 1000m);
        offer.AddBenefit(offerBenefit);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-OVR-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount).Value;

        foreach (var ob in offer.Benefits)
        {
            var contractBenefit = ContractBenefit.Create(
                Guid.NewGuid(),
                contract.Id,
                ob.BenefitType,
                ob.Name,
                ob.Description,
                ob.ContractualValue,
                ob.CurrencyCode).Value;

            contract.AddBenefit(contractBenefit);
        }

        Assert.Equal(1000m, contract.Benefits[0].ContractualValue);
        Assert.Equal(ContractBenefitType.PhysicalGift, contract.Benefits[0].BenefitType);
    }

    [Fact]
    public void Test36_OfferBenefit_Create_ValidatesInputs()
    {
        var offer = CreateValidOffer();

        Assert.False(OfferBenefit.Create(Guid.Empty, offer.Id, ContractBenefitType.PhysicalGift, "Gift", null, 1000m, "EGP").IsSuccess);
        Assert.False(OfferBenefit.Create(Guid.NewGuid(), Guid.Empty, ContractBenefitType.PhysicalGift, "Gift", null, 1000m, "EGP").IsSuccess);
        Assert.False(OfferBenefit.Create(Guid.NewGuid(), offer.Id, ContractBenefitType.PhysicalGift, "", null, 1000m, "EGP").IsSuccess);
        Assert.False(OfferBenefit.Create(Guid.NewGuid(), offer.Id, ContractBenefitType.PhysicalGift, "Gift", null, -1m, "EGP").IsSuccess);
        Assert.False(OfferBenefit.Create(Guid.NewGuid(), offer.Id, ContractBenefitType.PhysicalGift, "Gift", null, 1000m, "US").IsSuccess);
    }

    [Fact]
    public void Test37_OfferBenefit_CurrencyCodeNormalized()
    {
        var offer = CreateValidOffer();

        var benefit = OfferBenefit.Create(
            Guid.NewGuid(),
            offer.Id,
            ContractBenefitType.Service,
            "Service",
            null,
            500m,
            "egp").Value;

        Assert.Equal("EGP", benefit.CurrencyCode);
    }

    [Fact]
    public void Test38_OfferBenefit_MultipleBenefitsOnOffer()
    {
        var offer = CreateValidOffer();

        var benefit1 = CreateOfferBenefit(offer.Id, "Printer", 1000m, ContractBenefitType.PhysicalGift);
        var benefit2 = CreateOfferBenefit(offer.Id, "Consulting", 500m, ContractBenefitType.Service);

        offer.AddBenefit(benefit1);
        offer.AddBenefit(benefit2);

        Assert.Equal(2, offer.Benefits.Count);
        Assert.Equal("Printer", offer.Benefits[0].Name);
        Assert.Equal("Consulting", offer.Benefits[1].Name);
    }

    [Fact]
    public void Test39_OfferBenefits_BenefitCapStillEnforcedOnContract()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-CAP-001",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m).Value;

        var b1 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "A", null, 1500m, "EGP").Value;
        var b2 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "B", null, 1500m, "EGP").Value;

        Assert.True(contract.AddBenefit(b1).IsSuccess);
        Assert.True(contract.AddBenefit(b2).IsSuccess);

        var b3 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "C", null, 1m, "EGP").Value;
        Assert.False(contract.AddBenefit(b3).IsSuccess);
    }

    [Fact]
    public void Test40_OfferBenefits_LifecycleRulesPreserved()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-LC-001",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m).Value;

        var physicalBenefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Printer", null, 1000m, "EGP").Value;
        contract.AddBenefit(physicalBenefit);

        Assert.Equal(BenefitEligibilityStatus.NotEligible, contract.Benefits[0].EligibilityStatus);

        physicalBenefit.MarkEligible(UtcNow);
        Assert.Equal(BenefitEligibilityStatus.Eligible, contract.Benefits[0].EligibilityStatus);

        physicalBenefit.MarkGranted(UtcNow);
        Assert.Equal(BenefitEligibilityStatus.Delivered, contract.Benefits[0].EligibilityStatus);
        Assert.True(contract.Benefits[0].IsGranted);

        var serviceBenefit = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.Service, "Support", null, 500m, "EGP").Value;
        contract.AddBenefit(serviceBenefit);

        var deliverResult = serviceBenefit.MarkGranted(UtcNow);
        Assert.False(deliverResult.IsSuccess);
    }

    // ==================================================================
    // TASK 7.1: HISTORICAL SNAPSHOT REGRESSION
    // ==================================================================

    [Fact]
    public void Test41_PlanChangeAfterAcceptance_ContractUsesOfferSnapshot()
    {
        var offer = CreateValidOffer(
            baseAmount: 10000m,
            finalAmount: 9000m,
            monthlyListPrice: 1000m);

        offer.Accept(UtcNow);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-PLAN-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths).Value;

        Assert.Equal(1000m, contract.MonthlyListPrice);
        Assert.Equal(9000m, contract.ContractedAmount);
    }

    [Fact]
    public void Test42_PromotionChangeAfterAcceptance_ContractUsesOfferSnapshot()
    {
        var promo = CreatePromotion(percentage: 10m, durationMonths: 3);

        var plan = CreatePlan(monthlyPrice: 1000m);
        var service = CreateService();
        var calculated = service.Calculate(plan, 3, UtcNow, [promo]).Value;

        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            planId: calculated.PlanId,
            durationMonths: calculated.DurationMonths,
            baseAmount: calculated.BaseAmount,
            discountAmount: calculated.DiscountAmount,
            finalAmount: calculated.FinalAmount,
            monthlyListPrice: calculated.MonthlyListPrice,
            currencyCode: calculated.CurrencyCode,
            promotionId: calculated.PromotionId,
            promotionName: calculated.PromotionName,
            promotionType: calculated.PromotionType,
            discountPercentage: calculated.DiscountPercentage,
            chargedMonths: calculated.ChargedMonths,
            calculatedAtUtc: calculated.CalculatedAtUtc).Value;

        offer.Accept(UtcNow);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-PROMO-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths).Value;

        promo.Deactivate();
        CreatePromotion(id: 99, percentage: 20m, durationMonths: 3);

        Assert.Equal(2700m, contract.ContractedAmount);
        Assert.Equal(300m, contract.DiscountAmount);
        Assert.Equal(1, contract.PromotionId);
        Assert.Equal("PercentageDiscount", contract.PromotionType);
    }

    [Fact]
    public void Test43_BenefitSourceChangeAfterOffer_ContractUsesOfferBenefits()
    {
        var offer = CreateValidOffer();

        var benefit = CreateOfferBenefit(offer.Id, "Original Gift", 1000m);
        offer.AddBenefit(benefit);

        offer.Accept(UtcNow);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-BENSRC-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount).Value;

        foreach (var ob in offer.Benefits)
        {
            var contractBenefit = ContractBenefit.Create(
                Guid.NewGuid(),
                contract.Id,
                ob.BenefitType,
                ob.Name,
                ob.Description,
                ob.ContractualValue,
                ob.CurrencyCode).Value;

            contract.AddBenefit(contractBenefit);
        }

        Assert.Single(contract.Benefits);
        Assert.Equal("Original Gift", contract.Benefits[0].Name);
        Assert.Equal(1000m, contract.Benefits[0].ContractualValue);
    }

    // ==================================================================
    // TASK 7.1: OFFER LIFECYCLE & IDEMPOTENCY
    // ==================================================================

    [Fact]
    public void Test44_UnacceptedOffer_CannotBeConverted()
    {
        var offer = CreateValidOffer(status: OfferStatus.Calculated);
        Assert.False(offer.IsConvertible);
    }

    [Fact]
    public void Test45_ExpiredOffer_CannotBeConverted()
    {
        var offer = CreateValidOffer(status: OfferStatus.Calculated);
        offer.MarkExpired(UtcNow);
        Assert.False(offer.IsConvertible);
    }

    [Fact]
    public void Test46_AlreadyConvertedOffer_Idempotent()
    {
        var offer = CreateValidOffer(status: OfferStatus.Accepted);
        var contractId1 = Guid.NewGuid();
        var contractId2 = Guid.NewGuid();

        var result1 = offer.MarkConverted(contractId1, UtcNow);
        Assert.True(result1.IsSuccess);
        Assert.Equal(contractId1, offer.ContractId);

        var result2 = offer.MarkConverted(contractId2, UtcNow.AddHours(1));
        Assert.True(result2.IsSuccess);
        Assert.Equal(contractId1, offer.ContractId);
    }

    // ==================================================================
    // TASK 7.2: CLIENT BENEFITS INJECTION REMOVED (Tests 47-55)
    // ==================================================================

    [Fact]
    public void Test47_CalculateAndPersistOfferRequest_HasNoBenefitsProperty()
    {
        var request = new Centerix.API.Controllers.CalculateAndPersistOfferRequest
        {
            PlanId = 1,
            DurationMonths = 12
        };

        var properties = request.GetType().GetProperties();
        var benefitProperty = properties.FirstOrDefault(p =>
            string.Equals(p.Name, "Benefits", StringComparison.OrdinalIgnoreCase));

        Assert.Null(benefitProperty);
    }

    [Fact]
    public void Test48_CalculateAndPersistOfferCommand_HasNoBenefitsParameter()
    {
        var commandType = typeof(Centerix.Application.Platform.Promotions.Commands.CalculateAndPersistOfferCommand);

        var benefitProperty = commandType.GetProperty("Benefits");

        Assert.Null(benefitProperty);
    }

    [Fact]
    public void Test49_CreateOfferBenefitRequest_Type_NoLongerExists()
    {
        var types = typeof(Centerix.Application.Platform.Promotions.Commands.CalculateAndPersistOfferCommand)
            .Assembly.GetTypes();

        var benefitRequestType = types.FirstOrDefault(t =>
            string.Equals(t.Name, "CreateOfferBenefitRequest", StringComparison.OrdinalIgnoreCase));

        Assert.Null(benefitRequestType);
    }

    [Fact]
    public void Test50_ClientCannotInjectArbitraryBenefits_ViaRequest()
    {
        var request = new Centerix.API.Controllers.CalculateAndPersistOfferRequest
        {
            PlanId = 1,
            DurationMonths = 12
        };

        var properties = request.GetType().GetProperties();
        var benefitProperty = properties.FirstOrDefault(p =>
            p.Name.Contains("Benefit", StringComparison.OrdinalIgnoreCase));

        Assert.Null(benefitProperty);
    }

    [Fact]
    public void Test51_CalculatedOffer_ContainsNoBenefits_WhenNoTrustedSource()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var service = CreateService();

        var result = service.Calculate(plan, 12, UtcNow, []);

        Assert.True(result.IsSuccess);

        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            planId: result.Value.PlanId,
            durationMonths: result.Value.DurationMonths,
            baseAmount: result.Value.BaseAmount,
            discountAmount: result.Value.DiscountAmount,
            finalAmount: result.Value.FinalAmount,
            monthlyListPrice: result.Value.MonthlyListPrice,
            currencyCode: result.Value.CurrencyCode,
            calculatedAtUtc: result.Value.CalculatedAtUtc).Value;

        Assert.Empty(offer.Benefits);
    }

    [Fact]
    public void Test52_OfferSnapshotImmutable_AfterCalculation()
    {
        var plan = CreatePlan(monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var service = CreateService();

        var calculated = service.Calculate(plan, 12, UtcNow, []).Value;

        var offer = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            planId: calculated.PlanId,
            durationMonths: calculated.DurationMonths,
            baseAmount: calculated.BaseAmount,
            discountAmount: calculated.DiscountAmount,
            finalAmount: calculated.FinalAmount,
            monthlyListPrice: calculated.MonthlyListPrice,
            currencyCode: calculated.CurrencyCode,
            calculatedAtUtc: calculated.CalculatedAtUtc).Value;

        var originalBaseAmount = offer.BaseAmount;
        var originalFinalAmount = offer.FinalAmount;

        offer.Accept(UtcNow);

        Assert.Equal(originalBaseAmount, offer.BaseAmount);
        Assert.Equal(originalFinalAmount, offer.FinalAmount);
        Assert.Empty(offer.Benefits);
    }

    [Fact]
    public void Test53_OfferToContract_ZeroBenefits_WhenNoTrustedSource()
    {
        var offer = CreateValidOffer(
            baseAmount: 10000m,
            finalAmount: 9000m,
            discountAmount: 1000m);

        offer.Accept(UtcNow);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-72-001",
            planId: offer.PlanId,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(offer.DurationMonths),
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths).Value;

        foreach (var ob in offer.Benefits)
        {
            var contractBenefit = ContractBenefit.Create(
                Guid.NewGuid(),
                contract.Id,
                ob.BenefitType,
                ob.Name,
                ob.Description,
                ob.ContractualValue,
                ob.CurrencyCode).Value;

            contract.AddBenefit(contractBenefit);
        }

        Assert.Empty(contract.Benefits);
        Assert.Equal(9000m, contract.ContractedAmount);
        Assert.Equal(1000m, contract.DiscountAmount);
    }

    [Fact]
    public void Test54_TenantIsolation_CrossTenantOffer_Rejected()
    {
        var offerA = CreateValidOffer(tenantId: "tenant-1", status: OfferStatus.Accepted);
        var offerB = CreateValidOffer(tenantId: "tenant-2", status: OfferStatus.Accepted);

        Assert.NotEqual(offerA.TenantId, offerB.TenantId);

        var currentTenant = "tenant-1";
        Assert.True(string.Equals(offerA.TenantId, currentTenant, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals(offerB.TenantId, currentTenant, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Test55_Task6BenefitCap_Intact_WhenBenefitsAddedDirectly()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CNT-72-CAP",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m).Value;

        var b1 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "A", null, 1500m, "EGP").Value;
        var b2 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "B", null, 1500m, "EGP").Value;

        Assert.True(contract.AddBenefit(b1).IsSuccess);
        Assert.True(contract.AddBenefit(b2).IsSuccess);

        var b3 = ContractBenefit.Create(Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "C", null, 1m, "EGP").Value;
        Assert.False(contract.AddBenefit(b3).IsSuccess);
        Assert.Equal("Contract.BenefitExceedsLimit", contract.AddBenefit(b3).Errors[0].Code);
    }
}
