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
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// TASK 9 CORRECTION (application layer) — the benefit eligibility rule is configured promotion
/// data, exposed and consumed through the real handlers against an InMemory database.
/// <para>
/// T9-C-A01  Create persists the configured rule and the query returns it
/// T9-C-A02  Create without a rule on a benefit-bearing promotion returns a validation error
/// T9-C-A03  Create with a rule on a discount-only promotion returns a validation error
/// T9-C-A04  a malformed/non-canonical rule payload is rejected, never stored
/// T9-C-A05  Update changes the rule for future offers without touching existing offers
/// T9-C-A06  the configured rule reaches the persisted Offer child row unchanged
/// </para>
/// </summary>
public class Task9C_PromotionBenefitRuleApplicationTests : IClassFixture<Task9CPlatformAdminTestFactory>
{
    private const string TenantId = "tenant-freemonths-c";
    private readonly Task9CPlatformAdminTestFactory _factory;

    public Task9C_PromotionBenefitRuleApplicationTests(Task9CPlatformAdminTestFactory factory) => _factory = factory;

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EligibilityRule ConfiguredRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(4321m));

    private static string ConfiguredRuleJson() => EligibilityRuleSerializer.Serialize(ConfiguredRule());

    private (IAppDbContext Db, IMediator Mediator, IServiceScope Scope) NewScope()
    {
        var scope = _factory.Services.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<IAppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IMediator>(),
            scope);
    }

    private static async Task<int> SeedPlanAsync(IAppDbContext db, decimal monthlyPrice = 1000m)
    {
        var plan = Plan.Create(
            id: 0, code: $"T9C-{Guid.NewGuid():N}"[..12], displayName: "T9C plan",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: true,
            currencyCode: "EGP", durationMonths: 12, bonusMonths: 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private static CreatePromotionCommand BenefitCreate(int planId, string? ruleJson) =>
        new(
            Name: "T9C benefit promo",
            Type: PromotionType.AdditionalBenefits,
            PlanId: planId,
            DurationMonths: 12,
            StartsAtUtc: Start,
            EndsAtUtc: End,
            BenefitName: "Barcode Printer",
            BenefitDescription: "Free barcode printer",
            BenefitValue: 500m,
            BenefitType: ContractBenefitType.PhysicalGift,
            BenefitCurrencyCode: "EGP",
            BenefitEligibilityRule: ruleJson);

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A01 — Create persists the configured rule; the query returns it
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A01_Create_PersistsTheConfiguredRule_AndTheQueryReturnsIt()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, ConfiguredRuleJson()));
        Assert.True(created.IsSuccess);

        var read = await mediator.Send(new GetPromotionByIdQuery(created.Value));
        Assert.True(read.IsSuccess);
        Assert.Equal(ConfiguredRuleJson(), read.Value.BenefitEligibilityRule);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var stored = await db2.Promotions.AsNoTracking().FirstAsync(p => p.Id == created.Value);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(ConfiguredRule()),
            EligibilityRuleSerializer.Serialize(stored.BenefitEligibilityRule!));

        var list = await mediator.Send(new ListPromotionsQuery());
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Value, p => p.BenefitEligibilityRule == ConfiguredRuleJson());
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A02 — A benefit-bearing promotion without a rule is rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A02_Create_BenefitPromotionWithoutRule_IsRejected()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, ruleJson: null));

        Assert.False(created.IsSuccess);
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Required");

        // Nothing was persisted.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A03 — A rule on a discount-only promotion is rejected
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A03_Create_RuleOnDiscountOnlyPromotion_IsRejected()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(new CreatePromotionCommand(
            Name: "T9C discount promo",
            Type: PromotionType.PercentageDiscount,
            PlanId: planId,
            DurationMonths: 12,
            StartsAtUtc: Start,
            EndsAtUtc: End,
            Percentage: 10m,
            BenefitEligibilityRule: ConfiguredRuleJson()));

        Assert.False(created.IsSuccess);
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_NotSupportedForType");
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A04a/b/c/d — Canonicality of the accepted rule JSON
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A04a_Create_MalformedJson_IsRejected()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, "not json at all"));

        Assert.False(created.IsSuccess);
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Invalid");

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    [Fact]
    public async Task T9_C_A04b_Create_UnknownDiscriminator_IsRejected()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, "{\"type\":\"unknown_rule\"}"));

        Assert.False(created.IsSuccess);
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Invalid");

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    [Fact]
    public async Task T9_C_A04c_Create_ValidRuleWithUnknownProperty_IsRejected()
    {
        // THE CRITICAL REGRESSION TEST.
        // The serializer happily deserializes this into ContractActiveRule while silently
        // dropping "extra". Accepting it would mean normalizing non-canonical rule JSON.
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(
            BenefitCreate(planId, "{\"type\":\"contract_active\",\"extra\":\"ignored\"}"));

        Assert.False(created.IsSuccess, "A non-canonical rule payload must be rejected.");
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Invalid");

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    [Fact]
    public async Task T9_C_A04d_Create_NonCanonicalCompositeRepresentation_IsRejected()
    {
        // Build a real rule through the domain, take its canonical JSON, and alter the
        // representation without changing the intended semantics.
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(4321m));
        var canonical = EligibilityRuleSerializer.Serialize(rule);

        var planId = await SeedPlanAsync(db);

        // (i) extra whitespace — semantically identical, not the canonical string
        var spaced = canonical.Replace(":", " : ");
        var spacedResult = await mediator.Send(BenefitCreate(planId, spaced));
        Assert.False(spacedResult.IsSuccess, "A non-canonical composite payload must be rejected.");

        // (ii) discriminator not first — same semantics, different property order
        var reordered = "{\"rules\":[{\"type\":\"contract_active\"},"
            + "{\"type\":\"amount_paid_at_least\",\"amount\":4321}],\"type\":\"all_of\"}";
        var reorderedResult = await mediator.Send(BenefitCreate(planId, reordered));
        Assert.False(reorderedResult.IsSuccess, "A reordered composite payload must be rejected.");

        // (iii) nested child carrying an unknown property
        var childWithExtra = "{\"type\":\"all_of\",\"rules\":"
            + "[{\"type\":\"contract_active\",\"extra\":\"ignored\"},"
            + "{\"type\":\"amount_paid_at_least\",\"amount\":4321}]}";
        var childResult = await mediator.Send(BenefitCreate(planId, childWithExtra));
        Assert.False(childResult.IsSuccess, "A nested non-canonical child must be rejected.");

        // The canonical form of that very same rule is still accepted.
        var canonicalResult = await mediator.Send(BenefitCreate(planId, canonical));
        Assert.True(canonicalResult.IsSuccess);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(1, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A04e — Canonical JSON is accepted end to end
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A04e_Create_CanonicalJson_IsAccepted_AndStaysCanonical()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var canonical = EligibilityRuleSerializer.Serialize(
            EligibilityRule.AllOf(EligibilityRule.ContractActive(), EligibilityRule.AmountPaidAtLeast(4321m)));

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, canonical));

        // The Promotion is created.
        Assert.True(created.IsSuccess);

        // The stored rule is canonical.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var stored = await db2.Promotions.AsNoTracking().FirstAsync(p => p.Id == created.Value);
        Assert.Equal(canonical, EligibilityRuleSerializer.Serialize(stored.BenefitEligibilityRule!));

        // The query returns exactly the canonical JSON.
        var read = await mediator.Send(new GetPromotionByIdQuery(created.Value));
        Assert.True(read.IsSuccess);
        Assert.Equal(canonical, read.Value.BenefitEligibilityRule);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A04f — Canonical round trip is stable
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A04f_CanonicalJson_RoundTripsUnchanged()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var rule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.NoOverdueInstallment());

        var canonical = EligibilityRuleSerializer.Serialize(rule);

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, canonical));
        Assert.True(created.IsSuccess);

        // rule -> Serialize -> Parse -> Serialize must be byte-identical.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var stored = await db2.Promotions.AsNoTracking().FirstAsync(p => p.Id == created.Value);
        var reSerialized = EligibilityRuleSerializer.Serialize(stored.BenefitEligibilityRule!);
        Assert.Equal(canonical, reSerialized);

        var read = await mediator.Send(new GetPromotionByIdQuery(created.Value));
        Assert.Equal(canonical, read.Value.BenefitEligibilityRule);
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A04g — Unknown / polymorphic CLR-resolution payloads are rejected
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"$type\":\"System.Exception, mscorlib\",\"assembly\":\"mscorlib\"}")]
    [InlineData("{\"typeName\":\"Centerix.Domain.SomeRule\",\"clrType\":\"SomeRule\"}")]
    public async Task T9_C_A04g_ClrTypeResolutionPayloads_AreRejected(string payload)
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, payload));

        Assert.False(created.IsSuccess);
        Assert.Contains(created.Errors!, e => e.Code == "Promotion.BenefitEligibilityRule_Invalid");

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A04 — malformed / non-canonical payloads are rejected, never stored
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"kind\":\"TotallyUnknownRule\",\"value\":1}")]
    [InlineData("{\"kind\":\"AmountPaidAtLeastRule\",\"value\":-5}")]
    [InlineData("{\"kind\":\"PaymentTermsEqualsRule\",\"terms\":999}")]
    [InlineData("")]
    public async Task T9_C_A04_Create_MalformedRulePayload_IsRejectedAndNeverStored(string payload)
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, payload));

        // An empty payload means "no rule configured", which is itself rejected for a
        // benefit-bearing promotion. Any other payload is rejected as an invalid rule.
        Assert.False(created.IsSuccess);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(0, await db2.Promotions.CountAsync(p => p.PlanId == planId));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A05 — Update changes the rule for future offers only
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A05_Update_ChangesTheRule_WithoutTouchingAnExistingOffer()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, ConfiguredRuleJson()));
        Assert.True(created.IsSuccess);

        // Create leaves the promotion in Draft; activate it so it is actually applied.
        var db0 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var toActivate = await db0.Promotions.FirstAsync(p => p.Id == created.Value);
        Assert.True(toActivate.Activate().IsSuccess);
        await db0.SaveChangesAsync();

        // Calculate an offer while the original rule is configured.
        var offerResult = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offerResult.IsSuccess);
        Assert.Single(offerResult.Value.Benefits);

        var replacement = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(999m));

        var updated = await mediator.Send(new UpdatePromotionCommand(
            Id: created.Value,
            Name: "T9C benefit promo",
            Type: PromotionType.AdditionalBenefits,
            PlanId: planId,
            DurationMonths: 12,
            StartsAtUtc: Start,
            EndsAtUtc: End,
            Priority: 0,
            BenefitName: "Barcode Printer",
            BenefitValue: 500m,
            BenefitType: ContractBenefitType.PhysicalGift,
            BenefitCurrencyCode: "EGP",
            BenefitEligibilityRule: EligibilityRuleSerializer.Serialize(replacement)));
        Assert.True(updated.IsSuccess);

        // The existing offer snapshot keeps the rule it was calculated with.
        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var existingOffer = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.Benefits)
            .AsNoTracking()
            .FirstAsync(o => o.Id == offerResult.Value.Id);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(ConfiguredRule()),
            EligibilityRuleSerializer.Serialize(Assert.Single(existingOffer.Benefits).EligibilityRule!));

        // A new calculation uses the new rule.
        var secondOffer = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(secondOffer.IsSuccess);
        var newOffer = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.Benefits)
            .AsNoTracking()
            .FirstAsync(o => o.Id == secondOffer.Value.Id);
        Assert.Equal(
            EligibilityRuleSerializer.Serialize(replacement),
            EligibilityRuleSerializer.Serialize(Assert.Single(newOffer.Benefits).EligibilityRule!));
    }

    // ─────────────────────────────────────────────────────────────────
    // T9-C-A06 — The configured rule reaches the persisted Offer child row
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T9_C_A06_ConfiguredRule_ReachesThePersistedOfferBenefitUnchanged()
    {
        var (db, mediator, scope) = NewScope();
        using var _ = scope;

        var planId = await SeedPlanAsync(db);
        var created = await mediator.Send(BenefitCreate(planId, ConfiguredRuleJson()));
        Assert.True(created.IsSuccess);

        // The promotion must be active to be applied.
        var db0 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var promotion = await db0.Promotions.FirstAsync(p => p.Id == created.Value);
        Assert.True(promotion.Activate().IsSuccess);
        await db0.SaveChangesAsync();

        var offerResult = await mediator.Send(
            new CalculateAndPersistOfferCommand(planId, 12, PaymentTerms.FullUpfront));
        Assert.True(offerResult.IsSuccess);

        var db2 = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var offer = await db2.Offers
            .IgnoreQueryFilters()
            .Include(o => o.Benefits)
            .AsNoTracking()
            .FirstAsync(o => o.Id == offerResult.Value.Id);

        var benefit = Assert.Single(offer.Benefits);
        Assert.NotNull(benefit.EligibilityRule);
        Assert.Equal(
            ConfiguredRuleJson(),
            EligibilityRuleSerializer.Serialize(benefit.EligibilityRule!));
    }
}

/// <summary>
/// An InMemory host that grants the PLATFORM authorization boundary, so the real
/// CreatePromotionHandler / UpdatePromotionHandler authorization + validation path executes.
/// </summary>
public sealed class Task9CPlatformAdminTestFactory : TaskCFakeTenantTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPlatformAdminGuard>();
            services.AddSingleton<IPlatformAdminGuard, AllowingPlatformAdminGuard>();
        });
    }

    private sealed class AllowingPlatformAdminGuard : IPlatformAdminGuard
    {
        public Task<Result<Updated>> EnsurePlatformAdminAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<Result<Updated>>(Result.Updated);
    }
}
