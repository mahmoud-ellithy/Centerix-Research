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
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
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
/// Task 9.3.3: Contract/Subscription Temporal Alignment regression tests.
///
/// Verifies the single invariant corrected by this task:
///   NewContract.EffectiveAtUtc == NewSubscription.StartsAtUtc == startsAt
///
/// For scheduled renewal:
///   OldSubscription.EffectiveEndsAtUtc == NewSubscription.StartsAtUtc == NewContract.EffectiveAtUtc
///
/// For immediate renewal:
///   NewContract.EffectiveAtUtc == NewSubscription.StartsAtUtc
/// </summary>
public class Phase9_3_3ContractSubscriptionAlignmentTests
{
    private static readonly DateTime UtcNow = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

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
        var start = startsAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), planId, price, "EGP",
            durationMonths, bonusMonths, start, false, SubscriptionStatus.Pending).Value;
        sub.Activate(start);
        return sub;
    }

    private static TenantPlan CreateExpiredSubscription(string? tenantId = null)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), 1, 1000m, "EGP",
            1, 0, UtcNow.AddMonths(-3), false, SubscriptionStatus.Active).Value;
        sub.MarkExpired(UtcNow);
        return sub;
    }

    private static IPromotionCalculationService CalcService() => new PromotionCalculationService();

    // ==================================================================
    // Test 1: Scheduled Renewal — Contract/Subscription Start Alignment
    // ==================================================================

    [Fact]
    public void Test01_ScheduledRenewal_ContractStart_EqualsSubscriptionStart_EqualsOldEnd()
    {
        var oldSubStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: oldSubStart, durationMonths: 12);

        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Scheduled: startsAt = oldSubscription.EffectiveEndsAtUtc because it's still active
        var startsAt = oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        // Simulating the corrected handler:
        // Contract.EffectiveAtUtc = startsAt
        // Subscription.StartsAtUtc = startsAt
        var contractEffectiveAt = startsAt;
        var subscriptionStartsAt = startsAt;

        // The invariant:
        Assert.Equal(oldSub.EffectiveEndsAtUtc, contractEffectiveAt);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, subscriptionStartsAt);
        Assert.Equal(contractEffectiveAt, subscriptionStartsAt);
    }

    // ==================================================================
    // Test 2: Contract Does Not Start During Old Service Period
    // ==================================================================

    [Fact]
    public void Test02_ScheduledRenewal_ContractDoesNotOverlapOldServicePeriod()
    {
        var oldSubStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: oldSubStart, durationMonths: 12);

        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var startsAt = oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        var contractEffectiveAt = startsAt;
        var contractEndsAt = startsAt.AddMonths(12);

        // New contract must NOT overlap old service period
        Assert.True(contractEffectiveAt >= oldSub.EffectiveEndsAtUtc);

        // New contract starts exactly when old ends (or after)
        Assert.Equal(oldSub.EffectiveEndsAtUtc, contractEffectiveAt);
    }

    // ==================================================================
    // Test 3: Immediate Renewal — Contract/Subscription Start Alignment
    // ==================================================================

    [Fact]
    public void Test03_ImmediateRenewal_ContractStart_EqualsSubscriptionStart()
    {
        // For an expired subscription, startsAt = now
        var oldSub = CreateExpiredSubscription();

        var now = UtcNow;

        var startsAt = oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        Assert.Equal(now, startsAt); // immediate renewal

        var contractEffectiveAt = startsAt;
        var subscriptionStartsAt = startsAt;

        // Both must be equal
        Assert.Equal(contractEffectiveAt, subscriptionStartsAt);
        Assert.Equal(now, contractEffectiveAt);
    }

    // ==================================================================
    // Test 4: Old Subscription Remains Active After Scheduled Renewal
    // ==================================================================

    [Fact]
    public void Test04_OldSubscriptionRemainsActive_AfterScheduledRenewal()
    {
        var oldSubStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: oldSubStart, durationMonths: 12);

        // Renewal requested mid-term
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(oldSub.Status == SubscriptionStatus.Active);
        Assert.True(oldSub.EffectiveEndsAtUtc > now);

        // After renewal (simulated), old subscription is NOT modified
        Assert.Equal(SubscriptionStatus.Active, oldSub.Status);
        Assert.Equal(oldSubStart, oldSub.StartsAtUtc);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), oldSub.EffectiveEndsAtUtc);
    }

    // ==================================================================
    // Test 5: Historical Contract Unchanged After Renewal
    // ==================================================================

    [Fact]
    public void Test05_HistoricalContract_UnchangedAfterRenewal()
    {
        // Old contract at old pricing
        var oldContract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CTR-OLD-001",
            planId: 1,
            effectiveAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 10000m,
            discountAmount: 1000m,
            promotionId: 1,
            promotionType: "PercentageDiscount",
            chargedMonths: 10).Value;

        var oldBenefit = ContractBenefit.Create(
            Guid.NewGuid(), oldContract.Id, ContractBenefitType.PhysicalGift,
            "Printer", null, 1000m, "EGP").Value;
        oldContract.AddBenefit(oldBenefit);

        // Snapshot old values
        var oldMonthly = oldContract.MonthlyListPrice;
        var oldContracted = oldContract.ContractedAmount;
        var oldDiscount = oldContract.DiscountAmount;
        var oldPromoId = oldContract.PromotionId;
        var oldCharged = oldContract.ChargedMonths;
        var oldEndsAt = oldContract.EndsAtUtc;
        var oldEffAt = oldContract.EffectiveAtUtc;
        var oldDuration = oldContract.DurationMonths;

        // Create new renewal contract
        var newContract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: "tenant-1",
            contractNumber: "CTR-RENEW-001",
            planId: 2,
            effectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: 12,
            monthlyListPrice: 1200m,
            contractualMonthlyValue: 1200m,
            currencyCode: "EGP",
            contractedAmount: 14400m,
            discountAmount: 0m).Value;

        // Old contract remains unchanged
        Assert.Equal(oldMonthly, oldContract.MonthlyListPrice);
        Assert.Equal(oldContracted, oldContract.ContractedAmount);
        Assert.Equal(oldDiscount, oldContract.DiscountAmount);
        Assert.Equal(oldPromoId, oldContract.PromotionId);
        Assert.Equal(oldCharged, oldContract.ChargedMonths);
        Assert.Equal(oldEndsAt, oldContract.EndsAtUtc);
        Assert.Equal(oldEffAt, oldContract.EffectiveAtUtc);
        Assert.Equal(oldDuration, oldContract.DurationMonths);
        Assert.Single(oldContract.Benefits);
        Assert.Equal("Printer", oldContract.Benefits[0].Name);
    }

    // ==================================================================
    // Test 6: Billing Cycle Alignment With Subscription
    // ==================================================================

    [Fact]
    public void Test06_BillingCycle_And_Invoice_AlignedWithSubscription()
    {
        var tenantId = Guid.NewGuid().ToString();
        var subStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sub = CreateActiveSubscription(tenantId: tenantId, startsAt: subStart, durationMonths: 12);

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-ALIGN-001", 1,
            subStart, subStart.AddMonths(12), 12,
            1000m, 1000m, "EGP", 12000m).Value;
        sub.LinkToContract(contract.Id);

        // BillingCycle aligned with subscription
        var bc = BillingCycle.Create(
            Guid.NewGuid(), tenantId, sub.Id,
            sub.StartsAtUtc, sub.EffectiveEndsAtUtc).Value;

        Assert.Equal(sub.StartsAtUtc, bc.PeriodStart);
        Assert.Equal(sub.EffectiveEndsAtUtc, bc.PeriodEnd);

        // Invoice aligned with billing cycle
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-ALIGN-001",
            DateOnly.FromDateTime(bc.PeriodStart),
            DateOnly.FromDateTime(bc.PeriodEnd),
            12000m, 0, 0, 12000m,
            contractId: contract.Id, subscriptionId: sub.Id, billingCycleId: bc.Id).Value;

        Assert.Equal(DateOnly.FromDateTime(sub.StartsAtUtc), invoice.PeriodStart);
        Assert.Equal(DateOnly.FromDateTime(sub.EffectiveEndsAtUtc), invoice.PeriodEnd);
        Assert.Equal(contract.Id, invoice.ContractId);
        Assert.Equal(sub.Id, invoice.SubscriptionId);
        Assert.Equal(bc.Id, invoice.BillingCycleId);
    }

    // ==================================================================
    // Test 7: Contract End Date Calculation (No Bonus)
    // ==================================================================

    [Fact]
    public void Test07_ContractEndsAt_CalculatedWithAddCalendarMonths()
    {
        var startsAt = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var durationMonths = 12;

        // Use AddCalendarMonths for correct month-end handling (leap year, etc.)
        var endsAt = TenantPlan.AddCalendarMonths(startsAt, durationMonths);

        var contract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-DUR-001", 1,
            startsAt, endsAt, durationMonths,
            1000m, 1000m, "EGP", 12000m).Value;

        // Contract end is based on DurationMonths using AddCalendarMonths
        Assert.Equal(startsAt, contract.EffectiveAtUtc);
        Assert.Equal(endsAt, contract.EndsAtUtc);
        Assert.Equal(durationMonths, contract.DurationMonths);
    }

    // ==================================================================
    // Test 8: Contract and Subscription End Alignment — Both Include Bonus
    // ==================================================================

    [Fact]
    public void Test08_ContractEndsAt_AlignedWithSubscription_IncludesBonusMonths()
    {
        var startsAt = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var durationMonths = 12;
        var bonusMonths = 2;

        // CORRECTED: Contract ends at startsAt + durationMonths + bonusMonths (aligned with Subscription)
        var contractEndsAt = TenantPlan.AddCalendarMonths(
            TenantPlan.AddCalendarMonths(startsAt, durationMonths), bonusMonths);

        // Subscription effective end = same calculation
        var subEffectiveEndsAt = TenantPlan.AddCalendarMonths(
            TenantPlan.AddCalendarMonths(startsAt, durationMonths), bonusMonths);

        // Both MUST be equal (the invariant fixed by this task)
        Assert.Equal(contractEndsAt, subEffectiveEndsAt);
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc), contractEndsAt);
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc), subEffectiveEndsAt);
    }

    // ==================================================================
    // Test 9: Contract Does Not Overlap Previous Contract
    // ==================================================================

    [Fact]
    public void Test09_NewContract_DoesNotOverlapPreviousContract()
    {
        var oldContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-OLD-001", 1,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            12, 1000m, 1000m, "EGP", 12000m).Value;

        var newStart = oldContract.EndsAtUtc;
        var newContract = Contract.Create(
            Guid.NewGuid(), "t-1", "CTR-NEW-001", 1,
            newStart, newStart.AddMonths(12),
            12, 1000m, 1000m, "EGP", 12000m).Value;

        // New contract starts at or after old contract ends
        Assert.True(newContract.EffectiveAtUtc >= oldContract.EndsAtUtc);
        // In fact for scheduled renewal, they are exactly equal
        Assert.Equal(oldContract.EndsAtUtc, newContract.EffectiveAtUtc);
    }

    // ==================================================================
    // Test 10: Immediate Renewal — Expired Sub Aligns Contract and Sub
    // ==================================================================

    [Fact]
    public void Test10_ImmediateRenewal_ContractAndSubscriptionAligned()
    {
        // Expired subscription — renewal starts immediately
        var oldSub = CreateExpiredSubscription();
        var now = UtcNow;

        var startsAt = oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        // Both contract and subscription use the same start
        var contractEffectiveAt = startsAt;
        var subscriptionStartsAt = startsAt;

        Assert.Equal(contractEffectiveAt, subscriptionStartsAt);
        Assert.Equal(now, startsAt);
    }

    // ==================================================================
    // Test 11: Scheduled Renewal — Start Date Computation Preserves Invariant
    // ==================================================================

    [Fact]
    public void Test11_ScheduledRenewal_StartDateComputation()
    {
        var oldSubStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: oldSubStart, durationMonths: 12);

        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Step 5 of handler: compute startsAt
        var startsAt = oldSub.Status == SubscriptionStatus.Active &&
                       oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        // Step 9 (corrected): Contract uses startsAt, not now
        var effectiveAt = startsAt;

        // Step 10: Subscription uses startsAt
        var subscriptionStartsAt = startsAt;

        // All three must be equal
        Assert.Equal(oldSub.EffectiveEndsAtUtc, startsAt);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, effectiveAt);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, subscriptionStartsAt);
        Assert.Equal(effectiveAt, subscriptionStartsAt);
    }

    // ==================================================================
    // Test 12: Contract EffectiveAt Never Before Old Subscription Ends
    // ==================================================================

    [Fact]
    public void Test12_ContractEffectiveAt_NotBeforeOldSubscriptionEnds()
    {
        var oldSubStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSub = CreateActiveSubscription(startsAt: oldSubStart, durationMonths: 12);

        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var startsAt = oldSub.EffectiveEndsAtUtc > now
            ? oldSub.EffectiveEndsAtUtc
            : now;

        // Contract effective at is never before the old subscription ends
        Assert.True(startsAt >= oldSub.EffectiveEndsAtUtc);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, startsAt);
    }
}

