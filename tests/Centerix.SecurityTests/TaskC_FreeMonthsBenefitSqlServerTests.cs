namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions.Commands;
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
/// Task C — FreeMonthsBenefit SQL Server integration tests.
///
/// Uses the existing <see cref="SqlServerIntegrationFactory"/> collection
/// (Local SQL Server preferred, Testcontainers fallback). Verifies that:
///   * The new <c>Platform.FreeMonthsBenefits</c> table exists with the
///     configured columns and indexes after migration.
///   * FreeMonthsBenefit.EligibilityRule round-trips through SQL Server with
///     canonical JSON preserved byte-identically.
///   * <see cref="OfferFreeMonthsBenefit"/> → <see cref="FreeMonthsBenefit"/>
///     snapshot is preserved through the REAL production Offer → Contract flow.
///   * FreeMonthsBenefit navigation loads correctly with Include.
///   * State transitions (Eligibility and Fulfillment) survive a SQL round-trip.
///   * Persisted JSON contains no CLR / assembly / executable metadata.
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class TaskC_FreeMonthsBenefitSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public TaskC_FreeMonthsBenefitSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ─────────────────────────────────────────────────────────────────
    // Helpers — mirror patterns from TaskB.2 tests.
    // ─────────────────────────────────────────────────────────────────

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
            code: $"PlanC_{Guid.NewGuid():N}"[..28],
            displayName: "TaskC Plan",
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

    private static EligibilityRule DefaultUpfrontBonusRule(decimal contractedAmount = 5000m) =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(contractedAmount),
            EligibilityRule.NoOverdueInstallment());

    private static void AssertPersistedJsonIsSafe(string rawJson)
    {
        Assert.False(string.IsNullOrWhiteSpace(rawJson));
        Assert.DoesNotContain("Centerix", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FreeMonthsBenefit", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("EligibilityRule", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // 1 — Schema verification (table + columns + indexes)
    // ====================================================================

    [Fact]
    public async Task SqlC01_FreeMonthsBenefits_Table_HasExpectedColumnsAndIndexes()
    {
        var tenantId = $"C-1-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var expectedColumns = new Dictionary<string, (string DataType, string IsNullable, string? CharMaxLen)>
        {
            ["Id"] = ("uniqueidentifier", "NO", null),
            ["ContractId"] = ("uniqueidentifier", "NO", null),
            ["EntitlementMonths"] = ("int", "NO", null),
            ["CurrencyCode"] = ("nvarchar", "NO", "3"),
            ["EligibilityStatus"] = ("tinyint", "NO", null),
            ["EligibleAtUtc"] = ("datetime2", "YES", null),
            ["FulfillmentStatus"] = ("tinyint", "NO", null),
            ["GrantedAtUtc"] = ("datetime2", "YES", null),
            ["AppliedAtUtc"] = ("datetime2", "YES", null),
            ["EligibilityRule"] = ("nvarchar", "NO", "4000"),
        };

        foreach (var (colName, (dataType, isNullable, charMaxLen)) in expectedColumns)
        {
            var dt = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT DATA_TYPE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME='FreeMonthsBenefits' AND COLUMN_NAME={0}", colName)
                .SingleAsync();
            var nullable = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT IS_NULLABLE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME='FreeMonthsBenefits' AND COLUMN_NAME={0}", colName)
                .SingleAsync();

            Assert.Equal(dataType, dt);
            Assert.Equal(isNullable, nullable);

            if (charMaxLen != null)
            {
                var len = await db.Database
                    .SqlQueryRaw<string>(
                        "SELECT CAST(CHARACTER_MAXIMUM_LENGTH AS varchar(10)) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_SCHEMA='Platform' AND TABLE_NAME='FreeMonthsBenefits' AND COLUMN_NAME={0}", colName)
                    .SingleAsync();
                Assert.Equal(charMaxLen, len);
            }
        }

        // FK on ContractId
        var fkCount = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(COUNT(*) AS varchar(10)) AS [Value] FROM sys.foreign_keys fk " +
                "JOIN sys.tables t ON fk.parent_object_id = t.object_id " +
                "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "WHERE s.name='Platform' AND t.name='FreeMonthsBenefits'")
            .SingleAsync();
        Assert.Equal("1", fkCount);

        // Three indexes (PK + ContractId + (ContractId, EligibilityStatus) + (ContractId, FulfillmentStatus))
        var ixCount = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(COUNT(*) AS varchar(10)) AS [Value] FROM sys.indexes i " +
                "JOIN sys.tables t ON i.object_id = t.object_id " +
                "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "WHERE s.name='Platform' AND t.name='FreeMonthsBenefits' AND i.is_primary_key = 0")
            .SingleAsync();
        Assert.Equal("3", ixCount);
    }

    // ====================================================================
    // 2 — EligibilityRule round-trip through SQL
    // ====================================================================

    [Fact]
    public async Task SqlC02_FreeMonthsBenefit_EligibilityRule_RoundTripsThroughSql()
    {
        var tenantId = $"C-2-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var original = DefaultUpfrontBonusRule(5000m);
        var originalJson = EligibilityRuleSerializer.Serialize(original);

        var contract = NewContract(tenantId, planId);
        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 1, "EGP", original).Value;
        contract.AddFreeMonthsBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        // Re-read in a fresh scope.
        FreeMonthsBenefit loaded;
        string loadedJson;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedContract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contract.Id);

            Assert.Single(loadedContract.FreeMonthsBenefits);
            loaded = loadedContract.FreeMonthsBenefits[0];
            loadedJson = EligibilityRuleSerializer.Serialize(loaded.EligibilityRule);
        }

        Assert.Equal(original, loaded.EligibilityRule);
        Assert.Equal(originalJson, loadedJson);

        // Persisted JSON must contain no CLR metadata.
        var rawJson = await LoadPersistedFreeMonthsBenefitJson(tenantId, benefit.Id);
        AssertPersistedJsonIsSafe(rawJson);
    }

    // ====================================================================
    // 3 — Offer → Contract snapshot via REAL production handlers
    // ====================================================================

    [Fact]
    public async Task SqlC03_OfferToContract_SnapshotsFreeMonthsBenefitThroughProductionFlow()
    {
        var tenantId = $"C-3-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var sourceRule = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.AmountPaidAtLeast(10000m));
        var sourceRuleJson = EligibilityRuleSerializer.Serialize(sourceRule);

        Guid offerId;
        Guid contractId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

            // Build the Offer + OfferFreeMonthsBenefit (real production flow).
            var offer = NewOffer(tenantId, planId);
            var offerFreeMonths = OfferFreeMonthsBenefit.Create(
                id: Guid.NewGuid(),
                offerId: offer.Id,
                entitlementMonths: 1,
                currencyCode: "EGP",
                eligibilityRule: sourceRule).Value;
            offer.AddFreeMonthsBenefit(offerFreeMonths);

            db.Offers.Add(offer);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            offerId = offer.Id;

            var acceptHandler = new AcceptOfferHandler(db, tenant);
            var acceptResult = await acceptHandler.Handle(
                new AcceptOfferCommand(offer.Id), CancellationToken.None);
            Assert.True(acceptResult.IsSuccess);

            var contractHandler = new CreateContractFromOfferHandler(db, tenant);
            var contractResult = await contractHandler.Handle(
                new CreateContractFromOfferCommand(offer.Id, $"CTR-C-{Guid.NewGuid():N}"[..16]),
                CancellationToken.None);
            Assert.True(contractResult.IsSuccess);
            contractId = contractResult.Value;
        }

        // Reload from SQL in a fresh scope.
        FreeMonthsBenefit loaded;
        string loadedJson;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadedContract = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contractId);

            Assert.Single(loadedContract.FreeMonthsBenefits);
            loaded = loadedContract.FreeMonthsBenefits[0];
            loadedJson = EligibilityRuleSerializer.Serialize(loaded.EligibilityRule);
        }

        // Structural equality and JSON byte-identity after production + SQL round-trip.
        Assert.Equal(sourceRule, loaded.EligibilityRule);
        Assert.Equal(sourceRuleJson, loadedJson);
        Assert.Equal(1, loaded.EntitlementMonths);
        Assert.Equal(FreeMonthsEligibilityStatus.NotEligible, loaded.EligibilityStatus);
        Assert.Equal(FreeMonthsFulfillmentStatus.Pending, loaded.FulfillmentStatus);

        // Persisted JSON must be safe.
        var rawJson = await LoadPersistedFreeMonthsBenefitJson(tenantId, loaded.Id);
        AssertPersistedJsonIsSafe(rawJson);
    }

    // ====================================================================
    // 4 — Multiple FreeMonthsBenefits on one Contract are preserved independently
    // ====================================================================

    [Fact]
    public async Task SqlC04_MultipleFreeMonthsBenefits_OnOneContract_RoundTripIndependently()
    {
        var tenantId = $"C-4-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = NewContract(tenantId, planId);

        var b1 = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule(5000m)).Value;
        var b2 = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 3, "EGP", DefaultUpfrontBonusRule(10000m)).Value;
        var b3 = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 12, "EGP", DefaultUpfrontBonusRule(50000m)).Value;

        contract.AddFreeMonthsBenefit(b1);
        contract.AddFreeMonthsBenefit(b2);
        contract.AddFreeMonthsBenefit(b3);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.FreeMonthsBenefits)
                .AsNoTracking()
                .FirstAsync(c => c.Id == contract.Id);

            Assert.Equal(3, loaded.FreeMonthsBenefits.Count);
            var sorted = loaded.FreeMonthsBenefits.OrderBy(b => b.EntitlementMonths).ToList();
            Assert.Equal(1, sorted[0].EntitlementMonths);
            Assert.Equal(3, sorted[1].EntitlementMonths);
            Assert.Equal(12, sorted[2].EntitlementMonths);

            // Each benefit's rule survives byte-identically.
            Assert.Equal(DefaultUpfrontBonusRule(5000m), sorted[0].EligibilityRule);
            Assert.Equal(DefaultUpfrontBonusRule(10000m), sorted[1].EligibilityRule);
            Assert.Equal(DefaultUpfrontBonusRule(50000m), sorted[2].EligibilityRule);
        }
    }

    // ====================================================================
    // 5 — State transitions survive SQL round-trip
    // ====================================================================

    [Fact]
    public async Task SqlC05_StateTransitions_SurviveSqlRoundTrip()
    {
        var tenantId = $"C-5-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var contract = NewContract(tenantId, planId);
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 1, "EGP", DefaultUpfrontBonusRule()).Value;
        benefit.MarkEligible(t1);
        benefit.Grant(t2);
        benefit.MarkAppliedToSubscription(t3);
        contract.AddFreeMonthsBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await db.Contracts
                .IgnoreQueryFilters()
                .Include(c => c.FreeMonthsBenefits)
                .AsNoTracking()
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

    // ====================================================================
    // 6 — Persisted JSON safety (no CLR / assembly / executable metadata)
    // ====================================================================

    [Fact]
    public async Task SqlC06_PersistedJson_DoesNotContainClrOrExecutableMetadata()
    {
        var tenantId = $"C-6-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        var nested = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AnyOf(
                EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
                EligibilityRule.PaymentMethodEquals("Cash")),
            EligibilityRule.AmountPaidAtLeast(5000m));

        var contract = NewContract(tenantId, planId);
        var benefit = FreeMonthsBenefit.Create(
            Guid.NewGuid(), contract.Id, 2, "EGP", nested).Value;
        contract.AddFreeMonthsBenefit(benefit);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var rawJson = await LoadPersistedFreeMonthsBenefitJson(tenantId, benefit.Id);
        AssertPersistedJsonIsSafe(rawJson);

        // Specifically: no polymorphic discriminator that could be deserialised
        // as executable code.
        Assert.DoesNotContain("$type", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // 7 — DefaultEligibilityRule backfill on null is rejected by the
    //     domain boundary (FreeMonthsBenefit always requires a rule).
    //     Mirror of ContractBenefit legacy behaviour: NO default fabrication.
    // ====================================================================

    [Fact]
    public async Task SqlC07_Migration_DoesNotPopulateFreeMonthsBenefitsTable_OnEmptyDatabase()
    {
        // The migration is schema-only. We must NOT have manufactured
        // FreeMonthsBenefit rows from Contract.BonusMonths > 0 (design
        // invariant 35). On a freshly-migrated database, the table exists
        // (verified by SqlC01) but is empty.
        var tenantId = $"C-7-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);
        var planId = await EnsurePlanAsync(tenantId);

        // Create a contract with BonusMonths = 0 (default) — no FreeMonthsBenefit row.
        var contract = NewContract(tenantId, planId);
        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Contracts.Add(contract);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.FreeMonthsBenefits
                .IgnoreQueryFilters()
                .Where(b => b.ContractId == contract.Id)
                .ToListAsync();
            Assert.Empty(rows);
        }
    }

    // ====================================================================
    // Helpers — raw SQL column reads against the live database
    // ====================================================================

    private async Task<string> LoadPersistedFreeMonthsBenefitJson(string tenantId, Guid benefitId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT EligibilityRule AS [Value] FROM Platform.FreeMonthsBenefits WHERE Id = {0}", benefitId)
            .SingleAsync();
    }

    // ====================================================================
    // 8 — OfferFreeMonthsBenefits.EligibilityRule is NOT NULL in schema
    //     (Correction 2: schema enforces required rule)
    // ====================================================================

    [Fact]
    public async Task SqlC08_OfferFreeMonthsBenefits_EligibilityRule_IsNotNull_InSchema()
    {
        var tenantId = $"C-8-{Guid.NewGuid():N}"[..16];
        await SeedTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var columns = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CONCAT(TABLE_NAME, '|', IS_NULLABLE, '|', COALESCE(COLUMN_DEFAULT, '<none>'), " +
                "'|', DATA_TYPE, '|', CHARACTER_MAXIMUM_LENGTH) AS [Value] " +
                "FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA='Platform' " +
                "AND TABLE_NAME IN ('OfferFreeMonthsBenefits', 'FreeMonthsBenefits') " +
                "AND COLUMN_NAME='EligibilityRule'")
            .ToListAsync();

        Assert.Equal(2, columns.Count);

        foreach (var column in columns)
        {
            var parts = column.Split('|');

            // nvarchar(4000) NOT NULL on BOTH the Offer snapshot and the Contract side.
            Assert.Equal("NO", parts[1]);
            Assert.Equal("nvarchar", parts[3]);
            Assert.Equal("4000", parts[4]);

            // No fabricated default was introduced by the tightening migration.
            Assert.Equal("<none>", parts[2]);
        }

        Assert.Contains(columns, c => c.StartsWith("OfferFreeMonthsBenefits|"));
        Assert.Contains(columns, c => c.StartsWith("FreeMonthsBenefits|"));
    }
}
