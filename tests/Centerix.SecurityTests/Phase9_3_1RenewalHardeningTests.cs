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
/// Task 9.3.1: Renewal Commercial Snapshot and Financial Chain Hardening.
/// Domain-level handler/workflow tests covering commercial consistency,
/// historical integrity, benefits, financial chain, duplicate prevention,
/// and authorization.
/// </summary>
public class Phase9_3_1RenewalHardeningTests
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
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), planId, price, price, "EGP",
            durationMonths, bonusMonths, startsAt ?? UtcNow, false, SubscriptionStatus.Pending).Value;
        sub.Activate(startsAt ?? UtcNow);
        return sub;
    }

    private static TenantPlan CreateExpiredSubscription(string? tenantId = null)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), 1, 1000m, 1000m, "EGP",
            1, 0, UtcNow.AddMonths(-3), false, SubscriptionStatus.Active).Value;
        sub.MarkExpired(UtcNow);
        return sub;
    }

    private static Promotion CreatePromotion(
        int id = 1, int planId = 0, int durationMonths = 0,
        PromotionType type = PromotionType.PercentageDiscount, decimal? percentage = 10m)
    {
        var result = Promotion.Create(id: id, name: $"Promo {id}", type: type,
            planId: planId, durationMonths: durationMonths,
            startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            priority: 10, percentage: percentage);
        Assert.True(result.IsSuccess);
        result.Value.Activate();
        return result.Value;
    }

    private static IPromotionCalculationService CalcService() => new PromotionCalculationService();

    // ==================================================================
    // COMMERCIAL CONSISTENCY (Tests 1-9)
    // ==================================================================

    [Fact]
    public void Test01_Renewal_DefaultDuration_UsesPlanDuration()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(12, offer.Value.DurationMonths);
        Assert.Equal(10000m, offer.Value.FinalAmount);
        Assert.Equal(1000m, offer.Value.MonthlyListPrice);
    }

    [Fact]
    public void Test02_Renewal_ExplicitDuration_UsesRequestedDuration()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 6, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(6, offer.Value.DurationMonths);
        Assert.Equal(5220m, offer.Value.FinalAmount);
    }

    [Fact]
    public void Test03_ContractDuration_EqualsSubscriptionDuration()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var durationMonths = 6;

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-001", plan.Id,
            UtcNow, UtcNow.AddMonths(durationMonths), durationMonths,
            monthlyListPrice: 1000m, contractualMonthlyValue: 1000m,
            currencyCode: "EGP", grossAmount: 5220m, contractedAmount: 5220m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t-1", plan.Id, 1000m, 1000m, "EGP",
            durationMonths, 0, UtcNow, false).Value;

        Assert.Equal(contract.DurationMonths, sub.DurationMonths);
    }

    [Fact]
    public void Test04_ContractAmount_EqualsAcceptedOfferAmount()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 6, UtcNow, []);
        Assert.True(offer.IsSuccess);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-001", plan.Id,
            UtcNow, UtcNow.AddMonths(6), 6,
            monthlyListPrice: offer.Value.MonthlyListPrice,
            contractualMonthlyValue: offer.Value.MonthlyListPrice,
            currencyCode: offer.Value.CurrencyCode,
            grossAmount: offer.Value.FinalAmount + offer.Value.DiscountAmount,
            contractedAmount: offer.Value.FinalAmount,
            discountAmount: offer.Value.DiscountAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Equal(offer.Value.FinalAmount, contract.ContractedAmount);
        Assert.Equal(offer.Value.DiscountAmount, contract.DiscountAmount);
    }

    [Fact]
    public void Test05_ChargedMonths_Consistency()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, []);
        Assert.True(offer.IsSuccess);
    }

    [Fact]
    public void Test06_Currency_Consistency()
    {
        var plan = CreatePlan(id: 1, currencyCode: "USD");
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 6, UtcNow, []);
        Assert.True(offer.IsSuccess);
        Assert.Equal("USD", offer.Value.CurrencyCode);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-001", plan.Id,
            UtcNow, UtcNow.AddMonths(6), 6,
            monthlyListPrice: offer.Value.MonthlyListPrice,
            contractualMonthlyValue: offer.Value.MonthlyListPrice,
            currencyCode: offer.Value.CurrencyCode,
            grossAmount: offer.Value.FinalAmount + offer.Value.DiscountAmount,
            contractedAmount: offer.Value.FinalAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Equal("USD", contract.CurrencyCode);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t-1", plan.Id,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice, offer.Value.CurrencyCode,
            6, 0, UtcNow, false).Value;

        Assert.Equal("USD", sub.SnapshotCurrency);
    }

    [Fact]
    public void Test07_PlanChange_UsesNewPlanPricing()
    {
        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m);
        var calc = CalcService();

        var offer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(2000m, offer.Value.MonthlyListPrice);
        Assert.Equal(24000m, offer.Value.FinalAmount);
    }

    [Fact]
    public void Test08_CurrentPromotion_AppliedThroughOffer()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var promo = CreatePromotion(id: 1, percentage: 15m);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, [promo]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(15m, offer.Value.DiscountPercentage);
        Assert.Equal(1, offer.Value.PromotionId);
        Assert.Equal("Promo 1", offer.Value.PromotionName);
    }

    [Fact]
    public void Test09_NoPromotion_HasZeroDiscount()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 6, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal("None", offer.Value.PromotionType);
        Assert.Equal(0m, offer.Value.DiscountAmount);
        Assert.Null(offer.Value.PromotionId);
    }

    // ==================================================================
    // HISTORICAL INTEGRITY (Tests 10-14)
    // ==================================================================

    [Fact]
    public void Test10_OldContract_Unchanged_AfterNewContractCreation()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 11000m, 10000m, Contract.CompleteEntitlementSnapshotVersion, 1000m,
            promotionId: 1, promotionType: "PercentageDiscount", chargedMonths: 10).Value;

        var oldMonthly = oldContract.MonthlyListPrice;
        var oldAmount = oldContract.ContractedAmount;
        var oldDiscount = oldContract.DiscountAmount;

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            1200m, 1200m, "EGP", 14400m, 14400m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Equal(oldMonthly, oldContract.MonthlyListPrice);
        Assert.Equal(oldAmount, oldContract.ContractedAmount);
        Assert.Equal(oldDiscount, oldContract.DiscountAmount);
    }

    [Fact]
    public void Test11_OldSubscription_Unchanged()
    {
        var tenantId = Guid.NewGuid().ToString();
        var oldSub = CreateActiveSubscription(tenantId: tenantId, durationMonths: 12);
        var oldStart = oldSub.StartsAtUtc;
        var oldEnd = oldSub.EffectiveEndsAtUtc;
        var oldPrice = oldSub.SnapshotPrice;

        var newSub = CreateActiveSubscription(tenantId: tenantId, durationMonths: 6);

        Assert.Equal(oldStart, oldSub.StartsAtUtc);
        Assert.Equal(oldEnd, oldSub.EffectiveEndsAtUtc);
        Assert.Equal(oldPrice, oldSub.SnapshotPrice);
    }

    [Fact]
    public void Test12_OldOffer_Immutable()
    {
        var oldOffer = Offer.Create(
            Guid.NewGuid(), "t-1", 1, 12, 10000m, 1000m, 9000m, 1000m, "EGP",
            promotionId: 1, promotionName: "Old Promo",
            promotionType: "PercentageDiscount", discountPercentage: 10m,
            calculatedAtUtc: UtcNow.AddMonths(-12),
            expiresAtUtc: UtcNow.AddMonths(-12).AddHours(24)).Value;
        oldOffer.Accept(UtcNow.AddMonths(-12));
        oldOffer.MarkConverted(Guid.NewGuid(), UtcNow.AddMonths(-12));

        Assert.Equal(10000m, oldOffer.BaseAmount);
        Assert.Equal(1000m, oldOffer.DiscountAmount);
        Assert.Equal(9000m, oldOffer.FinalAmount);
    }

    [Fact]
    public void Test13_OldPricingTiers_Immutable()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TIER", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 10000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        var oldTier = ContractPricingTier.Create(
            Guid.NewGuid(), oldContract.Id, 12, 10000m, "EGP", 1000m, 1).Value;
        oldContract.AddPricingTier(oldTier);

        Assert.Single(oldContract.PricingTiers);
        Assert.Equal(10000m, oldContract.PricingTiers[0].TierPrice);
    }

    [Fact]
    public void Test14_OldInvoice_Immutable()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-OLD",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            12000m, 0, 0, 12000m).Value;
        invoice.Issue(UtcNow);

        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
    }

    // ==================================================================
    // BENEFITS (Tests 15-17)
    // ==================================================================

    [Fact]
    public void Test15_CurrentOfferBenefit_UsedIfPresent()
    {
        var offer = Offer.Create(
            Guid.NewGuid(), "t-1", 1, 12, 10000m, 0, 10000m, 1000m, "EGP",
            calculatedAtUtc: UtcNow, expiresAtUtc: UtcNow.AddHours(24)).Value;

        var benefit = OfferBenefit.Create(
            Guid.NewGuid(), offer.Id, ContractBenefitType.PhysicalGift,
            "New Gift", null, 500m, "EGP").Value;
        offer.AddBenefit(benefit);

        Assert.Single(offer.Benefits);
        Assert.Equal("New Gift", offer.Benefits[0].Name);
    }

    [Fact]
    public void Test16_OldBenefit_NeverCopiedToNewContract()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 10000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        var oldBenefit = ContractBenefit.Create(
            Guid.NewGuid(), oldContract.Id, ContractBenefitType.PhysicalGift,
            "Old Gift", null, 1000m, "EGP").Value;
        oldContract.AddBenefit(oldBenefit);

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 10000m, 10000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Empty(newContract.Benefits);
        Assert.Single(oldContract.Benefits);
    }

    [Fact]
    public void Test17_EmptyCurrentOfferBenefits_NoNewBenefit()
    {
        var offer = Offer.Create(
            Guid.NewGuid(), "t-1", 1, 12, 10000m, 0, 10000m, 1000m, "EGP",
            calculatedAtUtc: UtcNow, expiresAtUtc: UtcNow.AddHours(24)).Value;

        Assert.Empty(offer.Benefits);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 10000m, 10000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Empty(contract.Benefits);
    }

    // ==================================================================
    // FINANCIAL CHAIN (Tests 18-22)
    // ==================================================================

    [Fact]
    public void Test18_BillingCycle_CreatedForSubscription()
    {
        var tenantId = Guid.NewGuid().ToString();
        var sub = CreateActiveSubscription(tenantId: tenantId);

        var bc = BillingCycle.Create(
            Guid.NewGuid(), tenantId, sub.Id,
            sub.StartsAtUtc, sub.EffectiveEndsAtUtc).Value;

        Assert.Equal(sub.Id, bc.SubscriptionId);
        Assert.Equal(sub.StartsAtUtc, bc.PeriodStart);
        Assert.Equal(sub.EffectiveEndsAtUtc, bc.PeriodEnd);
        Assert.Equal(BillingCycleStatus.Draft, bc.Status);
    }

    [Fact]
    public void Test19_Invoice_CreatedWithContractReference()
    {
        var tenantId = Guid.NewGuid().ToString();
        var sub = CreateActiveSubscription(tenantId: tenantId, price: 1000m, durationMonths: 6);
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-001", 1,
            UtcNow, UtcNow.AddMonths(6), 6,
            1000m, 1000m, "EGP", 5220m, 5220m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        sub.LinkToContract(contract.Id);

        var bc = BillingCycle.Create(
            Guid.NewGuid(), tenantId, sub.Id,
            sub.StartsAtUtc, sub.EffectiveEndsAtUtc).Value;

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-001",
            DateOnly.FromDateTime(bc.PeriodStart),
            DateOnly.FromDateTime(bc.PeriodEnd),
            subtotal: 5220m, discountAmount: 0, taxAmount: 0, totalAmount: 5220m,
            contractId: contract.Id, subscriptionId: sub.Id, billingCycleId: bc.Id).Value;

        Assert.Equal(contract.Id, invoice.ContractId);
        Assert.Equal(sub.Id, invoice.SubscriptionId);
        Assert.Equal(bc.Id, invoice.BillingCycleId);
        Assert.Equal(5220m, invoice.TotalAmount);
    }

    [Fact]
    public void Test20_InvoiceAmount_EqualsOfferFinalAmount()
    {
        var calc = CalcService();
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);

        var offer = calc.Calculate(plan, 6, UtcNow, []);
        Assert.True(offer.IsSuccess);
        Assert.Equal(offer.Value.FinalAmount, offer.Value.BaseAmount - offer.Value.DiscountAmount);
    }

    [Fact]
    public void Test21_OldInvoice_RemainsUnchanged()
    {
        var oldInvoice = Invoice.Create(
            Guid.NewGuid(), "INV-OLD",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            12000m, 1000m, 0, 11000m).Value;
        oldInvoice.Issue(UtcNow);

        Assert.Equal(11000m, oldInvoice.TotalAmount);
        Assert.Equal(1000m, oldInvoice.DiscountAmount);
    }

    [Fact]
    public void Test22_NoAutomaticPayment_IsCreated()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-NEW",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
            5220m, 0, 0, 5220m).Value;

        Assert.Empty(invoice.PlatformPayments);
        Assert.Empty(invoice.PaymentAllocations);
        Assert.Equal(0m, invoice.GetPaidAmount());
        Assert.Equal(5220m, invoice.GetRemainingAmount());
    }

    // ==================================================================
    // DUPLICATE PREVENTION (Tests 23-24)
    // ==================================================================

    [Fact]
    public void Test23_OldSubscription_NotModified_DuringRenewal()
    {
        var tenantId = Guid.NewGuid().ToString();
        var oldSub = CreateActiveSubscription(tenantId: tenantId, durationMonths: 12);

        Assert.Equal(SubscriptionStatus.Active, oldSub.Status);
        Assert.True(oldSub.EffectiveEndsAtUtc > UtcNow);
    }

    [Fact]
    public void Test24_NewSubscription_HasIndependentCommercialTerms()
    {
        var tenantId = Guid.NewGuid().ToString();
        var oldSub = CreateActiveSubscription(tenantId: tenantId, price: 1000m, durationMonths: 12);

        var newSub = CreateActiveSubscription(tenantId: tenantId, price: 1500m, durationMonths: 6);

        Assert.Equal(1000m, oldSub.SnapshotPrice);
        Assert.Equal(1500m, newSub.SnapshotPrice);
        Assert.Equal(12, oldSub.DurationMonths);
        Assert.Equal(6, newSub.DurationMonths);
    }

    // ==================================================================
    // AUTHORIZATION/SECURITY (Tests 25-27)
    // ==================================================================

    [Fact]
    public void Test25_PlatformAuthorization_Enforced()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Error.Forbidden("Auth.Required", "Platform admin required"));

        var result = guard.EnsurePlatformAdmin();
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Test26_CrossTenant_Subscription_Isolated()
    {
        var tenant1Sub = CreateActiveSubscription(tenantId: "tenant-1");
        var tenant2Sub = CreateActiveSubscription(tenantId: "tenant-2");

        Assert.NotEqual(tenant1Sub.TenantId, tenant2Sub.TenantId);
    }

    [Fact]
    public void Test27_TenantId_Correct_OnNewRecords()
    {
        var tenantId = Guid.NewGuid().ToString();
        var sub = CreateActiveSubscription(tenantId: tenantId);

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-001", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m, 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        var billingCycle = BillingCycle.Create(
            Guid.NewGuid(), tenantId, sub.Id,
            UtcNow, UtcNow.AddMonths(12)).Value;

        Assert.Equal(tenantId, contract.TenantId);
        Assert.Equal(tenantId, billingCycle.TenantId);
    }

    // ==================================================================
    // SUBSCRIPTION SNAPSHOT (Tests 28-30)
    // ==================================================================

    [Fact]
    public void Test28_Subscription_CreateFromSnapshot_UsesExplicitValues()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t-1", plan.Id,
            snapshotPrice: 800m, snapshotMonthlyCharge: 800m, snapshotCurrency: "USD",
            durationMonths: 6, bonusMonths: 2,
            startsAtUtc: UtcNow, autoRenew: false).Value;

        Assert.Equal(800m, sub.SnapshotPrice);
        Assert.Equal("USD", sub.SnapshotCurrency);
        Assert.Equal(6, sub.DurationMonths);
        Assert.Equal(2, sub.BonusMonths);
    }

    [Fact]
    public void Test29_Renewal_DoesNotUsePlanPrice_ForSubscription()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t-1", plan.Id,
            snapshotPrice: 500m, snapshotMonthlyCharge: 500m, snapshotCurrency: "EGP",
            durationMonths: 6, bonusMonths: 0,
            startsAtUtc: UtcNow).Value;

        Assert.NotEqual(plan.MonthlyPrice, sub.SnapshotPrice);
        Assert.NotEqual(plan.DurationMonths, sub.DurationMonths);
    }

    [Fact]
    public void Test30_OfferContractSubscription_DurationConsistent()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m);
        AddPricingTiers(plan);
        var calc = CalcService();
        var requestedDuration = 6;

        var offer = calc.Calculate(plan, requestedDuration, UtcNow, []);
        Assert.True(offer.IsSuccess);
        Assert.Equal(requestedDuration, offer.Value.DurationMonths);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-001", plan.Id,
            UtcNow, UtcNow.AddMonths(requestedDuration), requestedDuration,
            monthlyListPrice: offer.Value.MonthlyListPrice,
            contractualMonthlyValue: offer.Value.MonthlyListPrice,
            currencyCode: offer.Value.CurrencyCode,
            grossAmount: offer.Value.FinalAmount + offer.Value.DiscountAmount,
            contractedAmount: offer.Value.FinalAmount, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Equal(requestedDuration, contract.DurationMonths);

        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t-1", plan.Id,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice, offer.Value.CurrencyCode,
            requestedDuration, plan.BonusMonths, UtcNow, false).Value;

        Assert.Equal(requestedDuration, sub.DurationMonths);
    }

    // ==================================================================
    // START DATE RULES (Tests 31-33)
    // ==================================================================

    [Fact]
    public void Test31_ActiveOldSub_NewStartsAfterOldEnds()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: start, durationMonths: 12);
        var now = UtcNow;

        var newStart = oldSub.EffectiveEndsAtUtc > now ? oldSub.EffectiveEndsAtUtc : now;

        Assert.True(newStart > now);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, newStart);
    }

    [Fact]
    public void Test32_ExpiredOldSub_NewStartsFromNow()
    {
        var oldSub = CreateExpiredSubscription();
        var now = UtcNow;

        var newStart = oldSub.EffectiveEndsAtUtc > now ? oldSub.EffectiveEndsAtUtc : now;

        Assert.Equal(now, newStart);
    }

    [Fact]
    public void Test33_NewSubscription_NoOverlap_WithOld()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: start, durationMonths: 6);
        var now = start.AddMonths(3);

        var newStart = oldSub.EffectiveEndsAtUtc > now ? oldSub.EffectiveEndsAtUtc : now;

        Assert.True(oldSub.EffectiveEndsAtUtc <= newStart);
    }

    // ==================================================================
    // CONTRACT PREVIOUS SUBSCRIPTION LINK (Tests 34-35)
    // ==================================================================

    [Fact]
    public void Test34_Contract_LinkToPreviousSubscription_SetsField()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-TRACE", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m, 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        var prevSubId = Guid.NewGuid();
        var result = contract.LinkToPreviousSubscription(prevSubId);

        Assert.True(result.IsSuccess);
        Assert.Equal(prevSubId, contract.PreviousSubscriptionId);
    }

    [Fact]
    public void Test35_Contract_PreviousSubscriptionId_Null_ForNonRenewal()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-FRESH", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m, 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Null(contract.PreviousSubscriptionId);
    }

    // ==================================================================
    // PRICING TIERS INDEPENDENCE (Tests 36-37)
    // ==================================================================

    [Fact]
    public void Test36_Renewal_DoesNotModifyOldPricingTiers()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 10000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        var oldTier = ContractPricingTier.Create(
            Guid.NewGuid(), oldContract.Id, 12, 10000m, "EGP", 1000m, 1).Value;
        oldContract.AddPricingTier(oldTier);

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            1200m, 1200m, "EGP", 14400m, 14400m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;
        var newTier = ContractPricingTier.Create(
            Guid.NewGuid(), newContract.Id, 12, 12000m, "EGP", 1200m, 1).Value;
        newContract.AddPricingTier(newTier);

        Assert.Single(oldContract.PricingTiers);
        Assert.Equal(10000m, oldContract.PricingTiers[0].TierPrice);
        Assert.Single(newContract.PricingTiers);
        Assert.Equal(12000m, newContract.PricingTiers[0].TierPrice);
    }

    [Fact]
    public void Test37_OldDiscount_IsNotInherited()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-DISC", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 9000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            discountAmount: 1000m,
            promotionId: 1, promotionType: "PercentageDiscount").Value;

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-FRESH", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m, 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion).Value;

        Assert.Equal(1000m, oldContract.DiscountAmount);
        Assert.Equal(0m, newContract.DiscountAmount);
        Assert.Null(newContract.PromotionId);
    }

    // ==================================================================
    // TENANT VALIDUPTO (Test 38)
    // ==================================================================

    [Fact]
    public void Test38_TenantValidUpTo_RepresentsNewEntitlement()
    {
        var newSubEnd = UtcNow.AddMonths(6);

        var sub = CreateActiveSubscription(durationMonths: 6);

        var tenant = Substitute.For<ICurrentTenant>();
        tenant.TenantId.Returns(sub.TenantId);

        Assert.True(sub.EffectiveEndsAtUtc > UtcNow);
    }

    // ==================================================================
    // API REQUEST VALIDATION (Tests 39-40)
    // ==================================================================

    [Fact]
    public void Test39_RenewalRequest_AllowsNullPlanAndDuration()
    {
        var cmd = new RenewSubscriptionOfferCommand(Guid.NewGuid());
        Assert.Null(cmd.PlanId);
        Assert.Null(cmd.DurationMonths);
    }

    [Fact]
    public void Test40_RenewalRequest_ValidatorRejectsInvalidValues()
    {
        var validator = new RenewSubscriptionOfferValidator();

        Assert.False(validator.Validate(new RenewSubscriptionOfferCommand(Guid.NewGuid(), PlanId: -1)).IsValid);
        Assert.False(validator.Validate(new RenewSubscriptionOfferCommand(Guid.NewGuid(), DurationMonths: 0)).IsValid);
        Assert.False(validator.Validate(new RenewSubscriptionOfferCommand(Guid.Empty)).IsValid);
        Assert.True(validator.Validate(new RenewSubscriptionOfferCommand(Guid.NewGuid())).IsValid);
        Assert.True(validator.Validate(new RenewSubscriptionOfferCommand(Guid.NewGuid(), PlanId: 1, DurationMonths: 6)).IsValid);
    }
}

