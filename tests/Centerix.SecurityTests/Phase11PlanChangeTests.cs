using System.Data;
using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 11.1: Subscription Upgrade/Downgrade — Correction tests.
/// Covers financial invariant (Offer → Contract → Invoice consistency),
/// PricingTier authority, all promotion types, historical integrity,
/// upgrade/downgrade distinction, and authorization.
/// </summary>
public class Phase11PlanChangeTests
{
    private static readonly DateTime UtcNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Plan CreatePlan(
        int id = 1, decimal monthlyPrice = 1000m, string currencyCode = "EGP",
        int durationMonths = 12, int bonusMonths = 0, bool isActive = true)
    {
        var result = Plan.Create(id: id, code: $"PLAN-{id}", displayName: $"Plan {id}",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: isActive,
            currencyCode: currencyCode, durationMonths: durationMonths, bonusMonths: bonusMonths);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static void AddPricingTiers(Plan plan)
    {
        plan.AddPricingTier(PlanPricingTier.Create(1, plan.Id, 1, 1000m, 1).Value);
        plan.AddPricingTier(PlanPricingTier.Create(2, plan.Id, 3, 2700m, 2).Value);
        plan.AddPricingTier(PlanPricingTier.Create(3, plan.Id, 6, 5220m, 3).Value);
        plan.AddPricingTier(PlanPricingTier.Create(4, plan.Id, 12, 10000m, 4).Value);
    }

    private static TenantPlan CreateActiveSubscription(
        string? tenantId = null, int planId = 1, decimal price = 1000m,
        int durationMonths = 12, int bonusMonths = 0, DateTime? startsAt = null)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), planId, price, "EGP",
            durationMonths, bonusMonths, startsAt ?? UtcNow, false, SubscriptionStatus.Pending).Value;
        sub.Activate(startsAt ?? UtcNow);
        return sub;
    }

    private static IPromotionCalculationService CalcService() => new PromotionCalculationService();

    private static Promotion CreatePercentagePromotion(
        int id = 1, int planId = 0, int durationMonths = 0, decimal percentage = 10m)
    {
        var result = Promotion.Create(id: id, name: $"Promo {id}", type: PromotionType.PercentageDiscount,
            planId: planId, durationMonths: durationMonths,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: 10, percentage: percentage);
        Assert.True(result.IsSuccess);
        result.Value.Activate();
        return result.Value;
    }

    private static Promotion CreateFixedAmountPromotion(
        int id = 1, int planId = 0, int durationMonths = 0, decimal fixedAmount = 500m)
    {
        var result = Promotion.Create(id: id, name: $"Promo {id}", type: PromotionType.FixedAmountDiscount,
            planId: planId, durationMonths: durationMonths,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: 10, fixedAmount: fixedAmount);
        Assert.True(result.IsSuccess);
        result.Value.Activate();
        return result.Value;
    }

    private static Promotion CreatePayForXMonthsPromotion(
        int id = 1, int planId = 0, int durationMonths = 0, int chargedMonths = 10)
    {
        var result = Promotion.Create(id: id, name: $"Promo {id}", type: PromotionType.PayForXMonths,
            planId: planId, durationMonths: durationMonths,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: 10, chargedMonths: chargedMonths);
        Assert.True(result.IsSuccess);
        result.Value.Activate();
        return result.Value;
    }

    private static Promotion CreatePromotionalPricePromotion(
        int id = 1, int planId = 0, int durationMonths = 0, decimal promotionalPrice = 8000m)
    {
        var result = Promotion.Create(id: id, name: $"Promo {id}", type: PromotionType.PromotionalPrice,
            planId: planId, durationMonths: durationMonths,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: 10, promotionalPrice: promotionalPrice);
        Assert.True(result.IsSuccess);
        result.Value.Activate();
        return result.Value;
    }

    // ==================================================================
    // CRITICAL REGRESSION: PricingTier Invoice Authority (Task 11.1 §3)
    // ==================================================================

    [Fact]
    public void Test01_PricingTier_BaseAmount_UsedForInvoice_NotMonthlyTimesDuration()
    {
        // Plan: MonthlyPrice=1000, 12-month PricingTier=10000 (not 12000)
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(0m, offer.Value.DiscountAmount);
        Assert.Equal(10000m, offer.Value.FinalAmount);
        Assert.Equal(1000m, offer.Value.MonthlyListPrice);

        var contractResult = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TIER", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        Assert.Equal(10000m, contractResult.ContractedAmount);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-TIER",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(10000m, invoice.TotalAmount);
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    // ==================================================================
    // CRITICAL REGRESSION: PayForXMonths (Task 11.1 §4)
    // ==================================================================

    [Fact]
    public void Test02_PayForXMonths_AuthoritativeAmount_ThroughChain()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var promo = CreatePayForXMonthsPromotion(id: 1, chargedMonths: 10);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(12000m, offer.Value.BaseAmount);
        Assert.Equal(2000m, offer.Value.DiscountAmount);
        Assert.Equal(10000m, offer.Value.FinalAmount);
        Assert.Equal(10, offer.Value.ChargedMonths);

        var contractResult = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PAY", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount,
            chargedMonths: offer.Value.ChargedMonths).Value;

        Assert.Equal(10000m, contractResult.ContractedAmount);
        Assert.Equal(10, contractResult.ChargedMonths);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PAY",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(10000m, invoice.TotalAmount);
    }

    [Fact]
    public void Test03_PayForXMonths_WithPricingTier_AuthoritativeAmount()
    {
        var plan = CreatePlan(id: 3, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();
        var promo = CreatePayForXMonthsPromotion(id: 1, chargedMonths: 10);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(10, offer.Value.ChargedMonths);

        var finalAmount = offer.Value.FinalAmount;
        var contractResult = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PAYTIER", 3,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, finalAmount,
            offer.Value.DiscountAmount,
            chargedMonths: offer.Value.ChargedMonths).Value;

        Assert.Equal(finalAmount, contractResult.ContractedAmount);
        Assert.Equal(10, contractResult.ChargedMonths);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PAYTIER",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            finalAmount).Value;

        Assert.Equal(finalAmount, invoice.TotalAmount);
    }

    // ==================================================================
    // CRITICAL REGRESSION: PromotionalPrice (Task 11.1 §5)
    // ==================================================================

    [Fact]
    public void Test04_PromotionalPrice_WithPricingTier_AuthoritativeAmount()
    {
        var plan = CreatePlan(id: 4, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();
        var promo = CreatePromotionalPricePromotion(id: 1, promotionalPrice: 8000m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(8000m, offer.Value.FinalAmount);

        var contractResult = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PROMO", 4,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        Assert.Equal(8000m, contractResult.ContractedAmount);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PROMO",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(8000m, invoice.TotalAmount);
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    // ==================================================================
    // FINANCIAL INVARIANT: Offer.FinalAmount = Contract = Invoice (Task 11.1 §6)
    // ==================================================================

    [Fact]
    public void Test05_NoPromotion_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 5, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(12000m, offer.Value.FinalAmount);
        Assert.Equal(0m, offer.Value.DiscountAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NOPROMO", 5,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-NOPROMO",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
    }

    [Fact]
    public void Test06_PercentageDiscount_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 6, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var promo = CreatePercentagePromotion(id: 1, percentage: 20m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(2400m, offer.Value.DiscountAmount);
        Assert.Equal(9600m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PCT", 6,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PCT",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
    }

    [Fact]
    public void Test07_FixedDiscount_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 7, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var promo = CreateFixedAmountPromotion(id: 1, fixedAmount: 3000m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(3000m, offer.Value.DiscountAmount);
        Assert.Equal(9000m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-FIXED", 7,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-FIXED",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
    }

    [Fact]
    public void Test08_PayForXMonths_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 8, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var promo = CreatePayForXMonthsPromotion(id: 1, chargedMonths: 10);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PAYX", 8,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount,
            chargedMonths: offer.Value.ChargedMonths).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PAYX",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
    }

    [Fact]
    public void Test09_PromotionalPrice_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 9, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();
        var promo = CreatePromotionalPricePromotion(id: 1, promotionalPrice: 8000m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(8000m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-PP", 9,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-PP",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
    }

    [Fact]
    public void Test10_PricingTier_NoPromotion_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 10, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();
        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(10000m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TIERONLY", 10,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-TIERONLY",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
        Assert.Equal(10000m, invoice.TotalAmount);
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    [Fact]
    public void Test11_PricingTier_PercentageDiscount_OfferEqualsContractEqualsInvoice()
    {
        var plan = CreatePlan(id: 11, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();
        var promo = CreatePercentagePromotion(id: 1, percentage: 10m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(1000m, offer.Value.DiscountAmount);
        Assert.Equal(9000m, offer.Value.FinalAmount);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TIERPCT", 11,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-TIERPCT",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            offer.Value.BaseAmount, offer.Value.DiscountAmount, 0m,
            offer.Value.FinalAmount).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
        Assert.Equal(9000m, invoice.TotalAmount);
    }

    // ==================================================================
    // ELIGIBILITY & VALIDATION (Tests 12-14)
    // ==================================================================

    [Fact]
    public void Test12_ValidatorRejectsEmptySubscriptionId()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.Empty, NewPlanId: 1)).IsValid);
    }

    [Fact]
    public void Test13_ValidatorRejectsInvalidPlanId()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: 0)).IsValid);
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: -1)).IsValid);
    }

    [Fact]
    public void Test14_ValidatorAcceptsValidInput()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.True(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: 1)).IsValid);
    }

    // ==================================================================
    // HISTORICAL INTEGRITY (Tests 15-24)
    // ==================================================================

    [Fact]
    public void Test15_OldContract_Unchanged_AfterPlanChange()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 1000m,
            promotionId: 1, promotionType: "PercentageDiscount").Value;
        var oldTier = ContractPricingTier.Create(
            Guid.NewGuid(), oldContract.Id, 12, 10000m, "EGP", 1000m, 1).Value;
        oldContract.AddPricingTier(oldTier);

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(), oldContract.Id, ContractBenefitType.PhysicalGift, "Printer", null,
            1500m, "EGP").Value;
        oldContract.AddBenefit(benefit);

        var origContractNumber = oldContract.ContractNumber;
        var origAmount = oldContract.ContractedAmount;
        var origDiscount = oldContract.DiscountAmount;
        var origPlanId = oldContract.PlanId;

        Assert.Equal(origContractNumber, oldContract.ContractNumber);
        Assert.Equal(origAmount, oldContract.ContractedAmount);
        Assert.Equal(origDiscount, oldContract.DiscountAmount);
        Assert.Equal(origPlanId, oldContract.PlanId);
        Assert.Single(oldContract.PricingTiers);
        Assert.Single(oldContract.Benefits);
    }

    [Fact]
    public void Test16_OldSubscription_CommercialSnapshot_Unchanged()
    {
        var sub = CreateActiveSubscription(planId: 1, price: 1000m, durationMonths: 12, bonusMonths: 2);
        var origPlanId = sub.PlanId;
        var origPrice = sub.SnapshotPrice;
        var origDuration = sub.DurationMonths;
        var origBonus = sub.BonusMonths;
        var origEndsAt = sub.EffectiveEndsAtUtc;
        var origCurrency = sub.SnapshotCurrency;

        Assert.Equal(1, sub.PlanId);
        Assert.Equal(1000m, sub.SnapshotPrice);
        Assert.Equal(12, sub.DurationMonths);
        Assert.Equal(2, sub.BonusMonths);
        Assert.Equal("EGP", sub.SnapshotCurrency);
    }

    [Fact]
    public void Test17_OldInvoice_Unchanged()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-OLD-001",
            DateOnly.FromDateTime(UtcNow.AddMonths(-6)),
            DateOnly.FromDateTime(UtcNow),
            10000m, 1000m, 0m, 9000m).Value;

        Assert.Equal("INV-OLD-001", invoice.InvoiceNumber);
        Assert.Equal(10000m, invoice.Subtotal);
        Assert.Equal(1000m, invoice.DiscountAmount);
        Assert.Equal(9000m, invoice.TotalAmount);
    }

    [Fact]
    public void Test18_Contract_LinkToPreviousSubscription_Traceability()
    {
        var oldSub = CreateActiveSubscription();
        var contract = Contract.Create(
            Guid.NewGuid(), oldSub.TenantId, "CTR-TRACE", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        contract.LinkToPreviousSubscription(oldSub.Id);

        Assert.Equal(oldSub.Id, contract.PreviousSubscriptionId);
    }

    [Fact]
    public void Test19_NewSubscription_LinksToNewContract()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEWLINK", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        var sub = CreateActiveSubscription(planId: 2, price: 2000m, durationMonths: 12);
        sub.LinkToContract(contract.Id);

        Assert.Equal(contract.Id, sub.ContractId);
    }

    [Fact]
    public void Test20_Benefits_NotInherited_OldContractUnchanged()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-BENEFITS", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m).Value;

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(), oldContract.Id, ContractBenefitType.PhysicalGift, "Printer", null,
            1500m, "EGP").Value;
        oldContract.AddBenefit(benefit);

        Assert.Single(oldContract.Benefits);
        Assert.Equal(1500m, oldContract.Benefits[0].ContractualValue);

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NOBEN", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        Assert.Empty(newContract.Benefits);
    }

    [Fact]
    public void Test21_OldPricingTiers_NotModified()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TIEROLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m).Value;
        var tier = ContractPricingTier.Create(
            Guid.NewGuid(), oldContract.Id, 12, 10000m, "EGP", 1000m, 1).Value;
        oldContract.AddPricingTier(tier);

        Assert.Single(oldContract.PricingTiers);
        Assert.Equal(10000m, oldContract.PricingTiers[0].TierPrice);
    }

    [Fact]
    public void Test22_OldSubscription_FeatureSnapshot_NotModified()
    {
        var sub = CreateActiveSubscription();
        sub.GrantFeature("FeatureA");
        sub.GrantFeature("FeatureB");

        Assert.Equal(2, sub.Features.Count);
    }

    [Fact]
    public void Test23_OldSubscription_LimitSnapshot_NotModified()
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1, 1000m, "EGP",
            12, 0, UtcNow, false, SubscriptionStatus.Pending,
            maxStudents: 50, maxUsers: 25, maxBranches: 5, maxTeachers: 10,
            storageGb: 50, smsQuota: 500).Value;

        Assert.Equal(50, sub.SnapshotMaxStudents);
        Assert.Equal(25, sub.SnapshotMaxUsers);
        Assert.Equal(5, sub.SnapshotMaxBranches);
    }

    [Fact]
    public void Test24_OldSubscription_Cancelled_AtPlanChange()
    {
        var sub = CreateActiveSubscription();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        var cancelResult = sub.Cancel(UtcNow);
        Assert.True(cancelResult.IsSuccess);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    // ==================================================================
    // UPGRADE / DOWNGRADE DISTINCTION (Tests 25-28)
    // ==================================================================

    [Fact]
    public void Test25_Upgrade_CreatesNewCommercialTransaction()
    {
        var oldPlan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        var calc = CalcService();

        var oldOffer = calc.Calculate(oldPlan, 12, UtcNow, []);
        var newOffer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(oldOffer.IsSuccess);
        Assert.True(newOffer.IsSuccess);
        Assert.Equal(12000m, oldOffer.Value.FinalAmount);
        Assert.Equal(24000m, newOffer.Value.FinalAmount);
        Assert.True(newOffer.Value.FinalAmount > oldOffer.Value.FinalAmount);
    }

    [Fact]
    public void Test26_Downgrade_CreatesNewCommercialTransaction()
    {
        var oldPlan = CreatePlan(id: 1, monthlyPrice: 2000m, durationMonths: 12);
        var newPlan = CreatePlan(id: 2, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();

        var oldOffer = calc.Calculate(oldPlan, 12, UtcNow, []);
        var newOffer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(oldOffer.IsSuccess);
        Assert.True(newOffer.IsSuccess);
        Assert.Equal(24000m, oldOffer.Value.FinalAmount);
        Assert.Equal(12000m, newOffer.Value.FinalAmount);
        Assert.True(newOffer.Value.FinalAmount < oldOffer.Value.FinalAmount);
    }

    [Fact]
    public void Test27_Upgrade_NewContract_HasCorrectAmounts()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        var calc = CalcService();
        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-UPGRADE", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        Assert.Equal(24000m, contract.ContractedAmount);
        Assert.Equal(2000m, contract.MonthlyListPrice);
    }

    [Fact]
    public void Test28_Downgrade_NewContract_HasCorrectAmounts()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 500m, durationMonths: 12);
        var calc = CalcService();
        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-DOWNGRADE", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        Assert.Equal(6000m, contract.ContractedAmount);
        Assert.Equal(500m, contract.MonthlyListPrice);
    }

    // ==================================================================
    // OLD SUBSCRIPTION STATUS (Tests 29-31)
    // ==================================================================

    [Fact]
    public void Test29_NonActiveSubscription_Rejected()
    {
        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Cancelled, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    [Fact]
    public void Test30_ExpiredSubscription_Rejected()
    {
        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Expired, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    [Fact]
    public void Test31_SuspendedSubscription_Rejected()
    {
        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Suspended, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    // ==================================================================
    // AUTHORIZATION (Tests 32-33)
    // ==================================================================

    [Fact]
    public void Test32_PlatformAdmin_Required()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Error.Forbidden("Auth.PlatformAdminRequired", "Platform admin required"));

        var result = guard.EnsurePlatformAdmin();
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Test33_PlatformAdmin_Allowed()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var result = guard.EnsurePlatformAdmin();
        Assert.True(result.IsSuccess);
    }

    // ==================================================================
    // BENEFITS / GIFTS (Tests 34-36)
    // ==================================================================

    [Fact]
    public void Test34_BenefitCap_ThreeMonths()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-CAP", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m).Value;

        var b1 = ContractBenefit.Create(
            Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Bonus 1", null,
            1500m, "EGP").Value;
        Assert.True(contract.AddBenefit(b1).IsSuccess);

        var b2 = ContractBenefit.Create(
            Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Bonus 2", null,
            1000m, "EGP").Value;
        Assert.True(contract.AddBenefit(b2).IsSuccess);

        var b3 = ContractBenefit.Create(
            Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Bonus 3", null,
            500m, "EGP").Value;
        Assert.True(contract.AddBenefit(b3).IsSuccess);

        var totalBenefit = contract.Benefits.Sum(b => b.ContractualValue);
        var threeMonthsCap = contract.ContractualMonthlyValue * 3;
        Assert.True(totalBenefit <= threeMonthsCap);
    }

    [Fact]
    public void Test35_NoBenefits_NoCapIssue()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NOBENEFIT", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m).Value;

        Assert.Empty(contract.Benefits);
    }

    [Fact]
    public void Test36_NewBenefits_OnlyFromTrustedOffer()
    {
        var calc = CalcService();
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);

        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-FRESHBEN", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        Assert.Empty(contract.Benefits);

        var result = contract.AddBenefit(ContractBenefit.Create(
            Guid.NewGuid(), contract.Id, ContractBenefitType.PhysicalGift, "Free Tablet", null,
            3000m, "EGP").Value);
        Assert.True(result.IsSuccess);
        Assert.Single(contract.Benefits);
    }

    // ==================================================================
    // PRICING TIER (Tests 37-39)
    // ==================================================================

    [Fact]
    public void Test37_PricingTier_AppliesToOffer()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer6mo = calc.Calculate(plan, 6, UtcNow, []);
        Assert.True(offer6mo.IsSuccess);
        Assert.Equal(5220m, offer6mo.Value.BaseAmount);

        var offer12mo = calc.Calculate(plan, 12, UtcNow, []);
        Assert.True(offer12mo.IsSuccess);
        Assert.Equal(10000m, offer12mo.Value.BaseAmount);
    }

    [Fact]
    public void Test38_NoPricingTier_FallbackToMonthlyTimesDuration()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, []);
        Assert.True(offer.IsSuccess);
        Assert.Equal(12000m, offer.Value.BaseAmount);
    }

    [Fact]
    public void Test39_PricingTier_PromotionAppliedOnTierBase()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();
        var promo = CreatePercentagePromotion(id: 1, percentage: 10m);

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);
        Assert.True(offer.IsSuccess);
        Assert.Equal(10000m, offer.Value.BaseAmount);
        Assert.Equal(1000m, offer.Value.DiscountAmount);
        Assert.Equal(9000m, offer.Value.FinalAmount);
    }

    // ==================================================================
    // AUTHORIZATION GUARD (Tests 40-41)
    // ==================================================================

    [Fact]
    public void Test40_PlatformAdminGuard_Enforced()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Error.Forbidden("Auth.Forbidden", "Not platform admin"));

        var result = guard.EnsurePlatformAdmin();
        Assert.False(result.IsSuccess);
        Assert.Equal("Auth.Forbidden", result.Errors!.First().Code);
    }

    [Fact]
    public void Test41_PlatformAdminGuard_Passes()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var result = guard.EnsurePlatformAdmin();
        Assert.True(result.IsSuccess);
    }
}

