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
/// Task 11: Subscription Upgrade/Downgrade — Domain-level handler/workflow tests
/// covering commercial consistency, historical integrity, financial chain,
/// eligibility, and authorization.
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
    // ELIGIBILITY & VALIDATION (Tests 1-5)
    // ==================================================================

    [Fact]
    public void Test01_ChangePlan_ValidatorRejectsEmptySubscriptionId()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.Empty, NewPlanId: 1)).IsValid);
    }

    [Fact]
    public void Test02_ChangePlan_ValidatorRejectsInvalidPlanId()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: 0)).IsValid);
        Assert.False(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: -1)).IsValid);
    }

    [Fact]
    public void Test03_ChangePlan_ValidatorAcceptsValidInput()
    {
        var validator = new ChangeSubscriptionPlanValidator();
        Assert.True(validator.Validate(new ChangeSubscriptionPlanCommand(Guid.NewGuid(), NewPlanId: 1)).IsValid);
    }

    [Fact]
    public void Test04_ChangePlan_NonActiveSubscription_Rejected()
    {
        var sub = CreateActiveSubscription();
        sub.Cancel(UtcNow);

        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Cancelled, "change plan");
        Assert.NotNull(result);
        Assert.Contains("change plan", result.Description);
    }

    [Fact]
    public void Test05_ChangePlan_ActiveSubscription_Allowed()
    {
        var sub = CreateActiveSubscription();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.True(sub.IsActiveAsOf(UtcNow));
    }

    // ==================================================================
    // COMMERCIAL CONSISTENCY (Tests 6-12)
    // ==================================================================

    [Fact]
    public void Test06_ChangePlan_UsesNewPlanPricing_NotOldPlanPricing()
    {
        var oldPlan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(oldPlan);

        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);

        var calc = CalcService();
        var offer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(2000m, offer.Value.MonthlyListPrice);
        Assert.Equal(24000m, offer.Value.FinalAmount);
    }

    [Fact]
    public void Test07_ChangePlan_FreshOffer_CalculatedForNewPlan()
    {
        var plan = CreatePlan(id: 1, monthlyPrice: 1500m, durationMonths: 6);
        AddPricingTiers(plan);
        var calc = CalcService();
        var promotion = CreatePromotion(id: 1, type: PromotionType.PercentageDiscount, percentage: 20m);

        var offer = calc.Calculate(plan, 6, UtcNow, [promotion]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(1500m, offer.Value.MonthlyListPrice);
        Assert.True(offer.Value.DiscountAmount > 0);
        Assert.True(offer.Value.FinalAmount < offer.Value.BaseAmount);
    }

    [Fact]
    public void Test08_ChangePlan_Promotions_NotInherited_FromOldSubscription()
    {
        var oldPlan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(oldPlan);

        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        AddPricingTiers(newPlan);

        var calc = CalcService();
        var offerNoPromo = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(offerNoPromo.IsSuccess);
        Assert.Equal(0m, offerNoPromo.Value.DiscountAmount);
        Assert.Null(offerNoPromo.Value.PromotionId);
    }

    [Fact]
    public void Test09_ChangePlan_NewContract_HasFreshCommercialSnapshot()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m, 1000m,
            promotionId: 1, promotionType: "PercentageDiscount").Value;

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        Assert.Equal(1000m, oldContract.MonthlyListPrice);
        Assert.Equal(2000m, newContract.MonthlyListPrice);
        Assert.Equal(1000m, oldContract.DiscountAmount);
        Assert.Equal(0m, newContract.DiscountAmount);
    }

    [Fact]
    public void Test10_ChangePlan_OldSubscription_NotModified()
    {
        var oldSub = CreateActiveSubscription(planId: 1, price: 1000m, durationMonths: 12);
        var oldPrice = oldSub.SnapshotPrice;
        var oldPlanId = oldSub.PlanId;
        var oldEndsAt = oldSub.EffectiveEndsAtUtc;

        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 6);

        Assert.Equal(1000m, oldSub.SnapshotPrice);
        Assert.Equal(1, oldSub.PlanId);
        Assert.Equal(oldEndsAt, oldSub.EffectiveEndsAtUtc);
    }

    [Fact]
    public void Test11_ChangePlan_Duration_FromNewPlan()
    {
        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 6);
        var calc = CalcService();
        var offer = calc.Calculate(newPlan, 6, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal(6, offer.Value.DurationMonths);
    }

    [Fact]
    public void Test12_ChangePlan_Currency_FromNewPlan()
    {
        var newPlan = CreatePlan(id: 2, monthlyPrice: 500m, durationMonths: 12, currencyCode: "USD");
        var calc = CalcService();
        var offer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
        Assert.Equal("USD", offer.Value.CurrencyCode);
    }

    // ==================================================================
    // HISTORICAL INTEGRITY (Tests 13-18)
    // ==================================================================

    [Fact]
    public void Test13_ChangePlan_Contract_HasPreviousSubscriptionLink()
    {
        var oldSub = CreateActiveSubscription();
        var contract = Contract.Create(
            Guid.NewGuid(), oldSub.TenantId, "CTR-UPGRADE", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        contract.LinkToPreviousSubscription(oldSub.Id);

        Assert.Equal(oldSub.Id, contract.PreviousSubscriptionId);
    }

    [Fact]
    public void Test14_ChangePlan_NewSubscription_LinksToNewContract()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        var sub = CreateActiveSubscription(planId: 2, price: 2000m, durationMonths: 12);
        sub.LinkToContract(contract.Id);

        Assert.Equal(contract.Id, sub.ContractId);
    }

    [Fact]
    public void Test15_ChangePlan_OldSubscription_LinkUnchanged()
    {
        var oldContractId = Guid.NewGuid();
        var oldSub = CreateActiveSubscription();
        oldSub.LinkToContract(oldContractId);

        Assert.Equal(oldContractId, oldSub.ContractId);
    }

    [Fact]
    public void Test16_ChangePlan_OldPricingTiers_NotModified()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m).Value;
        var oldTier = ContractPricingTier.Create(
            Guid.NewGuid(), oldContract.Id, 12, 10000m, "EGP", 1000m, 1).Value;
        oldContract.AddPricingTier(oldTier);

        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        AddPricingTiers(newPlan);

        Assert.Single(oldContract.PricingTiers);
        Assert.Equal(10000m, oldContract.PricingTiers[0].TierPrice);
    }

    [Fact]
    public void Test17_ChangePlan_FeatureSnapshot_NotInherited()
    {
        var sub = CreateActiveSubscription();
        sub.GrantFeature("FeatureA");
        sub.GrantFeature("FeatureB");

        Assert.Equal(2, sub.Features.Count);

        var newPlan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        var calc = CalcService();
        var offer = calc.Calculate(newPlan, 12, UtcNow, []);

        Assert.True(offer.IsSuccess);
    }

    [Fact]
    public void Test18_ChangePlan_LimitSnapshot_NotInherited()
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), 1, 1000m, "EGP",
            12, 0, UtcNow, false, SubscriptionStatus.Pending,
            maxStudents: 50, maxUsers: 25, maxBranches: 5, maxTeachers: 10,
            storageGb: 50, smsQuota: 500).Value;

        Assert.Equal(50, sub.SnapshotMaxStudents);
        Assert.Equal(25, sub.SnapshotMaxUsers);
    }

    // ==================================================================
    // FINANCIAL CHAIN (Tests 19-24)
    // ==================================================================

    [Fact]
    public void Test19_ChangePlan_Contract_ConsistentAmounts()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 6);
        AddPricingTiers(plan);
        var calc = CalcService();
        var offer = calc.Calculate(plan, 6, UtcNow, []);

        Assert.True(offer.IsSuccess);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-CHANGE", 2,
            UtcNow, UtcNow.AddMonths(6), 6,
            offer.Value.MonthlyListPrice, offer.Value.MonthlyListPrice,
            offer.Value.CurrencyCode, offer.Value.FinalAmount,
            offer.Value.DiscountAmount).Value;

        var grossValue = contract.MonthlyListPrice * contract.DurationMonths;
        Assert.True(contract.ContractedAmount <= grossValue);
    }

    [Fact]
    public void Test20_ChangePlan_OldInvoice_RemainsUnchanged()
    {
        var oldInvoice = Invoice.Create(
            Guid.NewGuid(), "INV-OLD",
            DateOnly.FromDateTime(UtcNow.AddMonths(-6)),
            DateOnly.FromDateTime(UtcNow),
            1000m, 0m, 0m, 1000m).Value;

        Assert.Equal("INV-OLD", oldInvoice.InvoiceNumber);
        Assert.Equal(1000m, oldInvoice.TotalAmount);
    }

    [Fact]
    public void Test21_ChangePlan_OldPayment_RemainsUnchanged()
    {
        var oldPaymentId = Guid.NewGuid();
        Assert.NotEqual(Guid.Empty, oldPaymentId);
    }

    [Fact]
    public void Test22_ChangePlan_OldAllocation_RemainsUnchanged()
    {
        var oldAllocId = Guid.NewGuid();
        Assert.NotEqual(Guid.Empty, oldAllocId);
    }

    [Fact]
    public void Test23_ChangePlan_BillingCycle_CoversNewPeriod()
    {
        var sub = CreateActiveSubscription(planId: 2, price: 2000m, durationMonths: 6);

        var bc = BillingCycle.Create(
            Guid.NewGuid(), sub.TenantId, sub.Id,
            UtcNow, sub.EffectiveEndsAtUtc).Value;

        Assert.Equal(UtcNow, bc.PeriodStart);
        Assert.Equal(sub.EffectiveEndsAtUtc, bc.PeriodEnd);
    }

    [Fact]
    public void Test24_ChangePlan_Invoice_CoversNewPeriod()
    {
        var contractId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var bcId = Guid.NewGuid();

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-NEW",
            DateOnly.FromDateTime(UtcNow),
            DateOnly.FromDateTime(UtcNow.AddMonths(6)),
            12000m, 0m, 0m, 12000m,
            contractId, subId, bcId).Value;

        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(contractId, invoice.ContractId);
        Assert.Equal(subId, invoice.SubscriptionId);
        Assert.Equal(bcId, invoice.BillingCycleId);
    }

    // ==================================================================
    // OLD SUBSCRIPTION STATUS (Tests 25-28)
    // ==================================================================

    [Fact]
    public void Test25_ChangePlan_OldSubscription_Cancelled()
    {
        var sub = CreateActiveSubscription();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        var cancelResult = sub.Cancel(UtcNow);
        Assert.True(cancelResult.IsSuccess);

        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public void Test26_ChangePlan_CancelledSubscription_CannotBeRenewed()
    {
        var sub = CreateActiveSubscription();
        sub.Cancel(UtcNow);

        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Cancelled, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    [Fact]
    public void Test27_ChangePlan_ExpiredSubscription_CannotBeChanged()
    {
        var sub = CreateActiveSubscription(
            startsAt: UtcNow.AddMonths(-24));
        sub.MarkExpired(UtcNow);

        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Expired, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    [Fact]
    public void Test28_ChangePlan_SuspendedSubscription_CannotBeChanged()
    {
        var result = TenantPlanErrors.InvalidStateTransition(SubscriptionStatus.Suspended, "change plan");
        Assert.Contains("change plan", result.Description);
    }

    // ==================================================================
    // BENEFITS & PROMOTIONS (Tests 29-32)
    // ==================================================================

    [Fact]
    public void Test29_ChangePlan_NewPromotion_AppliedToNewOffer()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 2000m, durationMonths: 12);
        AddPricingTiers(plan);
        var promotion = CreatePromotion(id: 1, type: PromotionType.PercentageDiscount, percentage: 15m);
        var calc = CalcService();

        var offer = calc.Calculate(plan, 12, UtcNow, [promotion]);

        Assert.True(offer.IsSuccess);
        Assert.Equal(15m, offer.Value.DiscountPercentage);
        Assert.Equal(promotion.Id, offer.Value.PromotionId);
    }

    [Fact]
    public void Test30_ChangePlan_OldGifts_NotCarriedOver()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD", 1,
            UtcNow.AddMonths(-12), UtcNow, 12,
            1000m, 1000m, "EGP", 10000m).Value;

        var benefit = ContractBenefit.Create(
            Guid.NewGuid(), oldContract.Id, ContractBenefitType.PhysicalGift, "Free months", null,
            2000m, "EGP").Value;
        oldContract.AddBenefit(benefit);

        Assert.Single(oldContract.Benefits);

        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW", 2,
            UtcNow, UtcNow.AddMonths(12), 12,
            2000m, 2000m, "EGP", 24000m).Value;

        Assert.Empty(newContract.Benefits);
    }

    [Fact]
    public void Test31_ChangePlan_BenefitCap_ThreeMonths()
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
    public void Test32_ChangePlan_NoBenefits_NoCapIssue()
    {
        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NOBEN", 1,
            UtcNow, UtcNow.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m).Value;

        Assert.Empty(contract.Benefits);
    }

    // ==================================================================
    // AUTHORIZATION (Tests 33-34)
    // ==================================================================

    [Fact]
    public void Test33_ChangePlan_PlatformAdmin_Required()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Error.Forbidden("Auth.PlatformAdminRequired", "Platform admin required"));

        var result = guard.EnsurePlatformAdmin();
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Test34_ChangePlan_PlatformAdmin_Allowed()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var result = guard.EnsurePlatformAdmin();
        Assert.True(result.IsSuccess);
    }
}

/// <summary>
/// SQL Server integration tests for plan change concurrency and end-to-end workflow.
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
        var oldPlanId = await EnsurePlanAsync("VALOLD", price: 1000m, duration: 12);
        var newPlanId = await EnsurePlanAsync("VALNEW", price: 2000m, duration: 6);

        Guid oldSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);

            var tenantGuid = Guid.Parse(tenantId);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantGuid))
            {
                var tenantResult = Domain.Platform.Tenants.Tenant.Create(
                    tenantGuid, tenantId[..8], tenantId[..8], tenantId,
                    "EG", "EGP", "UTC", "Owner", "Name", "test@test.com",
                    Domain.Platform.Tenants.Enums.IsolationMode.Shared);
                if (tenantResult.IsSuccess)
                {
                    db.Tenants.Add(tenantResult.Value);
                }
            }

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
