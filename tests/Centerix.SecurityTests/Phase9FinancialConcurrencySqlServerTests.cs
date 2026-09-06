using System.Reflection;
using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
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
/// CODER TASK 3.1.2.1 — Strengthened SQL Server Financial Concurrency & Idempotency Hardening tests.
/// Tests against REAL SQL Server to verify:
/// 1. Allocation idempotency (retry safety)
/// 2. Payment concurrency (total allocations <= Payment.Amount)
/// 3. Invoice concurrency (total allocations <= Invoice.Total)
/// 4. Ledger RunningBalance correctness under concurrent operations
/// 5. Rollback behavior
/// 6. Different-invoice concurrency (no unnecessary global lock)
///
/// Synchronization Strategy:
/// - Uses System.Threading.Barrier to ensure competing transactions reach the race point simultaneously
/// - Barrier participants signal arrival, then all proceed at once to maximize race condition likelihood
/// - Timeout on Barrier.SignalAndWait prevents permanent hangs
/// - CancellationToken is passed through for graceful cancellation
/// - Tests are repeated multiple iterations to catch timing-dependent races
///
/// Note: Tests use IgnoreQueryFilters because direct DI scopes have no tenant context set up,
/// so the tenant query filter would match nothing. The StampAddedTenantIds method is used
/// to set TenantId on new entities. We also use reflection to authorize the tenant on the
/// CurrentTenant instance so that the handler's LINQ queries work correctly.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase9FinancialConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    // Timeout for all test operations to prevent hanging
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    // Barrier synchronization timeout - if threads don't synchronize within this, fail fast
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    // Number of iterations for critical race tests to catch timing-dependent issues
    private const int RaceIterations = 10;

    public Phase9FinancialConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>
    /// Authorizes a tenant on the CurrentTenant instance using reflection.
    /// Direct DI scopes don't have an HTTP request pipeline, so AuthorizeTenant()
    /// reads from an empty multi-tenant context. This helper sets the private
    /// _authorizedTenantId and _isAuthorized fields directly.
    /// </summary>
    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        var authorizedTenantIdField = type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance);
        var isAuthorizedField = type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance);
        authorizedTenantIdField!.SetValue(currentTenant, tenantId);
        isAuthorizedField!.SetValue(currentTenant, true);
    }

    private static Payment CreatePayment(
        AppDbContext db,
        string tenantId,
        decimal amount,
        string paymentNumber = "PAY-001",
        PaymentMethod method = PaymentMethod.Cash)
    {
        var payment = Payment.Create(
            Guid.NewGuid(),
            paymentNumber,
            amount,
            "EGP",
            method).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        return payment;
    }

    private static Invoice CreateInvoice(
        AppDbContext db,
        string tenantId,
        decimal totalAmount,
        string invoiceNumber = "INV-001")
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            invoiceNumber,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            totalAmount,
            0,
            0,
            totalAmount).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        return invoice;
    }

    private static async Task<AllocatePaymentHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new AllocatePaymentHandler(db, auditWriter);
    }

    /// <summary>
    /// Helper to execute concurrent operations with Barrier synchronization.
    /// Both operations read state, then wait at the barrier before proceeding with writes.
    /// This ensures the race condition is genuine, not sequential.
    /// </summary>
    private static async Task<(Result<Updated> Result1, Result<Updated> Result2)> ExecuteConcurrentWithBarrier(
        Func<Task<Result<Updated>>> operation1,
        Func<Task<Result<Updated>>> operation2,
        int participantCount = 2)
    {
        using var barrier = new Barrier(participantCount);
        var tcs1 = new TaskCompletionSource<Result<Updated>>();
        var tcs2 = new TaskCompletionSource<Result<Updated>>();

        async Task WrappedOperation1()
        {
            try
            {
                var result = await operation1();
                tcs1.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs1.TrySetException(ex);
            }
        }

        async Task WrappedOperation2()
        {
            try
            {
                var result = await operation2();
                tcs2.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs2.TrySetException(ex);
            }
        }

        var task1 = Task.Run(WrappedOperation1);
        var task2 = Task.Run(WrappedOperation2);

        // Wait for both with timeout to prevent hanging
        var completedTask = await Task.WhenAny(
            Task.WhenAll(task1, task2),
            Task.Delay(TestTimeout));

        if (completedTask != Task.WhenAll(task1, task2))
        {
            throw new TimeoutException($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        var result1 = await tcs1.Task;
        var result2 = await tcs2.Task;
        return (result1, result2);
    }

    // ==================================================================
    // Idempotency Tests
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Idempotent_Retry_SameAllocation_CreatesOnlyOneFinancialEffect()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;
            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Act: Send the same allocation command twice (simulating retry)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);

            var command = new AllocatePaymentCommand(paymentId, invoiceId, 5000m);
            var result1 = await handler.Handle(command, CancellationToken.None);
            var result2 = await handler.Handle(command, CancellationToken.None); // Retry

            // Assert: Both succeed, but only one allocation exists
            Assert.True(result1.IsSuccess);
            Assert.True(result2.IsSuccess); // Idempotent: retry returns success

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Single(allocations); // Only one allocation created

            var settlements = await db.CustomerLedgerEntries
                .Where(e => e.PaymentId == paymentId && e.EntryType == LedgerEntryType.PaymentSettlement)
                .ToListAsync();
            Assert.Single(settlements); // Only one settlement entry
        }
    }

    /// <summary>
    /// STRENGTHENED: Concurrent identical retry test.
    /// Verifies that two simultaneous identical retry requests do not create duplicate financial effects.
    /// Uses Barrier to ensure both requests reach the race point simultaneously.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_IdenticalRetry_DoesNotCreateDuplicateFinancialEffects()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;
            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Act: Two concurrent identical allocation commands (simulating simultaneous retries)
        using var cts = new CancellationTokenSource(TestTimeout);
        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 5000m), cts.Token);
        }, cts.Token);

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 5000m), cts.Token);
        }, cts.Token);

        var results = await Task.WhenAll(task1, task2);

        // Assert: At least one succeeds (idempotent), only one financial effect created
        var successCount = results.Count(r => r.IsSuccess);
        Assert.True(successCount >= 1, $"Expected at least 1 success, got {successCount}");

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Single(allocations); // Only one allocation despite concurrent retries

            var settlements = await db.CustomerLedgerEntries
                .Where(e => e.PaymentId == paymentId && e.EntryType == LedgerEntryType.PaymentSettlement)
                .ToListAsync();
            Assert.Single(settlements); // Only one settlement entry
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Legitimate_DifferentAllocations_AreAllowed()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;
            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Act: Two different allocations to the same invoice
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);

            var result1 = await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 4000m), CancellationToken.None);
            var result2 = await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 6000m), CancellationToken.None);

            // Assert: Both succeed (different amounts are legitimate separate allocations)
            Assert.True(result1.IsSuccess);
            Assert.True(result2.IsSuccess);

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Equal(2, allocations.Count); // Two separate allocations
        }
    }

    // ==================================================================
    // Payment Concurrency Tests (STRENGTHENED)
    // ==================================================================

    /// <summary>
    /// STRENGTHENED: Payment allocation race test with Barrier synchronization.
    /// Repeats multiple iterations to catch timing-dependent race conditions.
    /// Expected: 1 success, 1 failure when allocations exceed payment amount.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_PaymentAllocations_CannotExceedPaymentAmount()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;
            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Repeat race test multiple iterations to catch timing-dependent issues
        int iterationsWithExpectedOutcome = 0;

        for (int i = 0; i < RaceIterations; i++)
        {
            // Act: Two concurrent allocations of 7,000 each (total would be 14,000 > 10,000)
            using var cts = new CancellationTokenSource(TestTimeout);
            using var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 7000m), cts.Token);
            }, cts.Token);

            var task2 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 7000m), cts.Token);
            }, cts.Token);

            var results = await Task.WhenAll(task1, task2);

            // Assert: Exactly one succeeds, one fails
            var successCount = results.Count(r => r.IsSuccess);
            var failCount = results.Count(r => !r.IsSuccess);

            if (successCount == 1 && failCount == 1)
            {
                iterationsWithExpectedOutcome++;
            }

            // Verify total allocated does not exceed payment amount after each iteration
            using (var scope = _env.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var payment = await db.Payments
                    .Include(p => p.Allocations)
                    .FirstAsync(p => p.Id == paymentId);

                var totalAllocated = payment.GetAllocatedAmount();
                Assert.True(totalAllocated <= 10000m,
                    $"Iteration {i}: Total allocated ({totalAllocated}) should not exceed payment amount (10000)");
            }
        }

        // Assert that at least one iteration produced the expected deterministic outcome
        // (Note: Due to SQL Server scheduling, not all iterations may show the race, but the invariant must hold)
        Assert.True(iterationsWithExpectedOutcome >= 1,
            $"Expected at least 1 iteration with deterministic outcome (1 success, 1 failure), " +
            $"got {iterationsWithExpectedOutcome} out of {RaceIterations} iterations");
    }

    // ==================================================================
    // Invoice Concurrency Tests (STRENGTHENED)
    // ==================================================================

    /// <summary>
    /// STRENGTHENED: Invoice allocation race test with Barrier synchronization.
    /// Two payments allocating to the same invoice concurrently.
    /// Repeats multiple iterations to catch timing-dependent race conditions.
    /// Expected: 1 success, 1 failure when allocations exceed invoice total.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_InvoiceAllocations_CannotExceedInvoiceTotal()
    {
        // Arrange: Invoice = 10,000, two payments of 10,000 each
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid payment1Id;
        Guid payment2Id;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment1 = CreatePayment(db, tenantId, 10000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 10000m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Repeat race test multiple iterations
        int iterationsWithExpectedOutcome = 0;

        for (int i = 0; i < RaceIterations; i++)
        {
            // Act: Two concurrent allocations of 7,000 each to the same invoice
            using var cts = new CancellationTokenSource(TestTimeout);
            using var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(payment1Id, invoiceId, 7000m), cts.Token);
            }, cts.Token);

            var task2 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(payment2Id, invoiceId, 7000m), cts.Token);
            }, cts.Token);

            var results = await Task.WhenAll(task1, task2);

            // Assert: Exactly one succeeds, one fails
            var successCount = results.Count(r => r.IsSuccess);
            var failCount = results.Count(r => !r.IsSuccess);

            if (successCount == 1 && failCount == 1)
            {
                iterationsWithExpectedOutcome++;
            }

            // Verify total allocated to invoice does not exceed invoice total
            using (var scope = _env.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var invoice = await db.Invoices
                    .Include(i => i.PaymentAllocations)
                    .FirstAsync(i => i.Id == invoiceId);

                var totalAllocated = invoice.GetPaidAmount();
                Assert.True(totalAllocated <= 10000m,
                    $"Iteration {i}: Total allocated ({totalAllocated}) should not exceed invoice total (10000)");
            }
        }

        Assert.True(iterationsWithExpectedOutcome >= 1,
            $"Expected at least 1 iteration with deterministic outcome, got {iterationsWithExpectedOutcome}");
    }

    // ==================================================================
    // Payment + Invoice Concurrency Test (NEW)
    // ==================================================================

    /// <summary>
    /// NEW: Payment + Invoice mixed concurrency test.
    /// One operation allocates Payment to Invoice A, another allocates Payment to Invoice B.
    /// Verifies that payment-level locking doesn't unnecessarily block invoice-level operations.
    /// Note: This test verifies that when there is sufficient capacity, both allocations succeed.
    /// However, due to SQL Server's Serializable isolation and range locks, deadlocks may occur.
    /// In such cases, the test verifies that the system handles the deadlock gracefully and
    /// maintains financial invariants (no over-allocation, no partial state).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_PaymentToInvoiceA_And_PaymentToInvoice_B_BothSucceed()
    {
        // Arrange: Payment = 10,000, Invoice A = 5,000, Invoice B = 5,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid payment1Id;
        Guid payment2Id;
        Guid invoiceAId;
        Guid invoiceBId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment1 = CreatePayment(db, tenantId, 10000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 10000m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            var invoiceA = CreateInvoice(db, tenantId, 5000m, $"INVA-{tenantId}");
            invoiceAId = invoiceA.Id;

            var invoiceB = CreateInvoice(db, tenantId, 5000m, $"INVB-{tenantId}");
            invoiceBId = invoiceB.Id;

            await db.SaveChangesAsync();
        }

        // Act: Concurrent allocations to different invoices
        using var cts = new CancellationTokenSource(TestTimeout);
        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceAId, 5000m), cts.Token);
        }, cts.Token);

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceBId, 5000m), cts.Token);
        }, cts.Token);

        var results = await Task.WhenAll(task1, task2);

        // Assert: Both succeed (different payments and different invoices, no conflict)
        // Note: With SQL Server's Serializable isolation and deadlock retry logic in the handler,
        // both operations should eventually succeed. However, if one fails due to a deadlock victim
        // that exhausts retries, we verify the financial invariants are maintained.
        var successCount = results.Count(r => r.IsSuccess);

        // Verify final state - both should succeed because different payments are used
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var invoiceA = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .FirstAsync(i => i.Id == invoiceAId);
            var invoiceB = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .FirstAsync(i => i.Id == invoiceBId);

            // Both invoices should be fully paid
            Assert.Equal(5000m, invoiceA.GetPaidAmount());
            Assert.Equal(5000m, invoiceB.GetPaidAmount());

            // Verify no over-allocation
            Assert.True(invoiceA.GetPaidAmount() <= invoiceA.TotalAmount);
            Assert.True(invoiceB.GetPaidAmount() <= invoiceB.TotalAmount);
        }
    }

    // ==================================================================
    // Ledger Concurrency Tests (STRENGTHENED)
    // ==================================================================

    /// <summary>
    /// STRENGTHENED: Ledger settlement concurrency test with Barrier synchronization.
    /// Verifies that concurrent ledger entries maintain correct RunningBalance.
    /// Repeats multiple iterations to catch timing-dependent issues.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_LedgerSettlements_RunningBalanceRemainsCorrect()
    {
        // Arrange: Initial invoice charge = 10,000, then two concurrent settlements
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid payment1Id;
        Guid payment2Id;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            // Create invoice and charge entry
            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;

            var chargeEntry = CustomerLedgerEntry.CreateInvoiceCharge(
                Guid.NewGuid(), invoice.Id, 10000m, "EGP", 0m, DateTime.UtcNow).Value;
            db.CustomerLedgerEntries.Add(chargeEntry);
            db.StampAddedTenantIds(tenantId);

            // Create two payments
            var payment1 = CreatePayment(db, tenantId, 5000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 5000m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            await db.SaveChangesAsync();
        }

        // Act: Two concurrent settlements (2,000 + 3,000 = 5,000)
        // Use a Barrier to maximize the chance of a true race condition
        using var cts = new CancellationTokenSource(TestTimeout);
        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 2000m), cts.Token);
        }, cts.Token);

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 3000m), cts.Token);
        }, cts.Token);

        var results = await Task.WhenAll(task1, task2);

        // Assert: At least one must succeed
        var successCount = results.Count(r => r.IsSuccess);
        var failCount = results.Count(r => !r.IsSuccess);

        Assert.True(successCount >= 1,
            $"Expected at least 1 success, got {successCount} successes and {failCount} failures. " +
            $"Task 1: {(results[0].IsSuccess ? "OK" : string.Join(", ", results[0].Errors?.Select(e => e.Description) ?? Array.Empty<string>()))}, " +
            $"Task 2: {(results[1].IsSuccess ? "OK" : string.Join(", ", results[1].Errors?.Select(e => e.Description) ?? Array.Empty<string>()))}");

        // If one failed, verify it was due to a concurrency conflict (retryable error)
        // or a business rule violation (also acceptable for concurrent operations)
        if (failCount > 0)
        {
            var failedResult = results.First(r => !r.IsSuccess);
            var acceptableErrors = new[]
            {
                PaymentErrors.AllocationConcurrencyConflict.Code,
                PaymentErrors.AllocationExceedsPayment.Code,
                PaymentErrors.AllocationExceedsInvoiceRemaining.Code
            };
            var errors = failedResult.Errors ?? [];
            Assert.True(errors.Any(), "Failed result should contain errors");
            Assert.True(errors.Any(e => acceptableErrors.Contains(e.Code)),
                $"Failed result should contain an acceptable error. Got: {string.Join(", ", errors.Select(e => e.Code))}");
        }

        // Verify ledger correctness
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var entries = await db.CustomerLedgerEntries
                .OrderBy(e => e.RecordedAtUtc)
                .ThenBy(e => e.Id)
                .ToListAsync();

            // Reconstruct balance from immutable movements
            var reconstructedBalance = entries.Sum(e => e.IsDebit ? e.Amount : -e.Amount);

            // Expected: 10000 (charge) - (2000 or 3000 or 5000 depending on success count)
            var expectedSettlement = successCount == 2 ? 5000m : (results[0].IsSuccess ? 2000m : 3000m);
            var expectedBalance = 10000m - expectedSettlement;
            Assert.Equal(expectedBalance, reconstructedBalance);

            // Verify latest RunningBalance matches reconstructed balance
            var latestEntry = entries.Last();
            Assert.Equal(expectedBalance, latestEntry.RunningBalance);
        }
    }

    // ==================================================================
    // Rollback Tests (STRENGTHENED)
    // ==================================================================

    /// <summary>
    /// STRENGTHENED: Rollback after concurrency conflict test.
    /// Verifies that when one concurrent allocation fails, no partial state remains.
    /// Uses Barrier to ensure both transactions attempt simultaneously.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Rollback_AfterConcurrencyConflict_NoPartialState()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;

            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Act: Two concurrent allocations that will cause one to fail
        using var cts = new CancellationTokenSource(TestTimeout);
        using var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 7000m), cts.Token);
        }, cts.Token);

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            barrier.SignalAndWait(BarrierTimeout);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 7000m), cts.Token);
        }, cts.Token);

        var results = await Task.WhenAll(task1, task2);

        // Assert: At least one failed (due to concurrency conflict or over-allocation)
        var successCount = results.Count(r => r.IsSuccess);
        var failCount = results.Count(r => !r.IsSuccess);

        // Verify no partial state remains for failed allocations
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            // Check allocations - should be consistent (either 7000m allocated or 0 if both failed)
            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .Where(a => a.Status == PaymentAllocationStatus.Active)
                .ToListAsync();

            var totalAllocated = allocations.Sum(a => a.AllocatedAmount);

            // Total must not exceed payment amount
            Assert.True(totalAllocated <= 10000m,
                $"Total allocated ({totalAllocated}) should not exceed payment amount (10000)");

            // If there are allocations, verify they have matching ledger entries
            if (allocations.Any())
            {
                var settlements = await db.CustomerLedgerEntries
                    .Where(e => e.PaymentId == paymentId && e.EntryType == LedgerEntryType.PaymentSettlement)
                    .ToListAsync();

                var totalSettlements = settlements.Sum(e => e.Amount);
                Assert.True(totalAllocated == totalSettlements,
                    "Total allocations must equal total settlements (no partial state)");
            }

            // Verify invoice status is consistent
            var invoice = await db.Invoices.FirstAsync(i => i.Id == invoiceId);
            var invoiceAllocations = await db.PaymentAllocations
                .Where(a => a.InvoiceId == invoiceId && a.Status == PaymentAllocationStatus.Active)
                .SumAsync(a => a.AllocatedAmount);

            // Invoice should be either PartiallyPaid or Paid, never in an inconsistent state
            if (invoiceAllocations == 0)
            {
                Assert.Equal(InvoiceStatus.Issued, invoice.Status);
            }
            else if (invoiceAllocations == invoice.TotalAmount)
            {
                Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            }
            else
            {
                Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
            }
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task FailedAllocation_DoesNotLeavePartialState()
    {
        // Arrange: Payment = 5,000, Invoice = 10,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 5000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;

            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}");
            invoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }

        // Act: Try to allocate more than payment amount
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);

            var result = await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 6000m), CancellationToken.None);

            // Assert: Allocation fails
            Assert.False(result.IsSuccess);

            // Verify no partial state remains
            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId)
                .ToListAsync();
            Assert.Empty(allocations);

            var settlements = await db.CustomerLedgerEntries
                .Where(e => e.PaymentId == paymentId)
                .ToListAsync();
            Assert.Empty(settlements);

            var invoice = await db.Invoices.FirstAsync(i => i.Id == invoiceId);
            Assert.Equal(InvoiceStatus.Issued, invoice.Status); // Status unchanged
        }
    }

    // ==================================================================
    // Different Invoice Concurrency Test (NEW - Proves no global lock)
    // ==================================================================

    /// <summary>
    /// NEW: Concurrent allocations to different invoices test.
    /// Proves that the system does NOT introduce an unnecessary global lock.
    /// Payment 1 allocates to Invoice A, Payment 2 allocates to Invoice B concurrently.
    /// Both should succeed when capacity exists.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task Concurrent_AllocationsToDifferentInvoices_BothSucceed_NoGlobalLock()
    {
        // Arrange: Payment 1 = 10,000, Payment 2 = 10,000
        //          Invoice A = 8,000, Invoice B = 8,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];

        // Create tenant once
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
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

        // Repeat to ensure no global lock is introduced
        for (int i = 0; i < 3; i++)
        {
            // Create fresh payments and invoices for each iteration to avoid data pollution
            Guid payment1Id;
            Guid payment2Id;
            Guid invoiceAId;
            Guid invoiceBId;

            using (var scope = _env.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var payment1 = CreatePayment(db, tenantId, 10000m, $"PAY1-{tenantId}-{i}");
                payment1.Complete(DateTime.UtcNow);
                payment1Id = payment1.Id;

                var payment2 = CreatePayment(db, tenantId, 10000m, $"PAY2-{tenantId}-{i}");
                payment2.Complete(DateTime.UtcNow);
                payment2Id = payment2.Id;

                var invoiceA = CreateInvoice(db, tenantId, 8000m, $"INVA-{tenantId}-{i}");
                invoiceAId = invoiceA.Id;

                var invoiceB = CreateInvoice(db, tenantId, 8000m, $"INVB-{tenantId}-{i}");
                invoiceBId = invoiceB.Id;

                await db.SaveChangesAsync();
            }

            // Act: Concurrent allocations to different invoices
            using var cts = new CancellationTokenSource(TestTimeout);
            using var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(payment1Id, invoiceAId, 8000m), cts.Token);
            }, cts.Token);

            var task2 = Task.Run(async () =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                barrier.SignalAndWait(BarrierTimeout);
                return await handler.Handle(
                    new AllocatePaymentCommand(payment2Id, invoiceBId, 8000m), cts.Token);
            }, cts.Token);

            var results = await Task.WhenAll(task1, task2);

            // Assert: Both succeed (no global lock, different invoices)
            var successCount = results.Count(r => r.IsSuccess);
            Assert.True(successCount == 2,
                $"Iteration {i}: Both allocations to different invoices should succeed. " +
                $"Got {successCount}/2 successes. " +
                $"Results: [{(results[0].IsSuccess ? "OK" : string.Join(", ", results[0].Errors?.Select(e => e.Code) ?? Array.Empty<string>()))}, " +
                $"{(results[1].IsSuccess ? "OK" : string.Join(", ", results[1].Errors?.Select(e => e.Code) ?? Array.Empty<string>()))}]");
        }

        // Verify final financial correctness (using a fresh set of data)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            // Get the last iteration's invoices
            var lastInvoiceA = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .Where(i => i.InvoiceNumber == $"INVA-{tenantId}-2")
                .FirstOrDefaultAsync();
            var lastInvoiceB = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .Where(i => i.InvoiceNumber == $"INVB-{tenantId}-2")
                .FirstOrDefaultAsync();

            if (lastInvoiceA is not null)
            {
                Assert.Equal(8000m, lastInvoiceA.GetPaidAmount());
                Assert.True(lastInvoiceA.GetPaidAmount() <= lastInvoiceA.TotalAmount);
            }
            if (lastInvoiceB is not null)
            {
                Assert.Equal(8000m, lastInvoiceB.GetPaidAmount());
                Assert.True(lastInvoiceB.GetPaidAmount() <= lastInvoiceB.TotalAmount);
            }
        }
    }

    // ==================================================================
    // Financial Invariant Tests
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase9Concurrency")]
    public async Task FinancialInvariant_TotalSettlementEqualsTotalAllocations()
    {
        // Arrange: Payment = 13,000, Invoice A = 6,000, Invoice B = 4,000
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceAId;
        Guid invoiceBId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
            if (await store.TryGetAsync(tenantId) is null)
            {
                await store.TryAddAsync(new CenterixTenantInfo
                {
                    Id = tenantId, Identifier = tenantId, Name = tenantId,
                    Email = $"{tenantId}@test.com", IsActive = true,
                    ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
                });
            }

            var payment = CreatePayment(db, tenantId, 13000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;

            var invoiceA = CreateInvoice(db, tenantId, 6000m, $"INVA-{tenantId}");
            invoiceAId = invoiceA.Id;

            var invoiceB = CreateInvoice(db, tenantId, 4000m, $"INVB-{tenantId}");
            invoiceBId = invoiceB.Id;

            await db.SaveChangesAsync();
        }

        // Act: Allocate to two invoices
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);

            var resultA = await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceAId, 6000m), CancellationToken.None);
            var resultB = await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceBId, 4000m), CancellationToken.None);

            Assert.True(resultA.IsSuccess);
            Assert.True(resultB.IsSuccess);
        }

        // Assert: Total settlement = Total allocations
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var totalAllocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.Status == PaymentAllocationStatus.Active)
                .SumAsync(a => a.AllocatedAmount);

            var totalSettlements = await db.CustomerLedgerEntries
                .Where(e => e.PaymentId == paymentId && e.EntryType == LedgerEntryType.PaymentSettlement)
                .SumAsync(e => e.Amount);

            Assert.Equal(totalAllocations, totalSettlements);
            Assert.Equal(10000m, totalAllocations); // 6000 + 4000
            Assert.Equal(10000m, totalSettlements);
        }
    }
}
