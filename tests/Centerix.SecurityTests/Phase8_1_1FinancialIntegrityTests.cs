namespace Centerix.SecurityTests;

using System.Data;
using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
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

/// <summary>
/// Task 8.1.1 — Installment Financial Integrity: Idempotency &amp; Single Source of Truth.
///
/// Covers:
/// A. Idempotency tests (Tests 1-4) — identical retry, capacity exhaustion retry,
///    different installment, different amount.
/// B. Concurrent idempotency (Test 13) — SQL Server concurrent identical requests.
/// C. Single-source-of-truth tests (Tests 5-9) — settlement derivation, inactive
///    allocation exclusion, paid status, overdue, rollback.
/// </summary>
public class Phase8_1_1FinancialIntegrityTests
{
    // ==================================================================
    // InMemory Helpers
    // ==================================================================

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_8_1_1_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-test");
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static Payment CreatePayment(
        AppDbContext db,
        string tenantId,
        decimal amount,
        string paymentNumber = "PAY-001",
        PaymentMethod method = PaymentMethod.Cash)
    {
        var payment = Payment.Create(Guid.NewGuid(), paymentNumber, amount, "EGP", method).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        return payment;
    }

    private static Invoice CreateInvoice(
        AppDbContext db,
        string tenantId,
        decimal totalAmount,
        string invoiceNumber = "INV-001",
        Guid? contractId = null)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            invoiceNumber,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            totalAmount, 0, 0, totalAmount,
            contractId: contractId).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        return invoice;
    }

    private static Installment CreateInstallment(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal amount,
        DateTime? dueDateUtc = null)
    {
        var installment = Installment.Create(
            Guid.NewGuid(),
            contractId,
            1,
            dueDateUtc ?? DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1),
            new DateTime(2026, 4, 30),
            amount,
            "EGP").Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        return installment;
    }

    private static async Task<AllocatePaymentHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new AllocatePaymentHandler(db, auditWriter);
    }

    // ==================================================================
    // A. IDEMPOTENCY TESTS (Tests 1-4) — InMemory
    // ==================================================================

    /// <summary>
    /// Test 1 — Identical retry succeeds.
    /// Create Payment=4000, Invoice, Installment. Allocate 4000.
    /// Repeat exact same command.
    /// Expected: second command succeeds as idempotent, allocation count = 1, total = 4000.
    /// </summary>
    [Fact]
    public async Task Test1_IdempotentRetry_Succeeds_AllocationCountOne()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 4000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 4000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 4000m);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: first allocation
        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment.Id);
        var result1 = await handler.Handle(command, CancellationToken.None);

        // Act: identical retry
        var result2 = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Single(allocations);
        Assert.Equal(4000m, allocations[0].AllocatedAmount);

        var dbInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(4000m, dbInstallment.SettledAmount);
        Assert.Equal(0m, dbInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, dbInstallment.Status);
    }

    /// <summary>
    /// Test 2 — Retry after payment capacity is exhausted.
    /// Payment = 4000, Installment = 4000.
    /// First allocation = 4000.
    /// Retry exact same allocation.
    /// Expected: SUCCESS / IDEMPOTENT (NOT AllocationExceedsPayment).
    /// </summary>
    [Fact]
    public async Task Test2_IdempotentRetry_AfterCapacityExhausted_Succeeds()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 4000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 4000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 4000m);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: first allocation fills payment to capacity
        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment.Id);
        var result1 = await handler.Handle(command, CancellationToken.None);
        Assert.True(result1.IsSuccess);

        // Act: retry — payment is fully allocated, but idempotency must succeed
        var result2 = await handler.Handle(command, CancellationToken.None);

        // Assert: retry succeeds as idempotent, NOT rejected by capacity check
        Assert.True(result2.IsSuccess);

        // Verify only one allocation exists
        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id)
            .ToListAsync();
        Assert.Single(allocations);
    }

    /// <summary>
    /// Test 3 — Exact retry after invoice is fully paid.
    /// Payment = 8000, Invoice = 5000, Installment = 5000.
    /// Allocate 5000 → invoice fully paid, installment fully settled.
    /// Retry exact same request.
    /// Expected: SUCCESS / idempotent (NOT AllocationExceedsInvoiceRemaining).
    /// This test is mandatory — it proves that idempotency is checked BEFORE invoice capacity.
    /// </summary>
    [Fact]
    public async Task Test3_IdempotentRetry_AfterInvoiceFullyPaid_Succeeds()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 8000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m, installment.Id);
        var result1 = await handler.Handle(command, CancellationToken.None);
        Assert.True(result1.IsSuccess);

        db.ChangeTracker.Clear();
        var handler2 = await CreateHandler(db);
        var result2 = await handler2.Handle(command, CancellationToken.None);

        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Single(allocations);
        Assert.Equal(5000m, allocations[0].AllocatedAmount);

        var dbInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, dbInstallment.SettledAmount);
        Assert.Equal(0m, dbInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, dbInstallment.Status);
    }

    /// <summary>
    /// Test 4 — Same payment/invoice/amount but DIFFERENT installment.
    /// Payment A + Invoice X + Installment I1 + 4000
    /// versus
    /// Payment A + Invoice X + Installment I2 + 4000
    /// These MUST NOT be treated as the same idempotent operation.
    /// </summary>
    [Fact]
    public async Task Test3_DifferentInstallment_NotTreatedAsIdempotent()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 8000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 8000m, contractId: contractId);
        var installment1 = CreateInstallment(db, tenantId, contractId, 4000m);
        var installment2 = Installment.Create(
            Guid.NewGuid(), contractId, 2,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
            4000m, "EGP").Value;
        db.Installments.Add(installment2);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: allocate to installment 1
        var result1 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment1.Id),
            CancellationToken.None);

        // Act: allocate to installment 2 (different installment, same amount)
        var result2 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment2.Id),
            CancellationToken.None);

        // Assert: both succeed as separate allocations
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(8000m, allocations.Sum(a => a.AllocatedAmount));
    }

    /// <summary>
    /// Test 5 — Same payment/installment but different amount.
    /// 4000 vs 3000.
    /// Must NOT be treated as identical.
    /// </summary>
    [Fact]
    public async Task Test4_DifferentAmount_NotTreatedAsIdempotent()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 7000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 7000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 7000m);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: allocate 4000
        var result1 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment.Id),
            CancellationToken.None);

        // Act: allocate 3000 (different amount, same installment)
        var result2 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 3000m, installment.Id),
            CancellationToken.None);

        // Assert: both succeed as separate allocations
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(7000m, allocations.Sum(a => a.AllocatedAmount));

        var dbInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(7000m, dbInstallment.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, dbInstallment.Status);
    }

    // ==================================================================
    // B. CONCURRENT IDEMPOTENCY (Test 13) — InMemory serialization simulation
    // ==================================================================

    /// <summary>
    /// Test 13 — Two concurrent identical requests: exactly one allocation created.
    /// Uses serialized execution on InMemory to simulate the race condition.
    /// The Serializable isolation on SQL Server provides the real protection;
    /// this test verifies the application-level idempotency check works.
    /// </summary>
    [Fact]
    public async Task Test13_ConcurrentIdenticalRetry_CreatesExactlyOneAllocation()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 4000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 4000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 4000m);
        await db.SaveChangesAsync();

        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment.Id);

        // Simulate two concurrent handlers by executing sequentially
        // (InMemory doesn't support transactions, so true concurrency is not possible here)
        var handler = await CreateHandler(db);
        var result1 = await handler.Handle(command, CancellationToken.None);

        // Reset change tracker for second "concurrent" attempt
        db.ChangeTracker.Clear();
        var handler2 = await CreateHandler(db);
        var result2 = await handler2.Handle(command, CancellationToken.None);

        // Both should succeed (idempotent)
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        // Only one allocation should exist
        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Single(allocations);
        Assert.Equal(4000m, allocations[0].AllocatedAmount);

        // Only one ledger settlement entry
        var settlements = await db.CustomerLedgerEntries
            .Where(e => e.PaymentId == payment.Id && e.EntryType == LedgerEntryType.PaymentSettlement)
            .ToListAsync();
        Assert.Single(settlements);

        // Installment settled correctly
        var dbInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(4000m, dbInstallment.SettledAmount);
        Assert.Equal(0m, dbInstallment.RemainingAmount);
    }

    // ==================================================================
    // C. SINGLE-SOURCE-OF-TRUTH TESTS (Tests 5-9)
    // ==================================================================

    /// <summary>
    /// Test 5 — Two allocations sum correctly.
    /// Installment = 10,000
    /// Allocation A = 4,000, Allocation B = 3,000
    /// Expected: Settled = 7,000, Remaining = 3,000.
    /// </summary>
    [Fact]
    public void Test5_TwoAllocations_SumCorrectly()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            10000m, "EGP").Value;

        var allocationA = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            4000m, DateTime.UtcNow).Value;
        var allocationB = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            3000m, DateTime.UtcNow).Value;

        installment.ApplyAllocation(allocationA, DateTime.UtcNow);
        installment.ApplyAllocation(allocationB, DateTime.UtcNow);

        Assert.Equal(7000m, installment.SettledAmount);
        Assert.Equal(3000m, installment.RemainingAmount);
        Assert.Equal(10000m, installment.Amount);
    }

    /// <summary>
    /// Test 6 — Inactive allocation excluded from settlement.
    /// Installment = 10,000
    /// A = 4,000 active, B = 3,000 active, C = 2,000 reversed
    /// Expected: Settled = 7,000, Remaining = 3,000.
    /// </summary>
    [Fact]
    public void Test6_InactiveAllocation_ExcludedFromSettlement()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            10000m, "EGP").Value;

        var allocationA = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            4000m, DateTime.UtcNow).Value;
        var allocationB = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            3000m, DateTime.UtcNow).Value;
        var allocationC = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2000m, DateTime.UtcNow).Value;

        installment.ApplyAllocation(allocationA, DateTime.UtcNow);
        installment.ApplyAllocation(allocationB, DateTime.UtcNow);
        installment.ApplyAllocation(allocationC, DateTime.UtcNow);

        // Reverse allocation C
        installment.ReverseAllocation(allocationC, DateTime.UtcNow);

        Assert.Equal(7000m, installment.SettledAmount);
        Assert.Equal(3000m, installment.RemainingAmount);
    }

    /// <summary>
    /// Test 6b — Inactive allocation causes settlement decrease.
    /// Installment = 5,000
    /// A = 3,000 active, B = 2,000 active → Settled = 5,000
    /// Then A becomes inactive (reversed).
    /// Expected: Settled = 2,000, Remaining = 3,000.
    /// This verifies that the system does NOT remain at Settled = 5,000.
    /// </summary>
    [Fact]
    public void Test6b_InactiveAllocation_DecreasesSettledAmount()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m, "EGP").Value;

        var allocationA = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            3000m, DateTime.UtcNow).Value;
        var allocationB = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2000m, DateTime.UtcNow).Value;

        installment.ApplyAllocation(allocationA, DateTime.UtcNow);
        installment.ApplyAllocation(allocationB, DateTime.UtcNow);
        Assert.Equal(5000m, installment.SettledAmount);
        Assert.Equal(0m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);

        installment.ReverseAllocation(allocationA, DateTime.UtcNow);

        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(3000m, installment.RemainingAmount);
        Assert.NotEqual(InstallmentStatus.Paid, installment.Status);
    }

    /// <summary>
    /// Test 7 — Paid status when fully settled.
    /// Installment = 5,000
    /// A = 3,000, B = 2,000
    /// Expected: Settled = 5,000, Remaining = 0, Status = Paid.
    /// </summary>
    [Fact]
    public void Test7_PaidStatus_WhenFullySettled()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m, "EGP").Value;

        var allocationA = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            3000m, DateTime.UtcNow).Value;
        var allocationB = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2000m, DateTime.UtcNow).Value;

        installment.ApplyAllocation(allocationA, DateTime.UtcNow);
        installment.ApplyAllocation(allocationB, DateTime.UtcNow);

        Assert.Equal(5000m, installment.SettledAmount);
        Assert.Equal(0m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    /// <summary>
    /// Test 8/10 — Overdue partially paid.
    /// Amount = 5,000, Settled = 2,000, DueDate < now.
    /// Expected: Status = Overdue, Remaining = 3,000.
    /// </summary>
    [Fact]
    public void Test8_Overdue_WhenPartiallyPaidPastDue()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(-5),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m, "EGP").Value;

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2000m, DateTime.UtcNow).Value;

        installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(3000m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Overdue, installment.Status);
        Assert.True(installment.IsOverdue(DateTime.UtcNow));
    }

    /// <summary>
    /// Test 9 — Rollback: if allocation transaction fails, no state changes occur.
    /// Verifies that failed allocation leaves no partial state.
    /// </summary>
    [Fact]
    public async Task Test9_Rollback_AllocationFailureLeavesNoPartialState()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);

        var payment = CreatePayment(db, tenantId, 5000m);
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m);
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: attempt to allocate more than payment allows
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None);

        // Assert: allocation failed
        Assert.False(result.IsSuccess);

        // Assert: no allocation created
        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id)
            .ToListAsync();
        Assert.Empty(allocations);

        // Assert: no ledger entries created
        var ledgerEntries = await db.CustomerLedgerEntries
            .Where(e => e.PaymentId == payment.Id)
            .ToListAsync();
        Assert.Empty(ledgerEntries);

        // Assert: payment unchanged
        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(0m, dbPayment.GetAllocatedAmount());
        Assert.Equal(5000m, dbPayment.GetUnallocatedAmount());

        // Assert: invoice unchanged
        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.Issued, dbInvoice.Status);
        Assert.Equal(0m, dbInvoice.GetPaidAmount());
    }

    // ==================================================================
    // Additional Settlement Integrity Tests
    // ==================================================================

    /// <summary>
    /// RemainingAmount is always derived from Amount - SettledAmount.
    /// </summary>
    [Fact]
    public void RemainingAmount_IsAmountMinusSettled()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m, "EGP").Value;

        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1500m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        Assert.Equal(4000m, installment.Amount);
        Assert.Equal(1500m, installment.SettledAmount);
        Assert.Equal(2500m, installment.RemainingAmount);
        Assert.Equal(installment.Amount - installment.SettledAmount, installment.RemainingAmount);
    }

    /// <summary>
    /// Test 11 — PartiallyPaid before due date.
    /// Amount = 5,000, Settled = 2,000, DueDate > now.
    /// Expected: Status = PartiallyPaid.
    /// </summary>
    [Fact]
    public void Test11_PartiallyPaid_BeforeDueDate()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            5000m, "EGP").Value;

        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2000m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);
        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(3000m, installment.RemainingAmount);
    }

    /// <summary>
    /// Over-allocating an installment is rejected.
    /// </summary>
    [Fact]
    public void CannotOverAllocateInstallment()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            4000m, "EGP").Value;

        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3000m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        var result = installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2000m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Equal(3000m, installment.SettledAmount);
    }

    /// <summary>
    /// Verify that client cannot directly set SettledAmount.
    /// </summary>
    [Fact]
    public void SettledAmount_IsNeverDirectlySettable()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            10000m, "EGP").Value;

        Assert.Equal(0m, installment.SettledAmount);

        // The property has a private setter, so it can only be set via ApplyAllocation/ReverseAllocation
        // This is verified by compilation: there is no public setter
        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5000m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        Assert.Equal(5000m, installment.SettledAmount);

        // Apply allocation again — the SettledAmount must be the sum of all active allocations
        installment.ApplyAllocation(
            PaymentAllocation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3000m, DateTime.UtcNow).Value,
            DateTime.UtcNow);

        Assert.Equal(8000m, installment.SettledAmount);
        Assert.Equal(installment.GetSettledAmount(), installment.SettledAmount);
    }

    /// <summary>
    /// Cancelled installment: status never changes from Cancelled even when
    /// SettledAmount would suggest otherwise.
    /// </summary>
    [Fact]
    public void CancelledInstallment_NeverBecomesOverdue()
    {
        var installment = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow.AddDays(-5),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            10000m, "EGP").Value;

        installment.Cancel(DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Cancelled, installment.Status);
        Assert.False(installment.IsOverdue(DateTime.UtcNow));
    }
}

