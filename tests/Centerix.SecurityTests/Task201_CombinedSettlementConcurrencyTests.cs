namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 20.1 — Combined settlement concurrency test.
/// Verifies F-20.7: AllocatePayment + ApplyCreditToInvoice operating concurrently
/// against the same Invoice cannot over-settle or produce a negative remaining balance.
/// Uses SQL Server/Testcontainers with barrier synchronization.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task201_CombinedSettlementConcurrencyTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    public Task201_CombinedSettlementConcurrencyTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        type.GetField("_authorizedTenantId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, tenantId);
        type.GetField("_isAuthorized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(currentTenant, true);
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

    private static async Task<(Guid paymentId, Guid invoiceId, Guid creditId)> SetupInvoice(
        AppDbContext db, string tenantId, decimal invoiceTotal)
    {
        var payment = Domain.Platform.Billing.Payments.Payment.Create(
            Guid.NewGuid(), $"PAY-CSC-{Guid.NewGuid():N}"[..12],
            invoiceTotal, "EGP", Domain.Platform.Billing.Payments.Enums.PaymentMethod.Cash).Value;
        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);

        var invoice = Domain.Platform.Billing.Invoicing.Invoice.Create(
            Guid.NewGuid(),
            $"INV-CSC-{Guid.NewGuid():N}"[..12],
            new DateOnly(DateTime.UtcNow.Year, 1, 1),
            new DateOnly(DateTime.UtcNow.Year, 1, 31),
            invoiceTotal, 0, 0, invoiceTotal,
            contractId: null).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);

        var credit = Domain.Platform.Billing.Credits.TenantCredit.CreateSubscriptionChange(
            Guid.NewGuid(), 3000m, Guid.NewGuid(),
            2000m, "EGP",
            idempotencyKey: $"CSC-{Guid.NewGuid():N}").Value;
        db.TenantCredits.Add(credit);

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        return (payment.Id, invoice.Id, credit.Id);
    }

    private static async Task<(Result<Updated> Result1, Result<Updated> Result2)> ExecuteConcurrentWithBarrier(
        Func<CancellationToken, Task<Result<Updated>>> operation1,
        Func<CancellationToken, Task<Result<Updated>>> operation2,
        CancellationToken cancellationToken)
    {
        using var barrier = new Barrier(2);
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

        var result1 = await tcs1.Task;
        var result2 = await tcs2.Task;
        return (result1, result2);
    }

    /// <summary>
    /// Invoice Total = 10,000. Credit available = 3,000.
    /// Concurrent: AllocatePayment(10,000) + ApplyCreditToInvoice(3,000).
    /// Expected: exactly one succeeds (allocation fills the invoice), credit application either
    /// succeeds (invoice has 0 remaining) or fails (invoice already settled).
    /// Invoice must never show remaining &lt; 0.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Concurrency")]
    public async Task Concurrent_AllocatePayment_And_ApplyCredit_InvoiceNeverOverSettled()
    {
        var tenantId = $"tenant-csc-{Guid.NewGuid():N}"[..20];

        // Seed tenant and invoice data OUTSIDE the concurrent barrier
        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid paymentId;
        Guid invoiceId;
        Guid creditId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (paymentId, invoiceId, creditId) = await SetupInvoice(db, tenantId, 10000m);
        }

        using var cts = new CancellationTokenSource(TestTimeout);

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var guard = Substitute.For<IPlatformAdminGuard>();
                guard.EnsurePlatformAdmin().Returns(Result.Updated);
                var handler = new AllocatePaymentHandler(
                    db,
                    Substitute.For<IAuditWriter>(),
                    NullSubscriptionReconciliationService.Instance,
                    guard);
                return await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 10000m),
                    ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = new ApplyCreditToInvoiceHandler(
                    db,
                    Substitute.For<IAuditWriter>());
                return await handler.Handle(
                    new ApplyCreditToInvoiceCommand(creditId, invoiceId, 3000m, $"csc-{Guid.NewGuid():N}"),
                    ct);
            },
            cts.Token);

        // At least one must succeed
        Assert.True(result1.IsSuccess || result2.IsSuccess,
            $"Neither operation succeeded. R1: {(result1.IsSuccess ? "OK" : string.Join(", ", result1.Errors!.Select(e => e.Code)))}, " +
            $"R2: {(result2.IsSuccess ? "OK" : string.Join(", ", result2.Errors!.Select(e => e.Code)))}");

        // Verify final invoice state: remaining balance >= 0
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var invoice = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .Include(i => i.CreditApplications)
                .FirstAsync(i => i.Id == invoiceId);

            var remaining = invoice.GetRemainingAmount();
            Assert.True(remaining >= 0m,
                $"Invoice remaining balance went negative: {remaining}. Payment succeeded: {result1.IsSuccess}, Credit succeeded: {result2.IsSuccess}");

            var paidByPayment = invoice.GetPaidAmount();
            var paidByCredit = invoice.GetAppliedCreditAmount();
            var totalSettled = paidByPayment + paidByCredit;

            Assert.True(totalSettled <= invoice.TotalAmount,
                $"Over-settlement detected: {totalSettled} > {invoice.TotalAmount}");
        }
    }

    /// <summary>
    /// Partial settlement: Invoice Total = 10,000. Credit = 3,000.
    /// AllocatePayment(6,000) + ApplyCreditToInvoice(3,000) concurrently.
    /// Both can succeed (total = 9,000 &lt;= 10,000).
    /// Verify invoice remaining = 1,000.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Concurrency")]
    public async Task Concurrent_PartialAllocate_And_PartialCredit_BothSucceed()
    {
        var tenantId = $"tenant-csc2-{Guid.NewGuid():N}"[..20];

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid paymentId;
        Guid invoiceId;
        Guid creditId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (paymentId, invoiceId, creditId) = await SetupInvoice(db, tenantId, 10000m);
        }

        using var cts = new CancellationTokenSource(TestTimeout);

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var guard = Substitute.For<IPlatformAdminGuard>();
                guard.EnsurePlatformAdmin().Returns(Result.Updated);
                var handler = new AllocatePaymentHandler(
                    db,
                    Substitute.For<IAuditWriter>(),
                    NullSubscriptionReconciliationService.Instance,
                    guard);
                return await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 6000m),
                    ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = new ApplyCreditToInvoiceHandler(
                    db,
                    Substitute.For<IAuditWriter>());
                return await handler.Handle(
                    new ApplyCreditToInvoiceCommand(creditId, invoiceId, 3000m, $"csc2-{Guid.NewGuid():N}"),
                    ct);
            },
            cts.Token);

        // Both should succeed (6,000 + 3,000 = 9,000 <= 10,000)
        Assert.True(result1.IsSuccess,
            $"Payment allocation failed: {string.Join(", ", result1.Errors?.Select(e => e.Code) ?? [])}");
        Assert.True(result2.IsSuccess,
            $"Credit application failed: {string.Join(", ", result2.Errors?.Select(e => e.Code) ?? [])}");

        // Verify final state: remaining = 1,000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var invoice = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .Include(i => i.CreditApplications)
                .FirstAsync(i => i.Id == invoiceId);

            var remaining = invoice.GetRemainingAmount();
            Assert.Equal(1000m, remaining);

            var totalSettled = invoice.GetPaidAmount() + invoice.GetAppliedCreditAmount();
            Assert.Equal(9000m, totalSettled);
            Assert.Equal(invoice.TotalAmount, totalSettled + remaining);
        }
    }

    /// <summary>
    /// Over-settlement attempt: Invoice Total = 10,000. Credit = 3,000.
    /// AllocatePayment(8,000) + ApplyCreditToInvoice(3,000) concurrently.
    /// Combined settlement (8,000 + 3,000 = 11,000) exceeds the invoice total.
    /// The overpayment-capping business rule (excess becomes TenantCredit) means the
    /// allocation may still succeed AFTER the credit application commits — capped at the
    /// committed remaining (7,000) with the 1,000 excess issued as overpayment credit.
    /// The invariant under proof: total settled NEVER exceeds Invoice.TotalAmount and
    /// remaining NEVER goes negative, regardless of which operation commits first.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Task201Concurrency")]
    public async Task Concurrent_OverAllocate_And_Credit_OnlyOneCommits()
    {
        var tenantId = $"tenant-csc3-{Guid.NewGuid():N}"[..20];

        using (var scope = _env.Factory.Services.CreateScope())
        {
            await EnsureTenantExists(scope.ServiceProvider, tenantId);
        }

        Guid paymentId;
        Guid invoiceId;
        Guid creditId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (paymentId, invoiceId, creditId) = await SetupInvoice(db, tenantId, 10000m);
        }

        using var cts = new CancellationTokenSource(TestTimeout);

        var (result1, result2) = await ExecuteConcurrentWithBarrier(
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var guard = Substitute.For<IPlatformAdminGuard>();
                guard.EnsurePlatformAdmin().Returns(Result.Updated);
                var handler = new AllocatePaymentHandler(
                    db,
                    Substitute.For<IAuditWriter>(),
                    NullSubscriptionReconciliationService.Instance,
                    guard);
                return await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 8000m),
                    ct);
            },
            async ct =>
            {
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = new ApplyCreditToInvoiceHandler(
                    db,
                    Substitute.For<IAuditWriter>());
                return await handler.Handle(
                    new ApplyCreditToInvoiceCommand(creditId, invoiceId, 3000m, $"csc3-{Guid.NewGuid():N}"),
                    ct);
            },
            cts.Token);

        // At least one must succeed. Both outcomes are legitimate:
        //   - Allocation commits first → credit (3,000 > remaining 2,000) is rejected
        //     with TenantCredit.ExceedsInvoiceRemaining → exactly 1 success.
        //   - Credit commits first → allocation is capped at remaining 7,000 with the
        //     1,000 excess issued as overpayment TenantCredit → both succeed.
        // The invariant under proof is that settlement NEVER exceeds the invoice total
        // and remaining NEVER goes negative — regardless of interleaving.
        var successCount = (result1.IsSuccess ? 1 : 0) + (result2.IsSuccess ? 1 : 0);
        Assert.True(successCount >= 1,
            $"Expected at least 1 success. Got {successCount}. " +
            $"R1: {(result1.IsSuccess ? "OK" : string.Join(", ", result1.Errors?.Select(e => e.Code) ?? []))}, " +
            $"R2: {(result2.IsSuccess ? "OK" : string.Join(", ", result2.Errors?.Select(e => e.Code) ?? []))}");

        // Final invariants: remaining >= 0, total settled <= TotalAmount, ledger identity
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var invoice = await db.Invoices
                .Include(i => i.PaymentAllocations)
                .Include(i => i.CreditApplications)
                .FirstAsync(i => i.Id == invoiceId);

            var remaining = invoice.GetRemainingAmount();
            var totalSettled = invoice.GetPaidAmount() + invoice.GetAppliedCreditAmount();

            Assert.True(remaining >= 0m,
                $"Negative remaining balance: {remaining}");
            Assert.True(totalSettled <= invoice.TotalAmount,
                $"Total settled ({totalSettled}) exceeds invoice total ({invoice.TotalAmount})");
            Assert.Equal(invoice.TotalAmount, totalSettled + remaining);

            // When both succeed, the credit committed first: allocation must be capped
            // at 7,000 (with 1,000 excess as overpayment credit) and credit = 3,000.
            if (result1.IsSuccess && result2.IsSuccess)
            {
                Assert.Equal(7000m, invoice.GetPaidAmount());
                Assert.Equal(3000m, invoice.GetAppliedCreditAmount());
            }
        }
    }
}
