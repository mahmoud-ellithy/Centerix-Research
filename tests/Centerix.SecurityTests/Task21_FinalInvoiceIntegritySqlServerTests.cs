using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// TASK 21 (final closure) — the DATABASE half of the invoice closure.
///
/// Everything asserted here is invisible to an InMemory test: the InMemory provider ignores
/// column precision, filtered unique indexes and foreign keys entirely. These tests run against
/// the real migration chain on a real SQL Server and prove three claims that the closure report
/// otherwise only asserts in prose:
///
///  1. PRECISION AT REST — Platform.Invoices (Subtotal, DiscountAmount, TaxAmount, TotalAmount)
///     and Platform.InvoiceLines (UnitPrice, LineTotal) are decimal(18,2) in the live schema,
///     and a value above the old decimal(10,2) ceiling round-trips unchanged, so
///     Invoice.TotalAmount == Contract.ContractedAmount survives being written to disk.
///
///  2. THE PRECISION ALTERATION IS SAFE — the six altered columns carry no defaults, no CHECK
///     constraints, are not computed, and participate in no index. Widening precision therefore
///     needs no drop-and-recreate of anything, and cannot silently change behaviour.
///
///  3. INTEGRITY IS ENFORCED BY THE ENGINE, NOT BY CONVENTION — FKs from Invoice to
///     Contracts / TenantPlans / BillingCycles exist and reject bad references, and
///     UX_Invoices_BillingCycleId is a UNIQUE FILTERED index (WHERE BillingCycleId IS NOT NULL)
///     that rejects a second invoice for the same cycle even under concurrency.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task21_FinalInvoiceIntegritySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Task21_FinalInvoiceIntegritySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static readonly DateTime Start2026 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now2026 = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The largest value decimal(10,2) can hold — the ceiling the chain outgrew.</summary>
    private const decimal LegacyDecimal10Max = 99_999_999.99m;

    // ==================================================================
    // Helpers
    // ==================================================================

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(currentTenant, true);
    }

    private async Task EnsureTenantExistsAsync(IServiceProvider services, string tenantId)
    {
        var store = services.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId, Identifier = tenantId, Name = tenantId,
                Email = $"{tenantId}@test.com", IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>
    /// Runs a single-column catalog query. Kept as raw SQL on purpose: the point of these tests
    /// is the physical schema SQL Server actually holds, not what the EF model claims.
    /// </summary>
    private async Task<List<string>> QueryAsync(string sql)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
    }

    private static string ColumnPrecisionSql(string table, string column) =>
        "SELECT CONCAT(CAST(NUMERIC_PRECISION AS varchar(10)), ',', CAST(NUMERIC_SCALE AS varchar(10))) " +
        "FROM INFORMATION_SCHEMA.COLUMNS " +
        $"WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'";

    private static string DescribeSqlError(SqlException error, string because)
        => $"{because} — got {error.Number}: {error.Message}";

    private static TimeProvider FrozenClock(DateTime utcNow)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(utcNow));
        return timeProvider;
    }

    /// <summary>COLUMN_LENGTH(...) of each money column of a table — the column ids to match on.</summary>
    private static string MoneyColumnIds(string table, params string[] columns)
        => string.Join(", ", columns.Select(c => $"COL_LENGTH(OBJECT_ID('Platform.{table}'), '{c}')"));

    private static string DefaultsSql(string table, params string[] columns) =>
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.default_constraints dc " +
        "JOIN sys.tables t ON dc.parent_object_id = t.object_id " +
        "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        $"WHERE s.name = 'Platform' AND t.name = '{table}' " +
        $"AND dc.parent_column_id IN ({MoneyColumnIds(table, columns)})";

    private static string ChecksSql(string table, params string[] columns) =>
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.check_constraints cc " +
        "JOIN sys.tables t ON cc.parent_object_id = t.object_id " +
        "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        $"WHERE s.name = 'Platform' AND t.name = '{table}' " +
        $"AND cc.parent_column_id IN ({MoneyColumnIds(table, columns)})";

    private static string IndexParticipationSql(string table, params string[] columns) =>
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.index_columns ic " +
        "JOIN sys.tables t ON ic.object_id = t.object_id " +
        "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        $"WHERE s.name = 'Platform' AND t.name = '{table}' " +
        $"AND ic.column_id IN ({MoneyColumnIds(table, columns)})";

    private static string ComputedSql(string table, params string[] columns) =>
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.computed_columns cc " +
        "JOIN sys.tables t ON cc.object_id = t.object_id " +
        "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
        $"WHERE s.name = 'Platform' AND t.name = '{table}' AND cc.name IN (" +
        string.Join(", ", columns.Select(c => $"'{c}'")) + ")";

    private static string RegularColumnCountSql(string table, params string[] columns) =>
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM INFORMATION_SCHEMA.COLUMNS " +
        $"WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = '{table}' AND COLUMN_NAME IN (" +
        string.Join(", ", columns.Select(c => $"'{c}'")) + ")";

    private async Task<string> QuerySingleAsync(string sql)
    {
        var rows = await QueryAsync(sql);
        Assert.Single(rows);
        return rows[0];
    }

    // ==================================================================
    // 1. PRECISION AT REST — the six invoice money columns are decimal(18,2)
    //    in the live schema produced by the migration chain.
    // ==================================================================
    [Theory]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    [InlineData("Invoices", "Subtotal")]
    [InlineData("Invoices", "DiscountAmount")]
    [InlineData("Invoices", "TaxAmount")]
    [InlineData("Invoices", "TotalAmount")]
    [InlineData("InvoiceLines", "UnitPrice")]
    [InlineData("InvoiceLines", "LineTotal")]
    public async Task InvoiceMoneyColumns_AreDecimal18_2_InLiveSchema(string table, string column)
    {
        var observed = await QueryAsync(ColumnPrecisionSql(table, column));

        Assert.Single(observed);
        Assert.Equal("18,2", observed[0]);
    }

    // ==================================================================
    // 2. THE PRECISION ALTERATION IS SAFE — the altered columns are not bound by
    //    defaults, CHECK constraints, computed definitions or indexes. This is the
    //    claim the migration's data-safety comment rests on; here it is measured.
    // ==================================================================
    [Theory]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    [InlineData("Invoices", "Subtotal", "DiscountAmount", "TaxAmount", "TotalAmount")]
    [InlineData("InvoiceLines", "UnitPrice", "LineTotal")]
    public async Task InvoiceMoneyColumns_AreUnconstrainedAndNotIndexBacked(
        string table, params string[] columns)
    {
        // Sanity first: the columns really exist as ordinary columns, otherwise every
        // zero below would be vacuously true.
        Assert.Equal(columns.Length.ToString(),
            await QuerySingleAsync(RegularColumnCountSql(table, columns)));

        Assert.Equal("0", await QuerySingleAsync(ComputedSql(table, columns)));
        Assert.Equal("0", await QuerySingleAsync(DefaultsSql(table, columns)));
        Assert.Equal("0", await QuerySingleAsync(ChecksSql(table, columns)));
        Assert.Equal("0", await QuerySingleAsync(IndexParticipationSql(table, columns)));
    }

    // ==================================================================
    // 3. INTEGRITY — the one-invoice-per-cycle rule is a real filtered unique index.
    // ==================================================================
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task Invoices_DeclareFilteredUniqueIndex_OnBillingCycleId()
    {
        var rows = await QueryAsync(
            "SELECT CONCAT(i.name, '|', CASE WHEN i.is_unique = 1 THEN 'UNIQUE' ELSE 'NOT UNIQUE' END, " +
            "'|', ISNULL(i.filter_definition, '')) " +
            "FROM sys.indexes i " +
            "JOIN sys.tables t ON i.object_id = t.object_id " +
            "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id " +
            "WHERE s.name = 'Platform' AND t.name = 'Invoices' AND c.name = 'BillingCycleId' AND i.is_unique = 1");

        var index = Assert.Single(rows);
        var parts = index.Split('|');

        Assert.Equal("UX_Invoices_BillingCycleId", parts[0]);
        Assert.Equal("UNIQUE", parts[1]);
        Assert.Contains("IS NOT NULL", parts[2], StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // 4. INTEGRITY — the invoice points at real commercial facts, enforced by FKs.
    // ==================================================================
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task Invoices_DeclareForeignKeys_ToContractsSubscriptionsAndBillingCycles()
    {
        var rows = await QueryAsync(
            "SELECT CONCAT(fk.name, '->', rp.name) " +
            "FROM sys.foreign_keys fk " +
            "JOIN sys.tables t ON fk.parent_object_id = t.object_id " +
            "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "JOIN sys.tables rp ON fk.referenced_object_id = rp.object_id " +
            "WHERE s.name = 'Platform' AND t.name = 'Invoices'");

        Assert.Contains("FK_Invoices_Contracts_ContractId->Contracts", rows);
        Assert.Contains("FK_Invoices_TenantPlans_SubscriptionId->TenantPlans", rows);
        Assert.Contains("FK_Invoices_BillingCycles_BillingCycleId->BillingCycles", rows);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task TwoInvoicesForTheSameBillingCycle_AreRejectedByTheEngine()
    {
        var tenantId = $"t21-dup-{Guid.NewGuid():N}"[..20];
        var chain = await SeedChainAsync(tenantId, monthlyPrice: 1_000m);

        var error = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.Invoices.Add(NewInvoice(period: chain.Period, subscriptionId: chain.SubscriptionId,
                billingCycleId: chain.CycleId, suffix: "A"));
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            db.Invoices.Add(NewInvoice(period: chain.Period, subscriptionId: chain.SubscriptionId,
                billingCycleId: chain.CycleId, suffix: "B"));
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        });

        var sqlError = Assert.IsType<SqlException>(error.GetBaseException());
        Assert.True(
            sqlError.Number is 2601 or 2627,
            DescribeSqlError(sqlError, "Expected a unique-index violation (2601/2627) from UX_Invoices_BillingCycleId"));
        Assert.Contains("UX_Invoices_BillingCycleId", sqlError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task InvoicePointingAtANonExistentContract_IsRejectedByTheEngine()
    {
        var tenantId = $"t21-fk-{Guid.NewGuid():N}"[..20];
        var chain = await SeedChainAsync(tenantId, monthlyPrice: 1_000m);

        var error = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.Invoices.Add(NewInvoice(
                period: chain.Period,
                subscriptionId: chain.SubscriptionId,
                billingCycleId: null,
                suffix: "X",
                contractId: Guid.NewGuid())); // never existed — must not be billable

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        });

        var sqlError = Assert.IsType<SqlException>(error.GetBaseException());
        Assert.True(
            sqlError.Number == 547,
            DescribeSqlError(sqlError, "Expected a foreign-key conflict (547) from FK_Invoices_Contracts_ContractId"));
        Assert.Contains("FK_Invoices_Contracts_ContractId", sqlError.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // 5. PRECISION AT REST, END TO END — a full-term contract worth almost 1.2 BILLION,
    //    i.e. twelve times the ceiling the invoice columns used to have, is invoiced by the
    //    real handler, written to SQL Server, and read back digit-for-digit identical.
    // ==================================================================
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task FullTermInvoiceAboveTheLegacyTenTwoCeiling_RoundTripsThroughSqlServer_Unchanged()
    {
        var tenantId = $"t21-prec-{Guid.NewGuid():N}"[..20];

        // 99,999,999.99 is the most the ORIGIN column (Plans.MonthlyPrice, still decimal(10,2))
        // can carry; twelve of them are 1,199,999,999.88, which the pre-Task-21 invoice columns
        // could not have stored at all. See the origin-ceiling test below for that boundary.
        const decimal monthlyPrice = 99_999_999.99m;
        const decimal expectedTotal = 1_199_999_999.88m;

        var chain = await SeedChainAsync(tenantId, monthlyPrice: monthlyPrice, durationMonths: 12);

        // The premise of the whole task: this contract simply could not be invoiced before.
        Assert.Equal(expectedTotal, chain.ContractedAmount);
        Assert.True(chain.ContractedAmount > LegacyDecimal10Max);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await EnsureTenantExistsAsync(sp, tenantId);
            AuthorizeTenant(sp, tenantId);
            var db = sp.GetRequiredService<AppDbContext>();

            var handler = new CreateInvoiceFromBillingCycleHandler(
                db, sp.GetRequiredService<ICurrentTenant>(), FrozenClock(Now2026));

            var result = await handler.Handle(
                new CreateInvoiceFromBillingCycleCommand(chain.CycleId), CancellationToken.None);

            Assert.True(result.IsSuccess, Describe(result.Errors));
        }

        // Fresh scope, fresh DbContext: what follows can only have come from disk.
        using (var reload = _env.Factory.Services.CreateScope())
        {
            var db = reload.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(reload.ServiceProvider, tenantId);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .SingleAsync(i => i.BillingCycleId == chain.CycleId);

            var contractTotal = await db.Contracts
                .IgnoreQueryFilters()
                .Where(c => c.Id == chain.ContractId)
                .Select(c => c.ContractedAmount)
                .SingleAsync();

            Assert.Equal(expectedTotal, invoice.TotalAmount);
            // The cycle spans the whole contract, so this one cycle bills the entire term:
            // subtotal = contract total, not the monthly figure.
            Assert.Equal(expectedTotal, invoice.Subtotal);
            Assert.Equal(0.00m, invoice.DiscountAmount);
            Assert.Equal(0.00m, invoice.TaxAmount);
            Assert.Equal(0m, Math.Abs(invoice.TotalAmount
                - (invoice.Subtotal - invoice.DiscountAmount + invoice.TaxAmount)));

            // Invoice lines are created separately via AddInvoiceLineCommand, not by the
            // CreateInvoiceFromBillingCycleHandler. The primary invariant being tested here
            // is that Invoice.TotalAmount == Contract.ContractedAmount at database precision.
            // InvoiceLine precision is tested separately in InvoiceMoneyColumns_AreDecimal18_2_InLiveSchema.

            // The commercial invariant, at rest: the invoice total IS the contracted amount.
            Assert.Equal(contractTotal, invoice.TotalAmount);
        }
    }

    // ==================================================================
    // 6. THE PRECISION PROFILE OF THE ENTIRE COMMERCIAL CHAIN, as the live schema holds it.
    //    Pinned column by column: a silent narrowing anywhere in the chain, or a widening
    //    that quietly changes what a value means, fails here instead of in production.
    // ==================================================================
    [Theory]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    [InlineData("Plans", "MonthlyPrice", "10,2")]                 // origin — see the ceiling test below
    [InlineData("TenantPlans", "SnapshotPrice", "18,6")]          // snapshot keeps 6 dp; rounding happens at derivation
    [InlineData("TenantPlans", "SnapshotMonthlyCharge", "18,6")]
    [InlineData("Contracts", "MonthlyListPrice", "18,2")]
    [InlineData("Contracts", "ContractualMonthlyValue", "18,2")]
    [InlineData("Contracts", "GrossAmount", "18,2")]
    [InlineData("Contracts", "ContractedAmount", "18,2")]
    [InlineData("Contracts", "DiscountAmount", "18,2")]
    public async Task CommercialChain_MoneyColumns_MatchTheDocumentedPrecisionProfile(
        string table, string column, string expected)
    {
        Assert.Equal(expected, await QuerySingleAsync(ColumnPrecisionSql(table, column)));
    }

    // ==================================================================
    // 7. THE REMAINING CEILING SITS AT THE ORIGIN, NOT IN THE INVOICE CHAIN.
    //    A 100,000,000.00 / month plan (1.2B / year) cannot be created at all, because
    //    Plans.MonthlyPrice is still decimal(10,2). The invoice side no longer limits
    //    anything; the plan catalogue does. Recorded as a pinned fact rather than a wish.
    // ==================================================================
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task21Closure")]
    public async Task PlanMonthlyPrice_IsTheRemainingOriginCeiling_NotTheInvoiceChain()
    {
        Assert.Equal("10,2", await QuerySingleAsync(ColumnPrecisionSql("Plans", "MonthlyPrice")));

        var tenantId = $"t21-origin-{Guid.NewGuid():N}"[..20];

        using var scope = _env.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await EnsureTenantExistsAsync(sp, tenantId);
        AuthorizeTenant(sp, tenantId);
        var db = sp.GetRequiredService<AppDbContext>();

        var planResult = Plan.Create(
            id: 0, code: $"P21O{Guid.NewGuid().ToString("N")[..10]}", displayName: "TASK 21 origin ceiling",
            monthlyPrice: LegacyDecimal10Max + 0.01m, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: true,
            description: "One cent above what decimal(10,2) can hold", currencyCode: "EGP",
            durationMonths: 12, bonusMonths: 0);

        // The DOMAIN accepts it — only the column refuses. That gap is the finding.
        Assert.True(planResult.IsSuccess, Describe(planResult.Errors));
        db.Plans.Add(planResult.Value);
        db.StampAddedTenantIds(tenantId);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var outOfRange = Assert.IsType<ArgumentException>(error.GetBaseException());
        Assert.Contains("100000000.00", outOfRange.Message, StringComparison.Ordinal);
    }

    // ==================================================================
    // Seeding — the authoritative commercial chain on real SQL Server
    // ==================================================================

    private sealed record Chain(
        Guid ContractId, Guid SubscriptionId, Guid CycleId, DateOnly Period, decimal ContractedAmount);

    private static string? Describe(IReadOnlyCollection<Error>? errors)
        => errors is null || errors.Count == 0
            ? null
            : string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));

    /// <summary>
    /// Plan → CalculatedOffer → Contract → Subscription snapshot → BillingCycle whose period IS
    /// the full paid term [StartsAtUtc, BaseEndsAtUtc], so the handler takes the identity path.
    /// Nothing is hand-written into the invoice: only commercial facts are seeded.
    /// </summary>
    private async Task<Chain> SeedChainAsync(string tenantId, decimal monthlyPrice, int durationMonths = 12)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await EnsureTenantExistsAsync(sp, tenantId);
        AuthorizeTenant(sp, tenantId);
        var db = sp.GetRequiredService<AppDbContext>();

        var planResult = Plan.Create(
            id: 0, code: $"P21{Guid.NewGuid().ToString("N")[..10]}", displayName: "TASK 21 closure plan",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 50, maxBranches: 10, maxTeachers: 20,
            storageGB: 100, smsQuota: 1000, isActive: true, description: "TASK 21 closure plan", currencyCode: "EGP",
            durationMonths: durationMonths, bonusMonths: 0);
        Assert.True(planResult.IsSuccess, Describe(planResult.Errors));
        var plan = planResult.Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var calculation = new PromotionCalculationService()
            .Calculate(plan, durationMonths, Start2026, Array.Empty<Promotion>());
        Assert.True(calculation.IsSuccess, Describe(calculation.Errors));
        var offer = calculation.Value;

        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(Start2026, durationMonths, bonusMonths: 0);

        var contractResult = Contract.Create(
            id: Guid.NewGuid(), tenantId: tenantId,
            contractNumber: $"CTR-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            planId: plan.Id, effectiveAtUtc: Start2026, endsAtUtc: endsAt, durationMonths: durationMonths,
            monthlyListPrice: offer.MonthlyListPrice, contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode, grossAmount: offer.BaseAmount, contractedAmount: offer.FinalAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            paymentTerms: PaymentTerms.Installments,
            discountAmount: offer.DiscountAmount, promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId, promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths, bonusMonths: 0,
            maxStudents: plan.MaxStudents, maxUsers: plan.MaxUsers, maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers, storageGb: plan.StorageGB, smsQuota: plan.SMSQuota);
        Assert.True(contractResult.IsSuccess, Describe(contractResult.Errors));
        var contract = contractResult.Value;

        Assert.True(contract.SubmitForApproval().IsSuccess);
        Assert.True(contract.Activate(Start2026).IsSuccess);
        db.Contracts.Add(contract);

        var snapshotResult = await new SubscriptionFactory(db).CreateFromSnapshotAsync(
            tenantId, plan.Id, contract.GetSubscriptionSnapshot(), Start2026,
            autoRenew: false, activate: true, CancellationToken.None);
        Assert.True(snapshotResult.IsSuccess, Describe(snapshotResult.Errors));
        var subscription = snapshotResult.Value;
        Assert.True(subscription.LinkToContract(contract.Id).IsSuccess);
        db.TenantPlans.Add(subscription);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var cycleResult = BillingCycle.Create(Guid.NewGuid(), tenantId, subscription.Id, Start2026, endsAt);
        Assert.True(cycleResult.IsSuccess, Describe(cycleResult.Errors));
        db.BillingCycles.Add(cycleResult.Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return new Chain(
            contract.Id, subscription.Id, cycleResult.Value.Id,
            DateOnly.FromDateTime(Start2026), contract.ContractedAmount);
    }

    private static Invoice NewInvoice(
        DateOnly period, Guid subscriptionId, Guid? billingCycleId, string suffix, Guid? contractId = null)
    {
        var created = Invoice.Create(
            id: Guid.NewGuid(),
            invoiceNumber: $"INV21{suffix}{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            periodStart: period,
            periodEnd: period.AddMonths(1),
            subtotal: 1_000m,
            discountAmount: 0m,
            taxAmount: 0m,
            totalAmount: 1_000m,
            contractId: contractId,
            subscriptionId: subscriptionId,
            billingCycleId: billingCycleId);

        Assert.True(created.IsSuccess, Describe(created.Errors));
        return created.Value;
    }
}
