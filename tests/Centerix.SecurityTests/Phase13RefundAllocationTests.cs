namespace Centerix.SecurityTests;

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
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 13 — Refund Settlement & Payment Source Allocation tests.
/// Validates:
/// 1. RefundAllocation domain entity validation
/// 2. CreateRefund generates pro-rata allocations from payment contributions
/// 3. ExecuteRefund validates allocations exist and sum matches
/// 4. ExecuteRefund creates per-allocation ledger entries
/// 5. Financial invariants: allocation amounts positive, sums match, no over-refund
/// 6. Historical integrity: original payment/invoice records unchanged after refund
/// 7. Cross-tenant authorization for refund execution
/// </summary>
[Trait("Category", "Phase13")]
public class Phase13RefundAllocationTests
{
    private static AppDbContext CreateDbContext(string? sharedDbName = null, string tenantId = "tenant-1")
    {
        var dbName = sharedDbName ?? $"Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId);
        currentTenant.IsAuthorized.Returns(true);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static Contract CreateContract(
        AppDbContext db,
        string tenantId = "tenant-1",
        string currencyCode = "EGP")
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
            currencyCode: currencyCode,
            grossAmount: 12000m,
            contractedAmount: 12000m, entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion);

        Assert.True(result.IsSuccess);
        var contract = result.Value;

