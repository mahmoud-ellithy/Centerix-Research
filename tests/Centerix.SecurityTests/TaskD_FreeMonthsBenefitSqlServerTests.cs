namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task D — FreeMonthsBenefit Grant &amp; Apply SQL Server Integration Tests.
///
/// Uses the <see cref="SqlServerIntegrationFactory"/> collection.
/// Verifies:
///   SQL-D01: Grant persists to SQL and reload confirms Granted
///   SQL-D02: Apply persists and reload confirms AppliedToSubscription + subscription extended
///   SQL-D03: Duplicate Apply results in exactly one extension (idempotency)
///   SQL-D04: Cross-tenant application is rejected with no mutation
///   SQL-D05: Concurrent application produces exactly one effective application
///   SQL-D06: Full field round-trip through SQL persistence
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskD_FreeMonthsBenefitSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskD_FreeMonthsBenefitSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        // Use AsyncLocal-based setter so ICurrentTenant.TenantId returns tenantId
        // for the current async flow (works even though ICurrentTenant is a singleton).
        TaskCFakeCurrentTenant.SetTenantId(tenantId);
    }

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@test.com",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<int> EnsurePlanAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var plan = Plan.Create(
            id: 0,
            code: $"PlanD_{Guid.NewGuid():N}"[..28],
            displayName: "TaskD Plan",
            monthlyPrice: 1000m,
            maxStudents: 100, maxUsers: 5, maxBranches: 1, maxTeachers: 10,
            storageGB: 10, smsQuota: 100,
            isActive: true,
            description: null,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 0).Value;

        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private static EligibilityRule DefaultUpfrontBonusRule(decimal contractedAmount = 5000m) =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(contractedAmount),
            EligibilityRule.NoOverdueInstallment());

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D01: Grant persists to SQL and reload confirms Granted
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD01_Grant_PersistsAndReloadConfirmsGranted()
    {
        var tenantId = $"D-1-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D1-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 2, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        Guid benefitId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
        }

        // Apply grant through the production handler
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var grantResult = await mediator.Send(new GrantFreeMonthsBenefitCommand(benefitId));
            Assert.True(grantResult.IsSuccess);
        }

        // Reload from fresh DbContext and verify
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloaded = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);

            Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
            Assert.NotNull(reloaded.GrantedAtUtc);
            Assert.Equal(2, reloaded.EntitlementMonths);
            Assert.Equal("EGP", reloaded.CurrencyCode);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D02: Apply persists and reload confirms AppliedToSubscription + subscription extended
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD02_Apply_PersistsAppliedToSubscription_AndExtendsSubscription()
    {
        var tenantId = $"D-2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D2-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 3, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow); // Must be Granted first
        contract.AddFreeMonthsBenefit(benefit);

        var startsAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: planId,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        Guid benefitId;
        Guid subscriptionId;
        DateTime originalEffectiveEnds;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.TenantPlans.Add(subscription);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
            subscriptionId = subscription.Id;
            originalEffectiveEnds = subscription.EffectiveEndsAtUtc;
        }

        // Apply through the production handler
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var applyResult = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));
            Assert.True(applyResult.IsSuccess);
            Assert.False(applyResult.Value.IsAlreadyApplied);
        }

        // Reload benefit
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloadedBenefit = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);

            Assert.Equal(FulfillmentStatus.AppliedToSubscription, reloadedBenefit.FulfillmentStatus);
            Assert.NotNull(reloadedBenefit.AppliedAtUtc);

            var reloadedSub = await db.TenantPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(tp => tp.Id == subscriptionId);

            // EffectiveEndsAtUtc should be extended by 3 months
            var expectedEnds = TenantPlan.AddCalendarMonths(
                TenantPlan.AddCalendarMonths(startsAt, 12), 3);
            Assert.Equal(expectedEnds, reloadedSub.EffectiveEndsAtUtc);

            // AppliedFreeMonthsBenefitIds contains benefit ID exactly once
            Assert.Contains(benefitId, reloadedSub.AppliedFreeMonthsBenefitIds);
            Assert.Single(reloadedSub.AppliedFreeMonthsBenefitIds);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D03: Duplicate Apply → exactly one extension, benefit ID once
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD03_ApplyTwice_ExactlyOneExtension_OneBenefitId()
    {
        var tenantId = $"D-3-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D3-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 2, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        var startsAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: planId,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        Guid benefitId;
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.TenantPlans.Add(subscription);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
            subscriptionId = subscription.Id;
        }

        // Apply once
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var r1 = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));
            Assert.True(r1.IsSuccess);
            Assert.False(r1.Value.IsAlreadyApplied);
        }

        // Apply again (idempotent retry)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var r2 = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));
            Assert.True(r2.IsSuccess);
            Assert.True(r2.Value.IsAlreadyApplied);
        }

        // Verify final state
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloadedSub = await db.TenantPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(tp => tp.Id == subscriptionId);

            // Exactly 2 months extended (not 4)
            var expectedEnds = TenantPlan.AddCalendarMonths(
                TenantPlan.AddCalendarMonths(startsAt, 12), 2);
            Assert.Equal(expectedEnds, reloadedSub.EffectiveEndsAtUtc);
            Assert.Single(reloadedSub.AppliedFreeMonthsBenefitIds);
            Assert.Contains(benefitId, reloadedSub.AppliedFreeMonthsBenefitIds);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D04: Cross-tenant application rejected, no mutation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD04_CrossTenantApply_Rejected_NoMutation()
    {
        var tenantA = $"D4A-{Guid.NewGuid():N}"[..16];
        var tenantB = $"D4B-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantA);
        await SeedTenantAsync(tenantB);
        var planIdA = await EnsurePlanAsync(tenantA);
        var planIdB = await EnsurePlanAsync(tenantB);

        // Tenant A owns the benefit and subscription
        var contractA = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantA,
            contractNumber: $"CNT-D4A-{Guid.NewGuid():N}"[..16],
            planId: planIdA,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contractA.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow);
        contractA.AddFreeMonthsBenefit(benefit);

        var startsAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subscriptionA = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantA,
            planId: planIdA,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscriptionA.LinkToContract(contractA.Id); // Required so handler can find subscription by ContractId

        Guid benefitId;
        DateTime originalEndsA;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contractA);
            db.TenantPlans.Add(subscriptionA);
            db.StampAddedTenantIds(tenantA);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
            originalEndsA = subscriptionA.EffectiveEndsAtUtc;
        }

        // Tenant B attempts to apply Tenant A's benefit (cross-tenant)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantB);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var result = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));

            Assert.False(result.IsSuccess);
            Assert.Equal("Contract.FreeMonthsBenefit.CrossTenant", result.Errors!.First().Code);
        }

        // Verify Tenant A's benefit was NOT mutated
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloadedBenefit = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);

            Assert.Equal(FulfillmentStatus.Granted, reloadedBenefit.FulfillmentStatus); // Still Granted, not Applied

            var reloadedSub = await db.TenantPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(tp => tp.Id == subscriptionA.Id);

            Assert.Equal(originalEndsA, reloadedSub.EffectiveEndsAtUtc); // Not extended
            Assert.Empty(reloadedSub.AppliedFreeMonthsBenefitIds);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D05: Concurrent application → exactly one effective application
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD05_ConcurrentApply_OneEffectiveApplication()
    {
        var tenantId = $"D-5-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D5-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 2, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        var startsAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: planId,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        Guid benefitId;
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.TenantPlans.Add(subscription);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
            subscriptionId = subscription.Id;
        }

        // Launch two concurrent Apply operations
        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<ApplyFreeMonthsBenefitResult>>();
        var tcs2 = new TaskCompletionSource<Result<ApplyFreeMonthsBenefitResult>>();

        async Task<Result<ApplyFreeMonthsBenefitResult>> ApplyInScope()
        {
            using var scope = _env.Factory.Services.CreateScope();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            barrier.SignalAndWait(BarrierTimeout);
            return await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));
        }

        var task1 = Task.Run(async () =>
        {
            try
            {
                var result = await ApplyInScope();
                tcs1.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs1.TrySetException(ex);
            }
        });

        var task2 = Task.Run(async () =>
        {
            try
            {
                var result = await ApplyInScope();
                tcs2.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs2.TrySetException(ex);
            }
        });

        await Task.WhenAll(task1, task2);

        var results = new[] { tcs1.Task.Result, tcs2.Task.Result };

        // At least one succeeded, neither should have a concurrency conflict error
        var successes = results.Where(r => r.IsSuccess).ToList();
        Assert.NotEmpty(successes);

        // Neither should be a concurrency conflict
        foreach (var r in results)
        {
            if (!r.IsSuccess)
            {
                Assert.NotEqual("FreeMonthsBenefit.ConcurrencyConflict", r.Errors!.First().Code);
            }
        }

        // Verify final state: exactly one effective application
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloadedSub = await db.TenantPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(tp => tp.Id == subscriptionId);

            // Exactly 2 months extended (not 4)
            var expectedEnds = TenantPlan.AddCalendarMonths(
                TenantPlan.AddCalendarMonths(startsAt, 12), 2);
            Assert.Equal(expectedEnds, reloadedSub.EffectiveEndsAtUtc);
            Assert.Single(reloadedSub.AppliedFreeMonthsBenefitIds);
            Assert.Contains(benefitId, reloadedSub.AppliedFreeMonthsBenefitIds);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D06: Full field round-trip through SQL persistence
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD06_GrantAndApply_AllFieldsRoundTripThroughSql()
    {
        var tenantId = $"D-6-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D6-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var originalRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 5, "EGP", originalRule).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        Guid benefitId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
        }

        // Reload and verify all fields
        FreeMonthsBenefit reloaded;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            reloaded = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(b => b.Contract)
                .FirstAsync(b => b.Id == benefitId);
        }

        Assert.Equal(benefitId, reloaded.Id);
        Assert.Equal(contract.Id, reloaded.ContractId);
        Assert.Equal(5, reloaded.EntitlementMonths);
        Assert.Equal("EGP", reloaded.CurrencyCode);
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, reloaded.EligibilityStatus);
        Assert.Equal(FulfillmentStatus.Granted, reloaded.FulfillmentStatus);
        Assert.NotNull(reloaded.GrantedAtUtc);
        Assert.NotNull(reloaded.EligibleAtUtc);
        Assert.Null(reloaded.AppliedAtUtc); // Not yet applied
        Assert.Equal(originalRule, reloaded.EligibilityRule);
        Assert.NotNull(reloaded.Contract);
        Assert.Equal(tenantId, reloaded.Contract.TenantId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL-D07: Expired subscription cannot receive Free Months benefit
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SqlD07_Apply_ExpiredSubscription_IsRejected_NoMutation()
    {
        var tenantId = $"D-7-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CNT-D7-{Guid.NewGuid():N}"[..16],
            planId: planId,
            effectiveAtUtc: DateTime.UtcNow,
            endsAtUtc: DateTime.UtcNow.AddYears(1),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            grossAmount: 12000m,
            contractedAmount: 12000m,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.FullUpfront,
            discountAmount: 0m).Value;

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 2, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        // Expired subscription — status=Expired and EffectiveEndsAtUtc in the past
        var startsAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: planId,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Expired).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        Guid benefitId;
        Guid subscriptionId;
        DateTime originalEffectiveEnds;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.TenantPlans.Add(subscription);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            benefitId = benefit.Id;
            subscriptionId = subscription.Id;
            originalEffectiveEnds = subscription.EffectiveEndsAtUtc;
        }

        // Attempt to apply
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var result = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId));

            Assert.False(result.IsSuccess);
            Assert.Equal("FreeMonthsBenefit.ActiveSubscriptionNotFound", result.Errors!.First().Code);
        }

        // Verify no mutation occurred
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var reloadedBenefit = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(b => b.Id == benefitId);

            Assert.Equal(FulfillmentStatus.Granted, reloadedBenefit.FulfillmentStatus);
            Assert.Null(reloadedBenefit.AppliedAtUtc);

            var reloadedSub = await db.TenantPlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(tp => tp.Id == subscriptionId);

            Assert.Equal(originalEffectiveEnds, reloadedSub.EffectiveEndsAtUtc);
            Assert.Empty(reloadedSub.AppliedFreeMonthsBenefitIds);
        }
    }
}
