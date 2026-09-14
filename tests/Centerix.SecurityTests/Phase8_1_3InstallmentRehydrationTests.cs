namespace Centerix.SecurityTests;

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
/// Task 8.1.3 — Installment Settlement Rehydration Integrity.
///
/// Proves that historical PaymentAllocations are correctly loaded and included
/// in settlement calculation when a new allocation is applied through the handler.
/// All tests use persisted/reloaded data (ChangeTracker.Clear) to prove the real persistence path.
/// </summary>
public class Phase8_1_3InstallmentRehydrationTests
{
    // ==================================================================
    // InMemory Helpers
    // ==================================================================

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_8_1_3_{Guid.NewGuid():N}";
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
            "EGP",
            Guid.NewGuid()).Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        return installment;
    }

    private static async Task<AllocatePaymentHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new AllocatePaymentHandler(db, auditWriter, NullSubscriptionReconciliationService.Instance);
    }

    /// <summary>
    /// Helper: creates a completed payment, saves it, runs the allocation handler with a fresh context.
    /// </summary>
    private async Task<(bool IsSuccess, AppDbContext Db)> AllocateAsync(
        AppDbContext db, string tenantId, Guid paymentId, Guid invoiceId, decimal amount, Guid installmentId)
    {
        db.ChangeTracker.Clear();
        var handler = await CreateHandler(db);
        var result = await handler.Handle(
            new AllocatePaymentCommand(paymentId, invoiceId, amount, installmentId),
            CancellationToken.None);
        return (result.IsSuccess, db);
    }

    /// <summary>
    /// Helper: creates a fresh payment, saves it, allocates via handler with a fresh context.
    /// </summary>
    private async Task<bool> AllocateNewPaymentAsync(
        AppDbContext db, string tenantId, Guid invoiceId, decimal amount, Guid installmentId, string paymentNumber)
    {
        db.ChangeTracker.Clear();
        var payment = CreatePayment(db, tenantId, amount, paymentNumber);
        payment.Complete(DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var handler = await CreateHandler(db);
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoiceId, amount, installmentId),
            CancellationToken.None);
        return result.IsSuccess;
    }

    // ==================================================================
    // Section 7 — Existing Allocation History + New Allocation (Partial)
    // ==================================================================

    /// <summary>
    /// Section 7: Installment = 10000, Existing A=3000, B=2000.
    /// Persist, clear context, reload. New allocation C=1000.
    /// Expected: Settled=6000, Remaining=4000, Status=PartiallyPaid.
    /// </summary>
    [Fact]
    public async Task S7_ExistingAllocations_PlusNew_PartialSettlement_Correct()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 5000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 1000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate A=3000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // Verify intermediate state
        db.ChangeTracker.Clear();
        var midInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, midInstallment.SettledAmount);

        // Allocate C=1000 (fresh context — proves rehydration)
        var (r3, _) = await AllocateAsync(db, tenantId, payment3.Id, invoice.Id, 1000m, installment.Id);
        Assert.True(r3);

        // Verify final state from database
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(6000m, finalInstallment.SettledAmount);
        Assert.Equal(4000m, finalInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.PartiallyPaid, finalInstallment.Status);

        // Verify persisted SettledAmount matches allocation sum
        var allAllocations = await db.PaymentAllocations
            .Where(a => a.InstallmentId == installment.Id && a.Status == PaymentAllocationStatus.Active)
            .ToListAsync();
        Assert.Equal(6000m, allAllocations.Sum(a => a.AllocatedAmount));
        Assert.Equal(finalInstallment.SettledAmount, allAllocations.Sum(a => a.AllocatedAmount));
    }

    // ==================================================================
    // Section 8 — Existing + New = Full Settlement
    // ==================================================================

    /// <summary>
    /// Section 8: Installment = 5000, Existing A=3000, New B=2000.
    /// Expected: Settled=5000, Remaining=0, Status=Paid.
    /// </summary>
    [Fact]
    public async Task S8_ExistingAllocations_PlusNew_FullSettlement_Paid()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 5000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 2000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        // Allocate A=3000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // Verify
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, finalInstallment.SettledAmount);
        Assert.Equal(0m, finalInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, finalInstallment.Status);
    }

    // ==================================================================
    // Section 9 — Over-allocation rejected after historical allocations
    // ==================================================================

    /// <summary>
    /// Section 9: Installment = 5000, Existing A=4000, New B=1500.
    /// Expected: AllocationExceedsInstallment, no side effects.
    /// </summary>
    [Fact]
    public async Task S9_ExistingAllocations_NewExceeds_Rejected_NoSideEffects()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 5000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 1500m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        // Allocate A=4000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 4000m, installment.Id);
        Assert.True(r1);

        // Verify intermediate
        db.ChangeTracker.Clear();
        var midInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(4000m, midInstallment.SettledAmount);

        // Attempt B=1500 — exceeds remaining 1000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 1500m, installment.Id);

        // Must be rejected
        Assert.False(r2);

        // Verify NO side effects
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(4000m, finalInstallment.SettledAmount);
        Assert.Equal(1000m, finalInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.PartiallyPaid, finalInstallment.Status);

        // No new allocation created for the failed attempt
        var allocationCount = await db.PaymentAllocations
            .Where(a => a.InstallmentId == installment.Id && a.Status == PaymentAllocationStatus.Active)
            .CountAsync();
        Assert.Equal(1, allocationCount);
    }

    // ==================================================================
    // Section 10 — EF Relationship Fixup: no double-count
    // ==================================================================

    /// <summary>
    /// Section 10: Tests that EF relationship fixup does not double-count
    /// or bypass capacity validation when dbContext.PaymentAllocations.Add()
    /// triggers navigation fixup before ApplyAllocation().
    /// </summary>
    [Fact]
    public async Task S10_EfRelationshipFixup_NoDoubleCount_NoBypass()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // ── Step 1: Load installment with allocations ──
        var loadedInstallment = await db.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installment.Id);

        Assert.Empty(loadedInstallment.PaymentAllocations);

        // ── Step 2: Create allocation and add to context ──
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, 3000m, DateTime.UtcNow, installment.Id).Value;

        db.PaymentAllocations.Add(allocation);

        // ── Step 3: ApplyAllocation ──
        var result = loadedInstallment.ApplyAllocation(allocation, DateTime.UtcNow);
        Assert.True(result.IsSuccess);

        // ── Verify: NO double-count ──
        Assert.Equal(3000m, loadedInstallment.SettledAmount);
        Assert.Equal(7000m, loadedInstallment.RemainingAmount);

        // The allocation should appear exactly once in the collection
        var allocationCount = loadedInstallment.PaymentAllocations
            .Count(a => a.Id == allocation.Id);
        Assert.Equal(1, allocationCount);

        // ── Step 4: Verify capacity validation still works ──
        var overAllocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, 8000m, DateTime.UtcNow, installment.Id).Value;

        var overResult = loadedInstallment.ApplyAllocation(overAllocation, DateTime.UtcNow);
        Assert.False(overResult.IsSuccess);

        Assert.Equal(3000m, loadedInstallment.SettledAmount);
    }

    /// <summary>
    /// Section 10b: EF fixup with existing historical allocations.
    /// Proves no double-count when adding to an installment that already has allocations.
    /// </summary>
    [Fact]
    public async Task S10b_EfFixup_WithHistoricalAllocations_NoDoubleCount()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 5000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 1000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate A=3000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // ── Clear context, load fresh ──
        db.ChangeTracker.Clear();
        var loadedInstallment = await db.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installment.Id);

        Assert.Equal(2, loadedInstallment.PaymentAllocations.Count);
        Assert.Equal(5000m, loadedInstallment.SettledAmount);

        // ── Add a new allocation via EF + ApplyAllocation ──
        var newAllocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment3.Id, invoice.Id, 1000m, DateTime.UtcNow, installment.Id).Value;

        db.PaymentAllocations.Add(newAllocation);
        db.StampAddedTenantIds(tenantId);

        var result3 = loadedInstallment.ApplyAllocation(newAllocation, DateTime.UtcNow);
        Assert.True(result3.IsSuccess);

        // Verify: 3000 + 2000 + 1000 = 6000 (NO double-count)
        Assert.Equal(6000m, loadedInstallment.SettledAmount);
        Assert.Equal(4000m, loadedInstallment.RemainingAmount);

        // Verify exactly 3 allocations in collection (no duplicates)
        var totalCount = loadedInstallment.PaymentAllocations.Count;
        Assert.Equal(3, totalCount);

        // Verify persisted state
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persistedInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(6000m, persistedInstallment.SettledAmount);
        Assert.Equal(InstallmentStatus.PartiallyPaid, persistedInstallment.Status);
    }

    // ==================================================================
    // Section 11 — Reversal after persistence/reload
    // ==================================================================

    /// <summary>
    /// Section 11: Persist A=3000, B=2000. Reload. Reverse A.
    /// Expected: Settled=2000, Remaining=Amount-2000.
    /// </summary>
    [Fact]
    public async Task S11_ReversalAfterPersistence_RecalculatesCorrectly()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 5000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 2000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        // Allocate A=3000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // Verify intermediate: fully paid
        db.ChangeTracker.Clear();
        var midInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, midInstallment.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, midInstallment.Status);

        // ── Reload from database (fresh context) ──
        db.ChangeTracker.Clear();
        var freshInstallment = await db.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installment.Id);

        // Find allocation A (the 3000 one)
        var allocationA = freshInstallment.PaymentAllocations
            .First(a => a.AllocatedAmount == 3000m);

        // Reverse allocation A
        var reverseResult = freshInstallment.ReverseAllocation(allocationA, DateTime.UtcNow);
        Assert.True(reverseResult.IsSuccess);

        // Save
        await db.SaveChangesAsync();

        // Verify: 5000 - 3000 = 2000 remaining settled
        Assert.Equal(2000m, freshInstallment.SettledAmount);
        Assert.Equal(3000m, freshInstallment.RemainingAmount);
        Assert.NotEqual(InstallmentStatus.Paid, freshInstallment.Status);

        // Verify from fresh database read
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(2000m, finalInstallment.SettledAmount);
        Assert.Equal(3000m, finalInstallment.RemainingAmount);

        // Verify persisted allocation status
        var reversedAllocation = await db.PaymentAllocations.FirstAsync(a => a.Id == allocationA.Id);
        Assert.Equal(PaymentAllocationStatus.Reversed, reversedAllocation.Status);
    }

    /// <summary>
    /// Section 11b: After reversal, new allocation can be applied.
    /// </summary>
    [Fact]
    public async Task S11b_ReversalThenNewAllocation_CorrectSettlement()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 2000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 1000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate A=3000 (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // ── Reload, reverse A ──
        db.ChangeTracker.Clear();
        var installmentLoaded = await db.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installment.Id);

        var allocA = installmentLoaded.PaymentAllocations.First(a => a.AllocatedAmount == 3000m);
        installmentLoaded.ReverseAllocation(allocA, DateTime.UtcNow);
        await db.SaveChangesAsync();

        // ── Reload again, add new allocation C=1000 ──
        var (r3, _) = await AllocateAsync(db, tenantId, payment3.Id, invoice.Id, 1000m, installment.Id);
        Assert.True(r3);

        // Verify: 3000(reversed) + 2000(active) + 1000(active) = 3000
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(3000m, finalInstallment.SettledAmount);
        Assert.Equal(7000m, finalInstallment.RemainingAmount);

        // Verify active allocation sum matches
        var activeSum = await db.PaymentAllocations
            .Where(a => a.InstallmentId == installment.Id && a.Status == PaymentAllocationStatus.Active)
            .SumAsync(a => a.AllocatedAmount);
        Assert.Equal(finalInstallment.SettledAmount, activeSum);
    }

    // ==================================================================
    // Section 12 — Idempotency still works
    // ==================================================================

    /// <summary>
    /// Section 12a: Idempotent retry succeeds after full allocation.
    /// </summary>
    [Fact]
    public async Task S12a_IdempotentRetry_ExactSameAllocation_Succeeds()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 5000m, "PAY-001");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m, installment.Id);

        // First allocation
        var (r1, _) = await AllocateAsync(db, tenantId, payment.Id, invoice.Id, 5000m, installment.Id);
        Assert.True(r1);

        // Identical retry (fresh context)
        db.ChangeTracker.Clear();
        var handler2 = await CreateHandler(db);
        var result2 = await handler2.Handle(command, CancellationToken.None);
        Assert.True(result2.IsSuccess);

        // Only one allocation should exist
        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Single(allocations);
        Assert.Equal(5000m, allocations[0].AllocatedAmount);

        // Installment correctly settled
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, finalInstallment.SettledAmount);
        Assert.Equal(0m, finalInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, finalInstallment.Status);
    }

    /// <summary>
    /// Section 12b: Idempotent retry after payment capacity exhausted.
    /// </summary>
    [Fact]
    public async Task S12b_IdempotentRetry_AfterCapacityExhausted_Succeeds()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 4000m, "PAY-001");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 4000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 4000m);
        await db.SaveChangesAsync();

        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m, installment.Id);

        // Fill payment to capacity
        var (r1, _) = await AllocateAsync(db, tenantId, payment.Id, invoice.Id, 4000m, installment.Id);
        Assert.True(r1);

        // Retry — payment is fully allocated, but idempotency must succeed
        db.ChangeTracker.Clear();
        var handler2 = await CreateHandler(db);
        var result2 = await handler2.Handle(command, CancellationToken.None);
        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id)
            .ToListAsync();
        Assert.Single(allocations);
    }

    /// <summary>
    /// Section 12c: Idempotent retry after installment fully paid.
    /// </summary>
    [Fact]
    public async Task S12c_IdempotentRetry_AfterInstallmentFullyPaid_Succeeds()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 8000m, "PAY-001");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 5000m);
        await db.SaveChangesAsync();

        var command = new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m, installment.Id);

        // Fill installment to capacity
        var (r1, _) = await AllocateAsync(db, tenantId, payment.Id, invoice.Id, 5000m, installment.Id);
        Assert.True(r1);

        // Verify installment is fully paid
        db.ChangeTracker.Clear();
        var midInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, midInstallment.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, midInstallment.Status);

        // Retry — installment fully paid, but idempotency must succeed
        db.ChangeTracker.Clear();
        var handler2 = await CreateHandler(db);
        var result2 = await handler2.Handle(command, CancellationToken.None);
        Assert.True(result2.IsSuccess);

        var allocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Single(allocations);

        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, finalInstallment.SettledAmount);
        Assert.Equal(InstallmentStatus.Paid, finalInstallment.Status);
    }

    // ==================================================================
    // Additional Invariant Tests
    // ==================================================================

    /// <summary>
    /// Verifies the authoritative invariant: Persisted SettledAmount
    /// always equals SUM(active PaymentAllocation.AllocatedAmount).
    /// </summary>
    [Fact]
    public async Task Invariant_PersistedSettledAmount_EqualsActiveAllocationSum()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 5000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 3000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate A=3000
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // Allocate B=2000
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // Allocate C=1500
        var (r3, _) = await AllocateAsync(db, tenantId, payment3.Id, invoice.Id, 1500m, installment.Id);
        Assert.True(r3);

        // ── Verify invariant ──
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        var activeSum = await db.PaymentAllocations
            .Where(a => a.InstallmentId == installment.Id && a.Status == PaymentAllocationStatus.Active)
            .SumAsync(a => a.AllocatedAmount);

        Assert.Equal(finalInstallment.SettledAmount, activeSum);
        Assert.Equal(6500m, finalInstallment.SettledAmount);
        Assert.Equal(3500m, finalInstallment.RemainingAmount);
    }

    /// <summary>
    /// Verifies that inactive allocations are excluded from settlement.
    /// </summary>
    [Fact]
    public async Task Invariant_InactiveAllocations_ExcludedFromSettlement()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 6000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 2000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 1000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 6000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 6000m);
        await db.SaveChangesAsync();

        // A=3000
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 3000m, installment.Id);
        Assert.True(r1);

        // B=2000
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 2000m, installment.Id);
        Assert.True(r2);

        // C=1000
        var (r3, _) = await AllocateAsync(db, tenantId, payment3.Id, invoice.Id, 1000m, installment.Id);
        Assert.True(r3);

        // ── Reload and reverse C ──
        db.ChangeTracker.Clear();
        var installmentLoaded = await db.Installments
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == installment.Id);

        var allocC = installmentLoaded.PaymentAllocations.First(a => a.AllocatedAmount == 1000m);
        installmentLoaded.ReverseAllocation(allocC, DateTime.UtcNow);
        await db.SaveChangesAsync();

        // Verify: 3000 + 2000 = 5000 (C excluded)
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(5000m, finalInstallment.SettledAmount);
        Assert.Equal(1000m, finalInstallment.RemainingAmount);

        var activeSum = await db.PaymentAllocations
            .Where(a => a.InstallmentId == installment.Id && a.Status == PaymentAllocationStatus.Active)
            .SumAsync(a => a.AllocatedAmount);
        Assert.Equal(finalInstallment.SettledAmount, activeSum);
    }

    /// <summary>
    /// After every allocation, RemainingAmount = Amount - SettledAmount and
    /// Status is deterministic.
    /// </summary>
    [Fact]
    public async Task Invariant_RemainingAndStatus_AreDeterministic()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 6000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate 4000 → PartiallyPaid
        var (r1, _) = await AllocateAsync(db, tenantId, payment.Id, invoice.Id, 4000m, installment.Id);
        Assert.True(r1);

        db.ChangeTracker.Clear();
        var i1 = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(4000m, i1.SettledAmount);
        Assert.Equal(6000m, i1.RemainingAmount);
        Assert.Equal(10000m - 4000m, i1.RemainingAmount);
        Assert.Equal(InstallmentStatus.PartiallyPaid, i1.Status);

        // Allocate 6000 more → Paid
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 6000m, installment.Id);
        Assert.True(r2);

        db.ChangeTracker.Clear();
        var i2 = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(10000m, i2.SettledAmount);
        Assert.Equal(0m, i2.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, i2.Status);
    }

    /// <summary>
    /// Verifies that the handler correctly loads PaymentAllocations when
    /// validating installment capacity. This is the core regression test
    /// for the rehydration bug.
    /// </summary>
    [Fact]
    public async Task Handler_LoadsAllocations_WhenValidatingCapacity()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var db = CreateDbContext(tenantId);
        var contractId = Guid.NewGuid();

        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-001");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 5000m, "PAY-002");
        payment2.Complete(DateTime.UtcNow);
        var payment3 = CreatePayment(db, tenantId, 4000m, "PAY-003");
        payment3.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, contractId: contractId);
        var installment = CreateInstallment(db, tenantId, contractId, 10000m);
        await db.SaveChangesAsync();

        // Allocate A=6000 through handler (fresh context)
        var (r1, _) = await AllocateAsync(db, tenantId, payment1.Id, invoice.Id, 6000m, installment.Id);
        Assert.True(r1);

        // Attempt to allocate B=5000 — exceeds remaining 4000 (fresh context)
        var (r2, _) = await AllocateAsync(db, tenantId, payment2.Id, invoice.Id, 5000m, installment.Id);

        // Must be rejected because 6000 + 5000 = 11000 > 10000
        Assert.False(r2);

        // Verify installment unchanged
        db.ChangeTracker.Clear();
        var finalInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(6000m, finalInstallment.SettledAmount);
        Assert.Equal(4000m, finalInstallment.RemainingAmount);

        // Now allocate B=4000 — fits within remaining (fresh context)
        var (r3, _) = await AllocateAsync(db, tenantId, payment3.Id, invoice.Id, 4000m, installment.Id);
        Assert.True(r3);

        db.ChangeTracker.Clear();
        var settledInstallment = await db.Installments.FirstAsync(i => i.Id == installment.Id);
        Assert.Equal(10000m, settledInstallment.SettledAmount);
        Assert.Equal(0m, settledInstallment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, settledInstallment.Status);
    }
}

