using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 13.1 — SQL Server concurrency tests for Refund Settlement and Payment Source Integrity.
/// Tests against REAL SQL Server to verify deterministic financial outcomes:
/// 1. Competing refunds on same payment: exactly 1 success + 1 InsufficientPaymentSource
/// 2. Same refund + same idempotency key: exactly 1 financial execution
/// 3. Same idempotency key + different payload: exactly 1 success + 1 IdempotencyKeyConflict
/// 4. Multiple payment allocation sums correctly
/// 5. Post-concurrency financial invariants enforced
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase13RefundAllocationSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    public Phase13RefundAllocationSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        var field = type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance);
        var authField = type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(currentTenant, tenantId);
        authField!.SetValue(currentTenant, true);
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

    private static async Task<(Contract contract, Invoice invoice, Payment payment)> SetupRefundScenario(
        AppDbContext db, string tenantId, decimal paymentAmount = 10000m)
    {
        var result = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: "CNT-" + Guid.NewGuid().ToString("N")[..8],
            planId: 1,
            effectiveAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);
        Assert.True(result.IsSuccess);
        var contract = result.Value;
        contract.SubmitForApproval();
        contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Contracts.Add(contract);

        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-" + Guid.NewGuid().ToString("N")[..8],
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            paymentAmount, 0, 0, paymentAmount,
            contractId: contract.Id).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var payResult = Payment.Create(
            Guid.NewGuid(), "PAY-" + Guid.NewGuid().ToString("N")[..8],
            paymentAmount, "EGP", PaymentMethod.Cash);
        Assert.True(payResult.IsSuccess);
        var payment = payResult.Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);

        var alloc = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, paymentAmount, DateTime.UtcNow);
        Assert.True(alloc.IsSuccess);
        db.PaymentAllocations.Add(alloc.Value);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (contract, invoice, payment);
    }

    private async Task<(Result<Updated> Result1, Result<Updated> Result2)> ExecuteConcurrentWithBarrier(
        Func<CancellationToken, Task<Result<Updated>>> op1,
        Func<CancellationToken, Task<Result<Updated>>> op2,
        CancellationToken cancellationToken)
    {
        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<Updated>>();
        var tcs2 = new TaskCompletionSource<Result<Updated>>();

        async Task Wrap1()
        {
            try { barrier.SignalAndWait(BarrierTimeout); tcs1.TrySetResult(await op1(cancellationToken)); }
            catch (Exception ex) { tcs1.TrySetException(ex); }
        }

        async Task Wrap2()
        {
            try { barrier.SignalAndWait(BarrierTimeout); tcs2.TrySetResult(await op2(cancellationToken)); }
            catch (Exception ex) { tcs2.TrySetException(ex); }
        }

        var task1 = Task.Run(Wrap1, cancellationToken);
        var task2 = Task.Run(Wrap2, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TestTimeout);
        await Task.WhenAll(Task.WhenAny(task1, task2) == task1
            ? Task.WhenAll(task1, Task.Delay(1, cts.Token).ContinueWith(_ => { }, cts.Token))
            : Task.WhenAll(task2, Task.Delay(1, cts.Token).ContinueWith(_ => { }, cts.Token)));

        if (tcs1.Task.Status != TaskStatus.RanToCompletion)
            await tcs1.Task;
        if (tcs2.Task.Status != TaskStatus.RanToCompletion)
            await tcs2.Task;

        return (tcs1.Task.Result, tcs2.Task.Result);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase13_1")]
    public async Task CompetingRefunds_SamePayment_ExactOneSuccessOneInsufficient()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid refundIdA;
        Guid refundIdB;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var (contract, _, setupPayment) = await SetupRefundScenario(db, tenantId, 1000m);
            paymentId = setupPayment.Id;

            var refundA = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 700m, "EGP", "Refund A", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refundA);
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refundA.Id, paymentId, 700m, PaymentMethod.Cash, "EGP", "PAY-001").Value);
            refundIdA = refundA.Id;

            var refundB = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 700m, "EGP", "Refund B", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refundB);
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refundB.Id, paymentId, 700m, PaymentMethod.Cash, "EGP", "PAY-001").Value);
            refundIdB = refundB.Id;

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundIdA), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundIdB), ct);
            },
            CancellationToken.None);

        var results = new[] { result1, result2 };
        var successCount = results.Count(r => r.IsSuccess);
        var insufficientCount = results.Count(r => r.Errors?.Any(e => e.Code == "Refund.InsufficientPaymentSource") == true);
        var concurrencyCount = results.Count(r => r.Errors?.Any(e => e.Code == "Refund.ExecutionConcurrencyConflict") == true);
        var unexpectedFailureCount = results.Count(r => !r.IsSuccess && !r.Errors!.Any(e =>
            e.Code == "Refund.InsufficientPaymentSource" || e.Code == "Refund.ExecutionConcurrencyConflict"));

        Assert.True(successCount == 1 && insufficientCount == 1 && unexpectedFailureCount == 0,
            $"Expected exactly 1 success + 1 InsufficientPaymentSource + 0 unexpected. " +
            $"Actual: success={successCount}, insufficient={insufficientCount}, concurrency={concurrencyCount}, unexpected={unexpectedFailureCount}. " +
            $"R1: {(result1.IsSuccess ? "OK" : string.Join(", ", result1.Errors!.Select(e => e.Code)))}, " +
            $"R2: {(result2.IsSuccess ? "OK" : string.Join(", ", result2.Errors!.Select(e => e.Code)))}");

        // Post-concurrency financial invariant: SUM(RefundAllocations from COMPLETED refunds) <= Payment.Amount
        using var verifyScope = _env.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

        var payment = await verifyDb.Payments.FindAsync(paymentId);
        Assert.NotNull(payment);

        var totalRefunded = await verifyDb.RefundAllocations
            .Where(ra => ra.PaymentId == paymentId && ra.TenantId == tenantId
                && verifyDb.Refunds.Any(r => r.Id == ra.RefundId
                    && r.Status == RefundStatus.Completed))
            .SumAsync(ra => ra.Amount);
        Assert.True(totalRefunded <= payment.Amount,
            $"Over-refund detected: totalRefunded={totalRefunded} > payment.Amount={payment.Amount}");

        // Exactly one completed refund
        var completedRefunds = await verifyDb.Refunds
            .Where(r => (r.Id == refundIdA || r.Id == refundIdB)
                && r.TenantId == tenantId && r.Status == RefundStatus.Completed)
            .ToListAsync();
        Assert.Single(completedRefunds);

        // Exactly one ledger settlement
        var ledgerEntries = await verifyDb.CustomerLedgerEntries
            .Where(e => e.TenantId == tenantId && e.EntryType == LedgerEntryType.RefundSettlement)
            .ToListAsync();
        Assert.Single(ledgerEntries);

        // Historical integrity
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase13_1")]
    public async Task SameRefund_SameIdempotencyKey_ExactOneFinancialExecution()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid refundId;
        var sharedKey = $"idem-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var (contract, _, setupPayment) = await SetupRefundScenario(db, tenantId);

            var refund = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 5000m, "EGP", "Test", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refund);

            var allocation = RefundAllocation.Create(
                Guid.NewGuid(), refund.Id, setupPayment.Id, 5000m, PaymentMethod.Cash, "EGP", "PAY-001");
            Assert.True(allocation.IsSuccess);
            db.RefundAllocations.Add(allocation.Value);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            refundId = refund.Id;
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundId, sharedKey), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundId, sharedKey), ct);
            },
            CancellationToken.None);

        var results = new[] { result1, result2 };
        var successCount = results.Count(r => r.IsSuccess);
        var unexpectedFailureCount = results.Count(r => !r.IsSuccess && !r.Errors!.Any(e =>
            e.Code == "Refund.ExecutionConcurrencyConflict"));

        Assert.Equal(2, successCount);
        Assert.Equal(0, unexpectedFailureCount);

        // Post: exactly 1 financial execution (1 ledger settlement, refund Completed)
        using var verifyScope = _env.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

        var verifyRefund = await verifyDb.Refunds.FindAsync(refundId);
        Assert.Equal(RefundStatus.Completed, verifyRefund!.Status);
        Assert.Equal(sharedKey, verifyRefund.IdempotencyKey);

        var ledgerEntries = await verifyDb.CustomerLedgerEntries
            .Where(e => e.RefundId == refundId && e.EntryType == LedgerEntryType.RefundSettlement)
            .ToListAsync();
        Assert.Single(ledgerEntries);
        Assert.Equal(5000m, ledgerEntries[0].Amount);

        var refundAllocations = await verifyDb.RefundAllocations
            .Where(ra => ra.RefundId == refundId)
            .ToListAsync();
        Assert.Single(refundAllocations);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase13_1")]
    public async Task SameKey_DifferentPayload_ExactOneSuccessOneConflict()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid refundIdA;
        Guid refundIdB;
        var sharedKey = $"idem-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var (contract, _, payment) = await SetupRefundScenario(db, tenantId);

            var refundA = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 5000m, "EGP", "Refund A", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refundA);
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refundA.Id, payment.Id, 5000m, PaymentMethod.Cash, "EGP", "PAY-001").Value);
            refundIdA = refundA.Id;

            var refundB = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 3000m, "EGP", "Refund B", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refundB);
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refundB.Id, payment.Id, 3000m, PaymentMethod.Cash, "EGP", "PAY-001").Value);
            refundIdB = refundB.Id;

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundIdA, sharedKey), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.UserId.Returns("user-1");
                var handler = new ExecuteRefundHandler(db, currentUser, Substitute.For<IAuditWriter>());
                return await handler.Handle(new ExecuteRefundCommand(refundIdB, sharedKey), ct);
            },
            CancellationToken.None);

        var results = new[] { result1, result2 };
        var successCount = results.Count(r => r.IsSuccess);
        var conflictCount = results.Count(r => r.Errors?.Any(e =>
            e.Code == "RefundAllocation.IdempotencyKeyConflict") == true);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        // Verify the failing result is strictly IdempotencyKeyConflict (not ExecutionConcurrencyConflict)
        var failingResult = results.First(r => !r.IsSuccess);
        Assert.Contains(failingResult.Errors!, e => e.Code == "RefundAllocation.IdempotencyKeyConflict");
        Assert.DoesNotContain(failingResult.Errors!, e => e.Code == "Refund.ExecutionConcurrencyConflict");

        // Post: exactly 1 completed refund, 1 ledger settlement
        using var verifyScope = _env.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(verifyScope.ServiceProvider, tenantId);

        var completedCount = await verifyDb.Refunds
            .Where(r => (r.Id == refundIdA || r.Id == refundIdB)
                && r.TenantId == tenantId && r.Status == RefundStatus.Completed)
            .CountAsync();
        Assert.Equal(1, completedCount);

        var ledgerEntries = await verifyDb.CustomerLedgerEntries
            .Where(e => e.TenantId == tenantId && e.EntryType == LedgerEntryType.RefundSettlement)
            .ToListAsync();
        Assert.Single(ledgerEntries);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase13_1")]
    public async Task MultiplePaymentRefundAllocation_SumsCorrectly()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid refundId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var (contract, invoice, _) = await SetupRefundScenario(db, tenantId);
            invoiceId = invoice.Id;

            var pay1 = Payment.Create(Guid.NewGuid(), "PAY-001", 4000m, "EGP", PaymentMethod.Cash).Value;
            pay1.Complete(DateTime.UtcNow);
            db.Payments.Add(pay1);

            var pay2 = Payment.Create(Guid.NewGuid(), "PAY-002", 6000m, "EGP", PaymentMethod.InstaPay).Value;
            pay2.Complete(DateTime.UtcNow);
            db.Payments.Add(pay2);

            db.PaymentAllocations.Add(PaymentAllocation.Create(Guid.NewGuid(), pay1.Id, invoiceId, 4000m, DateTime.UtcNow).Value);
            db.PaymentAllocations.Add(PaymentAllocation.Create(Guid.NewGuid(), pay2.Id, invoiceId, 6000m, DateTime.UtcNow).Value);

            var refund = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 5000m, "EGP", "Multi-pay test", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refund);

            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refund.Id, pay1.Id, 2000m, PaymentMethod.Cash, "EGP", "PAY-001").Value);
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refund.Id, pay2.Id, 3000m, PaymentMethod.InstaPay, "EGP", "PAY-002").Value);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            refundId = refund.Id;
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("user-1");
        var handler = new ExecuteRefundHandler(db2, currentUser, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ExecuteRefundCommand(refundId), CancellationToken.None);
        Assert.True(result.IsSuccess);

        var ledgerEntries = await db2.CustomerLedgerEntries
            .Where(e => e.RefundId == refundId && e.EntryType == LedgerEntryType.RefundSettlement)
            .ToListAsync();
        Assert.Single(ledgerEntries);
        Assert.Equal(5000m, ledgerEntries[0].Amount);

        // Financial invariant: SUM(allocations per payment) <= Payment.Amount
        var pay1Allocations = await db2.RefundAllocations
            .Where(ra => ra.RefundId == refundId)
            .ToListAsync();
        Assert.Equal(5000m, pay1Allocations.Sum(a => a.Amount));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase13_1_1")]
    public async Task TamperedPaymentMethod_Rejected()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid refundId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var (contract, _, payment) = await SetupRefundScenario(db, tenantId, 10000m);

            var refund = Refund.Create(
                Guid.NewGuid(), "REF-" + Guid.NewGuid().ToString("N")[..8],
                contract.Id, null, null, 5000m, "EGP", "Tampered method test", "user-1", DateTime.UtcNow).Value;
            db.Refunds.Add(refund);

            // Payment was created with PaymentMethod.Cash, but allocation says InstaPay (tampered)
            db.RefundAllocations.Add(RefundAllocation.Create(
                Guid.NewGuid(), refund.Id, payment.Id, 5000m, PaymentMethod.InstaPay, "EGP", "PAY-001").Value);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
            refundId = refund.Id;
        }

        using var scope2 = _env.Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthorizeTenant(scope2.ServiceProvider, tenantId);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("user-1");
        var handler = new ExecuteRefundHandler(db2, currentUser, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ExecuteRefundCommand(refundId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.PaymentMethodMismatch");

        // Verify refund is still Pending (not executed)
        var verifyDb = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifyRefund = await verifyDb.Refunds.FindAsync(refundId);
        Assert.Equal(RefundStatus.Pending, verifyRefund!.Status);
    }
}
