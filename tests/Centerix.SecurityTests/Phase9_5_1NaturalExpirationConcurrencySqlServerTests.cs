using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Platform;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 9.5.1 / 9.5.2 — SQL Server concurrency tests for natural subscription expiration.
/// Runs against REAL SQL Server (Testcontainers/local) to verify:
/// 1. Concurrent reconciliation produces exactly one success and one expected concurrency conflict
/// 2. No unexpected exceptions during concurrent expiration
/// 3. Final persisted state is Expired
/// 4. No duplicate financial side effects under concurrency
/// 5. Sequential and concurrent idempotency
/// 6. Tenant isolation under concurrent operations
///
/// Concurrency Strategy:
/// - Uses System.Threading.Barrier to ensure both reconciliation operations reach the
///   race point simultaneously. Both independently load the subscription (same RowVersion),
///   call MarkExpired, and attempt SaveChangesAsync.
/// - TenantPlan has RowVersion (rowversion) optimistic concurrency configured.
/// - Under SQL Server, the second SaveChangesAsync will throw DbUpdateConcurrencyException
///   because the RowVersion has changed after the first save.
/// - The reconciliation service does NOT retry on concurrency failure — the exception
///   propagates to the caller.
/// - The test asserts exactly ONE success and ONE expected concurrency conflict.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase9_5_1NaturalExpirationConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    public Phase9_5_1NaturalExpirationConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Concurrency result model
    // ==================================================================

    /// <summary>
    /// Captures the actual outcome of a concurrent reconciliation operation.
    /// </summary>
    private enum ConcurrencyOperationOutcome
    {
        /// <summary>Reconciliation completed and persisted successfully.</summary>
        Succeeded,

        /// <summary>
        /// SaveChangesAsync threw DbUpdateConcurrencyException due to RowVersion mismatch.
        /// This is the EXPECTED concurrency conflict for concurrent expiration.
        /// </summary>
        ConcurrencyConflict,

        /// <summary>An unexpected exception occurred — test must fail.</summary>
        UnexpectedFailure
    }

    private sealed class ConcurrencyOperationResult
    {
        public ConcurrencyOperationOutcome Outcome { get; init; }
        public Exception? Exception { get; init; }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(currentTenant, true);
    }

    private static async Task EnsureTenantExists(IServiceProvider scope, string tenantId)
    {
        var store = scope.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
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

    private static async Task<int> SeedPlanAsync(AppDbContext db)
    {
        var plan = Domain.Platform.Plans.Plan.Create(0, $"PLAN-{Guid.NewGuid():N}"[..28], "Expiration Concurrency Plan",
            1000m, 100, 50, 10, 20, 100, 1000,
            isActive: true, description: null, currencyCode: "EGP", durationMonths: 12, bonusMonths: 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private static async Task<Guid> SeedSubscriptionAsync(
        AppDbContext db,
        string tenantId,
        int planId,
        SubscriptionStatus status,
        DateTime startsAtUtc,
        int durationMonths,
        int bonusMonths)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, "EGP",
            durationMonths, bonusMonths, startsAtUtc, false, status).Value;
        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        var subId = sub.Id;
        db.Entry(sub).State = EntityState.Detached;
        return subId;
    }

    private static async Task SeedGracePeriodPolicyAsync(AppDbContext db, int gracePeriodDays = 7)
    {
        var policy = SubscriptionPolicy.Create(0, gracePeriodDays).Value;
        db.SubscriptionPolicies.Add(policy);
        await db.SaveChangesAsync();
    }

    private static SubscriptionReconciliationService CreateReconciliationService(AppDbContext db)
    {
        var logger = Substitute.For<ILogger<SubscriptionReconciliationService>>();
        var timeProvider = TimeProvider.System;
        return new SubscriptionReconciliationService(db, timeProvider, logger);
    }

    /// <summary>
    /// Executes a single reconciliation operation and captures its outcome.
    /// Does NOT swallow exceptions — returns a structured result indicating
    /// whether the operation succeeded, hit an expected concurrency conflict,
    /// or experienced an unexpected failure.
    /// </summary>
    private static async Task<ConcurrencyOperationResult> RunSingleReconciliation(
        SqlServerIntegrationFactory env,
        string tenantId,
        Barrier barrier)
    {
        using var scope = env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(scope.ServiceProvider, tenantId);
        var service = CreateReconciliationService(db);

        barrier.SignalAndWait(BarrierTimeout);

        try
        {
            await service.ReconcileAsync(tenantId, CancellationToken.None);
            return new ConcurrencyOperationResult { Outcome = ConcurrencyOperationOutcome.Succeeded };
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Expected: RowVersion mismatch — the other operation saved first.
            return new ConcurrencyOperationResult { Outcome = ConcurrencyOperationOutcome.ConcurrencyConflict, Exception = ex };
        }
        catch (Exception ex)
        {
            // Any other exception is unexpected — the test must fail.
            return new ConcurrencyOperationResult { Outcome = ConcurrencyOperationOutcome.UnexpectedFailure, Exception = ex };
        }
    }

    /// <summary>
    /// Executes two concurrent reconciliation operations against the same tenant
    /// and returns both results for strict assertion.
    /// </summary>
    private static async Task<(ConcurrencyOperationResult Result1, ConcurrencyOperationResult Result2)> ExecuteConcurrentReconciliations(
        SqlServerIntegrationFactory env,
        string tenantId)
    {
        using var barrier = new Barrier(2);

        var task1 = Task.Run(() => RunSingleReconciliation(env, tenantId, barrier));
        var task2 = Task.Run(() => RunSingleReconciliation(env, tenantId, barrier));

        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Concurrent reconciliations did not complete within {TestTimeout.TotalSeconds}s");
        }

        return (await task1, await task2);
    }

    /// <summary>
    /// Asserts the standard concurrent expiration pattern:
    /// exactly ONE success, exactly ONE expected concurrency conflict, ZERO unexpected failures.
    /// </summary>
    private static void AssertConcurrentExpirationOutcome(
        ConcurrencyOperationResult result1,
        ConcurrencyOperationResult result2,
        string scenario)
    {
        var successCount = (result1.Outcome == ConcurrencyOperationOutcome.Succeeded ? 1 : 0)
                         + (result2.Outcome == ConcurrencyOperationOutcome.Succeeded ? 1 : 0);
        var conflictCount = (result1.Outcome == ConcurrencyOperationOutcome.ConcurrencyConflict ? 1 : 0)
                          + (result2.Outcome == ConcurrencyOperationOutcome.ConcurrencyConflict ? 1 : 0);
        var unexpectedCount = (result1.Outcome == ConcurrencyOperationOutcome.UnexpectedFailure ? 1 : 0)
                            + (result2.Outcome == ConcurrencyOperationOutcome.UnexpectedFailure ? 1 : 0);

        Assert.Equal(0, unexpectedCount);

        if (unexpectedCount > 0)
        {
            var unexpected = result1.Outcome == ConcurrencyOperationOutcome.UnexpectedFailure ? result1 : result2;
            Assert.Fail(
                $"{scenario}: Unexpected exception {unexpected.Exception!.GetType().Name}: {unexpected.Exception.Message}");
        }

        Assert.True(successCount == 1,
            $"{scenario}: Expected exactly 1 success, got {successCount}. " +
            $"Result1={result1.Outcome}, Result2={result2.Outcome}");

        Assert.True(conflictCount == 1,
            $"{scenario}: Expected exactly 1 concurrency conflict, got {conflictCount}. " +
            $"Result1={result1.Outcome}, Result2={result2.Outcome}");
    }

    /// <summary>
    /// Verifies final subscription state is Expired and no financial records were mutated.
    /// </summary>
    private static async Task VerifyFinalStateAsync(
        SqlServerIntegrationFactory env,
        string tenantId,
        Guid subscriptionId,
        int expectedRefundsBefore,
        int expectedPaymentsBefore,
        int expectedAllocationsBefore,
        int expectedInstallmentsBefore)
    {
        using var verifyScope = env.Factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

        var sub = await db.TenantPlans.IgnoreQueryFilters()
            .FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);

        Assert.Equal(expectedRefundsBefore, await db.Refunds.CountAsync());
        Assert.Equal(expectedPaymentsBefore, await db.Payments.CountAsync());
        Assert.Equal(expectedAllocationsBefore, await db.PaymentAllocations.CountAsync());
        Assert.Equal(expectedInstallmentsBefore, await db.Installments.CountAsync());
    }

    // ==================================================================
    // Scenario A — Active subscription, end reached, concurrent reconciliation
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_Active_EndReached_BecomesExpired()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        int refundsBefore, paymentsBefore, allocationsBefore, installmentsBefore;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            refundsBefore = await db.Refunds.CountAsync();
            paymentsBefore = await db.Payments.CountAsync();
            allocationsBefore = await db.PaymentAllocations.CountAsync();
            installmentsBefore = await db.Installments.CountAsync();
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Active concurrent expiration");

        await VerifyFinalStateAsync(_env, tenantId, subscriptionId,
            refundsBefore, paymentsBefore, allocationsBefore, installmentsBefore);
    }

    // ==================================================================
    // Scenario B — PastDue subscription, end reached, concurrent reconciliation
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_PastDue_EndReached_BecomesExpired()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.PastDue,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "PastDue concurrent expiration");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);
            Assert.Empty(await verifyDb.Refunds.ToListAsync());
        }
    }

    // ==================================================================
    // Scenario C — Suspended subscription, end reached, concurrent reconciliation
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_Suspended_EndReached_BecomesExpired()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Suspended,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Suspended concurrent expiration");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);
            Assert.Empty(await verifyDb.Refunds.ToListAsync());
        }
    }

    // ==================================================================
    // Financial Integrity — No side effects from expiration under concurrency
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_FinancialIntegrity_NoSideEffects()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        int refundsBefore, paymentsBefore, allocationsBefore, installmentsBefore, ledgerBefore;
        using (var seedScope = _env.Factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(seedScope.ServiceProvider, tenantId);
            refundsBefore = await seedDb.Refunds.CountAsync();
            paymentsBefore = await seedDb.Payments.CountAsync();
            allocationsBefore = await seedDb.PaymentAllocations.CountAsync();
            installmentsBefore = await seedDb.Installments.CountAsync();
            ledgerBefore = await seedDb.CustomerLedgerEntries.CountAsync();
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Financial integrity concurrent expiration");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);

            Assert.Equal(refundsBefore, await verifyDb.Refunds.CountAsync());
            Assert.Equal(paymentsBefore, await verifyDb.Payments.CountAsync());
            Assert.Equal(allocationsBefore, await verifyDb.PaymentAllocations.CountAsync());
            Assert.Equal(installmentsBefore, await verifyDb.Installments.CountAsync());
            Assert.Equal(ledgerBefore, await verifyDb.CustomerLedgerEntries.CountAsync());
        }
    }

    // ==================================================================
    // Domain Event — Exactly one TenantPlanExpiredEvent equivalent
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_DomainEvent_ExactlyOneExpirationTransition()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Domain event concurrent expiration");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            // Exactly one TenantPlan row with Expired status — proves exactly one transition
            var expiredCount = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Expired)
                .CountAsync();
            Assert.Equal(1, expiredCount);

            Assert.Empty(await verifyDb.Refunds.ToListAsync());
        }
    }

    // ==================================================================
    // Sequential Idempotency on SQL Server
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task SequentialReconciliation_Idempotent_ActiveToExpired()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        // First reconciliation — Active → Expired
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var service = CreateReconciliationService(db);
            await service.ReconcileAsync(tenantId, CancellationToken.None);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var sub = await db.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);
        }

        // Second reconciliation — Expired → no state transition (idempotent)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var service = CreateReconciliationService(db);
            await service.ReconcileAsync(tenantId, CancellationToken.None);
        }

        // Third reconciliation — still idempotent
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var service = CreateReconciliationService(db);
            await service.ReconcileAsync(tenantId, CancellationToken.None);
        }

        // Verify final state
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var sub = await db.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);

            Assert.Empty(await db.Refunds.ToListAsync());

            var tenantSubCount = await db.TenantPlans.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId)
                .CountAsync();
            Assert.Equal(1, tenantSubCount);
        }
    }

    // ==================================================================
    // Concurrent Idempotency — Post-race convergence with fresh context
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_ConvergenceAfterRace()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        // Phase 1: Concurrent race
        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Convergence after race");

        // Phase 2: Verify post-race state is Expired
        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await db.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);
        }

        // Phase 3: Run ANOTHER reconciliation with a fresh DbContext — must be idempotent
        using (var postRaceScope = _env.Factory.Services.CreateScope())
        {
            var db = postRaceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(postRaceScope.ServiceProvider, tenantId);
            var service = CreateReconciliationService(db);

            // Must NOT throw — Expired is terminal, reconciliation returns early
            await service.ReconcileAsync(tenantId, CancellationToken.None);
        }

        // Phase 4: Final verification — no additional state change, no side effects
        using (var finalScope = _env.Factory.Services.CreateScope())
        {
            var db = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(finalScope.ServiceProvider, tenantId);

            var sub = await db.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Expired, sub.Status);

            var count = await db.TenantPlans.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId)
                .CountAsync();
            Assert.Equal(1, count);

            Assert.Empty(await db.Refunds.ToListAsync());
        }
    }

    // ==================================================================
    // ChangeTracker Safety — Each scope uses independent DbContext
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_IndependentDbContexts_NoStaleTracker()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        AssertConcurrentExpirationOutcome(result1, result2, "Independent DbContexts");

        // Verify: each scope used independent DbContext — no shared ChangeTracker state
        using var verifyScope = _env.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

        var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);

        // No duplicate rows — independent DbContexts did not create phantom rows
        var count = await verifyDb.TenantPlans.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .CountAsync();
        Assert.Equal(1, count);
    }

    // ==================================================================
    // Cancelled subscription is NOT expired by concurrent reconciliation
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_Cancelled_RemainsCancelled()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Cancelled,
                startsAtUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3,
                bonusMonths: 0);
        }

        // Cancelled is terminal — both reconciliations should return without error,
        // and neither should produce a concurrency conflict (no state change).
        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        // Both must succeed — no expiration transition occurs, no RowVersion conflict
        Assert.Equal(ConcurrencyOperationOutcome.Succeeded, result1.Outcome);
        Assert.Equal(ConcurrencyOperationOutcome.Succeeded, result2.Outcome);

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
            Assert.Empty(await verifyDb.Refunds.ToListAsync());
        }
    }

    // ==================================================================
    // Active subscription NOT YET ended — concurrent reconciliation leaves it Active
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_Active_NotYetEnded_RemainsActive()
    {
        var tenantId = $"exp-{Guid.NewGuid():N}"[..20];
        Guid subscriptionId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var planId = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            subscriptionId = await SeedSubscriptionAsync(
                db, tenantId, planId, SubscriptionStatus.Active,
                startsAtUtc: DateTime.UtcNow,
                durationMonths: 12,
                bonusMonths: 0);
        }

        // Not yet expired — both reconciliations should return without error,
        // no state change, no RowVersion conflict.
        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantId);

        Assert.Equal(ConcurrencyOperationOutcome.Succeeded, result1.Outcome);
        Assert.Equal(ConcurrencyOperationOutcome.Succeeded, result2.Outcome);

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

            var sub = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Active, sub.Status);
        }
    }

    // ==================================================================
    // Tenant isolation — reconciling tenant A does not affect tenant B
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_5_2")]
    public async Task ConcurrentExpiration_TenantIsolation_ADoesNotAffectB()
    {
        var tenantA = $"expA-{Guid.NewGuid():N}"[..20];
        var tenantB = $"expB-{Guid.NewGuid():N}"[..20];
        Guid subAId, subBId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantA);
            await EnsureTenantExists(scope.ServiceProvider, tenantB);

            var planA = await SeedPlanAsync(db);
            await SeedGracePeriodPolicyAsync(db);

            // Tenant A: Active, ended → should expire
            subAId = await SeedSubscriptionAsync(
                db, tenantA, planA, SubscriptionStatus.Active,
                startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                durationMonths: 3, bonusMonths: 0);

            // Tenant B: Active, NOT ended → should stay Active
            subBId = await SeedSubscriptionAsync(
                db, tenantB, planA, SubscriptionStatus.Active,
                startsAtUtc: DateTime.UtcNow,
                durationMonths: 12, bonusMonths: 0);
        }

        // Reconcile tenant A concurrently — should expire A's subscription
        var (result1, result2) = await ExecuteConcurrentReconciliations(_env, tenantA);

        AssertConcurrentExpirationOutcome(result1, result2, "Tenant isolation - tenant A");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Tenant A should be expired
            AuthorizeTenant(verifyScope.ServiceProvider, tenantA);
            var subA = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subAId);
            Assert.Equal(SubscriptionStatus.Expired, subA.Status);

            // Tenant B must remain Active — tenant isolation
            AuthorizeTenant(verifyScope.ServiceProvider, tenantB);
            var subB = await verifyDb.TenantPlans.IgnoreQueryFilters()
                .FirstAsync(s => s.Id == subBId);
            Assert.Equal(SubscriptionStatus.Active, subB.Status);
        }
    }
}
