namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task C — FreeMonthsBenefit Snapshot &amp; EF Core round-trip tests (InMemory).
///
/// Covers:
///   * Contract.AddFreeMonthsBenefit invariants (currency match, mutation).
///   * EF Core round-trip for <see cref="FreeMonthsBenefit"/> and
///     <see cref="OfferFreeMonthsBenefit"/> with the InMemory provider.
///   * Offer → Contract production flow (AcceptOffer + CreateContractFromOffer)
///     preserves FreeMonthsBenefit.EligibilityRule + EntitlementMonths verbatim.
///   * Multiple free months benefits on a single contract are preserved
///     independently.
///   * ContractedAmount is NOT derived from FreeMonthsBenefits.EntitlementMonths
///     (design invariant 32).
///   * A null <c>OfferFreeMonthsBenefit.EligibilityRule</c> is impossible to
///     create through the domain API, and the production handler fails
///     explicitly rather than silently dropping the commercial entitlement.
/// </summary>
public class TaskC_FreeMonthsBenefitSnapshotTests : IClassFixture<TaskCFakeTenantTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly TaskCFakeTenantTestFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _dbContext;

    public TaskC_FreeMonthsBenefitSnapshotTests(TaskCFakeTenantTestFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
    }

    private IMediator Mediator => _scope.ServiceProvider.GetRequiredService<IMediator>();

    private static EligibilityRule DefaultUpfrontBonusRule(decimal contractedAmount = 5000m) =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(contractedAmount),
            EligibilityRule.NoOverdueInstallment());

    private Offer NewOffer(int planId) => Offer.Create(
        id: Guid.NewGuid(),
        tenantId: TenantId,
        planId: planId,
        durationMonths: 12,
        baseAmount: 12000m,
        discountAmount: 0m,
        finalAmount: 12000m,
        monthlyListPrice: 1000m,
        currencyCode: "EGP",
        paymentTerms: PaymentTerms.FullUpfront,
        calculatedAtUtc: DateTime.UtcNow,
        expiresAtUtc: DateTime.UtcNow.AddDays(7),
        bonusMonths: 0,
        maxStudents: 100, maxUsers: 5, maxBranches: 1, maxTeachers: 10,
        storageGb: 10, smsQuota: 100,
        entitlementSnapshotVersion: Offer.CompleteEntitlementSnapshotVersion).Value;

    private Contract NewContract(int planId) => Contract.Create(
        id: Guid.NewGuid(),
        tenantId: TenantId,
        contractNumber: $"CNT-C-{Guid.NewGuid():N}"[..16],
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

    // ─────────────────────────────────────────────────────────────────
    // 1. Contract.AddFreeMonthsBenefit invariants
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TestC01_Contract_AddFreeMonthsBenefit_WithMatchingCurrency_AppendsToNavigation()
    {
        var contract = NewContract(planId: 1);
        var benefit = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: contract.Id,
            entitlementMonths: 1,
            currencyCode: "EGP",
            eligibilityRule: DefaultUpfrontBonusRule()).Value;

        var result = contract.AddFreeMonthsBenefit(benefit);

        Assert.True(result.IsSuccess);
        Assert.Single(contract.FreeMonthsBenefits);
        Assert.Equal(benefit.Id, contract.FreeMonthsBenefits[0].Id);
        Assert.Equal(1, contract.FreeMonthsBenefits[0].EntitlementMonths);
    }

    [Fact]
    public void TestC02_Contract_AddFreeMonthsBenefit_WithCurrencyMismatch_IsRejected()
    {
        var contract = NewContract(planId: 1);
        var benefit = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: contract.Id,
            entitlementMonths: 1,
            currencyCode: "USD",
            eligibilityRule: DefaultUpfrontBonusRule()).Value;

        var result = contract.AddFreeMonthsBenefit(benefit);

        Assert.False(result.IsSuccess);
        Assert.Equal("Contract.FreeMonthsBenefit.CurrencyMismatch", result.Errors!.First().Code);
        Assert.Empty(contract.FreeMonthsBenefits);
    }

    // ─────────────────────────────────────────────────────────────────
    // 2. EF Core round-trip
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestC03_EfCore_FreeMonthsBenefit_RoundTripsVerbatimThroughInMemory()
    {
        var contract = NewContract(planId: 1);
        var rule = DefaultUpfrontBonusRule(7500m);
        var benefit = FreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            contractId: contract.Id,
            entitlementMonths: 2,
            currencyCode: "EGP",
            eligibilityRule: rule).Value;

        contract.AddFreeMonthsBenefit(benefit);
        _dbContext.Contracts.Add(contract);
        _dbContext.StampAddedTenantIds(TenantId);
        await _dbContext.SaveChangesAsync();

        // Re-read in a fresh logical context.
        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.FreeMonthsBenefits)
            .FirstAsync(c => c.Id == contract.Id);

        Assert.Single(loaded.FreeMonthsBenefits);
        var loadedBenefit = loaded.FreeMonthsBenefits[0];
        Assert.Equal(2, loadedBenefit.EntitlementMonths);
        Assert.Equal(rule, loadedBenefit.EligibilityRule);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(rule),
            EligibilityRuleSerializer.Serialize(loadedBenefit.EligibilityRule));
    }

    [Fact]
    public async Task TestC04_EfCore_OfferFreeMonthsBenefit_RoundTripsVerbatimThroughInMemory()
    {
        var offer = NewOffer(planId: 1);
        var rule = DefaultUpfrontBonusRule();
        var benefit = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            entitlementMonths: 1,
            currencyCode: "EGP",
            eligibilityRule: rule).Value;

        offer.AddFreeMonthsBenefit(benefit);
        _dbContext.Offers.Add(offer);
        _dbContext.StampAddedTenantIds(TenantId);
        await _dbContext.SaveChangesAsync();

        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.FreeMonthsBenefits)
            .FirstAsync(o => o.Id == offer.Id);

        Assert.Single(loaded.FreeMonthsBenefits);
        var loadedBenefit = loaded.FreeMonthsBenefits[0];
        Assert.Equal(1, loadedBenefit.EntitlementMonths);
        Assert.Equal(rule, loadedBenefit.EligibilityRule);
    }

    [Fact]
    public async Task TestC05_EfCore_MultipleFreeMonthsBenefits_ArePreservedIndependently()
    {
        var contract = NewContract(planId: 1);

        // Three benefits: 1, 3, and 12 free months, with distinct rules.
        var b1 = FreeMonthsBenefit.Create(Guid.NewGuid(), contract.Id, 1, "EGP",
            DefaultUpfrontBonusRule(5000m)).Value;
        var b2 = FreeMonthsBenefit.Create(Guid.NewGuid(), contract.Id, 3, "EGP",
            DefaultUpfrontBonusRule(10000m)).Value;
        var b3 = FreeMonthsBenefit.Create(Guid.NewGuid(), contract.Id, 12, "EGP",
            DefaultUpfrontBonusRule(50000m)).Value;

        contract.AddFreeMonthsBenefit(b1);
        contract.AddFreeMonthsBenefit(b2);
        contract.AddFreeMonthsBenefit(b3);

        _dbContext.Contracts.Add(contract);
        _dbContext.StampAddedTenantIds(TenantId);
        await _dbContext.SaveChangesAsync();

        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.FreeMonthsBenefits)
            .FirstAsync(c => c.Id == contract.Id);

        Assert.Equal(3, loaded.FreeMonthsBenefits.Count);
        Assert.Equal(new[] { 1, 3, 12 }, loaded.FreeMonthsBenefits.Select(b => b.EntitlementMonths).OrderBy(x => x).ToArray());
    }

    // ─────────────────────────────────────────────────────────────────
    // 3. Production Offer → Contract snapshot flow
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestC06_OfferToContract_SnapshotsFreeMonthsBenefitThroughProductionFlow()
    {
        // Use the REAL production handlers (AcceptOffer + CreateContractFromOffer).
        var sourceRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));

        Guid offerId;
        Guid contractId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

            var offer = NewOffer(planId: 1);
            var offerBenefit = OfferFreeMonthsBenefit.Create(
                id: Guid.NewGuid(),
                offerId: offer.Id,
                entitlementMonths: 1,
                currencyCode: "EGP",
                eligibilityRule: sourceRule).Value;
            offer.AddFreeMonthsBenefit(offerBenefit);

            db.Offers.Add(offer);
            db.StampAddedTenantIds(TenantId);
            await db.SaveChangesAsync();
            offerId = offer.Id;

            var acceptHandler = new AcceptOfferHandler(db, tenant);
            var acceptResult = await acceptHandler.Handle(new AcceptOfferCommand(offer.Id), CancellationToken.None);
            Assert.True(acceptResult.IsSuccess);

            var contractHandler = new CreateContractFromOfferHandler(db, tenant);
            var contractResult = await contractHandler.Handle(
                new CreateContractFromOfferCommand(offer.Id, $"CTR-C-{Guid.NewGuid():N}"[..16]),
                CancellationToken.None);
            Assert.True(contractResult.IsSuccess);
            contractId = contractResult.Value;
        }

        // Re-load in a fresh scope to verify the snapshot.
        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loadedContract = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.FreeMonthsBenefits)
            .FirstAsync(c => c.Id == contractId);

        Assert.Single(loadedContract.FreeMonthsBenefits);
        var loadedBenefit = loadedContract.FreeMonthsBenefits[0];

        // Structural equality after the production flow.
        Assert.Equal(sourceRule, loadedBenefit.EligibilityRule);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(sourceRule),
            EligibilityRuleSerializer.Serialize(loadedBenefit.EligibilityRule));
        Assert.Equal(1, loadedBenefit.EntitlementMonths);

        // Initial state on the Contract-side row is NotEligible + Pending.
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, loadedBenefit.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, loadedBenefit.FulfillmentStatus);
        Assert.Null(loadedBenefit.GrantedAtUtc);
        Assert.Null(loadedBenefit.AppliedAtUtc);
    }

    [Fact]
    public void TestC07_OfferFreeMonthsBenefit_Create_RejectsNullRule_CannotSilentlyLoseEntitlement()
    {
        // Since OfferFreeMonthsBenefit.Create() now requires an EligibilityRule,
        // it is impossible to construct a null-rule benefit through the domain API.
        // This is the primary defence: no incomplete benefit can enter the system.
        // The handler additionally fails explicitly rather than skipping; the schema
        // enforces NOT NULL (verified by SqlC08 on Local SQL Server).
        var result = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: Guid.NewGuid(),
            entitlementMonths: 2,
            currencyCode: "EGP",
            eligibilityRule: null!);

        Assert.False(result.IsSuccess);
        Assert.Equal("OfferFreeMonthsBenefit.EligibilityRule_Required", result.Errors!.First().Code);
    }

    [Fact]
    public async Task TestC10_OfferToContract_WithNullRuleRow_FailsExplicitly_AndCreatesNoContract()
    {
        // Simulates a pre-invariant (legacy/corrupt) snapshot row that somehow carries
        // no EligibilityRule. The domain factory and the NOT NULL schema prevent this,
        // but the production handler must still fail explicitly instead of silently
        // dropping the commercial entitlement.
        var sourceRule = DefaultUpfrontBonusRule();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        var offer = NewOffer(planId: 1);
        var offerBenefit = OfferFreeMonthsBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            entitlementMonths: 1,
            currencyCode: "EGP",
            eligibilityRule: sourceRule).Value;
        offer.AddFreeMonthsBenefit(offerBenefit);

        db.Offers.Add(offer);
        db.StampAddedTenantIds(TenantId);
        await db.SaveChangesAsync();

        var acceptHandler = new AcceptOfferHandler(db, tenant);
        Assert.True((await acceptHandler.Handle(
            new AcceptOfferCommand(offer.Id), CancellationToken.None)).IsSuccess);

        // Force the invariant violation AFTER persistence, on the tracked entity that
        // the handler will resolve. EF (both InMemory and SQL Server) rejects a null
        // rule at write time, so this is the only way to hand the production handler
        // a row that a pre-invariant database could still contain.
        typeof(OfferFreeMonthsBenefit)
            .GetProperty(nameof(OfferFreeMonthsBenefit.EligibilityRule))!
            .SetValue(offerBenefit, null);

        var contractHandler = new CreateContractFromOfferHandler(db, tenant);
        var contractNumber = $"CTR-C-{Guid.NewGuid():N}"[..16];
        var contractResult = await contractHandler.Handle(
            new CreateContractFromOfferCommand(offer.Id, contractNumber),
            CancellationToken.None);

        Assert.False(contractResult.IsSuccess);
        Assert.Equal("Offer.IncompleteFreeMonthsBenefit", contractResult.Errors!.First().Code);

        // The commercial entitlement was NOT silently dropped: the conversion aborted
        // and no Contract row was persisted.
        Assert.False(await db.Contracts.IgnoreQueryFilters()
            .AnyAsync(c => c.ContractNumber == contractNumber));
    }

    // ─────────────────────────────────────────────────────────────────
    // 4. ContractedAmount is independent of FreeMonthsBenefits.EntitlementMonths
    //    (design invariant 32)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestC08_ContractedAmount_IsIndependentOfFreeMonthsBenefitEntitlementMonths()
    {
        // Two contracts identical except for FreeMonthsBenefit rows: their
        // ContractedAmount must be equal (no derivation from entitlement).
        decimal baseContracted = 12000m;

        var c1 = NewContract(planId: 1);
        var c2 = NewContract(planId: 1);

        c1.AddFreeMonthsBenefit(FreeMonthsBenefit.Create(
            Guid.NewGuid(), c1.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value);
        c2.AddFreeMonthsBenefit(FreeMonthsBenefit.Create(
            Guid.NewGuid(), c2.Id, 12, "EGP", DefaultUpfrontBonusRule()).Value);

        _dbContext.Contracts.Add(c1);
        _dbContext.Contracts.Add(c2);
        _dbContext.StampAddedTenantIds(TenantId);
        await _dbContext.SaveChangesAsync();

        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded1 = await db2.Contracts.IgnoreQueryFilters().FirstAsync(c => c.Id == c1.Id);
        var loaded2 = await db2.Contracts.IgnoreQueryFilters().FirstAsync(c => c.Id == c2.Id);

        Assert.Equal(baseContracted, loaded1.ContractedAmount);
        Assert.Equal(baseContracted, loaded2.ContractedAmount);
        // Bonus months is the legacy scalar — also independent (zero on both).
        Assert.Equal(0, loaded1.BonusMonths);
        Assert.Equal(0, loaded2.BonusMonths);
    }

    // ─────────────────────────────────────────────────────────────────
    // 5. EF Core: state transitions survive round-trip
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestC09_EfCore_StateTransitions_SurviveRoundTrip()
    {
        var contract = NewContract(planId: 1);
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(t1);
        benefit.Grant(t2);
        benefit.MarkAppliedToSubscription(t3);
        contract.AddFreeMonthsBenefit(benefit);

        _dbContext.Contracts.Add(contract);
        _dbContext.StampAddedTenantIds(TenantId);
        await _dbContext.SaveChangesAsync();

        var db2 = _scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.FreeMonthsBenefits)
            .FirstAsync(c => c.Id == contract.Id);

        var loadedBenefit = loaded.FreeMonthsBenefits[0];
        Assert.Equal(FreeMonthsEligibilityStatus.Eligible, loadedBenefit.EligibilityStatus);
        Assert.Equal(t1, loadedBenefit.EligibleAtUtc);
        Assert.Equal(FreeMonthsFulfillmentStatus.AppliedToSubscription, loadedBenefit.FulfillmentStatus);
        Assert.Equal(t2, loadedBenefit.GrantedAtUtc);
        Assert.Equal(t3, loadedBenefit.AppliedAtUtc);
        Assert.True(loadedBenefit.IsAppliedToSubscription);
    }
}

/// <summary>
/// Test factory for Task C. Wraps TestWebApplicationFactory and supplies a
/// FakeCurrentTenant that resolves to <c>tenant-freemonths-c</c>.
/// </summary>
public class TaskCFakeTenantTestFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentTenant));
            if (existing is not null) services.Remove(existing);
            services.AddSingleton<ICurrentTenant>(new TaskCFakeCurrentTenant());
        });
    }
}
