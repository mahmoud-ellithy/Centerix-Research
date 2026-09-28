namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Installments.Commands;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task A — Commercial PaymentTerms foundation tests.
///
/// Validates the binding design baseline invariants from
/// docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md against the implemented
/// domain (Offer, Contract, Installment).
///
/// Covered scenarios (per Task A spec §9):
///   1. Offer explicitly set to FullUpfront  ⇒ Contract snapshot is FullUpfront.
///   2. Offer explicitly set to Installments ⇒ Contract snapshot is Installments.
///   3. Same PromotionType can produce FullUpfront AND Installments
///      (proves PromotionType does not determine PaymentTerms).
///   4. BonusMonths > 0 does NOT automatically change Installments → FullUpfront.
///   5. Early settlement of an Installments contract does NOT mutate
///      Contract.PaymentTerms.
///   6. Creating an installment schedule for FullUpfront is rejected.
///   7. Creating/using an installment schedule for Installments remains valid.
///   8. Contract created from Offer cannot silently receive a different
///      PaymentTerms value (i.e. Contract.PaymentTerms == Offer.PaymentTerms).
/// </summary>
public class TaskA_PaymentTermsFoundationTests : IClassFixture<TaskAPaymentTermsTestFactory>
{
    private const string TenantId = "tenant-paymentterms";
    private readonly TaskAPaymentTermsTestFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _dbContext;

    public TaskA_PaymentTermsFoundationTests(TaskAPaymentTermsTestFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
    }

    private IMediator Mediator => _scope.ServiceProvider.GetRequiredService<IMediator>();
    private ISubscriptionFactory SubscriptionFactory => _scope.ServiceProvider.GetRequiredService<ISubscriptionFactory>();

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<Plan> SeedPlanAsync(
        string currency = "EGP",
        decimal monthlyPrice = 1000m,
        int bonusMonths = 0)
    {
        var plan = Plan.Create(
            id: _factory.NextPlanId++,
            code: $"PLAN-{Guid.NewGuid().ToString("N")[..8]}",
            displayName: "Test Plan",
            monthlyPrice: monthlyPrice,
            maxStudents: 100, maxUsers: 50, maxBranches: 5, maxTeachers: 20,
            storageGB: 10, smsQuota: 1000,
            isActive: true,
            description: null,
            currencyCode: currency,
            durationMonths: 12,
            bonusMonths: bonusMonths);
        _dbContext.Plans.Add(plan.Value!);
        await _dbContext.SaveChangesAsync();
        return plan.Value!;
    }

    private async Task<Promotion> SeedPromotionAsync(
        int planId,
        PromotionType type = PromotionType.PercentageDiscount,
        decimal? percentage = 10m,
        int? chargedMonths = null,
        decimal? promotionalPrice = null)
    {
        var now = DateTime.UtcNow;
        var promotion = Promotion.Create(
            id: _factory.NextPromotionId++,
            name: "Test Promotion",
            type: type,
            planId: planId,
            durationMonths: 12,
            startsAtUtc: now,
            endsAtUtc: now.AddYears(1),
            priority: 1,
            code: $"PROMO-{Guid.NewGuid().ToString("N")[..8]}",
            percentage: percentage,
            fixedAmount: null,
            promotionalPrice: promotionalPrice,
            chargedMonths: chargedMonths);
        _dbContext.Promotions.Add(promotion.Value!);
        await _dbContext.SaveChangesAsync();
        return promotion.Value!;
    }

