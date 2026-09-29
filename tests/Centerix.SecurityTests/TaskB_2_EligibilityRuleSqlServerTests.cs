namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Task B.2 — SQL Server verification of the EligibilityRule foundation.
///
/// Uses the existing <see cref="SqlServerIntegrationFactory"/> collection (Local SQL Server
/// preferred, Testcontainers fallback). Verifies that:
///   * The Task B.1 PaymentMethod canonicalisation survives a REAL database round-trip.
///   * Composite (AllOf/AnyOf) rules persist with identical structural shape and JSON.
///   * All primitive rule types round-trip.
///   * OfferBenefit.EligibilityRule → ContractBenefit.EligibilityRule snapshot is preserved.
///   * Null EligibilityRule (legacy/back-compat) survives round-trip without default fabrication.
///   * The persisted SQL Server schema matches the EF configuration (nvarchar(4000), nullable).
///   * Persisted JSON contains no CLR / assembly / executable metadata.
///   * Equality ↔ Serialisation invariant holds against the live database.
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskB_2_EligibilityRuleSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskB_2_EligibilityRuleSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ====================================================================
    // Helpers — mirror patterns from the existing SqlServer test classes.
    // ====================================================================

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
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
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
            code: $"PlanB2_{Guid.NewGuid():N}"[..28],
            displayName: "TaskB.2 Plan",
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

    private static Offer NewOffer(string tenantId, int planId) => Offer.Create(
        id: Guid.NewGuid(),
        tenantId: tenantId,
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

    private static Contract NewContract(string tenantId, int planId) => Contract.Create(
        id: Guid.NewGuid(),
        tenantId: tenantId,
        contractNumber: $"CNT-B2-{Guid.NewGuid():N}"[..16],
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
        paymentTerms: PaymentTerms.Installments,
        discountAmount: 0m).Value;

    // ====================================================================
    // 3.1 — PaymentMethod canonicalisation round-trip
    // ====================================================================

    [Fact]
    public async Task Sql01_PaymentMethod_Canonicalisation_SurvivesDatabaseRoundTrip()
    {
        var tenantId = $"B2-1-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        // Build the rule from intentionally NON-canonical input.
        var preCanonical = EligibilityRule.PaymentMethodEquals("  cash  ");
        var preCanonicalJson = EligibilityRuleSerializer.Serialize(preCanonical);

        // Sanity: input was canonicalised to "CASH" before persistence.
        Assert.Equal("CASH", ((PaymentMethodEqualsRule)preCanonical).PaymentMethod);
        Assert.Contains("\"paymentMethod\":\"CASH\"", preCanonicalJson);

        var offer = NewOffer(tenantId, planId);
        var benefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Printer",
            description: null,
            contractualValue: 1500m,
            currencyCode: "EGP",
            eligibilityRule: preCanonical).Value;
        offer.AddBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        // Re-read from SQL Server in a fresh scope to force a real round-trip.
        EligibilityRule? loaded;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedOffer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offer.Id);

            Assert.Single(loadedOffer.Benefits);
            loaded = loadedOffer.Benefits.First().EligibilityRule;
        }

        Assert.NotNull(loaded);
        Assert.Equal(preCanonical, loaded);

        var loadedPm = Assert.IsType<PaymentMethodEqualsRule>(loaded);
        Assert.Equal("CASH", loadedPm.PaymentMethod);

        // Serialised JSON before and after persistence is byte-identical.
        Assert.Equal(preCanonicalJson, EligibilityRuleSerializer.Serialize(loaded));

        // Three independently-built case variants must remain structurally and
        // serialisation-equal through the live database.
        var cash1 = EligibilityRule.PaymentMethodEquals("Cash");
        var cash2 = EligibilityRule.PaymentMethodEquals(" cash ");
        var cash3 = EligibilityRule.PaymentMethodEquals("CASH");

        var offer2Id = Guid.NewGuid();
        var cId1 = Guid.NewGuid(); var cId2 = Guid.NewGuid(); var cId3 = Guid.NewGuid();
        var offer2 = NewOffer(tenantId, planId);
        var b1 = OfferBenefit.Create(cId1, offer2.Id, ContractBenefitType.PhysicalGift, "B1", null, 100m, "EGP", cash1).Value;
        var b2 = OfferBenefit.Create(cId2, offer2.Id, ContractBenefitType.PhysicalGift, "B2", null, 100m, "EGP", cash2).Value;
        var b3 = OfferBenefit.Create(cId3, offer2.Id, ContractBenefitType.PhysicalGift, "B3", null, 100m, "EGP", cash3).Value;
        offer2.AddBenefit(b1); offer2.AddBenefit(b2); offer2.AddBenefit(b3);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer2);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await db.OfferBenefits
                .IgnoreQueryFilters()
                .Where(b => b.Id == cId1 || b.Id == cId2 || b.Id == cId3)
                .AsNoTracking()
                .ToListAsync();
            Assert.Equal(3, persisted.Count);

            var r1 = persisted.Single(b => b.Id == cId1).EligibilityRule!;
            var r2 = persisted.Single(b => b.Id == cId2).EligibilityRule!;
            var r3 = persisted.Single(b => b.Id == cId3).EligibilityRule!;

            Assert.Equal(r1, r2);
            Assert.Equal(r2, r3);

            var j1 = EligibilityRuleSerializer.Serialize(r1);
            var j2 = EligibilityRuleSerializer.Serialize(r2);
            var j3 = EligibilityRuleSerializer.Serialize(r3);
            Assert.Equal(j1, j2);
            Assert.Equal(j2, j3);
        }
    }

    // ====================================================================
    // 3.2 — Composite (AllOf) round-trip
    // ====================================================================

    [Fact]
    public async Task Sql02_AllOf_CompositeRule_RoundTripsExactly()
    {
        var tenantId = $"B2-2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var original = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(1000m),
            EligibilityRule.NoOverdueInstallment());

        var originalJson = EligibilityRuleSerializer.Serialize(original);

        var offer = NewOffer(tenantId, planId);
        var benefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Bundle",
            description: null,
            contractualValue: 1500m,
            currencyCode: "EGP",
            eligibilityRule: original).Value;
        offer.AddBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        EligibilityRule? loaded;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedOffer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offer.Id);
            loaded = loadedOffer.Benefits.Single().EligibilityRule;
        }

        Assert.NotNull(loaded);
        Assert.Equal(original, loaded);

        var loadedAllOf = Assert.IsType<AllOfRule>(loaded);
        Assert.Equal(4, loadedAllOf.Rules.Count);
        Assert.IsType<ContractActiveRule>(loadedAllOf.Rules[0]);
        Assert.IsType<PaymentTermsEqualsRule>(loadedAllOf.Rules[1]);
        Assert.IsType<AmountPaidAtLeastRule>(loadedAllOf.Rules[2]);
        Assert.IsType<NoOverdueInstallmentRule>(loadedAllOf.Rules[3]);

        // Child order preserved.
        var originalAllOf = (AllOfRule)original;
        for (var i = 0; i < originalAllOf.Rules.Count; i++)
            Assert.Equal(originalAllOf.Rules[i], loadedAllOf.Rules[i]);

        // Canonical JSON identical.
        Assert.Equal(originalJson, EligibilityRuleSerializer.Serialize(loaded));

        // No CLR / assembly metadata in persisted JSON.
        var rawJson = await LoadPersistedOfferBenefitJson(tenantId, benefit.Id);
        AssertPersistedJsonIsSafe(rawJson);
    }

    // ====================================================================
    // 3.3 — AnyOf round-trip
    // ====================================================================

    [Fact]
    public async Task Sql03_AnyOf_CompositeRule_RoundTripsExactly()
    {
        var tenantId = $"B2-3-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var original = EligibilityRule.AnyOf(
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.PaymentMethodEquals("BANK_TRANSFER"));

        var originalJson = EligibilityRuleSerializer.Serialize(original);

        var offer = NewOffer(tenantId, planId);
        var benefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "AnyOf Bundle",
            description: null,
            contractualValue: 800m,
            currencyCode: "EGP",
            eligibilityRule: original).Value;
        offer.AddBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        EligibilityRule? loaded;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedOffer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offer.Id);
            loaded = loadedOffer.Benefits.Single().EligibilityRule;
        }

        Assert.NotNull(loaded);
        Assert.Equal(original, loaded);
        Assert.Equal(originalJson, EligibilityRuleSerializer.Serialize(loaded));

        var loadedAnyOf = Assert.IsType<AnyOfRule>(loaded);
        Assert.Equal(2, loadedAnyOf.Rules.Count);

        var rawJson = await LoadPersistedOfferBenefitJson(tenantId, benefit.Id);
        AssertPersistedJsonIsSafe(rawJson);
    }

    // ====================================================================
    // 3.4 — All primitive rules round-trip
    // ====================================================================

    [Fact]
    public async Task Sql04_AllPrimitiveRules_RoundTripIndividually()
    {
        var tenantId = $"B2-4-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var utc = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var cases = new (string Tag, string Name, EligibilityRule Rule)[]
        {
            ("CA", "CA gift", EligibilityRule.ContractActive()),
            ("PT", "PT gift", EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront)),
            ("PM", "PM gift", EligibilityRule.PaymentMethodEquals("MobileWallet")),
            ("CU", "CU gift", EligibilityRule.CompletedByUtc(utc)),
            ("NI", "NI gift", EligibilityRule.NoOverdueInstallment()),
            ("AM", "AM gift", EligibilityRule.AmountPaidAtLeast(2500m)),
            ("DG", "DG gift", EligibilityRule.DaysFromContractStartGte(30)),
            ("DM", "DM gift", EligibilityRule.DurationMonthsGte(12)),
        };

        var offer = NewOffer(tenantId, planId);
        var added = new List<(string Tag, Guid BenefitId, EligibilityRule Rule, string Json)>();
        foreach (var (tag, name, rule) in cases)
        {
            var b = OfferBenefit.Create(
                id: Guid.NewGuid(),
                offerId: offer.Id,
                benefitType: ContractBenefitType.PhysicalGift,
                name: name,
                description: null,
                contractualValue: 100m,
                currencyCode: "EGP",
                eligibilityRule: rule).Value;
            offer.AddBenefit(b);
            added.Add((tag, b.Id, rule, EligibilityRuleSerializer.Serialize(rule)));
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await db.OfferBenefits
                .IgnoreQueryFilters()
                .Where(b => added.Select(a => a.BenefitId).Contains(b.Id))
                .AsNoTracking()
                .ToListAsync();

            Assert.Equal(added.Count, persisted.Count);
            foreach (var (tag, benefitId, originalRule, originalJson) in added)
            {
                var row = persisted.Single(b => b.Id == benefitId);
                Assert.NotNull(row.EligibilityRule);
                Assert.Equal(originalRule, row.EligibilityRule);
                Assert.Equal(originalJson, EligibilityRuleSerializer.Serialize(row.EligibilityRule!));
            }
        }
    }

    // ====================================================================
    // 4 — OfferBenefit → ContractBenefit snapshot via SQL
    // ====================================================================

    [Fact]
    public async Task Sql05_OfferToContract_PreservesEligibilityRuleAcrossSnapshot()
    {
        var tenantId = $"B2-5-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var sourceRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));

        var offer = NewOffer(tenantId, planId);
        var offerBenefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "VIP Gift",
            description: "Snapshot preserved",
            contractualValue: 2000m,
            currencyCode: "EGP",
            eligibilityRule: sourceRule).Value;
        offer.AddBenefit(offerBenefit);

        // Persist OfferBenefit through the production aggregate path.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        // Snapshot the source rule bytes as the reference.
        var offerBenefitRuleJson = EligibilityRuleSerializer.Serialize(sourceRule);

        // Build ContractBenefit through the existing production factory by passing the
        // OfferBenefit.EligibilityRule verbatim (mirrors CreateContractFromOfferCommand).
        var contract = NewContract(tenantId, planId);
        var snapshotBenefit = ContractBenefit.Create(
            id: Guid.NewGuid(),
            contractId: contract.Id,
            benefitType: offerBenefit.BenefitType,
            name: offerBenefit.Name,
            description: offerBenefit.Description,
            contractualValue: offerBenefit.ContractualValue,
            currencyCode: offerBenefit.CurrencyCode,
            eligibilityRule: offerBenefit.EligibilityRule).Value;
        var snapshotRule = snapshotBenefit.EligibilityRule!;
        var snapshotRuleJson = EligibilityRuleSerializer.Serialize(snapshotRule);

        contract.AddBenefit(snapshotBenefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        // Reload the ContractBenefit from SQL Server in a fresh scope.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedContract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.Benefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contract.Id);

            Assert.Single(loadedContract.Benefits);
            var loadedBenefit = loadedContract.Benefits.Single();
            Assert.NotNull(loadedBenefit.EligibilityRule);

            // Snapshot survived persistence end-to-end.
            Assert.Equal(sourceRule, loadedBenefit.EligibilityRule);
            Assert.Equal(
                offerBenefitRuleJson,
                EligibilityRuleSerializer.Serialize(loadedBenefit.EligibilityRule!));
            Assert.Equal(snapshotRuleJson, EligibilityRuleSerializer.Serialize(loadedBenefit.EligibilityRule!));

            Assert.IsType<AllOfRule>(loadedBenefit.EligibilityRule!);
        }

        // Equality ↔ serialisation invariant for the snapshot.
        var rebuilt = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedContract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.Benefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contract.Id);
            var loadedBenefit = loadedContract.Benefits.Single();
            Assert.Equal(rebuilt, loadedBenefit.EligibilityRule);
            Assert.Equal(
                EligibilityRuleSerializer.Serialize(rebuilt),
                EligibilityRuleSerializer.Serialize(loadedBenefit.EligibilityRule!));
        }
    }

    // ====================================================================
    // 5 — Null EligibilityRule (legacy/back-compat)
    // ====================================================================

    [Fact]
    public async Task Sql06_NullEligibilityRule_PersistsAndReloads()
    {
        var tenantId = $"B2-6-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var offer = NewOffer(tenantId, planId);
        var legacyOfferBenefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Legacy Gift",
            description: null,
            contractualValue: 500m,
            currencyCode: "EGP",
            eligibilityRule: null).Value;
        offer.AddBenefit(legacyOfferBenefit);

        var contract = NewContract(tenantId, planId);
        var legacyContractBenefit = ContractBenefit.Create(
            id: Guid.NewGuid(),
            contractId: contract.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Legacy Contract Gift",
            description: null,
            contractualValue: 500m,
            currencyCode: "EGP",
            eligibilityRule: null).Value;
        contract.AddBenefit(legacyContractBenefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedOffer = await db.Offers
                .IgnoreQueryFilters()
                .Include(o => o.Benefits)
                .AsNoTracking()
                .FirstAsync(o => o.Id == offer.Id);
            var loadedContract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.Benefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contract.Id);

            Assert.Null(loadedOffer.Benefits.Single().EligibilityRule);
            Assert.Null(loadedContract.Benefits.Single().EligibilityRule);
        }

        // Raw persisted column is also SQL NULL — no default rule was invented.
        var offerRuleColumn = await LoadRawOfferBenefitColumn(tenantId, legacyOfferBenefit.Id);
        var contractRuleColumn = await LoadRawContractBenefitColumn(tenantId, legacyContractBenefit.Id);
        Assert.True(string.IsNullOrEmpty(offerRuleColumn),
            $"Persisted OfferBenefits.EligibilityRule must be SQL NULL — got '{offerRuleColumn}'");
        Assert.True(string.IsNullOrEmpty(contractRuleColumn),
            $"Persisted ContractBenefits.EligibilityRule must be SQL NULL — got '{contractRuleColumn}'");
    }

    // ====================================================================
    // 6 — Schema verification
    // ====================================================================

    [Theory]
    [InlineData("OfferBenefits")]
    [InlineData("ContractBenefits")]
    public async Task Sql07_EligibilityRuleColumn_MatchesConfiguredSchema(string table)
    {
        var tenantId = $"B2-7-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dataType = await db.Database
            .SqlQueryRaw<string>(
                "SELECT DATA_TYPE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME={0} AND COLUMN_NAME='EligibilityRule'", table)
            .SingleAsync();

        var isNullable = await db.Database
            .SqlQueryRaw<string>(
                "SELECT IS_NULLABLE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME={0} AND COLUMN_NAME='EligibilityRule'", table)
            .SingleAsync();

        var charMaxLen = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(CHARACTER_MAXIMUM_LENGTH AS varchar(10)) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME={0} AND COLUMN_NAME='EligibilityRule'", table)
            .SingleAsync();

        Assert.Equal("nvarchar", dataType);
        Assert.Equal("YES", isNullable);
        Assert.Equal("4000", charMaxLen);

        var defaultCount = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(COUNT(*) AS varchar(10)) AS [Value] FROM sys.default_constraints dc " +
                "JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id " +
                "JOIN sys.tables t ON c.object_id = t.object_id " +
                "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "WHERE s.name='Platform' AND t.name={0} AND c.name='EligibilityRule'", table)
            .SingleAsync();

        Assert.Equal("0", defaultCount);
    }

    // ====================================================================
    // 7 — Safety: persisted JSON contains no CLR / assembly / executable metadata
    // ====================================================================

    [Fact]
    public async Task Sql08_PersistedJson_DoesNotContainClrOrExecutableMetadata()
    {
        var tenantId = $"B2-8-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var nested = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var offer = NewOffer(tenantId, planId);
        var benefit = OfferBenefit.Create(
            id: Guid.NewGuid(),
            offerId: offer.Id,
            benefitType: ContractBenefitType.PhysicalGift,
            name: "Nested Gift",
            description: null,
            contractualValue: 1500m,
            currencyCode: "EGP",
            eligibilityRule: nested).Value;
        offer.AddBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var rawJson = await LoadPersistedOfferBenefitJson(tenantId, benefit.Id);
        AssertPersistedJsonIsSafe(rawJson);

        // No polymorphic discriminator that could be deserialised as executable code.
        Assert.DoesNotContain("$type", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.", rawJson, StringComparison.Ordinal);
    }

    // ====================================================================
    // Helpers — raw SQL column reads against the live database
    // ====================================================================

    private async Task<string> LoadPersistedOfferBenefitJson(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT EligibilityRule AS [Value] FROM Platform.OfferBenefits WHERE Id = {0}", benefitId)
            .SingleAsync();
    }

    private async Task<string> LoadRawOfferBenefitColumn(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(ISNULL(EligibilityRule, '') AS varchar(4000)) AS [Value] FROM Platform.OfferBenefits WHERE Id = {0}", benefitId)
            .ToListAsync();
        return raw.Count == 0 ? string.Empty : (raw[0] ?? string.Empty);
    }

    private async Task<string> LoadRawContractBenefitColumn(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(ISNULL(EligibilityRule, '') AS varchar(4000)) AS [Value] FROM Platform.ContractBenefits WHERE Id = {0}", benefitId)
            .ToListAsync();
        return raw.Count == 0 ? string.Empty : (raw[0] ?? string.Empty);
    }

    private static void AssertPersistedJsonIsSafe(string rawJson)
    {
        Assert.False(string.IsNullOrWhiteSpace(rawJson));
        Assert.DoesNotContain("Centerix", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("EligibilityRule", rawJson, StringComparison.Ordinal); // no CLR type names
        Assert.DoesNotContain("System.", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Diagnostics", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("eval", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expression", rawJson, StringComparison.OrdinalIgnoreCase);
    }
}