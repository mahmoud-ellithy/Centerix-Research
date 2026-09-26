namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// TASK 21.2.1 — Bonus Months &amp; Billing Cycle Commercial Integrity
///
/// This test suite verifies:
/// 1. BonusMonths are FREE entitlement and do NOT increase billed months
/// 2. BillingCycle period represents paid term only (BaseEndsAtUtc, not EffectiveEndsAtUtc)
/// 3. SnapshotMonthlyCharge precision is sufficient to avoid rounding drift
/// 4. Full-term Invoice.TotalAmount equals Contract.ContractedAmount exactly
/// 5. Subscription.EffectiveEndsAtUtc still includes bonus months (entitlement preserved)
/// 6. PayForXMonths has no rounding drift
/// 7. Bonus + Discount combined scenario works correctly
///
/// Commercial semantics:
/// - Paid Term = Contract.DurationMonths (billed)
/// - Free Bonus Entitlement = Contract.BonusMonths (NOT billed)
/// - Subscription Effective End = Paid Term + Bonus Entitlement (access period)
/// - Invoiceable Billing Period = Paid Term (excludes bonus)
/// </summary>
public class TASK_21_2_1_BonusMonthsBillingPrecisionTests
{
    // ------------------------------------------------------------------
    // Constants & helpers
    // ------------------------------------------------------------------

    /// <summary>Must parse as a Guid — renewal/plan-change look up Tenant.Id via Guid.Parse.</summary>
    private const string TenantA = "6c1e2f3a-1b2c-4d5e-8f90-1234567890ab";
    private const decimal Tolerance = 0.01m;

