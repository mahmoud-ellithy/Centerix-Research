using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
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
/// Task 12.1 — SQL Server concurrency tests for the customer credit lifecycle.
/// Tests against REAL SQL Server (Testcontainers or local) to verify:
/// 1. Concurrent credit consumption: exactly one succeeds
/// 2. Concurrent overpayment processing: exactly one credit created
/// 3. Concurrent credit applications with different keys: both may succeed
/// 4. Concurrent credit applications with same key: idempotent convergence
/// 5. Final financial invariants: CreditRemaining ≥ 0, ΣApplications ≤ CreditCreated
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase12_1CreditConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);
    private const int RaceIterations = 5;

    public Phase12_1CreditConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        var authorizedTenantIdField = type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance);
        var isAuthorizedField = type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance);
        authorizedTenantIdField!.SetValue(currentTenant, tenantId);
        isAuthorizedField!.SetValue(currentTenant, true);
    }

    private static async Task<ApplyCreditToInvoiceHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new ApplyCreditToInvoiceHandler(db, auditWriter);
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

    private static async Task<(Result<Updated> Result1, Result<Updated> Result2)> ExecuteConcurrentWithBarrier(
        Func<CancellationToken, Task<Result<Updated>>> operation1,
        Func<CancellationToken, Task<Result<Updated>>> operation2,
        CancellationToken cancellationToken,
        int participantCount = 2)
    {
        using var barrier = new Barrier(participantCount);
        var tcs1 = new TaskCompletionSource<Result<Updated>>();
        var tcs2 = new TaskCompletionSource<Result<Updated>>();

        async Task WrappedOperation1()
        {
            try
            {
                barrier.SignalAndWait(BarrierTimeout);
                var result = await operation1(cancellationToken);
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
                barrier.SignalAndWait(BarrierTimeout);
                var result = await operation2(cancellationToken);
                tcs2.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs2.TrySetException(ex);
            }
        }

        var task1 = Task.Run(WrappedOperation1, cancellationToken);
        var task2 = Task.Run(WrappedOperation2, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TestTimeout);
        var allTasks = Task.WhenAll(task1, task2);

        try
        {
            await allTasks.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        return (await tcs1.Task, await tcs2.Task);
    }

    // ==================================================================
    // SCENARIO 1 — Credit = 1,000; A = 700, B = 700
    // Exactly one succeeds, one fails.
    // Final: total applied ≤ 700, remaining ≥ 300.
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentCreditApplications_CannotOverConsumeCredit()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid creditId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantId}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                5000m, 0, 0, 5000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);

            var credit = TenantCredit.Create(Guid.NewGuid(), 1000m, CreditSourceType.Manual).Value;
            db.TenantCredits.Add(credit);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            creditId = credit.Id;
            invoiceId = invoice.Id;
        }

        for (int i = 0; i < RaceIterations; i++)
        {
            using (var scope = _env.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);

                var existingApps = await db.CreditApplications.Where(ca => ca.CreditId == creditId).ToListAsync();
                db.CreditApplications.RemoveRange(existingApps);

                var credit = await db.TenantCredits.FindAsync(creditId);
                if (credit is not null)
                {
                    db.Entry(credit).Property(c => c.Amount).CurrentValue = 1000m;
                    db.Entry(credit).Property(c => c.RemainingAmount).CurrentValue = 1000m;
                    db.Entry(credit).Property(c => c.Status).CurrentValue = CreditStatus.Available;
                }

                await db.SaveChangesAsync();
            }

            var (result1, result2) = await ExecuteConcurrentWithBarrier(
                async ct =>
                {
                    using var scope = _env.Factory.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    AuthorizeTenant(scope.ServiceProvider, tenantId);
                    var handler = await CreateHandler(db);
                    return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, Guid.NewGuid().ToString("N")), ct);
                },
                async ct =>
                {
                    using var scope = _env.Factory.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    AuthorizeTenant(scope.ServiceProvider, tenantId);
                    var handler = await CreateHandler(db);
                    return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, Guid.NewGuid().ToString("N")), ct);
                },
                CancellationToken.None);

            var successCount = new[] { result1, result2 }.Count(r => r.IsSuccess);

            // Classify failures into expected business conflicts vs unexpected failures.
            // Expected conflict codes:
            // - ConcurrencyConflict: deadlock / DbUpdateConcurrencyException detected by handler
            // - InsufficientRemaining: credit ConsumeAmount rejected (concurrent read saw reduced remaining)
            // - InvalidApplicationAmount: credit remaining check failed (Serializable isolation
            //   properly serialized the transactions; second reads updated remaining and rejects)
            var expectedConflictCodes = new HashSet<string>
            {
                "CreditApplication.ConcurrencyConflict",
                "TenantCredit.InsufficientRemaining",
                "TenantCredit.InvalidApplicationAmount"
            };
            var conflictCount = new[] { result1, result2 }
                .Count(r => r.Errors?.Any(e => expectedConflictCodes.Contains(e.Code)) ?? false);
            var unexpectedFailures = new[] { result1, result2 }
                .Count(r => !r.IsSuccess && !(r.Errors?.Any(e => expectedConflictCodes.Contains(e.Code)) ?? false));

            Assert.True(successCount == 1,
                $"Iteration {i}: Expected exactly 1 success but got {successCount}. " +
                $"R1: {result1.IsSuccess} ({string.Join(", ", result1.Errors?.Select(e => e.Code) ?? [])}), " +
                $"R2: {result2.IsSuccess} ({string.Join(", ", result2.Errors?.Select(e => e.Code) ?? [])})");

            Assert.True(conflictCount == 1,
                $"Iteration {i}: Expected exactly 1 conflict but got {conflictCount}. " +
                $"R1 errors: {string.Join(", ", result1.Errors?.Select(e => e.Code) ?? [])}, " +
                $"R2 errors: {string.Join(", ", result2.Errors?.Select(e => e.Code) ?? [])}");

            Assert.Equal(0, unexpectedFailures);
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var credit = await db.TenantCredits.FindAsync(creditId);
            Assert.NotNull(credit);
            Assert.True(credit.RemainingAmount >= 300m,
                $"Credit remaining ({credit.RemainingAmount}) should be at least 300 after max one 700 application");
            Assert.True(credit.RemainingAmount <= 1000m,
                $"Credit remaining ({credit.RemainingAmount}) should not exceed 1000");

            var totalApplied = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId)
                .SumAsync(ca => ca.Amount);
            Assert.True(totalApplied <= 700m,
                $"Total credit applied ({totalApplied}) should be at most 700 (one successful application)");
            Assert.True(totalApplied <= credit.Amount,
                $"Total credit applied ({totalApplied}) must not exceed credit amount ({credit.Amount})");
        }
    }

    // ==================================================================
    // SCENARIO 2 — Concurrent overpayment: same payment processed twice
    // Payment = 13,000, Invoice = 12,000, Overpayment = 1,000
    // Expected: exactly one credit of 1,000.
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentOverpayment_CreditCreatedExactlyOnce()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid paymentId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantId}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                12000m, 0, 0, 12000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);

            var payment = Payment.Create(
                Guid.NewGuid(), $"PAY-{tenantId}",
                13000m, "EGP", PaymentMethod.Cash).Value;
            payment.Complete(DateTime.UtcNow);
            db.Payments.Add(payment);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            paymentId = payment.Id;
            invoiceId = invoice.Id;
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = new AllocatePaymentHandler(db, Substitute.For<IAuditWriter>(), NullSubscriptionReconciliationService.Instance);
                return await handler.Handle(new AllocatePaymentCommand(paymentId, invoiceId, 13000m), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = new AllocatePaymentHandler(db, Substitute.For<IAuditWriter>(), NullSubscriptionReconciliationService.Instance);
                return await handler.Handle(new AllocatePaymentCommand(paymentId, invoiceId, 13000m), ct);
            },
            CancellationToken.None);

        var successCount = new[] { result1, result2 }.Count(r => r.IsSuccess);
        Assert.True(successCount >= 1,
            $"At least one allocation should succeed. R1: {result1.IsSuccess}, R2: {result2.IsSuccess}");

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var overpaymentCredits = await db.TenantCredits
                .Where(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == paymentId)
                .ToListAsync();
            Assert.Single(overpaymentCredits);
            Assert.Equal(1000m, overpaymentCredits[0].Amount);
            Assert.Equal(1000m, overpaymentCredits[0].RemainingAmount);
            Assert.Equal("EGP", overpaymentCredits[0].CurrencyCode);

            var dbPayment = await db.Payments.FindAsync(paymentId);
            Assert.NotNull(dbPayment);
            Assert.Equal(13000m, dbPayment.Amount);

            var dbInvoice = await db.Invoices.FindAsync(invoiceId);
            Assert.Equal(InvoiceStatus.Paid, dbInvoice!.Status);
        }
    }

    // ==================================================================
    // SCENARIO 3 — Different keys, same amount, different invoices
    // Credit = 1,000; A = 700 (key X), B = 300 (key Y)
    // Both may succeed; total consumed ≤ 1,000.
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentCreditApplications_DifferentKeys_MayBothSucceed()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid creditId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantId}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                5000m, 0, 0, 5000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);

            var credit = TenantCredit.Create(Guid.NewGuid(), 1000m, CreditSourceType.Manual).Value;
            db.TenantCredits.Add(credit);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            creditId = credit.Id;
            invoiceId = invoice.Id;
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, "key-A-700"), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 300m, "key-B-300"), ct);
            },
            CancellationToken.None);

        var unexpectedFailures = new[] { result1, result2 }
            .Count(r => !r.IsSuccess && !(r.Errors?.Any(e =>
                e.Code == "CreditApplication.ConcurrencyConflict" ||
                e.Code == "TenantCredit.InsufficientRemaining") ?? false));
        Assert.Equal(0, unexpectedFailures);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var credit = await db.TenantCredits.FindAsync(creditId);
            Assert.NotNull(credit);
            Assert.True(credit.RemainingAmount >= 0m,
                $"Credit remaining ({credit.RemainingAmount}) must not be negative");
            Assert.True(credit.RemainingAmount <= 1000m,
                $"Credit remaining ({credit.RemainingAmount}) must not exceed credit amount");

            var totalApplied = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId)
                .SumAsync(ca => ca.Amount);
            Assert.True(totalApplied <= 1000m,
                $"Total applied ({totalApplied}) must not exceed credit amount (1000)");
        }
    }

    // ==================================================================
    // SCENARIO 4 — Same IdempotencyKey: concurrent retries converge
    // Two requests with the same key and same payload.
    // Expected: exactly one logical application (idempotent convergence).
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentCreditApplications_SameIdempotencyKey_ConvergesIdempotently()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid creditId;
        Guid invoiceId;
        var sharedKey = $"idem-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantId}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                5000m, 0, 0, 5000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);

            var credit = TenantCredit.Create(Guid.NewGuid(), 1000m, CreditSourceType.Manual).Value;
            db.TenantCredits.Add(credit);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            creditId = credit.Id;
            invoiceId = invoice.Id;
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, sharedKey), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, sharedKey), ct);
            },
            CancellationToken.None);

        var unexpectedFailures = new[] { result1, result2 }
            .Count(r => !r.IsSuccess && !(r.Errors?.Any(e =>
                e.Code == "CreditApplication.ConcurrencyConflict" ||
                e.Code == "TenantCredit.InvalidApplicationAmount") ?? false));
        Assert.Equal(0, unexpectedFailures);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var credit = await db.TenantCredits.FindAsync(creditId);
            Assert.NotNull(credit);
            Assert.True(credit.RemainingAmount >= 300m,
                $"Credit remaining ({credit.RemainingAmount}) should be at least 300 (max one 700 consumed)");
            Assert.True(credit.RemainingAmount <= 1000m,
                $"Credit remaining ({credit.RemainingAmount}) must not exceed credit amount");

            var appsForSharedKey = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId && ca.IdempotencyKey == sharedKey)
                .ToListAsync();
            Assert.Single(appsForSharedKey);
            Assert.Equal(700m, appsForSharedKey[0].Amount);

            var totalApplied = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId)
                .SumAsync(ca => ca.Amount);
            Assert.Equal(700m, totalApplied);
        }
    }

    // ==================================================================
    // SCENARIO 5 — Cross-tenant credit application is rejected
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentCreditApplication_CrossTenant_IsRejected()
    {
        var tenantA = $"tenantA-{Guid.NewGuid():N}"[..20];
        var tenantB = $"tenantB-{Guid.NewGuid():N}"[..20];
        Guid creditId;
        Guid invoiceId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantA);
            await EnsureTenantExists(scope.ServiceProvider, tenantB);

            var credit = TenantCredit.Create(Guid.NewGuid(), 1000m, CreditSourceType.Manual).Value;
            db.TenantCredits.Add(credit);
            db.StampAddedTenantIds(tenantA);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantB}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                2000m, 0, 0, 2000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);
            db.StampAddedTenantIds(tenantB);

            await db.SaveChangesAsync();
            creditId = credit.Id;
            invoiceId = invoice.Id;
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantA);
            var handler = await CreateHandler(db);

            var result = await handler.Handle(
                new ApplyCreditToInvoiceCommand(creditId, invoiceId, 1000m, Guid.NewGuid().ToString("N")),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.True(
                result.Errors!.Any(e => e.Code == "TenantCredit.CrossTenant") ||
                result.Errors!.Any(e => e.Code == "Invoice.NotFound") ||
                result.Errors!.Any(e => e.Code == "TenantCredit.NotFound"),
                $"Expected cross-tenant rejection. Got: {string.Join(", ", result.Errors!.Select(e => e.Code))}");
        }
    }

    // ==================================================================
    // SCENARIO 6 — Concurrent same key, different payload
    // Same IdempotencyKey but different Amounts (700 vs 500).
    // Exactly one persists; the loser must return IdempotencyKeyConflict.
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase12_1Concurrency")]
    public async Task ConcurrentCreditApplications_SameKeyDifferentPayload_ConflictOnLoser()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        Guid creditId;
        Guid invoiceId;
        var sharedKey = $"idem-diff-{Guid.NewGuid():N}";

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var invoice = Invoice.Create(
                Guid.NewGuid(), $"INV-{tenantId}",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                5000m, 0, 0, 5000m).Value;
            invoice.Issue(DateTime.UtcNow);
            db.Invoices.Add(invoice);

            var credit = TenantCredit.Create(Guid.NewGuid(), 1000m, CreditSourceType.Manual).Value;
            db.TenantCredits.Add(credit);

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();

            creditId = credit.Id;
            invoiceId = invoice.Id;
        }

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 700m, sharedKey), ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                return await handler.Handle(new ApplyCreditToInvoiceCommand(creditId, invoiceId, 500m, sharedKey), ct);
            },
            CancellationToken.None);

        // Exactly one must succeed. The loser MUST return IdempotencyKeyConflict:
        // UPDLOCK serializes the credit-row read so the second transaction blocks,
        // then re-reads the committed state, finds the winner's CreditApplication
        // via the idempotency check, and returns IdempotencyKeyConflict.
        // ConcurrencyConflict is NOT acceptable for same-key/different-payload.
        var successCount = new[] { result1, result2 }.Count(r => r.IsSuccess);
        var loserCount = new[] { result1, result2 }
            .Count(r => r.Errors?.Any(e => e.Code == "CreditApplication.IdempotencyKeyConflict") ?? false);

        Assert.True(successCount == 1,
            $"Expected exactly 1 success but got {successCount}. " +
            $"R1: {result1.IsSuccess} ({string.Join(", ", result1.Errors?.Select(e => e.Code) ?? [])}), " +
            $"R2: {result2.IsSuccess} ({string.Join(", ", result2.Errors?.Select(e => e.Code) ?? [])})");

        Assert.Equal(1, loserCount);

        // Verify final database state
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var credit = await db.TenantCredits.FindAsync(creditId);
            Assert.NotNull(credit);
            Assert.True(credit.RemainingAmount >= 0m,
                $"Credit remaining ({credit.RemainingAmount}) must not be negative");

            // Exactly one application with this key
            var appsForKey = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId && ca.IdempotencyKey == sharedKey)
                .ToListAsync();
            Assert.Single(appsForKey);

            // The persisted amount must be either 700 or 500 (the winning amount)
            Assert.True(appsForKey[0].Amount == 700m || appsForKey[0].Amount == 500m,
                $"Persisted amount ({appsForKey[0].Amount}) should be either 700 or 500");

            // Total consumed equals exactly the winning amount
            var totalApplied = await db.CreditApplications
                .Where(ca => ca.CreditId == creditId)
                .SumAsync(ca => ca.Amount);
            Assert.Equal(appsForKey[0].Amount, totalApplied);

            // Remaining credit matches
            Assert.Equal(1000m - totalApplied, credit.RemainingAmount);
        }
    }
}