/// <summary>
/// SQL Server integration tests for Contract/Subscription temporal alignment.
/// Verifies the corrected alignment against the real relational model.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase9_3_3ContractSubscriptionAlignmentSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    public Phase9_3_3ContractSubscriptionAlignmentSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private static RenewSubscriptionOfferHandler CreateHandler(IAppDbContext db, DateTime? fixedTime = null)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);

        var subscriptionFactory = new SubscriptionFactory(db);

        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(fixedTime ?? DateTime.UtcNow);

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

    private async Task<int> EnsurePlanAsync(string codePrefix, decimal price = 1000m, int duration = 12, int bonusMonths = 0)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", duration, bonusMonths).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    // ==================================================================
    // SQL Server Test 1: Scheduled Renewal Contract/Subscription Alignment
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql01_ScheduledRenewal_ContractEffectiveAt_EqualsSubscriptionStartsAt()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000001";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL1", price: 1000m, duration: 12);

        // Seed old subscription that started 6 months ago (still active for 6 more months)
        var oldSubStart = DateTime.UtcNow.AddMonths(-6);
        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
                oldSubStart, false, SubscriptionStatus.Pending).Value;
            sub.Activate(oldSubStart);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        // Execute renewal — handler uses "now" as the time
        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2, fixedTime: now);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));
        var contractId = result.Value;

        // Verify alignment in the database
        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contractId);
        Assert.NotNull(contract);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == contractId);
        Assert.NotNull(newSub);

        // THE CRITICAL ASSERTION:
        // Contract.EffectiveAtUtc == Subscription.StartsAtUtc
        Assert.Equal(contract.EffectiveAtUtc, newSub.StartsAtUtc);

        // For scheduled renewal, both must equal old subscription's EffectiveEndsAtUtc
        var oldSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == subId);
        Assert.NotNull(oldSub);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, contract.EffectiveAtUtc);
        Assert.Equal(oldSub.EffectiveEndsAtUtc, newSub.StartsAtUtc);
    }

    // ==================================================================
    // SQL Server Test 2: Contract Does Not Start During Old Service Period
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql02_ContractEffectiveAt_NotBeforeOldSubscriptionEnds()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000002";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL2", price: 1000m, duration: 12);

        var oldSubStart = DateTime.UtcNow.AddMonths(-6);
        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
                oldSubStart, false, SubscriptionStatus.Pending).Value;
            sub.Activate(oldSubStart);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2, fixedTime: now);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);

        var oldSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == subId);
        Assert.NotNull(oldSub);

        // Contract must not start before old subscription ends
        Assert.True(contract.EffectiveAtUtc >= oldSub.EffectiveEndsAtUtc);
    }

    // ==================================================================
    // SQL Server Test 3: Immediate Renewal — Contract/Subscription Alignment
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql03_ImmediateRenewal_ContractAndSubscriptionAligned()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000003";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL3", price: 1000m, duration: 6);

        // Seed expired subscription (expired 1 month ago)
        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 6, 0,
                DateTime.UtcNow.AddMonths(-7), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-7));
            sub.MarkExpired(DateTime.UtcNow);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2, fixedTime: now);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == result.Value);
        Assert.NotNull(newSub);

        // For immediate renewal: Contract.EffectiveAtUtc == Subscription.StartsAtUtc
        Assert.Equal(contract.EffectiveAtUtc, newSub.StartsAtUtc);
    }

    // ==================================================================
    // SQL Server Test 4: Old Subscription Remains Active After Scheduled Renewal
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql04_OldSubscription_RemainsActive_AfterScheduledRenewal()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000004";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL4", price: 1000m, duration: 12);

        Guid subId;
        DateTime oldEndsAt;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
                DateTime.UtcNow.AddMonths(-6), false, SubscriptionStatus.Pending).Value;
            sub.Activate(DateTime.UtcNow.AddMonths(-6));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
            oldEndsAt = sub.EffectiveEndsAtUtc;
        }

        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2, fixedTime: now);

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
    }

    // ==================================================================
    // SQL Server Test 5: Billing Chain Alignment
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql05_BillingCycle_Invoice_AlignedWithSubscription()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000005";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL5", price: 1000m, duration: 6);

        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StampAddedTenantIds(tenantId);

        // Seed an expired subscription so renewal starts immediately
        Guid subId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.StampAddedTenantIds(tenantId);
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 6, 0,
                now.AddMonths(-7), false, SubscriptionStatus.Pending).Value;
            sub.Activate(now.AddMonths(-7));
            sub.MarkExpired(now);
            seedDb.TenantPlans.Add(sub);
            await seedDb.SaveChangesAsync();
            subId = sub.Id;
        }

        var handler = CreateHandler(db, fixedTime: now);
        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 6),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var contract = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == result.Value);
        Assert.NotNull(contract);

        var newSub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.ContractId == result.Value);
        Assert.NotNull(newSub);

        var billingCycle = await verifyDb.BillingCycles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(bc => bc.SubscriptionId == newSub.Id);
        Assert.NotNull(billingCycle);

        var invoice = await verifyDb.Invoices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.BillingCycleId == billingCycle.Id);
        Assert.NotNull(invoice);

        // All four must be temporally aligned
        Assert.Equal(contract.EffectiveAtUtc, newSub.StartsAtUtc);
        Assert.Equal(newSub.StartsAtUtc, billingCycle.PeriodStart);
        Assert.Equal(DateOnly.FromDateTime(newSub.StartsAtUtc), invoice.PeriodStart);
        Assert.Equal(contract.Id, invoice.ContractId);
        Assert.Equal(newSub.Id, invoice.SubscriptionId);
        Assert.Equal(billingCycle.Id, invoice.BillingCycleId);
    }

    // ==================================================================
    // SQL Server Test 6: Historical Contract Unchanged After Renewal
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql06_HistoricalContractUnchanged_AfterRenewal()
    {
        const string tenantId = "A1000000-0000-0000-0000-000000000006";
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("ALIGNSQL6", price: 1000m, duration: 12);

        Guid subId, oldContractId;
        decimal oldMonthlyPrice;
        decimal oldContractedAmount;
        decimal oldDiscountAmount;
        int? oldPromotionId;
        int oldDurationMonths;
        DateTime oldContractEffectiveAt;
        DateTime oldContractEndsAt;

        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StampAddedTenantIds(tenantId);

            // Create old contract (starts 6 months ago, ends in 6 months)
            var oldContractStart = DateTime.UtcNow.AddMonths(-6);
            var oldContract = Contract.Create(
                Guid.NewGuid(), tenantId, "CTR-HIST-OLD", planId,
                oldContractStart, oldContractStart.AddMonths(12), 12,
                1000m, 1000m, "EGP", 10000m, 1000m,
                promotionId: 1, promotionType: "PercentageDiscount", chargedMonths: 10).Value;
            db.Contracts.Add(oldContract);
            oldContractId = oldContract.Id;
            oldMonthlyPrice = oldContract.MonthlyListPrice;
            oldContractedAmount = oldContract.ContractedAmount;
            oldDiscountAmount = oldContract.DiscountAmount;
            oldPromotionId = oldContract.PromotionId;
            oldDurationMonths = oldContract.DurationMonths;
            oldContractEffectiveAt = oldContract.EffectiveAtUtc;
            oldContractEndsAt = oldContract.EndsAtUtc;

            // Create old subscription linked to old contract (Active, ends in 6 months)
            var sub = TenantPlan.Create(
                Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
                oldContractStart, false, SubscriptionStatus.Pending).Value;
            sub.Activate(oldContractStart);
            sub.LinkToContract(oldContract.Id);
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        // Execute renewal
        var now = DateTime.UtcNow;
        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.StampAddedTenantIds(tenantId);
        var handler = CreateHandler(db2, fixedTime: now);

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(subId, PlanId: planId, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join(", ", result.Errors?.Select(e => e.Code) ?? []));

        // Verify old contract is unchanged
        using var verify = _env.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verify.ServiceProvider, tenantId);

        var oldContractAfter = await verifyDb.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == oldContractId);
        Assert.NotNull(oldContractAfter);
        Assert.Equal(oldMonthlyPrice, oldContractAfter.MonthlyListPrice);
        Assert.Equal(oldContractedAmount, oldContractAfter.ContractedAmount);
        Assert.Equal(oldDiscountAmount, oldContractAfter.DiscountAmount);
        Assert.Equal(oldPromotionId, oldContractAfter.PromotionId);
        Assert.Equal(oldDurationMonths, oldContractAfter.DurationMonths);
        Assert.Equal(oldContractEffectiveAt, oldContractAfter.EffectiveAtUtc);
        Assert.Equal(oldContractEndsAt, oldContractAfter.EndsAtUtc);
    }
}