/// <summary>
/// Task 8.1.3 — SQL Server integration tests for installment rehydration integrity.
/// </summary>
[Collection("SqlServerIntegration")]
public class Phase8_1_3RehydrationSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Phase8_1_3RehydrationSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

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
            amount, "EGP",
            Guid.NewGuid()).Value;
        db.Installments.Add(installment);
        db.StampAddedTenantIds(tenantId);
        return installment;
    }

    private static async Task<AllocatePaymentHandler> CreateHandler(AppDbContext db)
    {
        var auditWriter = Substitute.For<IAuditWriter>();
        return new AllocatePaymentHandler(db, auditWriter, NullSubscriptionReconciliationService.Instance);
    }

    /// <summary>
    /// SQL Server: Existing allocations + new allocation produces correct settlement
    /// after full persistence/reload cycle.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_3")]
    public async Task S7_SqlServer_ExistingAllocations_PlusNew_CorrectSettlement()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var contractId = Guid.NewGuid();
        Guid payment1Id, payment2Id, payment3Id, invoiceId, installmentId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var payment1 = CreatePayment(db, tenantId, 10000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 5000m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            var payment3 = CreatePayment(db, tenantId, 1000m, $"PAY3-{tenantId}");
            payment3.Complete(DateTime.UtcNow);
            payment3Id = payment3.Id;

            var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}", contractId: contractId);
            invoiceId = invoice.Id;

            var installment = CreateInstallment(db, tenantId, contractId, 10000m);
            installmentId = installment.Id;

            await db.SaveChangesAsync();
        }

        // Allocate A=3000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 3000m, installmentId),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Allocate B=2000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 2000m, installmentId),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Allocate C=1000 (fresh scope — proves rehydration)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(
                new AllocatePaymentCommand(payment3Id, invoiceId, 1000m, installmentId),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Verify from fresh scope
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var installment = await db.Installments.FirstAsync(i => i.Id == installmentId);
            Assert.Equal(6000m, installment.SettledAmount);
            Assert.Equal(4000m, installment.RemainingAmount);
            Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);

            var activeSum = await db.PaymentAllocations
                .Where(a => a.InstallmentId == installmentId && a.Status == PaymentAllocationStatus.Active)
                .SumAsync(a => a.AllocatedAmount);
            Assert.Equal(installment.SettledAmount, activeSum);
        }
    }

    /// <summary>
    /// SQL Server: Over-allocation is rejected after historical allocations exist.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_3")]
    public async Task S9_SqlServer_OverAllocation_Rejected()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var contractId = Guid.NewGuid();
        Guid payment1Id, payment2Id, invoiceId, installmentId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var payment1 = CreatePayment(db, tenantId, 5000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 1500m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            var invoice = CreateInvoice(db, tenantId, 5000m, $"INV-{tenantId}", contractId: contractId);
            invoiceId = invoice.Id;

            var installment = CreateInstallment(db, tenantId, contractId, 5000m);
            installmentId = installment.Id;

            await db.SaveChangesAsync();
        }

        // Allocate A=4000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 4000m, installmentId),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Attempt B=1500 (exceeds remaining 1000) — fresh scope
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 1500m, installmentId),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
        }

        // Verify no side effects
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var installment = await db.Installments.FirstAsync(i => i.Id == installmentId);
            Assert.Equal(4000m, installment.SettledAmount);
            Assert.Equal(1000m, installment.RemainingAmount);

            var allocationCount = await db.PaymentAllocations
                .Where(a => a.InstallmentId == installmentId && a.Status == PaymentAllocationStatus.Active)
                .CountAsync();
            Assert.Equal(1, allocationCount);
        }
    }

    /// <summary>
    /// SQL Server: Reversal after persistence recalculates correctly.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_3")]
    public async Task S11_SqlServer_ReversalAfterPersistence_Correct()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}"[..20];
        var contractId = Guid.NewGuid();
        Guid payment1Id, payment2Id, invoiceId, installmentId;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsureTenantExists(scope.ServiceProvider, tenantId);

            var payment1 = CreatePayment(db, tenantId, 5000m, $"PAY1-{tenantId}");
            payment1.Complete(DateTime.UtcNow);
            payment1Id = payment1.Id;

            var payment2 = CreatePayment(db, tenantId, 2000m, $"PAY2-{tenantId}");
            payment2.Complete(DateTime.UtcNow);
            payment2Id = payment2.Id;

            var invoice = CreateInvoice(db, tenantId, 5000m, $"INV-{tenantId}", contractId: contractId);
            invoiceId = invoice.Id;

            var installment = CreateInstallment(db, tenantId, contractId, 5000m);
            installmentId = installment.Id;

            await db.SaveChangesAsync();
        }

        // Allocate A=3000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            await handler.Handle(
                new AllocatePaymentCommand(payment1Id, invoiceId, 3000m, installmentId),
                CancellationToken.None);
        }

        // Allocate B=2000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            await handler.Handle(
                new AllocatePaymentCommand(payment2Id, invoiceId, 2000m, installmentId),
                CancellationToken.None);
        }

        // Reload, reverse A=3000
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var installment = await db.Installments
                .Include(i => i.PaymentAllocations)
                .FirstAsync(i => i.Id == installmentId);

            var allocA = installment.PaymentAllocations.First(a => a.AllocatedAmount == 3000m);
            installment.ReverseAllocation(allocA, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        // Verify from fresh scope
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var installment = await db.Installments.FirstAsync(i => i.Id == installmentId);
            Assert.Equal(2000m, installment.SettledAmount);
            Assert.Equal(3000m, installment.RemainingAmount);

            var activeSum = await db.PaymentAllocations
                .Where(a => a.InstallmentId == installmentId && a.Status == PaymentAllocationStatus.Active)
                .SumAsync(a => a.AllocatedAmount);
            Assert.Equal(installment.SettledAmount, activeSum);
        }
    }

    /// <summary>
    /// SQL Server: Idempotent retry still works after the fix.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Phase8_1_3")]
    public async Task S12_SqlServer_IdempotentRetry_Succeeds()
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

        var command = new AllocatePaymentCommand(paymentId, invoiceId, 4000m, installmentId);

        // First allocation
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(command, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Identical retry (fresh scope)
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);
            var handler = await CreateHandler(db);
            var result = await handler.Handle(command, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // Verify
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AuthorizeTenant(scope.ServiceProvider, tenantId);

            var allocations = await db.PaymentAllocations
                .Where(a => a.PaymentId == paymentId && a.InvoiceId == invoiceId)
                .ToListAsync();
            Assert.Single(allocations);
            Assert.Equal(4000m, allocations[0].AllocatedAmount);

            var installment = await db.Installments.FirstAsync(i => i.Id == installmentId);
            Assert.Equal(4000m, installment.SettledAmount);
            Assert.Equal(0m, installment.RemainingAmount);
            Assert.Equal(InstallmentStatus.Paid, installment.Status);
        }
    }
}