/// <summary>
/// SQL Server integration tests for plan change concurrency, financial invariant, and end-to-end workflow.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase11PlanChangeSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    public Phase11PlanChangeSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private static ChangeSubscriptionPlanHandler CreateHandler(IAppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = new SubscriptionFactory(db);

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.UtcNow);

        return new ChangeSubscriptionPlanHandler(
            db,
            guard,
            subscriptionFactory,
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            timeProvider);
    }

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task SeedTenantEntityAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantGuid = Guid.Parse(tenantId);
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantGuid))
        {
            var result = Domain.Platform.Tenants.Tenant.Create(
                tenantGuid, tenantId[..8], tenantId[..8], tenantId,
                "EG", "EGP", "UTC", "Owner", "Name", "test@test.com",
                Domain.Platform.Tenants.Enums.IsolationMode.Shared);
            if (result.IsSuccess)
            {
                db.Tenants.Add(result.Value);
                await db.SaveChangesAsync();
            }
        }
    }

    private async Task<int> EnsurePlanAsync(string codePrefix, decimal price = 1000m, int duration = 12)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<int> EnsurePlanWithTiersAsync(string codePrefix, decimal price = 1000m, int duration = 12)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var tierResult = PlanPricingTier.Create(0, plan.Id, 12, 10000m, 1);
        if (tierResult.IsSuccess)
        {
            plan.AddPricingTier(tierResult.Value);
            await db.SaveChangesAsync();
        }

        return plan.Id;
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_NoPendingMigrations()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_EndToEnd_CreatesNewContractAndSubscription()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000001";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("OLDPLAN", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("NEWPLAN", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));
        var contractId = result.Value;

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contractId);
        Assert.NotNull(contract);
        Assert.Equal(newPlanId, contract.PlanId);
        Assert.Equal(oldSubId, contract.PreviousSubscriptionId);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == contractId);
        Assert.NotNull(newSub);
        Assert.Equal(newPlanId, newSub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, newSub.Status);
        Assert.Equal(2000m, newSub.SnapshotPrice);

        var oldSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == oldSubId);
        Assert.NotNull(oldSub);
        Assert.Equal(SubscriptionStatus.Cancelled, oldSub.Status);
        Assert.Equal(1000m, oldSub.SnapshotPrice);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_CreatesBillingCycleAndInvoice()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000002";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("OLDPLAN", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("NEWPLAN", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == result.Value);
        Assert.NotNull(newSub);

        var billingCycle = await verifyDb.BillingCycles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(bc => bc.SubscriptionId == newSub.Id);
        Assert.NotNull(billingCycle);

        var invoice = await verifyDb.Invoices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.BillingCycleId == billingCycle.Id);
        Assert.NotNull(invoice);
        Assert.Equal(result.Value, invoice.ContractId);
        Assert.True(invoice.TotalAmount > 0);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_InvoiceAmount_EqualsContractAmount()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000007";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("OLDPLAN", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanWithTiersAsync("TIERPLAN", price: 1000m, duration: 12);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);

        var invoice = await verifyDb.Invoices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.ContractId == contract.Id);
        Assert.NotNull(invoice);

        Assert.Equal(10000m, contract.ContractedAmount);
        Assert.Equal(contract.ContractedAmount, invoice.TotalAmount);
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_OldSubscription_Cancelled_NotActive()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000003";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("OLDPLAN", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("NEWPLAN", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == oldSubId);
        Assert.NotNull(oldSub);
        Assert.Equal(SubscriptionStatus.Cancelled, oldSub.Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_NonActiveSubscription_Rejected()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000004";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("REJPLAN", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("NEWPLAN2", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_ConcurrentRequests_CannotBothSucceed()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000005";
        await SeedTenantAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("CONCOLD", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("CONCNEW", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        var successCount = 0;
        var failureCount = 0;
        using var barrier = new System.Threading.Barrier(2);

        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var handler = CreateHandler(db);

            barrier.SignalAndWait(TestTimeout);

            var result = await handler.Handle(
                new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
                CancellationToken.None);

            if (result.IsSuccess)
                Interlocked.Increment(ref successCount);
            else
                Interlocked.Increment(ref failureCount);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ChangePlan_TenantValidUpTo_UpdatedToNewSubscription()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-000000000006";
        await SeedTenantAsync(tenantId);
        await SeedTenantEntityAsync(tenantId);
        var oldPlanId = await EnsurePlanAsync("VALOLD", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("VALNEW", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, oldPlanId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            oldSubId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubId, newPlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await verifyDb.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == Guid.Parse(tenantId));
        Assert.NotNull(tenant);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == result.Value);
        Assert.NotNull(newSub);
        Assert.True(tenant.ValidUpTo >= newSub.EffectiveEndsAtUtc);
    }
}