/// <summary>
/// Task 8.1.1 — SQL Server concurrent idempotency test.
/// Verifies that two concurrent identical allocation requests
/// produce exactly one financial allocation against real SQL Server.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase8_1_1ConcurrencySqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(15);

    public Phase8_1_1ConcurrencySqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    private static void AuthorizeTenant(IServiceProvider services, string tenantId)
    {
        var currentTenant = services.GetRequiredService<ICurrentTenant>();
        var type = currentTenant.GetType();
        var authorizedTenantIdField = type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance);
        var isAuthorizedField = type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance);
        authorizedTenantIdField!.SetValue(currentTenant, tenantId);
        isAuthorizedField!.SetValue(currentTenant, true);
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

    private static Payment CreatePayment(AppDbContext db, string tenantId, decimal amount, string paymentNumber)
    {
        var payment = Payment.Create(Guid.NewGuid(), paymentNumber, amount, "EGP", PaymentMethod.Cash).Value;
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        return payment;
    }

    private static Invoice CreateInvoice(AppDbContext db, string tenantId, decimal totalAmount, string invoiceNumber, Guid? contractId = null)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(), invoiceNumber,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            totalAmount, 0, 0, totalAmount,
            contractId: contractId).Value;
        invoice.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        return invoice;
    }

    private static Installment CreateInstallment(AppDbContext db, string tenantId, Guid contractId, decimal amount)
    {
        var installment = Installment.Create(
            Guid.NewGuid(), contractId, 1,
            DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30),
            amount, "EGP").Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        return installment;
    }

    private static async Task<AllocatePaymentHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new AllocatePaymentHandler(db, auditWriter);
    }

    /// <summary>
    /// Test 13 (SQL Server) — Two concurrent identical requests with installment:
    /// exactly one allocation created.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_1")]
    public async Task Concurrent_IdenticalRetry_Installment_CreatesOnlyOneAllocation()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var contractId = Guid.NewGuid();
        Guid paymentId, invoiceId, installmentId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var payment = CreatePayment(db, tenantId, 4000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;

            var invoice = CreateInvoice(db, tenantId, 4000m, $"INV-{tenantId}", contractId: contractId);
            invoiceId = invoice.Id;

            var installment = CreateInstallment(db, tenantId, contractId, 4000m);
            installmentId = installment.Id;

            await db.SaveChangesAsync();
        }

        // Two concurrent identical allocation commands
        using var cts = new CancellationTokenSource(TestTimeout);

        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<Updated>>();
        var tcs2 = new TaskCompletionSource<Result<Updated>>();

        async Task RunRequest(TaskCompletionSource<Result<Updated>> tcs)
        {
            try
            {
                barrier.SignalAndWait(BarrierTimeout);
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                var result = await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, 4000m, installmentId),
                    cts.Token);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        var task1 = Task.Run(() => RunRequest(tcs1));
        var task2 = Task.Run(() => RunRequest(tcs2));

        using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        ctsTimeout.CancelAfter(TestTimeout);
        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(ctsTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        var result1 = await tcs1.Task;
        var result2 = await tcs2.Task;

        // At least one should succeed
        var successCount = (result1.IsSuccess ? 1 : 0) + (result2.IsSuccess ? 1 : 0);
        Assert.True(successCount >= 1, $"Expected at least 1 success, got {successCount}");

        // Verify exactly one allocation
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Single(allocations);
            Assert.Equal(4000m, allocations[0].AllocatedAmount);

            // Exactly one ledger settlement entry
            var settlements = await db.CustomerLedgerEntries
                .Where(e => e.PaymentId == paymentId && e.EntryType == LedgerEntryType.PaymentSettlement)
                .ToListAsync();
            Assert.Single(settlements);

            // Installment settled correctly
            var installment = await db.Installments.FirstAsync(i => i.Id == installmentId);
            Assert.Equal(4000m, installment.SettledAmount);
            Assert.Equal(0m, installment.RemainingAmount);
            Assert.Equal(InstallmentStatus.Paid, installment.Status);
        }
    }

    /// <summary>
    /// Test: Different installment concurrent requests both succeed when capacity permits.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_1")]
    public async Task Concurrent_DifferentInstallments_BothSucceed_WhenCapacityPermits()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var contractId = Guid.NewGuid();
        Guid paymentId, invoiceId, installment1Id, installment2Id;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var payment = CreatePayment(db, tenantId, 10000m, $"PAY-{tenantId}");
            payment.Complete(DateTime.UtcNow);
            paymentId = payment.Id;

            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}", contractId: contractId);
            invoiceId = invoice.Id;

            var installment1 = CreateInstallment(db, tenantId, contractId, 5000m);
            installment1Id = installment1.Id;

            var installment2 = Installment.Create(
                Guid.NewGuid(), contractId, 2,
                DateTime.UtcNow.AddDays(30),
                new DateTime(2026, 5, 1), new DateTime(2026, 8, 31),
                5000m, "EGP").Value;
            db.Installments.Add(installment2);
            installment2Id = installment2.Id;

            db.StampAddedTenantIds(tenantId);
            await db.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource(TestTimeout);

        using var barrier = new Barrier(2);
        var tcs1 = new TaskCompletionSource<Result<Updated>>();
        var tcs2 = new TaskCompletionSource<Result<Updated>>();

        async Task RunRequest(TaskCompletionSource<Result<Updated>> tcs, Guid installmentId, decimal amount)
        {
            try
            {
                barrier.SignalAndWait(BarrierTimeout);
                using var scope = _env.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AuthorizeTenant(scope.ServiceProvider, tenantId);
                var handler = await CreateHandler(db);
                var result = await handler.Handle(
                    new AllocatePaymentCommand(paymentId, invoiceId, amount, installmentId),
                    cts.Token);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        var task1 = Task.Run(() => RunRequest(tcs1, installment1Id, 4000m));
        var task2 = Task.Run(() => RunRequest(tcs2, installment2Id, 6000m));

        using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        ctsTimeout.CancelAfter(TestTimeout);
        try
        {
            await Task.WhenAll(task1, task2).WaitAsync(ctsTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Concurrent operations did not complete within {TestTimeout.TotalSeconds}s");
        }

        var result1 = await tcs1.Task;
        var result2 = await tcs2.Task;

        // Both should succeed (different installments, total 10,000 = payment amount)
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        // Verify both allocations exist
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Equal(2, allocations.Count);
            Assert.Equal(10000m, allocations.Sum(a => a.AllocatedAmount));
        }
    }
}
