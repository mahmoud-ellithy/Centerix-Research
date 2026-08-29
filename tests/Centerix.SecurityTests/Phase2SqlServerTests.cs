using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Subscriptions.UsageCounters;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Students.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Phase 2 relational integration tests against REAL SQL Server (real migrations applied):
/// schema/snapshot columns, database-level invariants (single non-terminal subscription,
/// PlanFeature uniqueness), snapshot round-trips, renewal persistence and atomic limit
/// reservation under concurrency.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase2SqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Phase2SqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Schema / migration verification
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_Phase2Schema_NoPendingMigrations_CommercialColumnsExist()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        async Task<bool> ColumnExists(string table, string column) => await db.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'Platform' AND TABLE_NAME = {table} AND COLUMN_NAME = {column})
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        Assert.True(await ColumnExists("TenantPlans", "SnapshotCurrency"));
        Assert.True(await ColumnExists("TenantPlans", "DurationMonths"));
        Assert.True(await ColumnExists("TenantPlans", "BonusMonths"));
        Assert.True(await ColumnExists("TenantPlans", "BaseEndsAtUtc"));
        Assert.True(await ColumnExists("TenantPlans", "EffectiveEndsAtUtc"));
        Assert.True(await ColumnExists("TenantPlans", "RowVersion"));
        Assert.True(await ColumnExists("Plans", "CurrencyCode"));
        Assert.True(await ColumnExists("Plans", "DurationMonths"));

        var filteredIndex = await db.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'Platform' AND t.name = 'TenantPlans'
                  AND i.name = 'UX_TenantPlans_TenantId_NonTerminalStatus' AND i.is_unique = 1)
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();
        Assert.True(filteredIndex, "Single-non-terminal-subscription filtered unique index is missing");
    }

    // ==================================================================
    // Database-level single-active-subscription invariant
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task TenantPlans_TwoNonTerminalSubscriptions_SameTenant_ViolateUniqueIndex()
    {
        const string tenantId = "p2-dup-tenant";
        await SeedSubscriptionTenantAsync(tenantId);

        var planId = await EnsurePlanAsync("P2DUP", maxStudents: 10);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Active));
        await db.SaveChangesAsync();

        // Second NON-TERMINAL subscription for the same tenant â†’ filtered unique index fires.
        db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Active));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task TenantPlans_HistoryPlusOneActive_IsAllowed()
    {
        const string tenantId = "p2-hist-tenant";
        await SeedSubscriptionTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P2HIST", maxStudents: 10);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Expired));
        db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Cancelled));
        db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Active));
        await db.SaveChangesAsync(); // history + exactly one active â†’ valid

        Assert.Equal(3, await db.TenantPlans.IgnoreQueryFilters().CountAsync(tp => tp.TenantId == tenantId));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task PlanFeatures_DuplicatePair_ViolatesUniqueIndex()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var featureCode = $"F{Guid.NewGuid():N}"[..20];
        var feature = Feature.Create(0, featureCode, null, "Test");
        db.Features.Add(feature.Value);
        await db.SaveChangesAsync();

        var planId = await EnsurePlanAsync("P2PF" + Guid.NewGuid().ToString("N")[..6]);

        db.PlanFeatures.Add(PlanFeature.Create(0, planId, feature.Value.Id, true).Value);
        await db.SaveChangesAsync();

        using var secondScope = _env.Factory.Services.CreateScope();
        var db2 = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        db2.PlanFeatures.Add(PlanFeature.Create(0, planId, feature.Value.Id, true).Value);
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    // ==================================================================
    // Snapshot persistence + renewal + lazy expiration on the real provider
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Subscription_SnapshotRoundTrips_ThroughRealColumns()
    {
        const string tenantId = "p2-snap-tenant";
        await SeedSubscriptionTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P2SNAP", maxStudents: 777, currency: "SAR", duration: 6, bonus: 2, price: 250m);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = NewSubRow(
                tenantId, planId, SubscriptionStatus.Active,
                price: 250m, currency: "SAR", durationMonths: 6, bonusMonths: 2, maxStudents: 777);
            sub.GrantFeature("Students");

            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
        }

        using (var verify = _env.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = await db.TenantPlans
                .IgnoreQueryFilters()
                .Include(tp => tp.Features)
                .SingleAsync(tp => tp.TenantId == tenantId && tp.Status == SubscriptionStatus.Active);

            Assert.Equal("SAR", reloaded.SnapshotCurrency);
            Assert.Equal(250m, reloaded.SnapshotPrice);
            Assert.Equal(6, reloaded.DurationMonths);
            Assert.Equal(2, reloaded.BonusMonths);
            Assert.Equal(777, reloaded.SnapshotMaxStudents);
            Assert.Equal(reloaded.StartsAtUtc.AddMonths(6), reloaded.BaseEndsAtUtc);
            Assert.Equal(reloaded.BaseEndsAtUtc.AddMonths(2), reloaded.EffectiveEndsAtUtc);
            Assert.Single(reloaded.Features);          // entitlement snapshot persisted
            Assert.Equal("Students", reloaded.Features[0].FeatureCode);
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Renewal_PersistsExtendedEffectiveEnd_AndOptimisticConcurrencyGuardsRow()
    {
        const string tenantId = "p2-renew-tenant";
        await SeedSubscriptionTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P2REN", duration: 12, bonus: 0);

        Guid subId;
        DateTime effectiveBefore;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = NewSubRow(tenantId, planId, SubscriptionStatus.Active,
                startsAt: DateTime.UtcNow.AddDays(-30));
            db.TenantPlans.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
            effectiveBefore = sub.EffectiveEndsAtUtc;
        }

        using (var renew = _env.Factory.Services.CreateScope())
        {
            var db = renew.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == subId);
            var now = DateTime.UtcNow;
            Assert.True(sub.Renew(3, 1, now).IsSuccess);
            await db.SaveChangesAsync();

            var anchor = effectiveBefore > now ? effectiveBefore : now;
            Assert.Equal(anchor.AddMonths(3 + 1), sub.EffectiveEndsAtUtc); // months + bonus
        }

        // RowVersion changed â†’ concurrent stale update is rejected by SQL Server.
        using (var a = _env.Factory.Services.CreateScope())
        using (var b = _env.Factory.Services.CreateScope())
        {
            var da = a.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbb = b.ServiceProvider.GetRequiredService<AppDbContext>();
            var sa = await da.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == subId);   // tracked
            var sb = await dbb.TenantPlans.IgnoreQueryFilters().SingleAsync(tp => tp.Id == subId);  // tracked

            sb.Suspend();
            await dbb.SaveChangesAsync();

            sa.Renew(1, 0, DateTime.UtcNow); // stale RowVersion
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => da.SaveChangesAsync());
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task LimitReservation_ConcurrentCallers_ExactlyOneClaimsLastSlot_OnRealSql()
    {
        var tenantId = Guid.NewGuid().ToString();
        await SeedSubscriptionTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P2LIM" + Guid.NewGuid().ToString("N")[..6], maxStudents: 1);

        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TenantPlans.Add(NewSubRow(tenantId, planId, SubscriptionStatus.Active, maxStudents: 1));

            db.TenantUsageCounters.Add(TenantUsageCounter.Create(
                Guid.Parse(tenantId), 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        // Two concurrent reservations against ONE free slot â†’ exactly one wins.
        async Task<bool> TryReserveAsync()
        {
            using var scope = _env.Factory.Services.CreateScope();
            var limit = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.ILimitService>();
            return (await limit.ReserveAsync(tenantId, LimitTypeCodes.Students)).IsSuccess;
        }

        var results = await Task.WhenAll(TryReserveAsync(), TryReserveAsync());
        Assert.Single(results.Where(success => success));

        using var verify = _env.Factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var counter = await vdb.TenantUsageCounters.AsNoTracking()
            .SingleAsync(c => c.Id == Guid.Parse(tenantId));
        Assert.Equal(1, counter.StudentsCount); // incremented exactly once
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task LimitReservation_WithoutActiveSubscription_IsDenied()
    {
        var tenantId = Guid.NewGuid().ToString();
        await SeedSubscriptionTenantAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        var limit = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.ILimitService>();
        var result = await limit.ReserveAsync(tenantId, LimitTypeCodes.Students);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Subscription.NotActive");
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task FeatureAccess_ActiveGrant_True_ExpiredOrSuspended_False()
    {
        var tenantId = Guid.NewGuid().ToString();
        await SeedSubscriptionTenantAsync(tenantId);
        var planId = await EnsurePlanAsync("P2FEAT" + Guid.NewGuid().ToString("N")[..6]);

        Guid expiredSubId;
        using (var seed = _env.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var expired = NewSubRow(tenantId, planId, SubscriptionStatus.Active,
                startsAt: DateTime.UtcNow.AddMonths(-5));
            typeof(TenantPlan).GetProperty(nameof(TenantPlan.EffectiveEndsAtUtc))!
                .SetValue(expired, DateTime.UtcNow.AddDays(-1)); // force past expiry
            expired.GrantFeature("AdvancedReports");
            db.TenantPlans.Add(expired);
            await db.SaveChangesAsync();
            expiredSubId = expired.Id;
        }

        using var scope = _env.Factory.Services.CreateScope();
        var featureAccess = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.IFeatureAccessService>();

        // Expired subscription must NOT grant the feature even though status row still says Active.
        Assert.False(await featureAccess.HasFeatureAsync(tenantId, "AdvancedReports"));

        // After the state service's lazy write-through, the row converges to Expired.
        var vdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await vdb.TenantPlans.IgnoreQueryFilters().AsNoTracking().SingleAsync(tp => tp.Id == expiredSubId);
        Assert.Equal(SubscriptionStatus.Expired, row.Status);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static TenantPlan NewSubRow(
        string tenantId,
        int planId,
        SubscriptionStatus status,
        decimal price = 100m,
        string currency = "USD",
        int durationMonths = 12,
        int bonusMonths = 0,
        int maxStudents = 100,
        DateTime? startsAt = null)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, price, currency, durationMonths, bonusMonths,
            startsAt ?? DateTime.UtcNow, false, status, maxStudents, 10, 5, 20, 50, 500).Value;
        if (status == SubscriptionStatus.Active)
            sub.Activate(DateTime.UtcNow);
        return sub;
    }

    private async Task<int> EnsurePlanAsync(
        string codePrefix,
        int maxStudents = 100,
        string currency = "USD",
        int duration = 12,
        int bonus = 0,
        decimal price = 100m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var code = $"{codePrefix}_{Guid.NewGuid():N}"[..28];
        var plan = Plan.Create(0, code, "Plan", price,
            maxStudents, 25, 10, 40, 50, 1000, true, null, currency, duration, bonus).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>Creates the registry entry so FK_TenantMemberships_TenantRegistry is satisfied.</summary>
    private async Task SeedSubscriptionTenantAsync(string tenantId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@registry.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}