    private static readonly DateTime Start2026 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now2026 = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record CommercialFixture(
        Plan Plan,
        CalculatedOffer Offer,
        Contract Contract,
        SubscriptionSnapshot Snapshot,
        TenantPlan Subscription);

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TASK2121_{Guid.NewGuid():N}")
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? TenantA);
        currentTenant.IsAuthorized.Returns(true);

        var db = new AppDbContext(options, mediator, currentTenant);
        db.StampAddedTenantIds(tenantId ?? TenantA);
        return db;
    }

    private static ICurrentTenant TenantService(string tenantId)
    {
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId);
        currentTenant.IsAuthorized.Returns(true);
        return currentTenant;
    }

    private static TimeProvider FrozenClock(DateTime utcNow)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(utcNow));
        return timeProvider;
    }

    private static Plan BuildPlan(int planId, decimal monthlyPrice, int durationMonths = 12, int bonusMonths = 0)
    {
        var result = Plan.Create(
            id: planId,
            code: $"P{planId}",
            displayName: $"Plan {planId}",
            monthlyPrice: monthlyPrice,
            maxStudents: 100,
            maxUsers: 50,
            maxBranches: 10,
            maxTeachers: 20,
            storageGB: 100,
            smsQuota: 1000,
            isActive: true,
            description: "TASK 21.2.1 fixture",
            currencyCode: "EGP",
            durationMonths: durationMonths,
            bonusMonths: bonusMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        return result.Value;
    }

    private static Promotion Percentage(int durationMonths, decimal percentage, int planId = 0)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"{percentage}% off {durationMonths} months",
            type: PromotionType.PercentageDiscount,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PCT10",
            percentage: percentage);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    private static Promotion PayForXMonths(int durationMonths, int chargedMonths, int planId = 0)
    {
        var result = Promotion.Create(
            id: 0,
            name: $"Pay {chargedMonths} get {durationMonths}",
            type: PromotionType.PayForXMonths,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 10,
            code: "PAY4X",
            chargedMonths: chargedMonths);

        Assert.True(result.IsSuccess, Describe(result.Errors));
        Assert.True(result.Value.Activate().IsSuccess);
        return result.Value;
    }

    /// <summary>
    /// Offer → Contract → Subscription snapshot, exactly like the production chain.
    /// Supports bonus months via the bonusMonths parameter.
    /// </summary>
    private static async Task<CommercialFixture> BuildCommercialAsync(
        AppDbContext db,
        int planId,
        decimal monthlyPrice,
        IReadOnlyList<Promotion>? promotions = null,
        int durationMonths = 12,
        int bonusMonths = 0)
    {
        var plan = BuildPlan(planId, monthlyPrice, durationMonths, bonusMonths);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var calculation = new PromotionCalculationService().Calculate(
            plan, durationMonths, Start2026, promotions ?? Array.Empty<Promotion>());

        Assert.True(calculation.IsSuccess, Describe(calculation.Errors));
        var offer = calculation.Value;

        // Contract period: DurationMonths + BonusMonths (total entitlement including bonus)
        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(Start2026, durationMonths, bonusMonths);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantA,
            contractNumber: $"CTR-2121-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            planId: plan.Id,
            effectiveAtUtc: Start2026,
            endsAtUtc: endsAt,
            durationMonths: durationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            grossAmount: offer.BaseAmount,
            contractedAmount: offer.FinalAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            discountAmount: offer.DiscountAmount,
            promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths,
            bonusMonths: bonusMonths,
            maxStudents: plan.MaxStudents,
            maxUsers: plan.MaxUsers,
            maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers,
            storageGb: plan.StorageGB,
            smsQuota: plan.SMSQuota);

        Assert.True(contractResult.IsSuccess, Describe(contractResult.Errors));
        var contract = contractResult.Value;
        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(Start2026).IsSuccess);
        db.Contracts.Add(contract);

        // Snapshot ONLY — no Plan catalog reads on this path.
        var snapshot = contract.GetSubscriptionSnapshot();

        var subscriptionResult = await new SubscriptionFactory(db).CreateFromSnapshotAsync(
            TenantA, plan.Id, snapshot, Start2026, autoRenew: false, activate: true, CancellationToken.None);

        Assert.True(subscriptionResult.IsSuccess, Describe(subscriptionResult.Errors));
        var subscription = subscriptionResult.Value;
        Assert.True(subscription.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(subscription);

        await db.SaveChangesAsync();

        return new CommercialFixture(plan, offer, contract, snapshot, subscription);
    }

    private static async Task<BillingCycle> AddDraftCycleAsync(
        AppDbContext db, TenantPlan subscription, DateTime periodStart, DateTime periodEnd)
    {
        var cycleResult = BillingCycle.Create(
            Guid.NewGuid(), TenantA, subscription.Id, periodStart, periodEnd);

        Assert.True(cycleResult.IsSuccess, Describe(cycleResult.Errors));
        db.BillingCycles.Add(cycleResult.Value);
        await db.SaveChangesAsync();
        return cycleResult.Value;
    }

    private static CreateInvoiceFromBillingCycleHandler CreateCycleInvoiceHandler(
        AppDbContext db, DateTime? clockUtc = null)
    {
        return new CreateInvoiceFromBillingCycleHandler(
            db, TenantService(TenantA), FrozenClock(clockUtc ?? Now2026));
    }

    private static async Task<Invoice> InvoiceFromCycleAsync(
        AppDbContext db, Guid billingCycleId, DateTime? clockUtc = null)
    {
        var handler = CreateCycleInvoiceHandler(db, clockUtc);
        var result = await handler.Handle(
            new CreateInvoiceFromBillingCycleCommand(billingCycleId), CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .SingleAsync(i => i.BillingCycleId == billingCycleId);

        return invoice;
    }

    private static void Amount(decimal expected, decimal actual, string because)
    {
        Assert.True(
            Math.Abs(expected - actual) <= Tolerance,
            $"{because}: expected {expected}, actual {actual}");
    }

    private static string? Describe(IReadOnlyCollection<Error>? errors)
        => errors is null || errors.Count == 0
            ? null
            : string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));

    private static IPlatformAdminGuard AdminGuard()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }

    // ==================================================================
    // 1. 12 paid + 0 bonus (baseline - no bonus months)
    // ==================================================================
    [Fact]
    public async Task Scenario01_NoBonus_FullTermCycle_InvoiceEqualsContractedAmount()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(db, planId: 21211, monthlyPrice: 1000m);

        Assert.Equal(12, fixture.Contract.DurationMonths);
        Assert.Equal(0, fixture.Contract.BonusMonths);
        Assert.Equal(12000m, fixture.Contract.ContractedAmount);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(0m, invoice.DiscountAmount);
        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);
    }

    // ==================================================================
    // 2. 12 paid + 2 bonus (bonus months do NOT affect invoice)
    // ==================================================================
    [Fact]
    public async Task Scenario02_TwoBonusMonths_InvoiceBilledForTwelveMonthsOnly()
    {
        using var db = CreateDbContext();
        // 12 paid months + 2 bonus months
        var fixture = await BuildCommercialAsync(
            db, planId: 21212, monthlyPrice: 1000m, bonusMonths: 2);

        Assert.Equal(12, fixture.Contract.DurationMonths);
        Assert.Equal(2, fixture.Contract.BonusMonths);
        Assert.Equal(12000m, fixture.Contract.ContractedAmount);

        // Subscription has total entitlement (12 + 2 = 14 months)
        Assert.Equal(14, fixture.Subscription.DurationMonths + fixture.Subscription.BonusMonths);
        Assert.Equal(Start2026.AddMonths(14), fixture.Subscription.EffectiveEndsAtUtc);

        // BillingCycle period should represent PAID term only (12 months, NOT 14)
        var paidPeriodEnd = Start2026.AddMonths(12);
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, paidPeriodEnd);

        // CRITICAL: BillingCycle period should NOT include bonus months
        Assert.Equal(Start2026.AddMonths(12), cycle.PeriodEnd);
        Assert.NotEqual(fixture.Subscription.EffectiveEndsAtUtc, cycle.PeriodEnd);

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // CRITICAL: Invoice should be for 12 months (paid term), NOT 14 months
        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(0m, invoice.DiscountAmount);
        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);

        // Bonus months did NOT increase the billed amount
        Assert.NotEqual(14000m, invoice.TotalAmount);
    }

    // ==================================================================
    // 3. 12 paid + 2 bonus + 10% discount (bonus + discount combined)
    // ==================================================================
    [Fact]
    public async Task Scenario03_BonusPlusDiscount_InvoiceBilledForTwelveMonthsWithDiscount()
    {
        using var db = CreateDbContext();
        // 12 paid months + 2 bonus months + 10% discount
        var fixture = await BuildCommercialAsync(
            db, planId: 21213, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) },
            bonusMonths: 2);

        Assert.Equal(12, fixture.Contract.DurationMonths);
        Assert.Equal(2, fixture.Contract.BonusMonths);
        Assert.Equal(10800m, fixture.Contract.ContractedAmount); // 12000 - 1200

        // Subscription has total entitlement (12 + 2 = 14 months)
        Assert.Equal(Start2026.AddMonths(14), fixture.Subscription.EffectiveEndsAtUtc);

        // BillingCycle period should represent PAID term only (12 months)
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // CRITICAL: Invoice should be for 12 months (paid term) with 10% discount
        Assert.Equal(12000m, invoice.Subtotal);
        Assert.Equal(1200m, invoice.DiscountAmount); // 10% of 12000
        Assert.Equal(10800m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);

        // Bonus months did NOT increase the billed amount
        Assert.NotEqual(13200m, invoice.TotalAmount); // 12 × 1100 (wrong)
    }

    // ==================================================================
    // 4. 12 paid + 2 bonus + PayForXMonths (bonus + pay-for-x)
    // ==================================================================
    [Fact]
    public async Task Scenario04_BonusPlusPayForX_InvoiceChargesTenMonthsOnly()
    {
        using var db = CreateDbContext();
        // 12 entitlement months + 2 bonus months + PayFor10Get12
        var fixture = await BuildCommercialAsync(
            db, planId: 21214, monthlyPrice: 1000m,
            promotions: new[] { PayForXMonths(durationMonths: 12, chargedMonths: 10) },
            bonusMonths: 2);

        Assert.Equal(12, fixture.Contract.DurationMonths);
        Assert.Equal(10, fixture.Contract.ChargedMonths);
        Assert.Equal(2, fixture.Contract.BonusMonths);
        Assert.Equal(10000m, fixture.Contract.ContractedAmount); // Pay for 10 months at 1000

        // BillingCycle period should represent PAID term only (12 months)
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // Invoice should charge for 10 months (PayForXMonths), NOT 12
        // Subtotal = list price × 12 entitlement months
        Assert.Equal(12000m, invoice.Subtotal);
        // Discount = pay-for-10 discount (the free 2 months)
        Assert.Equal(2000m, invoice.DiscountAmount);
        // Total = pay for 10 months only
        Assert.Equal(10000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);

        // Bonus months did NOT increase the billed amount beyond PayForXMonths
        Assert.NotEqual(12000m, invoice.TotalAmount);
    }

    // ==================================================================
    // 5. PayForXMonths 12/10 with non-divisible monthly charge (precision test)
    // ==================================================================
    [Fact]
    public async Task Scenario05_PayForXMonths_NonDivisibleCharge_NoRoundingDrift()
    {
        using var db = CreateDbContext();
        // Monthly price = 1000, Duration = 12, ChargedMonths = 10
        // ContractedAmount = 10,000
        // SnapshotMonthlyCharge = 10,000 / 12 = 833.333333...
        // With decimal(18,6): 833.333333 × 12 = 9,999.999996 ≈ 10,000
        var fixture = await BuildCommercialAsync(
            db, planId: 21215, monthlyPrice: 1000m,
            promotions: new[] { PayForXMonths(durationMonths: 12, chargedMonths: 10) });

        Assert.Equal(10, fixture.Contract.ChargedMonths);
        Assert.Equal(10000m, fixture.Contract.ContractedAmount);

        // Verify the monthly charge calculation (10000 / 12)
        var expectedMonthlyCharge = 10000m / 12m;
        Assert.Equal(833.333333m, expectedMonthlyCharge, 6);

        // The snapshot monthly charge should have high precision
        Assert.Equal(expectedMonthlyCharge, fixture.Snapshot.MonthlyCharge, 6);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // CRITICAL: Invoice total must equal ContractedAmount exactly (no drift)
        Assert.Equal(10000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);

        // Even with non-divisible charge, no rounding drift in final total
        Assert.NotEqual(9999.96m, invoice.TotalAmount);
        Assert.NotEqual(10000.04m, invoice.TotalAmount);
    }

    // ==================================================================
    // 6. Full-term invoice equals Contract.ContractedAmount (invariant)
    // ==================================================================
    [Fact]
    public async Task Scenario06_FullTermInvoice_AlwaysEqualsContractedAmount()
    {
        using var db = CreateDbContext();
        // Test with percentage discount
        var fixture1 = await BuildCommercialAsync(
            db, planId: 21216, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 15m) });

        var cycle1 = await AddDraftCycleAsync(
            db, fixture1.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice1 = await InvoiceFromCycleAsync(db, cycle1.Id);

        Assert.Equal(fixture1.Contract.ContractedAmount, invoice1.TotalAmount);
        Assert.Equal(10200m, invoice1.TotalAmount); // 12000 - 1800

        // Test with fixed amount discount
        var fixture2 = await BuildCommercialAsync(
            db, planId: 21217, monthlyPrice: 1500m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 20m) });

        var cycle2 = await AddDraftCycleAsync(
            db, fixture2.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice2 = await InvoiceFromCycleAsync(db, cycle2.Id);

        Assert.Equal(fixture2.Contract.ContractedAmount, invoice2.TotalAmount);
        Assert.Equal(14400m, invoice2.TotalAmount); // 18000 - 3600
    }

    // ==================================================================
    // 7. BillingCycle period does NOT include free bonus months
    // ==================================================================
    [Fact]
    public async Task Scenario07_BillingCyclePeriod_ExcludesBonusMonths()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 21218, monthlyPrice: 1000m, bonusMonths: 3);

        Assert.Equal(12, fixture.Contract.DurationMonths);
        Assert.Equal(3, fixture.Contract.BonusMonths);

        // Subscription entitlement: 12 paid + 3 bonus = 15 months total
        var expectedEffectiveEnd = Start2026.AddMonths(15);
        Assert.Equal(expectedEffectiveEnd, fixture.Subscription.EffectiveEndsAtUtc);

        // BillingCycle should be created for PAID term only
        var paidPeriodEnd = Start2026.AddMonths(12);
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, paidPeriodEnd);

        // CRITICAL: BillingCycle.PeriodEnd equals paid term end, NOT effective end
        Assert.Equal(paidPeriodEnd, cycle.PeriodEnd);
        Assert.NotEqual(fixture.Subscription.EffectiveEndsAtUtc, cycle.PeriodEnd);

        // Verify the difference: 3 bonus months
        var bonusMonthsIncluded = fixture.Subscription.EffectiveEndsAtUtc.Month
            - cycle.PeriodEnd.Month;
        Assert.Equal(3, bonusMonthsIncluded);
    }

    // ==================================================================
    // 8. Subscription EffectiveEndsAtUtc still includes bonus months (entitlement preserved)
    // ==================================================================
    [Fact]
    public async Task Scenario08_SubscriptionEffectiveEndsAt_IncludesBonusMonths()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 21219, monthlyPrice: 1000m, bonusMonths: 4);

        // Subscription duration breakdown
        Assert.Equal(12, fixture.Subscription.DurationMonths);
        Assert.Equal(4, fixture.Subscription.BonusMonths);

        // BaseEndsAtUtc = startsAt + DurationMonths (paid term)
        Assert.Equal(Start2026.AddMonths(12), fixture.Subscription.BaseEndsAtUtc);

        // EffectiveEndsAtUtc = BaseEndsAtUtc + BonusMonths (total entitlement)
        Assert.Equal(Start2026.AddMonths(16), fixture.Subscription.EffectiveEndsAtUtc);

        // Subscription access should include bonus months (16 months total access)
        Assert.True(fixture.Subscription.IsActiveAsOf(Start2026.AddMonths(14)));
        Assert.False(fixture.Subscription.IsActiveAsOf(Start2026.AddMonths(17)));

        // BillingCycle for the paid term should NOT include bonus in its period
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, fixture.Subscription.BaseEndsAtUtc);

        Assert.Equal(fixture.Subscription.BaseEndsAtUtc, cycle.PeriodEnd);
        Assert.NotEqual(fixture.Subscription.EffectiveEndsAtUtc, cycle.PeriodEnd);
    }

    // ==================================================================
    // 9. Plan mutation does NOT affect the snapshot
    // ==================================================================
    [Fact]
    public async Task Scenario09_PlanMutation_AfterSubscriptionCreation_InvoiceUnaffected()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 212110, monthlyPrice: 1000m, bonusMonths: 2,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) });

        var firstCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var firstInvoice = await InvoiceFromCycleAsync(db, firstCycle.Id);

        // Mutate the live catalog after the commercial snapshot was taken.
        var update = fixture.Plan.Update(
            fixture.Plan.Code, fixture.Plan.DisplayName, 9999m,
            maxStudents: 999, maxUsers: 999, maxBranches: 99, maxTeachers: 99,
            storageGB: 999, smsQuota: 9999, isActive: true);
        Assert.True(update.IsSuccess, Describe(update.Errors));
        await db.SaveChangesAsync();

        // Subscription snapshot should be unchanged
        Assert.Equal(1000m, fixture.Subscription.SnapshotPrice);
        Assert.Equal(900m, fixture.Subscription.SnapshotMonthlyCharge);

        // Invoice should be unaffected by plan mutation
        Assert.Equal(10800m, firstInvoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, firstInvoice.TotalAmount);

        // Bonus months in snapshot are also unaffected
        Assert.Equal(2, fixture.Subscription.BonusMonths);
    }

    // ==================================================================
    // 10. Promotion mutation does NOT affect the snapshot
    // ==================================================================
    [Fact]
    public async Task Scenario10_PromotionMutation_AfterContractCreation_InvoiceUnaffected()
    {
        using var db = CreateDbContext();
        var promo = Percentage(durationMonths: 12, percentage: 10m);
        db.Promotions.Add(promo);

        var fixture = await BuildCommercialAsync(
            db, planId: 212111, monthlyPrice: 1000m, bonusMonths: 1,
            promotions: new[] { promo });
        await db.SaveChangesAsync();

        var firstCycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var firstInvoice = await InvoiceFromCycleAsync(db, firstCycle.Id);

        // Kill the promotion after the contract snapshot was taken.
        Assert.True(promo.Deactivate().IsSuccess);
        await db.SaveChangesAsync();

        // Invoice should be unaffected by promotion mutation
        Assert.Equal(10800m, firstInvoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, firstInvoice.TotalAmount);

        // Bonus months in snapshot are also unaffected
        Assert.Equal(1, fixture.Subscription.BonusMonths);
    }

    // ==================================================================
    // 11. Renewal command creates BillingCycle with correct period (bonus excluded)
    // ==================================================================
    [Fact]
    public async Task Scenario11_RenewalCommand_BillingCycleExcludesBonusMonths()
    {
        using var db = CreateDbContext();

        var plan = BuildPlan(212112, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 2);
        db.Plans.Add(plan);

        var oldSubscription = TenantPlan.Create(
            Guid.NewGuid(), TenantA, plan.Id, 1000m, 1000m, "EGP", 12, 0,
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).Value;
        Assert.True(oldSubscription.Activate(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        db.TenantPlans.Add(oldSubscription);
        await db.SaveChangesAsync();

        var handler = new RenewSubscriptionOfferHandler(
            db,
            AdminGuard(),
            new SubscriptionFactory(db),
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            FrozenClock(Now2026));

        var result = await handler.Handle(
            new RenewSubscriptionOfferCommand(oldSubscription.Id, PlanId: plan.Id, DurationMonths: 12),
            CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var newContract = await db.Contracts.IgnoreQueryFilters()
            .Include(c => c.PricingTiers)
            .SingleAsync(c => c.Id == result.Value);

        var invoice = await db.Invoices.IgnoreQueryFilters()
            .SingleAsync(i => i.ContractId == newContract.Id);

        var cycle = await db.BillingCycles.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == invoice.BillingCycleId);

        var newSubscription = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == invoice.SubscriptionId);

        // CRITICAL: BillingCycle.PeriodEnd should equal BaseEndsAtUtc (paid term only)
        Assert.Equal(newSubscription.BaseEndsAtUtc, cycle.PeriodEnd);
        Assert.NotEqual(newSubscription.EffectiveEndsAtUtc, cycle.PeriodEnd);

        // Invoice should be for paid term only (12 months, not 14)
        Assert.Equal(12000m, invoice.TotalAmount);
        Assert.Equal(newContract.ContractedAmount, invoice.TotalAmount);

        // Subscription still has bonus months for entitlement
        Assert.Equal(2, newSubscription.BonusMonths);
        Assert.Equal(14, newSubscription.DurationMonths + newSubscription.BonusMonths);
    }

    // ==================================================================
    // 12. Change plan command creates BillingCycle with correct period (bonus excluded)
    // ==================================================================
    [Fact]
    public async Task Scenario12_ChangePlanCommand_BillingCycleExcludesBonusMonths()
    {
        using var db = CreateDbContext();

        var oldPlan = BuildPlan(212130, monthlyPrice: 800m, durationMonths: 12, bonusMonths: 0);
        db.Plans.Add(oldPlan);

        var oldSubscription = TenantPlan.Create(
            Guid.NewGuid(), TenantA, oldPlan.Id, 800m, 800m, "EGP", 12, 0,
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).Value;
        Assert.True(oldSubscription.Activate(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
        db.TenantPlans.Add(oldSubscription);

        var newPlan = BuildPlan(212131, monthlyPrice: 1000m, durationMonths: 12, bonusMonths: 3);
        db.Plans.Add(newPlan);

        var promo = Percentage(durationMonths: 12, percentage: 10m);
        db.Promotions.Add(promo);
        await db.SaveChangesAsync();

        var handler = new ChangeSubscriptionPlanHandler(
            db,
            AdminGuard(),
            new SubscriptionFactory(db),
            new PromotionCalculationService(),
            Substitute.For<ITenantRegistrySync>(),
            Substitute.For<IAuditWriter>(),
            FrozenClock(Now2026));

        var result = await handler.Handle(
            new ChangeSubscriptionPlanCommand(oldSubscription.Id, newPlan.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess, Describe(result.Errors));

        var newContract = await db.Contracts.IgnoreQueryFilters()
            .Include(c => c.PricingTiers)
            .SingleAsync(c => c.Id == result.Value);

        var invoice = await db.Invoices.IgnoreQueryFilters()
            .SingleAsync(i => i.ContractId == newContract.Id);

        var cycle = await db.BillingCycles.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == invoice.BillingCycleId);

        var newSubscription = await db.TenantPlans.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == invoice.SubscriptionId);

        // CRITICAL: BillingCycle.PeriodEnd should equal BaseEndsAtUtc (paid term only)
        Assert.Equal(newSubscription.BaseEndsAtUtc, cycle.PeriodEnd);
        Assert.NotEqual(newSubscription.EffectiveEndsAtUtc, cycle.PeriodEnd);

        // Invoice should be for 12 paid months (not 15 including bonus)
        Assert.Equal(10800m, invoice.TotalAmount); // 12000 - 1200
        Assert.Equal(newContract.ContractedAmount, invoice.TotalAmount);

        // Subscription still has bonus months for entitlement
        Assert.Equal(3, newSubscription.BonusMonths);
        Assert.Equal(15, newSubscription.DurationMonths + newSubscription.BonusMonths);
    }

    // ==================================================================
    // 13. Invoice arithmetic identity with bonus months
    // ==================================================================
    [Fact]
    public async Task Scenario13_InvoiceArithmeticIdentity_HoldsWithBonusMonths()
    {
        using var db = CreateDbContext();
        var fixture = await BuildCommercialAsync(
            db, planId: 212114, monthlyPrice: 1000m,
            promotions: new[] { Percentage(durationMonths: 12, percentage: 10m) },
            bonusMonths: 2);

        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));
        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // INV-01: TotalAmount = Subtotal − DiscountAmount + TaxAmount
        Amount(
            invoice.Subtotal - invoice.DiscountAmount + invoice.TaxAmount,
            invoice.TotalAmount,
            "invoice internal arithmetic");

        // Contract-derivation identity
        Amount(fixture.Contract.GrossAmount, invoice.Subtotal, "subtotal = contract gross");
        Amount(fixture.Contract.DiscountAmount, invoice.DiscountAmount, "discount = contract discount");
        Amount(fixture.Contract.ContractedAmount, invoice.TotalAmount, "total = contract contracted amount");

        // Bonus months did NOT affect the invoice
        Assert.Equal(12000m - 1200m, invoice.TotalAmount); // 10800
        Assert.NotEqual(14000m - 1400m, invoice.TotalAmount); // Would be 12600 if bonus included
    }

    // ==================================================================
    // 14. Non-divisible monthly charge with bonus months (precision + bonus combined)
    // ==================================================================
    [Fact]
    public async Task Scenario14_BonusPlusNonDivisibleCharge_NoRoundingDrift()
    {
        using var db = CreateDbContext();
        // Monthly price = 1000, Duration = 12, ChargedMonths = 10, BonusMonths = 2
        // ContractedAmount = 10,000
        // SnapshotMonthlyCharge = 10,000 / 12 = 833.333333...
        // Bonus months = 2 (free)
        var fixture = await BuildCommercialAsync(
            db, planId: 212115, monthlyPrice: 1000m,
            promotions: new[] { PayForXMonths(durationMonths: 12, chargedMonths: 10) },
            bonusMonths: 2);

        Assert.Equal(10, fixture.Contract.ChargedMonths);
        Assert.Equal(2, fixture.Contract.BonusMonths);
        Assert.Equal(10000m, fixture.Contract.ContractedAmount);

        // Verify the monthly charge calculation (10000 / 12)
        var expectedMonthlyCharge = 10000m / 12m;
        Assert.Equal(833.333333m, expectedMonthlyCharge, 6);

        // BillingCycle period should represent PAID term only (12 months, NOT 14)
        var cycle = await AddDraftCycleAsync(
            db, fixture.Subscription, Start2026, Start2026.AddMonths(12));

        var invoice = await InvoiceFromCycleAsync(db, cycle.Id);

        // CRITICAL: Invoice total must equal ContractedAmount exactly
        Assert.Equal(10000m, invoice.TotalAmount);
        Assert.Equal(fixture.Contract.ContractedAmount, invoice.TotalAmount);

        // Bonus months did NOT increase the billed amount
        Assert.NotEqual(11666.67m, invoice.TotalAmount); // Wrong: 10 × 1166.667
        Assert.NotEqual(12000m, invoice.TotalAmount); // Wrong: 12 × 1000
    }
}
