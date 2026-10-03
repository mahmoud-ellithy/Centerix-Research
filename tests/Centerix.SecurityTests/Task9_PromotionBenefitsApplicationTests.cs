namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// TASK 9 â€” Promotion free months &amp; additional benefits (Application layer).
///
/// Exercises the REAL production handlers through MediatR against an InMemory database
/// (see <see cref="TaskCFakeTenantTestFactory"/>):
/// <c>CalculateAndPersistOfferHandler</c>, <c>AcceptOfferHandler</c> and
/// <c>CreateContractFromOfferHandler</c>.
///
/// Verifies:
///   * T9-A01..A03  the calculated Offer snapshots the granted entitlement and its rule
///   * T9-A04..A05  offer-to-contract conversion materialises the entitlement on the Contract
///   * T9-A06..A07  Plan.BonusMonths and later Promotion edits never alter an existing Offer
///   * T9-A08..A10  zero-benefit cases stay zero, validation rejects bad config, DTO exposes it
/// </summary>
public class Task9_PromotionBenefitsApplicationTests : IClassFixture<TaskCFakeTenantTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly TaskCFakeTenantTestFactory _factory;

    public Task9_PromotionBenefitsApplicationTests(TaskCFakeTenantTestFactory factory) => _factory = factory;

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<(IAppDbContext Db, IMediator Mediator, IServiceScope Scope)> NewScopeAsync()
    {
        var scope = _factory.Services.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<IAppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IMediator>(),
            scope);
    }

    private static async Task<int> SeedPlanAsync(
        IAppDbContext db,
        decimal monthlyPrice = 1000m,
        int bonusMonths = 0,
        string currency = "EGP")
    {
        var plan = Plan.Create(
            id: 0,
            code: $"T9-{Guid.NewGuid():N}"[..12],
            displayName: "T9 plan",
            monthlyPrice: monthlyPrice,
            maxStudents: 100,
            maxUsers: 50,
            maxBranches: 10,
            maxTeachers: 20,
            storageGB: 100,
            smsQuota: 1000,
            isActive: true,
            currencyCode: currency,
            durationMonths: 12,
            bonusMonths: bonusMonths).Value;

        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private static async Task<int> SeedPromotionAsync(
        IAppDbContext db,
        int planId,
        PromotionType type,
        int durationMonths = 12,
        int? freeMonthsCount = null,
        string? benefitName = null,
        decimal? benefitValue = null,
        ContractBenefitType? benefitType = null,
        string? benefitCurrencyCode = null,
        decimal? percentage = null,
        int? chargedMonths = null,
        bool activate = true)
    {
        var promotion = Promotion.Create(
            id: 0,
            name: $"T9 promo {Guid.NewGuid():N}"[..16],
            type: type,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: Start,
            endsAtUtc: End,
            freeMonthsCount: freeMonthsCount,
            benefitName: benefitName,
            benefitValue: benefitValue,
            benefitType: benefitType,
            benefitCurrencyCode: benefitCurrencyCode,
            percentage: percentage,
            chargedMonths: chargedMonths).Value;

        if (activate)
            Assert.True(promotion.Activate().IsSuccess);

        db.Promotions.Add(promotion);
        await db.SaveChangesAsync();
        return promotion.Id;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A01 â€” Free months promotion produces an Offer free months snapshot
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A01_FreeMonthsPromotion_ProducesOfferFreeMonthsSnapshot()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 2);

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.True(result.IsSuccess);
        var dto = result.Value;

        // The amount is untouched.
        Assert.Equal(12000m, dto.BaseAmount);
        Assert.Equal(0m, dto.DiscountAmount);
        Assert.Equal(12000m, dto.FinalAmount);

        // The entitlement is snapshotted onto the Offer.
        var freeMonths = Assert.Single(dto.FreeMonthsBenefits);
        Assert.Equal(2, freeMonths.EntitlementMonths);
        Assert.Equal("EGP", freeMonths.CurrencyCode);

        // And it survives persistence with its rule intact.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var offer = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.FreeMonthsBenefits)
            .FirstAsync(o => o.Id == dto.Id);

        var persisted = Assert.Single(offer.FreeMonthsBenefits);
        Assert.Equal(2, persisted.EntitlementMonths);
        Assert.NotNull(persisted.EligibilityRule);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A02 â€” Additional benefits promotion produces an Offer benefit snapshot
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A02_AdditionalBenefitsPromotion_ProducesOfferBenefitSnapshot()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.AdditionalBenefits,
            benefitName: "Barcode Printer",
            benefitValue: 750.50m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.True(result.IsSuccess);
        var benefit = Assert.Single(result.Value.Benefits);
        Assert.Equal("Barcode Printer", benefit.Name);
        Assert.Equal(750.50m, benefit.ContractualValue);
        Assert.Equal(ContractBenefitType.PhysicalGift, benefit.BenefitType);
        Assert.Equal("EGP", benefit.CurrencyCode);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var offer = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.Benefits)
            .FirstAsync(o => o.Id == result.Value.Id);

        var persisted = Assert.Single(offer.Benefits);
        Assert.Equal("Barcode Printer", persisted.Name);
        Assert.Equal(750.50m, persisted.ContractualValue);
        Assert.NotNull(persisted.EligibilityRule);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A03 â€” The Offer carries the rule; the Plan is never consulted for it
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A03_OfferCarriesTheRule_SurvivesWithoutReReadingThePromotion()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var promotionId = await SeedPromotionAsync(
            db, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 3);

        var offerResult = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offerResult.IsSuccess);

        // The operator edits the promotion after the offer was calculated.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var promotion = await db2.Promotions.FirstAsync(p => p.Id == promotionId);
        Assert.True(promotion.Update(
            name: promotion.Name,
            type: promotion.Type,
            planId: promotion.PlanId,
            durationMonths: promotion.DurationMonths,
            startsAtUtc: promotion.StartsAtUtc,
            endsAtUtc: promotion.EndsAtUtc,
            priority: promotion.Priority,
            freeMonthsCount: 11).IsSuccess);
        await db2.SaveChangesAsync();

        // The existing Offer snapshot is unchanged â€” the offer is authoritative.
        var db3 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var offer = await db3.Offers
            .IgnoreQueryFilters()
            .Include(o => o.FreeMonthsBenefits)
            .FirstAsync(o => o.Id == offerResult.Value.Id);

        Assert.Equal(3, Assert.Single(offer.FreeMonthsBenefits).EntitlementMonths);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A04 â€” Free months reach the Contract on conversion
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A04_FreeMonthsPromotion_ReachesTheContractOnConversion()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 2);

        var offerResult = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offerResult.IsSuccess);

        var accept = await mediator.Send(new AcceptOfferCommand(offerResult.Value.Id));
        Assert.True(accept.IsSuccess);

        var contractResult = await mediator.Send(new CreateContractFromOfferCommand(
            offerResult.Value.Id,
            $"CNT-T9-{Guid.NewGuid():N}"[..16]));
        Assert.True(contractResult.IsSuccess);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var contract = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.FreeMonthsBenefits)
            .FirstAsync(c => c.Id == contractResult.Value);

        var benefit = Assert.Single(contract.FreeMonthsBenefits);
        Assert.Equal(2, benefit.EntitlementMonths);
        Assert.NotNull(benefit.EligibilityRule);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A05 â€” The additional benefit reaches the Contract on conversion
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A05_AdditionalBenefit_ReachesTheContractOnConversion()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.AdditionalBenefits,
            benefitName: "Barcode Printer",
            benefitValue: 750.50m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        var offerResult = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offerResult.IsSuccess);
        Assert.True((await mediator.Send(new AcceptOfferCommand(offerResult.Value.Id))).IsSuccess);

        var contractResult = await mediator.Send(new CreateContractFromOfferCommand(
            offerResult.Value.Id,
            $"CNT-T9-{Guid.NewGuid():N}"[..16]));
        Assert.True(contractResult.IsSuccess);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var contract = await db2.Contracts
            .IgnoreQueryFilters()
            .Include(c => c.Benefits)
            .FirstAsync(c => c.Id == contractResult.Value);

        var benefit = Assert.Single(contract.Benefits);
        Assert.Equal("Barcode Printer", benefit.Name);
        Assert.Equal(750.50m, benefit.ContractualValue);
        Assert.Equal("EGP", benefit.CurrencyCode);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A06 â€” Plan.BonusMonths never becomes a promotion entitlement
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A06_PlanBonusMonths_DoNotAppearAsOfferFreeMonths()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        // A plan that grants 3 bonus months, with NO promotion configured at all.
        var planId = await SeedPlanAsync(db, bonusMonths: 3);

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Empty(result.Value.Benefits);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A07 â€” A benefit above the 3x monthly cap is rejected, not persisted
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A07_BenefitAboveThreeMonthlyValue_IsRejectedWithValidationError()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db, monthlyPrice: 1000m); // cap = 3000
        await SeedPromotionAsync(
            db, planId, PromotionType.AdditionalBenefits,
            benefitName: "Too expensive",
            benefitValue: 5000m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.BenefitValue_ExceedsMaximum");
        Assert.Equal(ErrorKind.Validation, result.Errors!.First().Type);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A08 â€” Free months above the purchased term are rejected
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A08_FreeMonthsAboveTerm_AreRejected()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.FreeMonthsBonus, durationMonths: 3, freeMonthsCount: 5);

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 3, PaymentTerms.FullUpfront));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.FreeMonths_ExceedDuration");
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A09 â€” A discount-only promotion produces an Offer with zero benefits
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A09_DiscountOnlyPromotion_ProducesZeroBenefits()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.PercentageDiscount, percentage: 10m);

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.True(result.IsSuccess);
        Assert.Equal(10800m, result.Value.FinalAmount);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Empty(result.Value.Benefits);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // T9-A10 â€” A disabled promotion grants nothing
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task T9_A10_DisabledPromotion_GrantsNothing()
    {
        var (db, mediator, scope) = await NewScopeAsync();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        await SeedPromotionAsync(
            db, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 5, activate: false);

        var result = await mediator.Send(new CalculateAndPersistOfferCommand(planId, 3, PaymentTerms.FullUpfront));

        // A 5-month entitlement on a 3-month term would be invalid, but the promotion is a
        // draft and therefore never applied â€” the offer is valid and carries nothing.
        Assert.True(result.IsSuccess);
        Assert.Equal(3000m, result.Value.FinalAmount);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Null(result.Value.PromotionId);
    }
}
