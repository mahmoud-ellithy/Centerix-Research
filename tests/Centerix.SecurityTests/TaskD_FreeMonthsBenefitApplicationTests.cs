namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task D — FreeMonthsBenefit Grant &amp; Apply Application Tests (InMemory).
///
/// Uses the <see cref="TaskCFakeTenantTestFactory"/> (InMemory EF Core provider).
/// Exercises the REAL production handlers (GrantFreeMonthsBenefitHandler,
/// ApplyFreeMonthsBenefitToSubscriptionHandler) with an InMemory database.
///
/// Verifies:
///   * Grant production flow (domain state transition via handler)
///   * Apply production flow (subscription extension + benefit state transition)
///   * Idempotency: duplicate Apply results in exactly one subscription extension
///   * AppliedFreeMonthsBenefitIds round-trips through InMemory persistence
///
/// Cross-tenant isolation is covered by SQL-D04 (real SQL Server).
/// </summary>
public class TaskD_FreeMonthsBenefitApplicationTests : IClassFixture<TaskCFakeTenantTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly TaskCFakeTenantTestFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _db;
    private readonly IMediator _mediator;

    public TaskD_FreeMonthsBenefitApplicationTests(TaskCFakeTenantTestFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        _mediator = _scope.ServiceProvider.GetRequiredService<IMediator>();
    }

    private static EligibilityRule DefaultUpfrontBonusRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(5000m),
            EligibilityRule.NoOverdueInstallment());

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem01: Grant — Eligible + Pending → Granted (handler flow)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem01_Grant_EligiblePending_TransitionsToGranted()
    {
        // Seed a minimal contract for the benefit
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM01-{Guid.NewGuid():N}"[..16],
            planId: 1,
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

        // MarkEligible is a domain operation (not part of Task D handler)
        benefit.MarkEligible(DateTime.UtcNow);
        contract.AddFreeMonthsBenefit(benefit);

        _db.Contracts.Add(contract);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        // Invoke the production Grant handler
        var result = await _mediator.Send(new GrantFreeMonthsBenefitCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var grantResult = result.Value;
        Assert.Equal(benefit.Id, grantResult.BenefitId);
        Assert.False(grantResult.IsAlreadyGranted);

        // Reload and verify (reuse same scope — IAppDbContext is not IAsyncDisposable)
        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reloaded = await db2.FreeMonthsBenefits
            .IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);

        Assert.Equal(FreeMonthsFulfillmentStatus.Granted, reloaded.FulfillmentStatus);
        Assert.NotNull(reloaded.GrantedAtUtc);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem02: Grant — NotEligible → rejected by handler
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem02_Grant_NotEligible_IsRejected()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM02-{Guid.NewGuid():N}"[..16],
            planId: 1,
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
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        // NOT calling MarkEligible — benefit is NotEligible

        contract.AddFreeMonthsBenefit(benefit);
        _db.Contracts.Add(contract);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        var result = await _mediator.Send(new GrantFreeMonthsBenefitCommand(benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotEligible", result.Errors!.First().Code);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem03: Grant — Idempotent on already Granted
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem03_Grant_IdempotentOnAlreadyGranted_ReturnsSuccessNoMutation()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM03-{Guid.NewGuid():N}"[..16],
            planId: 1,
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
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        benefit.Grant(DateTime.UtcNow); // Already granted via domain
        contract.AddFreeMonthsBenefit(benefit);

        _db.Contracts.Add(contract);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        var result = await _mediator.Send(new GrantFreeMonthsBenefitCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsAlreadyGranted);
        Assert.Equal(benefit.Id, result.Value.BenefitId);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem04: Apply — Granted → AppliedToSubscription + subscription extended
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem04_Apply_Granted_AppliedToSubscription_AndSubscriptionExtended()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM04-{Guid.NewGuid():N}"[..16],
            planId: 1,
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
        benefit.Grant(DateTime.UtcNow); // Granted first
        contract.AddFreeMonthsBenefit(benefit);

        var startsAt = DateTime.UtcNow;
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            planId: 1,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        _db.Contracts.Add(contract);
        _db.TenantPlans.Add(subscription);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        var originalEffectiveEnds = subscription.EffectiveEndsAtUtc;

        var result = await _mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefit.Id));

        Assert.True(result.IsSuccess);
        var applyResult = result.Value;
        Assert.Equal(benefit.Id, applyResult.BenefitId);
        Assert.False(applyResult.IsAlreadyApplied);
        Assert.Equal(subscription.Id, applyResult.SubscriptionId);

        // Reload benefit (reuse same scope — IAppDbContext is not IAsyncDisposable)
        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reloadedBenefit = await db2.FreeMonthsBenefits
            .IgnoreQueryFilters()
            .FirstAsync(b => b.Id == benefit.Id);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, reloadedBenefit.FulfillmentStatus);

        // Reload subscription
        var reloadedSubscription = await db2.TenantPlans
            .IgnoreQueryFilters()
            .FirstAsync(tp => tp.Id == subscription.Id);

        // EffectiveEndsAtUtc extended by 3 months (EntitlementMonths)
        var expectedEnds = TenantPlan.AddCalendarMonths(
            TenantPlan.AddCalendarMonths(startsAt, 12), 3); // 12 base + 3 free = 15 months from start
        Assert.Equal(expectedEnds, reloadedSubscription.EffectiveEndsAtUtc);

        // AppliedFreeMonthsBenefitIds contains the benefit ID
        Assert.Contains(benefit.Id, reloadedSubscription.AppliedFreeMonthsBenefitIds);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem05: Apply — Idempotent (retry does not double-extend)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem05_Apply_Twice_ResultsInExactlyOneExtension()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM05-{Guid.NewGuid():N}"[..16],
            planId: 1,
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

        var startsAt = DateTime.UtcNow;
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            planId: 1,
            snapshotPrice: 1000m,
            snapshotMonthlyCharge: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: startsAt,
            autoRenew: false,
            status: SubscriptionStatus.Active).Value;
        subscription.LinkToContract(contract.Id); // Required so handler can find subscription by ContractId

        _db.Contracts.Add(contract);
        _db.TenantPlans.Add(subscription);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        var originalEffectiveEnds = subscription.EffectiveEndsAtUtc;

        // Apply once
        var result1 = await _mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefit.Id));
        Assert.True(result1.IsSuccess);
        Assert.False(result1.Value.IsAlreadyApplied);

        // Apply again (idempotent retry)
        var result2 = await _mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefit.Id));
        Assert.True(result2.IsSuccess);
        Assert.True(result2.Value.IsAlreadyApplied);

        // Reload subscription (reuse same scope — IAppDbContext is not IAsyncDisposable)
        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var reloaded = await db2.TenantPlans
            .IgnoreQueryFilters()
            .FirstAsync(tp => tp.Id == subscription.Id);

        // Exactly ONE extension (2 months, not 4)
        var expectedEnds = TenantPlan.AddCalendarMonths(
            TenantPlan.AddCalendarMonths(startsAt, 12), 2);
        Assert.Equal(expectedEnds, reloaded.EffectiveEndsAtUtc);
        Assert.Equal(originalEffectiveEnds.AddMonths(2), reloaded.EffectiveEndsAtUtc);

        // Benefit ID appears exactly once in AppliedFreeMonthsBenefitIds
        Assert.Single(reloaded.AppliedFreeMonthsBenefitIds.Where(id => id == benefit.Id));
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem06: Apply — NotGranted → rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem06_Apply_NotGranted_IsRejected()
    {
        var contract = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-D-INMEM06-{Guid.NewGuid():N}"[..16],
            planId: 1,
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
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(DateTime.UtcNow);
        // NOT granting — benefit is Eligible but Pending
        contract.AddFreeMonthsBenefit(benefit);

        _db.Contracts.Add(contract);
        _db.StampAddedTenantIds(TenantId);
        await _db.SaveChangesAsync();

        var result = await _mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefit.Id));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotGranted", result.Errors!.First().Code);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem07: Apply — NotFound → rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem07_Apply_NotFound_IsRejected()
    {
        var result = await _mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotFound", result.Errors!.First().Code);
    }

    // ─────────────────────────────────────────────────────────────────
    // TestD-InMem08: Grant — NotFound → rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestD_InMem08_Grant_NotFound_IsRejected()
    {
        var result = await _mediator.Send(new GrantFreeMonthsBenefitCommand(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.NotFound", result.Errors!.First().Code);
    }
}
