namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Application.Platform.Promotions.Queries;
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
using NSubstitute;
using Xunit;

/// <summary>
/// TASK 9 CORRECTION (SQL Server) — the benefit eligibility rule is configured, persisted
/// Promotion data that survives a full database round trip.
/// <para>
/// SQL-T9-C01  the additive rule column exists and is nullable after migration
/// SQL-T9-C02  a configured rule persists and reloads byte-identically
/// SQL-T9-C03  a malformed rule payload is rejected and nothing is persisted
/// SQL-T9-C04  a benefit-bearing promotion cannot be created without a rule
/// SQL-T9-C05  a rule on a discount-only promotion is rejected
/// SQL-T9-C06  the configured rule reaches the persisted OfferFreeMonthsBenefit row
/// SQL-T9-C07  the configured rule reaches the persisted OfferBenefit row
/// SQL-T9-C08  a later rule change applies to new offers only, never to existing snapshots
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class Task9C_PromotionBenefitRuleSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Task9C_PromotionBenefitRuleSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static readonly DateTime PromotionStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PromotionEnd = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
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

    private async Task<int> SeedPlanAsync(string tenantId, decimal monthlyPrice = 1000m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plan = Plan.Create(
            id: 0, code: $"Plan9C_{Guid.NewGuid():N}"[..28], displayName: "Task9C Plan",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 5, maxBranches: 1,
            maxTeachers: 10, storageGB: 10, smsQuota: 100, isActive: true, description: null,
            currencyCode: "EGP", durationMonths: 12, bonusMonths: 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>A distinctive configured rule the code could not have invented on its own.</summary>
    private static EligibilityRule ConfiguredRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(4321m));

    private static string FailureOf<T>(Result<T> result) =>
        result.IsSuccess || result.Errors is null
            ? string.Empty
            : string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));

    private static CreatePromotionCommand BenefitCreate(int planId, string? ruleJson) =>
        new(
            Name: "T9C benefit promo",
            Type: PromotionType.AdditionalBenefits,
            PlanId: planId,
            DurationMonths: 12,
            StartsAtUtc: PromotionStart,
            EndsAtUtc: PromotionEnd,
            BenefitName: "Barcode Printer",
            BenefitDescription: "Free barcode printer",
            BenefitValue: 500m,
            BenefitType: ContractBenefitType.PhysicalGift,
            BenefitCurrencyCode: "EGP",
            BenefitEligibilityRule: ruleJson);

    /// <summary>
    /// The REAL <see cref="CreatePromotionHandler"/> against the REAL SQL Server context, with only
    /// the platform authorization boundary and the audit sink substituted. This is the established
    /// pattern in this suite's SQL tests: the production handler code and the real database are what
    /// is under test, so a failure cannot be explained away by the harness.
    /// </summary>
    private static CreatePromotionHandler CreateHandler(IAppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var audit = Substitute.For<IAuditWriter>();
        return new CreatePromotionHandler(db, guard, audit);
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C01 — The additive rule column exists and is nullable
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C01_Promotions_Table_HasNullableBenefitEligibilityRuleColumn()
    {
        var tenantId = $"T9C-01-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = (await db.Database.SqlQuery<RuleColumnInfo>(
            $"""
            SELECT COLUMN_NAME AS Name,
                   DATA_TYPE AS DataType,
                   IS_NULLABLE AS IsNullable,
                   CHARACTER_MAXIMUM_LENGTH AS MaxLength
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = 'Promotions'
              AND COLUMN_NAME = 'BenefitEligibilityRule'
            """).ToListAsync()).SingleOrDefault();

        Assert.True(column is not null, "Platform.Promotions.BenefitEligibilityRule is missing after migration.");
        Assert.Equal("nvarchar", column!.DataType);
        Assert.Equal("YES", column.IsNullable);
        // SQL Server reports CHARACTER_MAXIMUM_LENGTH = -1 for nvarchar(max).
        Assert.Equal(-1, column.MaxLength);
    }

    private sealed class RuleColumnInfo
    {
        public string Name { get; set; } = default!;
        public string DataType { get; set; } = default!;
        public string IsNullable { get; set; } = default!;
        public int? MaxLength { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C02 — A configured rule persists and reloads byte-identically
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C02_ConfiguredRule_PersistsAndReloadsUnchanged()
    {
        var tenantId = $"T9C-02-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);

        var expected = EligibilityRuleSerializer.Serialize(ConfiguredRule());

        using (var write = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(write.ServiceProvider, tenantId);
            var db = write.ServiceProvider.GetRequiredService<AppDbContext>();
            var created = Promotion.Create(
                id: 0, name: "T9C benefit", type: PromotionType.AdditionalBenefits, planId: planId,
                durationMonths: 12, startsAtUtc: PromotionStart, endsAtUtc: PromotionEnd,
                benefitName: "Barcode Printer", benefitValue: 500m,
                benefitType: ContractBenefitType.PhysicalGift, benefitCurrencyCode: "EGP",
                benefitEligibilityRule: ConfiguredRule()).Value;
            db.Promotions.Add(created);
            await db.SaveChangesAsync();
        }

        // Read back through a brand new context.
        using (var read = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(read.ServiceProvider, tenantId);
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Promotions.AsNoTracking()
                .FirstAsync(p => p.PlanId == planId && p.Name == "T9C benefit");

            Assert.NotNull(stored.BenefitEligibilityRule);
            Assert.Equal(expected, EligibilityRuleSerializer.Serialize(stored.BenefitEligibilityRule!));
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C03 — A malformed rule payload is rejected and never persisted
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"kind\":\"TotallyUnknownRule\",\"value\":1}")]
    [InlineData("{\"kind\":\"AmountPaidAtLeastRule\",\"value\":-5}")]
    [InlineData("{\"kind\":\"PaymentTermsEqualsRule\",\"terms\":999}")]
    public async Task SqlT9_C03_MalformedRulePayload_IsRejectedAndNeverPersisted(string payload)
    {
        var tenantId = $"T9C-03-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = CreateHandler(db);

        var result = await handler.Handle(BenefitCreate(planId, payload), CancellationToken.None);
        Assert.False(result.IsSuccess, FailureOf(result));
        Assert.Contains(result.Errors!, e =>
            e.Code is "Promotion.BenefitEligibilityRule_Invalid"
                or "Promotion.BenefitEligibilityRule_Required");

        var count = await db.Promotions.IgnoreQueryFilters().CountAsync(p => p.PlanId == planId);
        Assert.Equal(0, count);
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C04 — A benefit-bearing promotion requires a rule
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C04_BenefitPromotionWithoutRule_IsRejected()
    {
        var tenantId = $"T9C-04-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = CreateHandler(db);

        var result = await handler.Handle(BenefitCreate(planId, ruleJson: null), CancellationToken.None);
        Assert.False(result.IsSuccess, FailureOf(result));
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Required");

        Assert.Equal(0, await db.Promotions.IgnoreQueryFilters().CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C05 — A rule on a discount-only promotion is rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C05_RuleOnDiscountOnlyPromotion_IsRejected()
    {
        var tenantId = $"T9C-05-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = CreateHandler(db);

        var result = await handler.Handle(new CreatePromotionCommand(
            Name: "T9C discount",
            Type: PromotionType.PercentageDiscount,
            PlanId: planId,
            DurationMonths: 12,
            StartsAtUtc: PromotionStart,
            EndsAtUtc: PromotionEnd,
            Percentage: 10m,
            BenefitEligibilityRule: EligibilityRuleSerializer.Serialize(ConfiguredRule())), CancellationToken.None);

        Assert.False(result.IsSuccess, FailureOf(result));
        Assert.Contains(result.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_NotSupportedForType");

        Assert.Equal(0, await db.Promotions.IgnoreQueryFilters().CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C06 / C07 — The configured rule reaches both Offer child rows
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C06_ConfiguredRule_ReachesTheOfferFreeMonthsRow()
    {
        var tenantId = $"T9C-06-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        var expected = EligibilityRuleSerializer.Serialize(ConfiguredRule());

        Guid offerId;
        using (var setup = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(setup.ServiceProvider, tenantId);
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var promotion = Promotion.Create(
                id: 0, name: "T9C free months", type: PromotionType.FreeMonthsBonus, planId: planId,
                durationMonths: 12, startsAtUtc: PromotionStart, endsAtUtc: PromotionEnd,
                freeMonthsCount: 2, benefitEligibilityRule: ConfiguredRule()).Value;
            Assert.True(promotion.Activate().IsSuccess);
            db.Promotions.Add(promotion);
            await db.SaveChangesAsync();
        }

        using (var calc = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(calc.ServiceProvider, tenantId);
            var mediator = calc.ServiceProvider.GetRequiredService<IMediator>();
            var offer = await mediator.Send(
                new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
            Assert.True(offer.IsSuccess, FailureOf(offer));
            offerId = offer.Value.Id;
        }

        using (var read = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(read.ServiceProvider, tenantId);
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offerId);

            var row = Assert.Single(offer.FreeMonthsBenefits);
            Assert.Equal(2, row.EntitlementMonths);
            Assert.NotNull(row.EligibilityRule);
            Assert.Equal(expected, EligibilityRuleSerializer.Serialize(row.EligibilityRule!));
        }
    }

    [Fact]
    public async Task SqlT9_C07_ConfiguredRule_ReachesTheOfferBenefitRow()
    {
        var tenantId = $"T9C-07-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);
        var expected = EligibilityRuleSerializer.Serialize(ConfiguredRule());

        Guid offerId;
        using (var setup = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(setup.ServiceProvider, tenantId);
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var promotion = Promotion.Create(
                id: 0, name: "T9C benefit", type: PromotionType.AdditionalBenefits, planId: planId,
                durationMonths: 12, startsAtUtc: PromotionStart, endsAtUtc: PromotionEnd,
                benefitName: "Barcode Printer", benefitValue: 500m,
                benefitType: ContractBenefitType.PhysicalGift, benefitCurrencyCode: "EGP",
                benefitEligibilityRule: ConfiguredRule()).Value;
            Assert.True(promotion.Activate().IsSuccess);
            db.Promotions.Add(promotion);
            await db.SaveChangesAsync();
        }

        using (var calc = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(calc.ServiceProvider, tenantId);
            var mediator = calc.ServiceProvider.GetRequiredService<IMediator>();
            var offer = await mediator.Send(
                new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
            Assert.True(offer.IsSuccess, FailureOf(offer));
            offerId = offer.Value.Id;
        }

        using (var read = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(read.ServiceProvider, tenantId);
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var offer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offerId);

            var row = Assert.Single(offer.Benefits);
            Assert.Equal("Barcode Printer", row.Name);
            Assert.NotNull(row.EligibilityRule);
            Assert.Equal(expected, EligibilityRuleSerializer.Serialize(row.EligibilityRule!));
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // SQL-T9-C08 — A later rule change applies to new offers only
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlT9_C08_RuleChange_AppliesToNewOffersOnly()
    {
        var tenantId = $"T9C-08-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await SeedPlanAsync(tenantId);

        var original = EligibilityRuleSerializer.Serialize(ConfiguredRule());
        var replacement = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(999m));
        var replacementJson = EligibilityRuleSerializer.Serialize(replacement);

        int promotionId;
        using (var setup = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(setup.ServiceProvider, tenantId);
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var promotion = Promotion.Create(
                id: 0, name: "T9C mutable", type: PromotionType.FreeMonthsBonus, planId: planId,
                durationMonths: 12, startsAtUtc: PromotionStart, endsAtUtc: PromotionEnd,
                freeMonthsCount: 2, benefitEligibilityRule: ConfiguredRule()).Value;
            Assert.True(promotion.Activate().IsSuccess);
            db.Promotions.Add(promotion);
            await db.SaveChangesAsync();
            promotionId = promotion.Id;
        }

        var firstOfferId = await CalculateAsync(tenantId, planId);
        var firstRule = await ReadFreeMonthsRuleAsync(tenantId, firstOfferId);
        Assert.Equal(original, firstRule);

        // Change the configured rule.
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
                freeMonthsCount: 2,
                benefitEligibilityRule: replacement).IsSuccess);
            await db.SaveChangesAsync();
        }

        // The existing offer snapshot is unchanged.
        Assert.Equal(original, await ReadFreeMonthsRuleAsync(tenantId, firstOfferId));

        // A new offer picks up the new rule.
        var secondOfferId = await CalculateAsync(tenantId, planId);
        Assert.Equal(replacementJson, await ReadFreeMonthsRuleAsync(tenantId, secondOfferId));
    }

    private async Task<Guid> CalculateAsync(string tenantId, int planId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var offer = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offer.IsSuccess, FailureOf(offer));
        return offer.Value.Id;
    }

    private async Task<string> ReadFreeMonthsRuleAsync(string tenantId, Guid offerId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var offer = await db.Offers
            .IgnoreQueryFilters()
            .Include(o => o.FreeMonthsBenefits)
            .AsNoTracking()
            .FirstAsync(o => o.Id == offerId);

        var row = Assert.Single(offer.FreeMonthsBenefits);
        return EligibilityRuleSerializer.Serialize(row.EligibilityRule!);
    }
}