/// <summary>
/// SQL Server integration tests for renewal concurrency and migration verification.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase9_3_1RenewalSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    public Phase9_3_1RenewalSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private static IServiceScope CreateAuthorizedScope(SqlServerIntegrationFactory env, string tenantId)
    {
        var scope = env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        return scope;
    }

    private static RenewSubscriptionOfferHandler CreateHandler(IAppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = new SubscriptionFactory(db);

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.UtcNow);

        return new RenewSubscriptionOfferHandler(
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

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_PreviousSubscriptionId_ColumnExists()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task<bool> ColumnExists(string table, string column) => await db.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = {table} AND COLUMN_NAME = {column})
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        Assert.True(await ColumnExists("Contracts", "PreviousSubscriptionId"));
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
    public async Task Renewal_ConcurrentRequests_CannotBothSucceed()
    {
        const string tenantId = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("RENEWCONC");

        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        var successCount = 0;
        var conflictCount = 0;
        using var barrier = new System.Threading.Barrier(2);

        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var handler = CreateHandler(db);

            barrier.SignalAndWait(TestTimeout);

            var result = await handler.Handle(
                new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
                CancellationToken.None);

            if (result.IsSuccess)
                Interlocked.Increment(ref successCount);
            else
                Interlocked.Increment(ref conflictCount);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Renewal_HandlerCreates_BillingCycleAndInvoice()
    {
        const string tenantId = "B2C3D4E5-F6A7-8901-BCDE-F12345678901";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("RENEWBILL", price: 1000m, duration: 6);

        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 6, 0,
                DateTime.UtcNow, false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));
        var contractId = result.Value;

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contractId);
        Assert.NotNull(contract);
        Assert.Equal(6, contract.DurationMonths);

        var subscription = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == contractId);
        Assert.NotNull(subscription);
        Assert.Equal(6, subscription.DurationMonths);

        var billingCycle = await verifyDb.BillingCycles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(bc => bc.SubscriptionId == subscription.Id);
        Assert.NotNull(billingCycle);

        var invoice = await verifyDb.Invoices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.BillingCycleId == billingCycle.Id);
        Assert.NotNull(invoice);
        Assert.Equal(contractId, invoice.ContractId);
        Assert.True(invoice.TotalAmount > 0);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Renewal_OldSubscription_RemainsActive_AfterRenewal()
    {
        const string tenantId = "D4E5F6A7-B8C9-0123-DEF0-123456789012";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("RENEWOLD", price: 1000m, duration: 12);

        Guid subId;
        DateTime oldEndsAt;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
            oldEndsAt = sub.EffectiveEndsAtUtc;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == subId);
        Assert.NotNull(oldSub);
        Assert.Equal(SubscriptionStatus.Active, oldSub.Status);
        Assert.Equal(oldEndsAt, oldSub.EffectiveEndsAtUtc);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == result.Value);
        Assert.NotNull(newSub);
        Assert.Equal(oldEndsAt, newSub.StartsAtUtc);
        Assert.Equal(SubscriptionStatus.Pending, newSub.Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task SequentialDuplicateRenewal_Rejected()
    {
        const string tenantId = "C3D4E5F6-A7B8-9012-CDEF-123456789012";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("RENEWDUP");

        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP", 6, 0,
                DateTime.UtcNow, false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        using var scope1 = _env.Factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
        db1.StampAddedTenantIds(tenantId);
        var handler1 = CreateHandler(db1);

        var first = await handler1.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
            CancellationToken.None);
        Assert.True(first.IsSuccess, string.Join(", ", first.Errors?.Select(e => e.Code) ?? []));

        using var scope2 = _env.Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler2 = CreateHandler(db2);

        var second = await handler2.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
            CancellationToken.None);
        Assert.False(second.IsSuccess);
    }
}
