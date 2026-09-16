using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 9.4.2 — SQL Server concurrency and idempotency tests for subscription cancellation.
/// Runs against REAL SQL Server (Testcontainers/local) to verify:
/// 1. Concurrent cancellation with refund produces at most one Refund
/// 2. Concurrent cancellation with zero refund produces zero Refund records
/// 3. Concurrent cancellation with outstanding amount is consistent
/// 4. Unique constraint on Refund.SubscriptionId prevents duplicates at DB level
/// 5. Partially-paid installment cancellation works on SQL Server
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase9_4_2CancellationConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);
    private const int RaceIterations = 5;

    public Phase9_4_2CancellationConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

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
        var plan = Plan.Create(0, $"PLAN-{Guid.NewGuid():N}"[..28], "Cancellation Test Plan",
            1000m, 100, 50, 10, 20, 100, 1000,
            true, null, "EGP", 12, 0).Value;
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private static async Task<Guid> SeedContractAsync(AppDbContext db, string tenantId, decimal monthly = 1000m, int months = 12)
    {
        var contractResult = Contract.Create(
            Guid.NewGuid(), tenantId, "CNT-" + Guid.NewGuid().ToString("N")[..8], 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(months),
            months, monthly, monthly, "EGP", monthly * months);
        if (!contractResult.IsSuccess) throw new InvalidOperationException($"Contract creation failed: {string.Join(", ", contractResult.Errors.Select(e => e.Description))}");
        var contract = contractResult.Value;
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 1, monthly, "EGP", monthly, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 3, monthly * 3, "EGP", monthly, 2).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 6, monthly * 6 * 0.87m, "EGP", monthly, 3).Value);
        contract.AddPricingTier(ContractPricingTier.Create(Guid.NewGuid(), contract.Id, 12, monthly * 12 * 0.833m, "EGP", monthly, 4).Value);
        contract.SubmitForApproval();
        contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return contract.Id;
    }

    private static async Task<Guid> SeedSubscriptionAsync(AppDbContext db, string tenantId, Guid contractId)
    {
        var planId = await SeedPlanAsync(db);
        var sub = TenantPlan.Create(Guid.NewGuid(), tenantId, planId, 1000m, "EGP", 12, 0,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), false, SubscriptionStatus.Active).Value;
        sub.LinkToContract(contractId);
        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private static async Task SeedPaymentAsync(AppDbContext db, string tenantId, Guid contractId, decimal amount)
    {
        var inv = Invoice.Create(Guid.NewGuid(), "INV-" + Guid.NewGuid().ToString("N")[..8],
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), amount, 0, 0, amount, contractId: contractId).Value;
        inv.Issue(DateTime.UtcNow);
        db.Invoices.Add(inv);

        var payResult = Payment.Create(Guid.NewGuid(), "PAY-" + Guid.NewGuid().ToString("N")[..8], amount, "EGP", PaymentMethod.Cash);
        if (!payResult.IsSuccess) throw new InvalidOperationException("Payment creation failed");
        var pay = payResult.Value;
        pay.Complete(DateTime.UtcNow);
        db.Payments.Add(pay);

        var allocResult = PaymentAllocation.Create(Guid.NewGuid(), pay.Id, inv.Id, amount, DateTime.UtcNow);
        if (!allocResult.IsSuccess) throw new InvalidOperationException("Allocation creation failed");
        db.PaymentAllocations.Add(allocResult.Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
    }

    private static CancelSubscriptionHandler CreateHandler(AppDbContext db)
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        var sync = Substitute.For<ITenantRegistrySync>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns("admin-sql-1");
        var audit = Substitute.For<IAuditWriter>();
        var calc = new RefundCalculationService();
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(DateTimeOffset.UtcNow);
        return new CancelSubscriptionHandler(db, calc, guard, sync, user, audit, tp);
    }

    /// <summary>
    /// Executes two concurrent cancellation requests against the same subscription
    /// using Barrier synchronization and independent DbContext instances.
    /// </summary>
    private static async Task ExecuteConcurrentCancellations(
        SqlServerIntegrationFactory env,
        string tenantId,
        Guid subscriptionId,
        DateTime cancellationDate,
        Func<Result<CancellationResult>, Result<CancellationResult>, Task> assertFn)
    {
        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<CancellationResult>>();
        var tcs2 = new TaskCompletionSource<Result<CancellationResult>>();

        async Task<Result<CancellationResult>> ExecuteCancellation()
        {
            using var scope = env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = CreateHandler(db);

            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new CancelSubscriptionCommand(subscriptionId, cancellationDate, "concurrent-test"),
                CancellationToken.None);
        }

        var task1 = Task.Run(async () =>
        {
            try { tcs1.TrySetResult(await ExecuteCancellation()); }
            catch (Exception ex) { tcs1.TrySetException(ex); }
        });
        var task2 = Task.Run(async () =>
        {
            try { tcs2.TrySetResult(await ExecuteCancellation()); }
            catch (Exception ex) { tcs2.TrySetException(ex); }
        });

        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Concurrent cancellations did not complete within {TestTimeout.TotalSeconds}s");
        }

        await assertFn(await tcs1.Task, await tcs2.Task);
    }

    // ==================================================================
    // Test 1 — Concurrent cancellation with refund
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task ConcurrentCancellation_WithRefund_OneRefundMaximum()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId, 1000m, 12);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);
            await SeedPaymentAsync(db, tenantId, contractId, 12000m);
        }

        await ExecuteConcurrentCancellations(
            _env, tenantId, subscriptionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            async (result1, result2) =>
            {
                var successCount = (result1.IsSuccess ? 1 : 0) + (result2.IsSuccess ? 1 : 0);
                Assert.True(successCount >= 1,
                    $"Expected at least 1 success, got {successCount}. " +
                    $"R1: {(result1.IsSuccess ? "OK" : string.Join(", ", result1.Errors?.Select(e => e.Code) ?? []))}, " +
                    $"R2: {(result2.IsSuccess ? "OK" : string.Join(", ", result2.Errors?.Select(e => e.Code) ?? []))}");

                using var verifyScope = _env.Factory.Services.CreateScope();
                var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

                var sub = await db.TenantPlans.FirstAsync(s => s.Id == subscriptionId);
                Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

                var refunds = await db.Refunds.Where(r => r.SubscriptionId == subscriptionId).ToListAsync();
                Assert.True(refunds.Count <= 1,
                    $"Expected at most 1 refund for subscription {subscriptionId}, found {refunds.Count}");
            });
    }

    // ==================================================================
    // Test 2 — Concurrent cancellation with zero refund
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task ConcurrentCancellation_WithZeroRefund_OneCancellationEffect()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId, 1000m, 12);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);
            await SeedPaymentAsync(db, tenantId, contractId, 3000m);
        }

        await ExecuteConcurrentCancellations(
            _env, tenantId, subscriptionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            async (result1, result2) =>
            {
                var successCount = (result1.IsSuccess ? 1 : 0) + (result2.IsSuccess ? 1 : 0);
                Assert.True(successCount >= 1,
                    $"Expected at least 1 success, got {successCount}");

                using var verifyScope = _env.Factory.Services.CreateScope();
                var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

                var sub = await db.TenantPlans.FirstAsync(s => s.Id == subscriptionId);
                Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

                var refunds = await db.Refunds.Where(r => r.SubscriptionId == subscriptionId).ToListAsync();
                Assert.Empty(refunds);
            });
    }

    // ==================================================================
    // Test 3 — Concurrent cancellation with outstanding amount
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task ConcurrentCancellation_WithOutstanding_NoDuplicateRefunds()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId, 1000m, 12);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);
            await SeedPaymentAsync(db, tenantId, contractId, 4000m);
        }

        await ExecuteConcurrentCancellations(
            _env, tenantId, subscriptionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            async (result1, result2) =>
            {
                var successCount = (result1.IsSuccess ? 1 : 0) + (result2.IsSuccess ? 1 : 0);
                Assert.True(successCount >= 1,
                    $"Expected at least 1 success, got {successCount}");

                using var verifyScope = _env.Factory.Services.CreateScope();
                var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

                var sub = await db.TenantPlans.FirstAsync(s => s.Id == subscriptionId);
                Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

                var refunds = await db.Refunds.Where(r => r.SubscriptionId == subscriptionId).ToListAsync();
                Assert.True(refunds.Count <= 1,
                    $"Expected at most 1 refund, found {refunds.Count}");
            });
    }

    // ==================================================================
    // Test 4 — Unique constraint violation on duplicate SubscriptionId
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task UniqueConstraint_RejectsDuplicateSubscriptionIdRefund()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId, 1000m, 12);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);
            await SeedPaymentAsync(db, tenantId, contractId, 12000m);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = CreateHandler(db);

            var r1 = await handler.Handle(
                new CancelSubscriptionCommand(subscriptionId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "first"),
                CancellationToken.None);
            Assert.True(r1.IsSuccess, $"First cancellation failed: {string.Join(", ", r1.Errors?.Select(e => e.Description) ?? [])}");
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = CreateHandler(db);

            var r2 = await handler.Handle(
                new CancelSubscriptionCommand(subscriptionId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "second"),
                CancellationToken.None);
            Assert.True(r2.IsSuccess, $"Second cancellation (idempotent) failed: {string.Join(", ", r2.Errors?.Select(e => e.Description) ?? [])}");
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var sub = await db.TenantPlans.FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

            var refunds = await db.Refunds.Where(r => r.SubscriptionId == subscriptionId).ToListAsync();
            Assert.Single(refunds);
        }
    }

    // ==================================================================
    // Test 5 — Partially-paid installment cancellation on SQL Server
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task PartiallyPaidInstallment_CancellationSucceeds_SqlServer()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId, 1000m, 12);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);

            var inv = Invoice.Create(Guid.NewGuid(), "INV-" + Guid.NewGuid().ToString("N")[..8],
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 2000m, 0, 0, 2000m, contractId: contractId).Value;
            inv.Issue(DateTime.UtcNow);
            db.Invoices.Add(inv);

            var payResult = Payment.Create(Guid.NewGuid(), "PAY-" + Guid.NewGuid().ToString("N")[..8], 2000m, "EGP", PaymentMethod.Cash);
            var pay = payResult.Value;
            pay.Complete(DateTime.UtcNow);
            db.Payments.Add(pay);

            var allocResult = PaymentAllocation.Create(Guid.NewGuid(), pay.Id, inv.Id, 2000m, DateTime.UtcNow);
            db.PaymentAllocations.Add(allocResult.Value);

            var partiallyPaidInst = Installment.Create(Guid.NewGuid(), contractId, 1, DateTime.UtcNow.AddMonths(-1),
                DateTime.UtcNow.AddMonths(-2), DateTime.UtcNow.AddMonths(-1), 4000m, "EGP", subscriptionId).Value;
            db.Installments.Add(partiallyPaidInst);

            var unpaidFutureInst = Installment.Create(Guid.NewGuid(), contractId, 2, DateTime.UtcNow.AddMonths(7),
                DateTime.UtcNow.AddMonths(6), DateTime.UtcNow.AddMonths(7), 1000m, "EGP", subscriptionId).Value;
            db.Installments.Add(unpaidFutureInst);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            partiallyPaidInst.ApplyAllocation(allocResult.Value, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = CreateHandler(db);

            var result = await handler.Handle(
                new CancelSubscriptionCommand(subscriptionId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "test"),
                CancellationToken.None);
            Assert.True(result.IsSuccess, $"Cancellation failed: {string.Join(", ", result.Errors?.Select(e => e.Description) ?? [])}");
            Assert.True(result.Value.IsCancelled);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var sub = await db.TenantPlans.FirstAsync(s => s.Id == subscriptionId);
            Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);

            var installments = await db.Installments.Where(i => i.SubscriptionId == subscriptionId).ToListAsync();
            Assert.Equal(2, installments.Count);

            var partiallyPaidInst = installments.Single(i => i.SequenceNumber == 1);
            Assert.True(partiallyPaidInst.Status is InstallmentStatus.PartiallyPaid or InstallmentStatus.Overdue);
            Assert.Equal(2000m, partiallyPaidInst.SettledAmount);
            Assert.False(partiallyPaidInst.Status == InstallmentStatus.Cancelled);

            var unpaidInst = installments.Single(i => i.SequenceNumber == 2);
            Assert.Equal(InstallmentStatus.Cancelled, unpaidInst.Status);

            var allocs = await db.PaymentAllocations.Where(a => a.Status == PaymentAllocationStatus.Active).ToListAsync();
            Assert.True(allocs.Any(), "Active allocations should be preserved");
        }
    }

    // ==================================================================
    // Test 6 — Unique constraint enforced at SQL level
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task UniqueIndex_OnePerSubscription_EnforcedAtSql()
    {
        var tenantId = $"cancel-{Guid.NewGuid():N}"[..20];
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid contractId, subscriptionId;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            contractId = await SeedContractAsync(db, tenantId);
            subscriptionId = await SeedSubscriptionAsync(db, tenantId, contractId);
        }

        Guid refundId1;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var refund1 = Refund.Create(
                Guid.NewGuid(), $"REF-TEST-{Guid.NewGuid():N}"[..16], contractId, subscriptionId, null,
                100m, "EGP", "test", "admin", DateTime.UtcNow).Value;
            db.Refunds.Add(refund1);
            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            refundId1 = refund1.Id;
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var refund2 = Refund.Create(
                Guid.NewGuid(), $"REF-TEST-{Guid.NewGuid():N}"[..16], contractId, subscriptionId, null,
                200m, "EGP", "test-dup", "admin", DateTime.UtcNow).Value;
            db.Refunds.Add(refund2);
            db.StampAddedTenantIds(tenantId);

            await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var refunds = await db.Refunds.Where(r => r.SubscriptionId == subscriptionId).ToListAsync();
            Assert.Single(refunds);
            Assert.Equal(refundId1, refunds[0].Id);
        }
    }

    // ==================================================================
    // Test 7 — Migration verification
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9_4_2")]
    public async Task UniqueSubscriptionIdIndex_Exists()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task<bool> IndexExists(string name) => await db.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM SYS.INDEXES i
                INNER JOIN SYS.OBJECTS o ON i.object_id = o.object_id
                WHERE o.schema_id = SCHEMA_ID('Platform')
                AND o.name = 'Refunds'
                AND i.name = {name})
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        Assert.True(await IndexExists("UX_Refunds_TenantId_SubscriptionId_OnePerSubscription"));
    }
}