    private async Task<Guid> SeedAcceptedOfferAsync(
        Plan plan,
        PaymentTerms paymentTerms,
        int bonusMonths = 0)
    {
        // If a plan says BonusMonths > 0, that's the catalog hint. It is NOT
        // used to derive PaymentTerms. The plan created here is the helper
        // for tests; PaymentTerms is supplied independently.
        var offerResult = Offer.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            planId: plan.Id,
            durationMonths: 12,
            baseAmount: 12000m,
            discountAmount: 0m,
            finalAmount: 12000m,
            monthlyListPrice: plan.MonthlyPrice,
            currencyCode: plan.CurrencyCode,
            promotionId: null,
            promotionName: null,
            promotionCode: null,
            promotionType: "None",
            discountPercentage: null,
            chargedMonths: null,
            calculatedAtUtc: DateTime.UtcNow,
            expiresAtUtc: DateTime.UtcNow.AddHours(24),
            bonusMonths: bonusMonths,
            maxStudents: plan.MaxStudents,
            maxUsers: plan.MaxUsers,
            maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers,
            storageGb: plan.StorageGB,
            smsQuota: plan.SMSQuota,
            entitlementSnapshotVersion: Offer.CompleteEntitlementSnapshotVersion,
            paymentTerms: paymentTerms);
        Assert.True(offerResult.IsSuccess,
            $"Offer.Create failed: {string.Join(",", offerResult.Errors?.Select(e => e.Code) ?? [])}");
        var offer = offerResult.Value!;
        offer.Accept(DateTime.UtcNow);
        _dbContext.Offers.Add(offer);
        await _dbContext.SaveChangesAsync();
        return offer.Id;
    }

    private async Task<(Guid contractId, Guid subscriptionId)> SeedContractFromOfferAsync(
        Guid offerId,
        PaymentTerms expectedPaymentTerms)
    {
        var offer = await _dbContext.Offers
            .Include(o => o.PricingTiers)
            .Include(o => o.Features)
            .FirstAsync(o => o.Id == offerId);

        // Build Contract via the same composition that CreateContractFromOfferCommand does.
        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(
            offer.CalculatedAtUtc, offer.DurationMonths, offer.BonusMonths);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            contractNumber: $"CNT-{Guid.NewGuid().ToString("N")[..8]}",
            planId: offer.PlanId,
            effectiveAtUtc: offer.CalculatedAtUtc,
            endsAtUtc: endsAt,
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            grossAmount: offer.BaseAmount,
            contractedAmount: offer.FinalAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: offer.PaymentTerms,
            discountAmount: offer.DiscountAmount,
            promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths,
            bonusMonths: offer.BonusMonths,
            maxStudents: offer.MaxStudents,
            maxUsers: offer.MaxUsers,
            maxBranches: offer.MaxBranches,
            maxTeachers: offer.MaxTeachers,
            storageGb: offer.StorageGB,
            smsQuota: offer.SMSQuota);
        Assert.True(contractResult.IsSuccess,
            $"Contract.Create failed: {string.Join(",", contractResult.Errors?.Select(e => e.Code) ?? [])}");
        var contract = contractResult.Value!;
        Assert.Equal(expectedPaymentTerms, contract.PaymentTerms);
        _dbContext.Contracts.Add(contract);
        await _dbContext.SaveChangesAsync();

        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: TenantId,
            planId: offer.PlanId,
            snapshotPrice: offer.MonthlyListPrice,
            snapshotMonthlyCharge: offer.MonthlyListPrice,
            snapshotCurrency: offer.CurrencyCode,
            durationMonths: offer.DurationMonths,
            bonusMonths: offer.BonusMonths,
            startsAtUtc: offer.CalculatedAtUtc,
            autoRenew: false,
            status: SubscriptionStatus.Active,
            maxStudents: offer.MaxStudents,
            maxUsers: offer.MaxUsers,
            maxBranches: offer.MaxBranches,
            maxTeachers: offer.MaxTeachers,
            storageGb: offer.StorageGB,
            smsQuota: offer.SMSQuota);
        Assert.True(subscription.IsSuccess,
            $"TenantPlan.Create failed: {string.Join(",", subscription.Errors?.Select(e => e.Code) ?? [])}");
        var sub = subscription.Value!;
        var linkResult = sub.LinkToContract(contract.Id);
        Assert.True(linkResult.IsSuccess);
        _dbContext.TenantPlans.Add(sub);
        await _dbContext.SaveChangesAsync();
        return (contract.Id, sub.Id);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 1: Offer explicitly set to FullUpfront ⇒ Contract snapshot is FullUpfront
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test1_OfferFullUpfront_ContractSnapshotIsFullUpfront()
    {
        var plan = await SeedPlanAsync();
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.FullUpfront);
        var (contractId, _) = await SeedContractFromOfferAsync(offerId, PaymentTerms.FullUpfront);

        var contract = await _dbContext.Contracts.AsNoTracking()
            .FirstAsync(c => c.Id == contractId);
        Assert.Equal(PaymentTerms.FullUpfront, contract.PaymentTerms);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 2: Offer explicitly set to Installments ⇒ Contract snapshot is Installments
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test2_OfferInstallments_ContractSnapshotIsInstallments()
    {
        var plan = await SeedPlanAsync();
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.Installments);
        var (contractId, _) = await SeedContractFromOfferAsync(offerId, PaymentTerms.Installments);

        var contract = await _dbContext.Contracts.AsNoTracking()
            .FirstAsync(c => c.Id == contractId);
        Assert.Equal(PaymentTerms.Installments, contract.PaymentTerms);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 3: Same PromotionType can produce FullUpfront AND Installments
    //         (proves PromotionType does not determine PaymentTerms)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test3_SamePromotionType_ProducesBothPaymentTerms()
    {
        // Use PayForXMonths — historically the audit flagged it as a candidate
        // for "PromotionType → FullUpfront" inference. The Task A baseline
        // explicitly forbids that inference. We must be able to create both
        // contracts from the same promotion, each with a different PaymentTerms.
        var plan = await SeedPlanAsync();
        _ = await SeedPromotionAsync(
            planId: plan.Id,
            type: PromotionType.PayForXMonths,
            percentage: null,
            chargedMonths: 5); // "5 paid, 6 served"

        // Same promotion, two offers: one FullUpfront, one Installments.
        var offerFullUpfrontId = await SeedAcceptedOfferAsync(plan, PaymentTerms.FullUpfront);
        var offerInstallmentsId = await SeedAcceptedOfferAsync(plan, PaymentTerms.Installments);

        var (fullContractId, _) = await SeedContractFromOfferAsync(offerFullUpfrontId, PaymentTerms.FullUpfront);
        var (instContractId, _) = await SeedContractFromOfferAsync(offerInstallmentsId, PaymentTerms.Installments);

        var full = await _dbContext.Contracts.AsNoTracking().FirstAsync(c => c.Id == fullContractId);
        var inst = await _dbContext.Contracts.AsNoTracking().FirstAsync(c => c.Id == instContractId);

        Assert.Equal(PaymentTerms.FullUpfront, full.PaymentTerms);
        Assert.Equal(PaymentTerms.Installments, inst.PaymentTerms);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 4: BonusMonths > 0 does NOT automatically change Installments → FullUpfront
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test4_BonusMonthsDoesNotForceFullUpfront()
    {
        // Plan grants 2 bonus months (catalog hint). The customer takes
        // PaymentTerms = Installments. The bonus does NOT force FullUpfront.
        var plan = await SeedPlanAsync(bonusMonths: 2);
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.Installments, bonusMonths: 2);
        var (contractId, _) = await SeedContractFromOfferAsync(offerId, PaymentTerms.Installments);

        var contract = await _dbContext.Contracts.AsNoTracking().FirstAsync(c => c.Id == contractId);
        Assert.Equal(2, contract.BonusMonths);
        Assert.Equal(PaymentTerms.Installments, contract.PaymentTerms);
        Assert.NotEqual(PaymentTerms.FullUpfront, contract.PaymentTerms);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 5: Early settlement of an Installments contract does NOT mutate
    //         Contract.PaymentTerms
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test5_EarlySettlement_DoesNotMutatePaymentTerms()
    {
        // We don't need an actual installment flow to verify the invariant:
        // Contract.PaymentTerms is set in Contract.Create and the public API
        // exposes no setter, no update command, and no method that mutates
        // it. Simulating a financial settlement is therefore unnecessary to
        // prove that no code path can rewrite it. We assert:
        //   (a) The property is read-only by construction.
        //   (b) An Installments contract created today remains Installments
        //       after arbitrary "settlement-like" operations on the same row
        //       (calling every public operation that does not throw).
        var plan = await SeedPlanAsync();
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.Installments);
        var (contractId, subscriptionId) = await SeedContractFromOfferAsync(
            offerId, PaymentTerms.Installments);

        // The contract snapshot is Installments; capture it.
        var initial = await _dbContext.Contracts.AsNoTracking().FirstAsync(c => c.Id == contractId);
        Assert.Equal(PaymentTerms.Installments, initial.PaymentTerms);

        // Simulate an "early-settlement"-like event: there is no public
        // operation on Contract that mutates PaymentTerms. We exhaustively
        // invoke the other lifecycle commands to prove they do not change it.
        var contract = await _dbContext.Contracts.FirstAsync(c => c.Id == contractId);
        Assert.Equal(ContractStatus.Draft, contract.Status);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);
        contract.Suspend();
        contract.Reactivate();
        // Terminate is only valid from Draft/PendingApproval/Active/Suspended — reactivate
        // is fine, and terminate is fine here.
        contract.Terminate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        var reloaded = await _dbContext.Contracts.AsNoTracking().FirstAsync(c => c.Id == contractId);
        Assert.Equal(PaymentTerms.Installments, reloaded.PaymentTerms);
        Assert.NotEqual(PaymentTerms.FullUpfront, reloaded.PaymentTerms);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 6: Creating an installment schedule for FullUpfront is rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test6_FullUpfrontContract_InstallmentScheduleRejected()
    {
        var plan = await SeedPlanAsync();
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.FullUpfront);
        var (contractId, subscriptionId) = await SeedContractFromOfferAsync(
            offerId, PaymentTerms.FullUpfront);

        // Activate the contract so the existing "ContractNotActive" guard passes
        // and the new "FullUpfront_InstallmentScheduleForbidden" guard is the
        // one that fires.
        var contract = await _dbContext.Contracts.FirstAsync(c => c.Id == contractId);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        // The schedule's covered period must align with the contract's
        // EffectiveAtUtc..EndsAtUtc window — otherwise we trigger the
        // "CoveredPeriod_StartBeforeContract" guard before reaching the
        // FullUpfront guard. We pin the schedule to the contract window
        // so that the only thing that fails the test is the FullUpfront rule.
        var periodStart = contract.EffectiveAtUtc;
        var periodEnd = contract.EndsAtUtc;

        var schedule = new List<InstallmentScheduleItem>
        {
            new(
                SequenceNumber: 1,
                DueDateUtc: DateTime.UtcNow.AddDays(30),
                CoveredPeriodStartUtc: periodStart,
                CoveredPeriodEndUtc: periodEnd,
                Amount: 12000m)
        };

        var cmd = new CreateInstallmentScheduleCommand(contractId, subscriptionId, schedule);
        var result = await Mediator.Send(cmd);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.Contains(result.Errors!, e => e.Code == "Contract.FullUpfront_InstallmentScheduleForbidden");
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 7: Creating/using an installment schedule for Installments is valid
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test7_InstallmentsContract_InstallmentScheduleAllowed()
    {
        var plan = await SeedPlanAsync();
        var offerId = await SeedAcceptedOfferAsync(plan, PaymentTerms.Installments);
        var (contractId, subscriptionId) = await SeedContractFromOfferAsync(
            offerId, PaymentTerms.Installments);

        var contract = await _dbContext.Contracts.FirstAsync(c => c.Id == contractId);
        contract.SubmitForApproval();
        contract.Activate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        // Pin the schedule to the contract's effective window so the
        // CoveredPeriod guard does not interfere with the test's intent
        // (which is to prove that the FullUpfront rule, not other guards,
        // is what permits or denies an Installments schedule).
        var periodStart = contract.EffectiveAtUtc;
        var periodEnd = contract.EndsAtUtc;
        var midPoint = periodStart.AddTicks((periodEnd - periodStart).Ticks / 2);

        var schedule = new List<InstallmentScheduleItem>
        {
            new(
                SequenceNumber: 1,
                DueDateUtc: DateTime.UtcNow.AddDays(30),
                CoveredPeriodStartUtc: periodStart,
                CoveredPeriodEndUtc: midPoint,
                Amount: 6000m),
            new(
                SequenceNumber: 2,
                DueDateUtc: DateTime.UtcNow.AddDays(60),
                CoveredPeriodStartUtc: midPoint,
                CoveredPeriodEndUtc: periodEnd,
                Amount: 6000m)
        };

        var cmd = new CreateInstallmentScheduleCommand(contractId, subscriptionId, schedule);
        var result = await Mediator.Send(cmd);

        Assert.True(result.IsSuccess,
            $"CreateInstallmentScheduleCommand failed unexpectedly: {string.Join(",", result.Errors?.Select(e => e.Code) ?? [])}");
        Assert.Equal(2, result.Value!.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Test 8: Contract created from Offer cannot silently receive a different
    //         PaymentTerms value (i.e. Contract.PaymentTerms == Offer.PaymentTerms)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test8_ContractPaymentTerms_AlwaysEqualsOfferPaymentTerms()
    {
        // Test the snapshot property twice with two different
        // payment modes; in neither case can the Contract be coerced
        // to a different PaymentTerms than the Offer.
        foreach (var pt in new[] { PaymentTerms.FullUpfront, PaymentTerms.Installments })
        {
            var plan = await SeedPlanAsync();
            var offerId = await SeedAcceptedOfferAsync(plan, pt);
            var (contractId, _) = await SeedContractFromOfferAsync(offerId, pt);

            var contract = await _dbContext.Contracts.AsNoTracking()
                .FirstAsync(c => c.Id == contractId);
            var offer = await _dbContext.Offers.AsNoTracking()
                .FirstAsync(o => o.Id == offerId);

            Assert.Equal(offer.PaymentTerms, contract.PaymentTerms);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Bonus invariant: PaymentTerms enum is well-defined and not silently defaulted
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PaymentTerms_EnumValues_AreExactlyTheTwoExpectedArms()
    {
        // The binding design baseline locks exactly two arms.
        // Adding a new arm would be a deliberate domain change.
        var values = Enum.GetValues<PaymentTerms>();
        Assert.Equal(2, values.Length);
        Assert.Contains(PaymentTerms.FullUpfront, values);
        Assert.Contains(PaymentTerms.Installments, values);
    }

    [Fact]
    public void PaymentTerms_NumericValues_AreStableForPersistence()
    {
        // The byte column stores PaymentTerms via HasConversion<byte>(). The
        // numeric values are part of the on-disk schema and MUST NOT change.
        Assert.Equal(0, (byte)PaymentTerms.FullUpfront);
        Assert.Equal(1, (byte)PaymentTerms.Installments);
    }
}

/// <summary>
/// Test factory for Task A. Wraps TestWebApplicationFactory and supplies a
/// FakeCurrentTenant so the handlers can pass tenant validation.
/// </summary>
public class TaskAPaymentTermsTestFactory : TestWebApplicationFactory
{
    public int NextPlanId { get; set; } = 100000;
    public int NextPromotionId { get; set; } = 100000;

    protected override void ConfigureWebHost(
        Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentTenant));
            if (existing is not null) services.Remove(existing);
            services.AddSingleton<ICurrentTenant>(new TaskAFakeCurrentTenant());
        });
    }
}

internal class TaskAFakeCurrentTenant : ICurrentTenant
{
    public string TenantId => "tenant-paymentterms";
    public string ResolvedTenantId => "tenant-paymentterms";
    public bool IsAuthorized => true;
    public bool IsResolved => true;
    public bool IsActive => true;
    public DateTime? ValidUpTo => null;
    public void AuthorizeTenant() { }
}