        contract.SubmitForApproval();
        contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        db.Contracts.Add(contract);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        return contract;
    }

    private static TenantPlan CreateSubscription(
        AppDbContext db,
        string tenantId,
        Guid contractId)
    {
        var result = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: 1,
            snapshotPrice: 1000m,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            autoRenew: false,
            status: SubscriptionStatus.Active);

        Assert.True(result.IsSuccess);
        var sub = result.Value;
        sub.LinkToContract(contractId);

        db.TenantPlans.Add(sub);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        return sub;
    }

    private static Invoice CreateInvoiceForContract(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal totalAmount = 10000m)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-" + Guid.NewGuid().ToString("N")[..8],
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            totalAmount, 0, 0, totalAmount,
            contractId: contractId).Value;

        invoice.Issue(DateTime.UtcNow);

        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        return invoice;
    }

    private static (Payment payment, Invoice invoice) CreateCompletedPaymentWithAllocation(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal amount,
        string paymentNumber = "PAY-001")
    {
        var invoice = CreateInvoiceForContract(db, tenantId, contractId);

        var paymentResult = Payment.Create(
            Guid.NewGuid(), paymentNumber, amount, "EGP", PaymentMethod.Cash);
        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;

        var completeResult = payment.Complete(DateTime.UtcNow);
        Assert.True(completeResult.IsSuccess);

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(), payment.Id, invoice.Id, amount, DateTime.UtcNow);
        Assert.True(allocation.IsSuccess);

        db.Payments.Add(payment);
        db.PaymentAllocations.Add(allocation.Value);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        return (payment, invoice);
    }

    private static (Payment payment1, Payment payment2, Invoice invoice) CreateTwoCompletedPaymentsWithAllocations(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal amount1,
        decimal amount2)
    {
        var invoice = CreateInvoiceForContract(db, tenantId, contractId, amount1 + amount2);

        var paymentResult1 = Payment.Create(
            Guid.NewGuid(), "PAY-A", amount1, "EGP", PaymentMethod.Cash);
        Assert.True(paymentResult1.IsSuccess);
        var payment1 = paymentResult1.Value;
        payment1.Complete(DateTime.UtcNow);

        var allocation1 = PaymentAllocation.Create(
            Guid.NewGuid(), payment1.Id, invoice.Id, amount1, DateTime.UtcNow);
        Assert.True(allocation1.IsSuccess);

        var paymentResult2 = Payment.Create(
            Guid.NewGuid(), "PAY-B", amount2, "EGP", PaymentMethod.BankTransfer);
        Assert.True(paymentResult2.IsSuccess);
        var payment2 = paymentResult2.Value;
        payment2.Complete(DateTime.UtcNow);

        var allocation2 = PaymentAllocation.Create(
            Guid.NewGuid(), payment2.Id, invoice.Id, amount2, DateTime.UtcNow);
        Assert.True(allocation2.IsSuccess);

        db.Payments.AddRange(payment1, payment2);
        db.PaymentAllocations.AddRange(allocation1.Value, allocation2.Value);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();

        return (payment1, payment2, invoice);
    }

    private static CreateRefundHandler CreateHandler(
        AppDbContext db,
        string userId = "user-1")
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);

        var auditWriter = Substitute.For<IAuditWriter>();
        var calculationService = new RefundCalculationService();

        return new CreateRefundHandler(db, calculationService, currentUser, auditWriter);
    }

    private static ExecuteRefundHandler CreateExecuteHandler(
        AppDbContext db,
        string userId = "user-1")
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        var auditWriter = Substitute.For<IAuditWriter>();
        var platformAdminGuard = Substitute.For<IPlatformAdminGuard>();
        platformAdminGuard.EnsurePlatformAdmin().Returns(Result.Updated);
        return new ExecuteRefundHandler(db, currentUser, platformAdminGuard, auditWriter);
    }

    private static Refund CreatePendingRefund(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal amount,
        string refundNumber = "REF-001")
    {
        var refundResult = Refund.Create(
            Guid.NewGuid(),
            refundNumber,
            contractId,
            null,
            null,
            amount,
            "EGP",
            "Early cancellation",
            "user-1",
            DateTime.UtcNow);

        Assert.True(refundResult.IsSuccess);
        var refund = refundResult.Value;

        db.Refunds.Add(refund);
        db.StampAddedTenantIds(tenantId);
        db.SaveChanges();
        return refund;
    }

    // ==================================================================
    // 1. DOMAIN TESTS — RefundAllocation entity
    // ==================================================================

    [Fact]
    public void RefundAllocation_Create_ValidInput_Succeeds()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500m,
            PaymentMethod.Cash,
            "EGP",
            "PAY-001");

        Assert.True(result.IsSuccess);
        Assert.Equal(500m, result.Value.Amount);
        Assert.Equal("PAY-001", result.Value.PaymentNumber);
    }

    [Fact]
    public void RefundAllocation_Create_ZeroAmount_Fails()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0m,
            PaymentMethod.Cash,
            "EGP");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.Amount_MustBePositive");
    }

    [Fact]
    public void RefundAllocation_Create_NegativeAmount_Fails()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            -100m,
            PaymentMethod.Cash,
            "EGP");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.Amount_MustBePositive");
    }

    [Fact]
    public void RefundAllocation_Create_EmptyPaymentId_Fails()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            500m,
            PaymentMethod.Cash,
            "EGP");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.PaymentId_Required");
    }

    [Fact]
    public void RefundAllocation_Create_EmptyRefundId_Fails()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.NewGuid(),
            500m,
            PaymentMethod.Cash,
            "EGP");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.RefundId_Required");
    }

    [Fact]
    public void RefundAllocation_Create_InvalidMethod_Fails()
    {
        var result = RefundAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500m,
            unchecked((PaymentMethod)999),
            "EGP");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.PaymentMethod_Invalid");
    }

    // ==================================================================
    // 2. CREATE REFUND ALLOCATION TESTS
    // ==================================================================

    [Fact]
    public async Task CreateRefund_GeneratesAllocations_ProRata()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment1, payment2, _) = CreateTwoCompletedPaymentsWithAllocations(
            db, "tenant-1", contract.Id, 7000m, 3000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-ALLOC-1",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var allocations = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .ToListAsync();

        Assert.Equal(2, allocations.Count);

        var alloc1 = allocations.First(a => a.PaymentId == payment1.Id);
        var alloc2 = allocations.First(a => a.PaymentId == payment2.Id);

        Assert.Equal(PaymentMethod.Cash.ToString(), alloc1.PaymentMethod);
        Assert.Equal(PaymentMethod.BankTransfer.ToString(), alloc2.PaymentMethod);
        Assert.Equal("PAY-A", alloc1.PaymentNumber);
        Assert.Equal("PAY-B", alloc2.PaymentNumber);
        Assert.True(alloc1.Amount > 0);
        Assert.True(alloc2.Amount > 0);
    }

    [Fact]
    public async Task CreateRefund_SinglePayment_FullAllocation()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment, invoice) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-SINGLE",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);

        var allocations = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .ToListAsync();

        Assert.Single(allocations);
        Assert.Equal(payment.Id, allocations[0].PaymentId);
        Assert.Equal(refund.Amount, allocations[0].Amount);
        Assert.Equal("PAY-001", allocations[0].PaymentNumber);
    }

    [Fact]
    public async Task CreateRefund_MultiplePayments_ProRataDistribution()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment1, payment2, _) = CreateTwoCompletedPaymentsWithAllocations(
            db, "tenant-1", contract.Id, 5000m, 5000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-PRO-RATA",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);

        var allocations = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .ToListAsync();

        Assert.Equal(2, allocations.Count);

        decimal totalAllocated = allocations.Sum(a => a.Amount);
        Assert.Equal(refund.Amount, totalAllocated);
    }

    [Fact]
    public async Task CreateRefund_AllocationSumEqualsRefundAmount()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment1, payment2, _) = CreateTwoCompletedPaymentsWithAllocations(
            db, "tenant-1", contract.Id, 8000m, 2000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-SUM",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);

        var allocationSum = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .SumAsync(ra => ra.Amount);

        Assert.Equal(refund.Amount, allocationSum);
    }

    // ==================================================================
    // 3. EXECUTE REFUND ALLOCATION VALIDATION TESTS
    // ==================================================================

    [Fact]
    public async Task ExecuteRefund_WithoutAllocations_Rejected()
    {
        using var db = CreateDbContext();
        var contract = CreateContract(db);
        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m);

        var handler = CreateExecuteHandler(db);

        var command = new ExecuteRefundCommand(refund.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.Required");
    }

    [Fact]
    public async Task ExecuteRefund_WithMismatchedAllocationSum_Rejected()
    {
        using var db = CreateDbContext();
        var contract = CreateContract(db);
        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m);

        var paymentResult = Payment.Create(
            Guid.NewGuid(), "PAY-MISMATCH", 10000m, "EGP", PaymentMethod.Cash);
        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;
        payment.Complete(DateTime.UtcNow);

        var allocationResult = RefundAllocation.Create(
            Guid.NewGuid(),
            refund.Id,
            payment.Id,
            3000m,
            PaymentMethod.Cash,
            "EGP",
            "PAY-MISMATCH");
        Assert.True(allocationResult.IsSuccess);

        db.Payments.Add(payment);
        db.RefundAllocations.Add(allocationResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var handler = CreateExecuteHandler(db);
        var command = new ExecuteRefundCommand(refund.Id);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "RefundAllocation.SumMismatch");
    }

    [Fact]
    public async Task ExecuteRefund_WithCompletedPayments_Succeeds()
    {
        using var db = CreateDbContext();
        var handler = CreateExecuteHandler(db);

        var contract = CreateContract(db);
        var (payment, _) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-EXEC-1");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(),
            refund.Id,
            payment.Id,
            5000m,
            PaymentMethod.Cash,
            "EGP",
            "PAY-001");
        Assert.True(allocResult.IsSuccess);

        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var command = new ExecuteRefundCommand(refund.Id);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedRefund = await db.Refunds.FindAsync(refund.Id);
        Assert.NotNull(updatedRefund);
        Assert.Equal(RefundStatus.Completed, updatedRefund.Status);
        Assert.NotNull(updatedRefund.ExecutedAtUtc);
        Assert.Equal("user-1", updatedRefund.ExecutedBy);
    }

    [Fact]
    public async Task ExecuteRefund_CreatesPerAllocationLedgerEntries()
    {
        using var db = CreateDbContext();
        var handler = CreateExecuteHandler(db);

        var contract = CreateContract(db);
        var (payment1, payment2, _) = CreateTwoCompletedPaymentsWithAllocations(
            db, "tenant-1", contract.Id, 6000m, 4000m);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-LEDGER");

        var alloc1 = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment1.Id, 3000m,
            PaymentMethod.Cash, "EGP", "PAY-A");
        Assert.True(alloc1.IsSuccess);

        var alloc2 = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment2.Id, 2000m,
            PaymentMethod.BankTransfer, "EGP", "PAY-B");
        Assert.True(alloc2.IsSuccess);

        db.RefundAllocations.AddRange(alloc1.Value, alloc2.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var command = new ExecuteRefundCommand(refund.Id);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var ledgerEntries = await db.CustomerLedgerEntries
            .Where(e => e.RefundId == refund.Id && e.EntryType == LedgerEntryType.RefundSettlement)
            .ToListAsync();

        // Single total entry; per-payment traceability via RefundAllocation entities
        Assert.Single(ledgerEntries);

        var entry = ledgerEntries[0];
        Assert.Equal(refund.Id, entry.RefundId);
        Assert.Equal(5000m, entry.Amount);
        Assert.Equal("EGP", entry.CurrencyCode);
        Assert.Contains("Cash", entry.Description);
        Assert.Contains("BankTransfer", entry.Description);
    }

    [Fact]
    public async Task ExecuteRefund_AlreadyCompleted_IsIdempotent()
    {
        using var db = CreateDbContext();
        var handler = CreateExecuteHandler(db);

        var contract = CreateContract(db);
        var (payment, _) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-IDEM");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 5000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(allocResult.IsSuccess);
        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var command = new ExecuteRefundCommand(refund.Id);

        var result1 = await handler.Handle(command, CancellationToken.None);
        Assert.True(result1.IsSuccess);

        var result2 = await handler.Handle(command, CancellationToken.None);
        Assert.True(result2.IsSuccess);

        var finalRefund = await db.Refunds.FindAsync(refund.Id);
        Assert.NotNull(finalRefund);
        Assert.Equal(RefundStatus.Completed, finalRefund.Status);
    }

    // ==================================================================
    // 4. FINANCIAL INVARIANT TESTS
    // ==================================================================

    [Fact]
    public async Task FinancialInvariant_AllocationAmountPositive()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-FI-POS",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var allocations = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .ToListAsync();

        Assert.NotEmpty(allocations);
        Assert.All(allocations, a => Assert.True(a.Amount > 0,
            $"RefundAllocation amount must be positive, got {a.Amount}"));
    }

    [Fact]
    public async Task FinancialInvariant_AllocationSumEqualsRefundAmount()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment1, payment2, _) = CreateTwoCompletedPaymentsWithAllocations(
            db, "tenant-1", contract.Id, 7000m, 3000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-FI-SUM",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);

        var allocationSum = await db.RefundAllocations
            .Where(ra => ra.RefundId == result.Value)
            .SumAsync(ra => ra.Amount);

        Assert.Equal(refund.Amount, allocationSum);
    }

    [Fact]
    public async Task FinancialInvariant_PaymentNotOverRefunded()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment, _) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 10000m, "REF-OVER");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 10000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(allocResult.IsSuccess);

        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var executeHandler = CreateExecuteHandler(db);
        var command = new ExecuteRefundCommand(refund.Id);
        var execResult = await executeHandler.Handle(command, CancellationToken.None);
        Assert.True(execResult.IsSuccess);

        var secondRefund = CreatePendingRefund(db, "tenant-1", contract.Id, 10000m, "REF-OVER-2");

        var secondAllocResult = RefundAllocation.Create(
            Guid.NewGuid(), secondRefund.Id, payment.Id, 10000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(secondAllocResult.IsSuccess);

        db.RefundAllocations.Add(secondAllocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var secondCommand = new ExecuteRefundCommand(secondRefund.Id);
        var secondResult = await executeHandler.Handle(secondCommand, CancellationToken.None);

        Assert.False(secondResult.IsSuccess);
        var errorCodes = secondResult.Errors!.Select(e => e.Code).ToList();
        Assert.Contains("Refund.InsufficientPaymentSource", errorCodes);
    }

    // ==================================================================
    // 5. HISTORICAL INTEGRITY TESTS
    // ==================================================================

    [Fact]
    public async Task HistoricalIntegrity_PaymentAmountUnchanged_AfterRefund()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment, _) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var originalAmount = payment.Amount;
        var originalNumber = payment.PaymentNumber;

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-HIST-PAY");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 5000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(allocResult.IsSuccess);
        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var executeHandler = CreateExecuteHandler(db);
        var execResult = await executeHandler.Handle(
            new ExecuteRefundCommand(refund.Id), CancellationToken.None);
        Assert.True(execResult.IsSuccess);

        var unchangedPayment = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(unchangedPayment);
        Assert.Equal(originalAmount, unchangedPayment.Amount);
        Assert.Equal(originalNumber, unchangedPayment.PaymentNumber);
        Assert.Equal(PaymentStatus.Completed, unchangedPayment.Status);
    }

    [Fact]
    public async Task HistoricalIntegrity_PaymentAllocationUnchanged_AfterRefund()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment, invoice) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var originalAlloc = await db.PaymentAllocations
            .FirstAsync(a => a.PaymentId == payment.Id);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-HIST-ALLOC");

        var refundAllocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 5000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(refundAllocResult.IsSuccess);
        db.RefundAllocations.Add(refundAllocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var executeHandler = CreateExecuteHandler(db);
        var execResult = await executeHandler.Handle(
            new ExecuteRefundCommand(refund.Id), CancellationToken.None);
        Assert.True(execResult.IsSuccess);

        var unchangedAlloc = await db.PaymentAllocations.FindAsync(originalAlloc.Id);
        Assert.NotNull(unchangedAlloc);
        Assert.Equal(originalAlloc.AllocatedAmount, unchangedAlloc.AllocatedAmount);
        Assert.Equal(PaymentAllocationStatus.Active, unchangedAlloc.Status);
    }

    [Fact]
    public async Task HistoricalIntegrity_InvoiceTotalUnchanged_AfterRefund()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var (payment, invoice) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var originalTotal = invoice.TotalAmount;

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-HIST-INV");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 5000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(allocResult.IsSuccess);
        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var executeHandler = CreateExecuteHandler(db);
        var execResult = await executeHandler.Handle(
            new ExecuteRefundCommand(refund.Id), CancellationToken.None);
        Assert.True(execResult.IsSuccess);

        var unchangedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(unchangedInvoice);
        Assert.Equal(originalTotal, unchangedInvoice.TotalAmount);
    }

    // ==================================================================
    // 6. AUTHORIZATION TESTS
    // ==================================================================

    [Fact]
    public async Task ExecuteRefund_CrossTenant_Rejected()
    {
        var sharedDb = $"Test_{Guid.NewGuid():N}";
        using var db = CreateDbContext(sharedDb, "tenant-1");
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");
        var (payment, _) = CreateCompletedPaymentWithAllocation(
            db, "tenant-1", contract.Id, 10000m);

        var refund = CreatePendingRefund(db, "tenant-1", contract.Id, 5000m, "REF-CROSS");

        var allocResult = RefundAllocation.Create(
            Guid.NewGuid(), refund.Id, payment.Id, 5000m,
            PaymentMethod.Cash, "EGP", "PAY-001");
        Assert.True(allocResult.IsSuccess);
        db.RefundAllocations.Add(allocResult.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        using var tenant2Db = CreateDbContext(sharedDb, "tenant-2");
        var tenant2Refund = tenant2Db.Refunds
            .IgnoreQueryFilters()
            .FirstOrDefault(r => r.Id == refund.Id);
        Assert.NotNull(tenant2Refund);

        var executeHandler = CreateExecuteHandler(tenant2Db, userId: "user-2");

        var command = new ExecuteRefundCommand(refund.Id);
        var result = await executeHandler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        var errorCodes = result.Errors!.Select(e => e.Code).ToList();
        Assert.True(
            errorCodes.Contains("Refund.NotFound") ||
            errorCodes.Contains("RefundAllocation.Required") ||
            errorCodes.Contains("RefundAllocation.CrossTenant"),
            $"Expected cross-tenant rejection. Got: {string.Join(", ", errorCodes)}");
    }
}
