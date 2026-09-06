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
/// CODER TASK 3.1.1 — SQL Server Financial Concurrency & Idempotency Hardening tests.
/// Tests against REAL SQL Server to verify:
/// 1. Allocation idempotency (retry safety)
/// 2. Payment concurrency (total allocations <= Payment.Amount)
/// 3. Invoice concurrency (total allocations <= Invoice.Total)
/// 4. Ledger RunningBalance correctness under concurrent operations
/// 5. Rollback behavior
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
    // Payment Concurrency Tests
    // ==================================================================

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

        // Act: Two concurrent allocations of 7,000 each (total would be 14,000 > 10,000)
        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 7000m), CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(paymentId, invoiceId, 7000m), CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert: Exactly one succeeds, one fails
        var successCount = results.Count(r => r.IsSuccess);
        var failCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failCount);

        // Verify total allocated does not exceed payment amount
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var payment = await db.Payments
                .Include(p => p.Allocations)
                .FirstAsync(p => p.Id == paymentId);

            var totalAllocated = payment.GetAllocatedAmount();
            Assert.True(totalAllocated <= 10000m,
                $"Total allocated ({totalAllocated}) should not exceed payment amount (10000)");
        }
    }

    // ==================================================================
    // Invoice Concurrency Tests
    // ==================================================================

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

        // Act: Two concurrent allocations of 7,000 each to the same invoice
        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 7000m), CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 7000m), CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert: Exactly one succeeds, one fails
        var successCount = results.Count(r => r.IsSuccess);
        var failCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failCount);

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
                $"Total allocated ({totalAllocated}) should not exceed invoice total (10000)");
        }
    }

    // ==================================================================
    // Ledger Concurrency Tests
    // ==================================================================

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
        var task1 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 2000m), CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            return await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 3000m), CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert: Both succeed
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);

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

            // Expected: 10000 (charge) - 2000 (settlement) - 3000 (settlement) = 5000
            Assert.Equal(5000m, reconstructedBalance);

            // Verify latest RunningBalance matches reconstructed balance
            var latestEntry = entries.Last();
            Assert.Equal(5000m, latestEntry.RunningBalance);
        }
    }

    // ==================================================================
    // Rollback Tests
    // ==================================================================

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
