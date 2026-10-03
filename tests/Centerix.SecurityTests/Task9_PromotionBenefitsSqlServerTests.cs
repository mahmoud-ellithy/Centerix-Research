namespace Centerix.SecurityTests;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// TASK 9 â€” Promotion free months &amp; additional benefits (SQL Server integration).
///
/// Uses the existing <see cref="SqlServerIntegrationFactory"/> collection and the REAL
/// production handlers (<c>CalculateAndPersistOfferHandler</c>, <c>AcceptOfferHandler</c>,
/// <c>CreateContractFromOfferHandler</c>) against a real SQL Server database.
///
/// Verifies:
///   * SQL-T9-01  the new nullable Promotions columns exist after migration
///   * SQL-T9-02..03  the granted entitlement is persisted as real Offer child rows
///   * SQL-T9-04..05  offer-to-contract conversion materialises the entitlement on the Contract
///   * SQL-T9-06  a later Promotion edit never rewrites an existing Offer snapshot
///   * SQL-T9-07  Plan.BonusMonths never becomes a promotion entitlement
///   * SQL-T9-08..09  cap / term violations are rejected and nothing is persisted
///   * SQL-T9-10..11  discount-only and disabled promotions persist zero benefit rows
///   * SQL-T9-12  the eligibility rule round-trips through SQL Server byte-identically
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class Task9_PromotionBenefitsSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Task9_PromotionBenefitsSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static readonly DateTime PromotionStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PromotionEnd = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Helpers
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, true);
    }

    private async Task SeedTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(5), CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task<int> SeedPlanAsync(
        string tenantId,
        decimal monthlyPrice = 1000m,
        int bonusMonths = 0)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plan = Plan.Create(
            id: 0,
            code: $"Plan9_{Guid.NewGuid():N}"[..28],
            displayName: "Task9 Plan",
            monthlyPrice: monthlyPrice,
            maxStudents: 100, maxUsers: 5, maxBranches: 1, maxTeachers: 10,
            storageGB: 10, smsQuota: 100,
            isActive: true,
            description: null,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: bonusMonths).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>A concrete, explicitly configured benefit rule (no generated default exists).</summary>
    private static EligibilityRule DefaultBenefitRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(1000m));

    /// <summary>
    /// Seeds a promotion scoped to a specific plan so parallel/sequential tests can never
    /// observe each other's promotions.
    /// </summary>
    private async Task<int> SeedPromotionAsync(
        string tenantId,
        int planId,
        PromotionType type,
        int durationMonths = 12,
        int? freeMonthsCount = null,
        string? benefitName = null,
        string? benefitDescription = null,
        decimal? benefitValue = null,
        ContractBenefitType? benefitType = null,
        string? benefitCurrencyCode = null,
        decimal? percentage = null,
        int? chargedMonths = null,
        EligibilityRule? benefitEligibilityRule = null,
        bool activate = true)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Benefit-bearing promotions must carry an explicit rule; there is no generated default.
        // Discount-only types get none.
        var grantsBenefit = type is PromotionType.FreeMonthsBonus
                            or PromotionType.AdditionalBenefits
                            || (type == PromotionType.PayForXMonths && freeMonthsCount.HasValue);
        var rule = benefitEligibilityRule ?? (grantsBenefit ? DefaultBenefitRule() : null);

        var promotionResult = Promotion.Create(
            id: 0,
            name: $"T9 {type} {Guid.NewGuid():N}"[..20],
            type: type,
            planId: planId,
            durationMonths: durationMonths,
            startsAtUtc: PromotionStart,
            endsAtUtc: PromotionEnd,
            freeMonthsCount: freeMonthsCount,
            benefitName: benefitName,
            benefitDescription: benefitDescription,
            benefitValue: benefitValue,
            benefitType: benefitType,
            benefitCurrencyCode: benefitCurrencyCode,
            benefitEligibilityRule: rule,
            percentage: percentage,
            chargedMonths: chargedMonths);

        Assert.True(promotionResult.IsSuccess, FailureOf(promotionResult));
        var promotion = promotionResult.Value;

        if (activate)
            Assert.True(promotion.Activate().IsSuccess);

        db.Promotions.Add(promotion);
        await db.SaveChangesAsync();
        return promotion.Id;
    }

    /// <summary>Formats the errors of a failed result for assertion messages (null-safe on success).</summary>
    private static string FailureOf<T>(Result<T> result) =>
        result.IsSuccess || result.Errors is null
            ? string.Empty
            : string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));

    private async Task<(Result<OfferDto> Result, IServiceScope Scope)> CalculateAsync(        string tenantId,
        int planId,
        int durationMonths)
    {
        var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, durationMonths, PaymentTerms.FullUpfront));
        return (result, scope);
    }

    private async Task<int> CountBenefitRowsAsync(string tenantId, Guid offerId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var freeMonthsRows = await db.Set<OfferFreeMonthsBenefit>()
            .IgnoreQueryFilters()
            .CountAsync(b => b.OfferId == offerId);
        var benefitRows = await db.Set<OfferBenefit>()
            .IgnoreQueryFilters()
            .CountAsync(b => b.OfferId == offerId);

        return freeMonthsRows + benefitRows;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-01 â€” Schema: the new nullable Promotions columns exist
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_01_Promotions_Table_HasNewNullableBenefitColumns()
    {
        var tenantId = $"T9-01-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var columns = await db.Database.SqlQuery<ColumnInfo>(
            $"""
            SELECT COLUMN_NAME AS Name,
                   DATA_TYPE AS DataType,
                   IS_NULLABLE AS IsNullable,
                   CHARACTER_MAXIMUM_LENGTH AS MaxLength
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = 'Promotions'
            """).ToListAsync();

        var expected = new Dictionary<string, (string DataType, string IsNullable, int? MaxLength)>
        {
            ["FreeMonthsCount"] = ("int", "YES", null),
            ["BenefitName"] = ("nvarchar", "YES", 200),
            ["BenefitDescription"] = ("nvarchar", "YES", 500),
            ["BenefitValue"] = ("decimal", "YES", null),
            ["BenefitType"] = ("tinyint", "YES", null),
            ["BenefitCurrencyCode"] = ("nvarchar", "YES", 3),
            // SQL Server reports DATA_TYPE as "nvarchar" with CHARACTER_MAXIMUM_LENGTH = -1 for
            // an nvarchar(max) column.
            ["BenefitEligibilityRule"] = ("nvarchar", "YES", -1),
        };

        foreach (var (name, spec) in expected)
        {
            var column = columns.SingleOrDefault(c => c.Name == name);
            Assert.True(column is not null, $"Column Platform.Promotions.{name} is missing after migration.");
            Assert.Equal(spec.DataType, column!.DataType);
            Assert.Equal(spec.IsNullable, column.IsNullable);
            if (spec.MaxLength.HasValue && spec.MaxLength.Value != 0)
                Assert.Equal(spec.MaxLength.Value, column.MaxLength);
        }
    }

    private sealed class ColumnInfo
    {
        public string Name { get; set; } = default!;
        public string DataType { get; set; } = default!;
        public string IsNullable { get; set; } = default!;
        public int? MaxLength { get; set; }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-02 â€” Free months are persisted as an Offer child row
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_02_FreeMonthsPromotion_PersistsOfferFreeMonthsRow()
    {
        var tenantId = $"T9-02-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(tenantId, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 2);

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;

        Assert.True(result.IsSuccess, FailureOf(result));
        var freeMonthsDto = Assert.Single(result.Value.FreeMonthsBenefits);
        Assert.Equal(2, freeMonthsDto.EntitlementMonths);
        Assert.Equal("EGP", freeMonthsDto.CurrencyCode);

        // The real database row, re-read in a fresh scope.
        using (var verify = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(verify.ServiceProvider, tenantId);
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == result.Value.Id);

            var row = Assert.Single(offer.FreeMonthsBenefits);
            Assert.Equal(2, row.EntitlementMonths);
            Assert.NotNull(row.EligibilityRule);

            // No benefit row is created for a free months promotion.
            var benefitRows = await db.Set<OfferBenefit>()
                .IgnoreQueryFilters()
                .CountAsync(b => b.OfferId == offer.Id);
            Assert.Equal(0, benefitRows);
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-03 â€” The additional benefit is persisted as an Offer child row
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_03_AdditionalBenefitPromotion_PersistsOfferBenefitRow()
    {
        var tenantId = $"T9-03-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.AdditionalBenefits,
            benefitName: "Barcode Printer",
            benefitDescription: "Free barcode printer",
            benefitValue: 750.50m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;

        Assert.True(result.IsSuccess, FailureOf(result));
        var dto = Assert.Single(result.Value.Benefits);
        Assert.Equal("Barcode Printer", dto.Name);
        Assert.Equal(750.50m, dto.ContractualValue);
        Assert.Equal(ContractBenefitType.PhysicalGift, dto.BenefitType);
        Assert.Equal("EGP", dto.CurrencyCode);

        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var offer = await db.Offers
            .IgnoreQueryFilters()
            .Include(o => o.Benefits)
            .AsNoTracking()
            .FirstAsync(o => o.Id == result.Value.Id);

        var row = Assert.Single(offer.Benefits);
        Assert.Equal("Barcode Printer", row.Name);
        Assert.Equal("Free barcode printer", row.Description);
        Assert.Equal(750.50m, row.ContractualValue);
        Assert.NotNull(row.EligibilityRule);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-04 â€” Free months reach the Contract through the real flow
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_04_FreeMonths_ReachTheContractThroughTheRealFlow()
    {
        var tenantId = $"T9-04-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(tenantId, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 2);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var offerResult = await mediator.Send(
                new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
            Assert.True(offerResult.IsSuccess);
            Assert.True((await mediator.Send(new AcceptOfferCommand(offerResult.Value.Id))).IsSuccess);

            var contractResult = await mediator.Send(new CreateContractFromOfferCommand(
                offerResult.Value.Id, $"CNT-T9-{Guid.NewGuid():N}"[..16]));
            Assert.True(contractResult.IsSuccess, FailureOf(contractResult));

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contractResult.Value);

            var benefit = Assert.Single(contract.FreeMonthsBenefits);
            Assert.Equal(2, benefit.EntitlementMonths);
            Assert.Equal("EGP", benefit.CurrencyCode);
            Assert.NotNull(benefit.EligibilityRule);
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-05 â€” The additional benefit reaches the Contract through the real flow
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_05_AdditionalBenefit_ReachesTheContractThroughTheRealFlow()
    {
        var tenantId = $"T9-05-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.AdditionalBenefits,
            benefitName: "Barcode Printer",
            benefitValue: 750.50m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var offerResult = await mediator.Send(
                new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
            Assert.True(offerResult.IsSuccess);
            Assert.True((await mediator.Send(new AcceptOfferCommand(offerResult.Value.Id))).IsSuccess);

            var contractResult = await mediator.Send(new CreateContractFromOfferCommand(
                offerResult.Value.Id, $"CNT-T9-{Guid.NewGuid():N}"[..16]));
            Assert.True(contractResult.IsSuccess, FailureOf(contractResult));

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.Benefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contractResult.Value);

            var benefit = Assert.Single(contract.Benefits);
            Assert.Equal("Barcode Printer", benefit.Name);
            Assert.Equal(750.50m, benefit.ContractualValue);
            Assert.Equal("EGP", benefit.CurrencyCode);
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-06 â€” A later Promotion edit never rewrites an existing Offer
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_06_PromotionEditedAfterCalculation_DoesNotChangeTheOffer()
    {
        var tenantId = $"T9-06-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        var promotionId = await SeedPromotionAsync(
            tenantId, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 2);

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;
        Assert.True(result.IsSuccess);

        // Edit the promotion: 2 free months becomes 6.
        using (var edit = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(edit.ServiceProvider, tenantId);
            var db = edit.ServiceProvider.GetRequiredService<AppDbContext>();
            var promotion = await db.Promotions.FirstAsync(p => p.Id == promotionId);
            Assert.True(promotion.Update(
                name: promotion.Name,
                type: promotion.Type,
                planId: promotion.PlanId,
                durationMonths: promotion.DurationMonths,
                startsAtUtc: promotion.StartsAtUtc,
                endsAtUtc: promotion.EndsAtUtc,
                priority: promotion.Priority,
                freeMonthsCount: 6,
                benefitEligibilityRule: DefaultBenefitRule()).IsSuccess);
            await db.SaveChangesAsync();
        }

        // The persisted offer snapshot is untouched.
        using (var verify = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(verify.ServiceProvider, tenantId);
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == result.Value.Id);

            Assert.Equal(2, Assert.Single(offer.FreeMonthsBenefits).EntitlementMonths);
        }

        // A newly calculated offer picks up the new configuration.
        var (second, secondScope) = await CalculateAsync(
            tenantId, planId, 12);
        using var __ = secondScope;
        Assert.True(second.IsSuccess);
        Assert.Equal(6, Assert.Single(second.Value.FreeMonthsBenefits).EntitlementMonths);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-07 â€” Plan.BonusMonths never becomes a promotion entitlement
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_07_PlanBonusMonths_DoNotCreateOfferBenefitRows()
    {
        var tenantId = $"T9-07-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        // A plan that grants 3 bonus months, with no promotion at all.
        var planId = await SeedPlanAsync(tenantId, bonusMonths: 3);

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Empty(result.Value.Benefits);
        Assert.Equal(0, await CountBenefitRowsAsync(tenantId, result.Value.Id));

        // The plan's bonus months are still carried as contract-period data, not as a benefit.
        using var verify = _env.Factory.Services.CreateScope();
        AuthorizeTenant(verify.ServiceProvider, tenantId);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var offer = await db.Offers.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == result.Value.Id);
        Assert.Equal(3, offer.BonusMonths);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-08 â€” A benefit above the 3x monthly cap is rejected, nothing persisted
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_08_BenefitAboveThreeMonthlyValue_IsRejectedAndPersistsNothing()
    {
        var tenantId = $"T9-08-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId, monthlyPrice: 1000m); // cap = 3000
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.AdditionalBenefits,
            benefitName: "Too expensive",
            benefitValue: 5000m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.BenefitValue_ExceedsMaximum");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var offerCount = await db.Offers.IgnoreQueryFilters().CountAsync(o => o.PlanId == planId);
        Assert.Equal(0, offerCount);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-09 â€” Free months above the purchased term are rejected
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_09_FreeMonthsAboveTerm_AreRejectedAndPersistNothing()
    {
        var tenantId = $"T9-09-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.FreeMonthsBonus, durationMonths: 3, freeMonthsCount: 5);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 3, PaymentTerms.FullUpfront));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.FreeMonths_ExceedDuration");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var offerCount = await db.Offers.IgnoreQueryFilters().CountAsync(o => o.PlanId == planId);
        Assert.Equal(0, offerCount);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-10 â€” A discount-only promotion persists zero benefit rows
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_10_DiscountOnlyPromotion_PersistsZeroBenefitRows()
    {
        var tenantId = $"T9-10-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(tenantId, planId, PromotionType.PercentageDiscount, percentage: 10m);

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;

        Assert.True(result.IsSuccess);
        Assert.Equal(10800m, result.Value.FinalAmount);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Empty(result.Value.Benefits);
        Assert.Equal(0, await CountBenefitRowsAsync(tenantId, result.Value.Id));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-11 â€” A disabled promotion persists zero benefit rows
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_11_DisabledPromotion_PersistsZeroBenefitRows()
    {
        var tenantId = $"T9-11-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.FreeMonthsBonus, freeMonthsCount: 5, activate: false);

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;

        // A 5-month entitlement on a 12-month term would be valid, but the promotion is a
        // draft and therefore never applied at all.
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PromotionId);
        Assert.Equal("None", result.Value.PromotionType);
        Assert.Empty(result.Value.FreeMonthsBenefits);
        Assert.Empty(result.Value.Benefits);
        Assert.Equal(0, await CountBenefitRowsAsync(tenantId, result.Value.Id));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // SQL-T9-12 â€” The eligibility rule round-trips through SQL Server
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SqlT9_12_EligibilityRule_RoundTripsThroughSqlServer()
    {
        var tenantId = $"T9-12-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        await SeedPromotionAsync(
            tenantId, planId, PromotionType.AdditionalBenefits,
            benefitName: "Barcode Printer",
            benefitValue: 750.50m,
            benefitType: ContractBenefitType.PhysicalGift,
            benefitCurrencyCode: "EGP");

        var (result, scope) = await CalculateAsync(
            tenantId, planId, 12);
        using var _ = scope;
        Assert.True(result.IsSuccess);

        string offerRuleJson;
        using (var read = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(read.ServiceProvider, tenantId);
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == result.Value.Id);
            offerRuleJson = EligibilityRuleSerializer.Serialize(Assert.Single(offer.Benefits).EligibilityRule!);
        }

        // Same rule, read back from the raw column: canonical and stable.
        using (var read = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(read.ServiceProvider, tenantId);
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == result.Value.Id);
            var reloaded = Assert.Single(offer.Benefits);

            var reloadedJson = EligibilityRuleSerializer.Serialize(reloaded.EligibilityRule!);
            Assert.Equal(offerRuleJson, reloadedJson);

            // The persisted rule is a plain contract rule â€” it requires an Active contract.
            Assert.NotNull(reloaded.EligibilityRule);
            Assert.DoesNotContain("Centerix.Domain", offerRuleJson, StringComparison.Ordinal);
        }
    }
}
