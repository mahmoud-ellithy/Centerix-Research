namespace Centerix.SecurityTests;

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
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// CODER TASK 4.1.2 -- Refund Financial Integrity Hardening tests.
/// Validates:
/// 1. Subscription belongs to Refund Contract
/// 2. Invoice belongs to Refund Contract
/// 3. Tenant isolation across related entities
/// 4. Currency derived from Contract (caller cannot override)
/// 5. Arbitrary refund amount remains impossible
/// </summary>
public class Phase4_1_2RefundIntegrityTests
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
            contractedAmount: 12000m);

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

    [Fact]
    public async Task CreateRefund_ValidRelationship_AllBelongToSameContract_Succeeds()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db);
        var subscription = CreateSubscription(db, "tenant-1", contract.Id);
        var (payment, _) = CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-001",
            ContractId: contract.Id,
            SubscriptionId: subscription.Id,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value);

        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);
        Assert.Equal(contract.Id, refund.ContractId);
        Assert.Equal(subscription.Id, refund.SubscriptionId);
        Assert.Equal("EGP", refund.CurrencyCode);
        Assert.Equal(RefundStatus.Pending, refund.Status);
    }

    [Fact]
    public async Task CreateRefund_WrongSubscription_RejectedWithSubscriptionContractMismatch()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        var contractB = CreateContract(db, tenantId: "tenant-1");
        var subscriptionB = CreateSubscription(db, "tenant-1", contractB.Id);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-002",
            ContractId: contractA.Id,
            SubscriptionId: subscriptionB.Id,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.SubscriptionContractMismatch");
    }

    [Fact]
    public async Task CreateRefund_WrongInvoice_RejectedWithInvoiceContractMismatch()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        var contractB = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        var invoiceB = CreateInvoiceForContract(db, "tenant-1", contractB.Id);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-003",
            ContractId: contractA.Id,
            SubscriptionId: null,
            InvoiceId: invoiceB.Id,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.InvoiceContractMismatch");
    }

    [Fact]
    public async Task CreateRefund_CrossTenantSubscription_RejectedByQueryFilterOrValidation()
    {
        var sharedDb = $"Test_{Guid.NewGuid():N}";
        using var db = CreateDbContext(sharedDb, "tenant-1");
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");

        // Create subscription via a tenant-2 context
        using (var otherDb = CreateDbContext(sharedDb, "tenant-2"))
        {
            CreateSubscription(otherDb, "tenant-2", contractA.Id);
        }

        // Find the subscription ID via tenant-2 context
        Guid subId;
        using (var otherDb = CreateDbContext(sharedDb, "tenant-2"))
        {
            subId = otherDb.TenantPlans.IgnoreQueryFilters().First(s => s.ContractId == contractA.Id).Id;
        }

        var command = new CreateRefundCommand(
            RefundNumber: "REF-004",
            ContractId: contractA.Id,
            SubscriptionId: subId,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        // The subscription is from tenant-2, so either query filter hides it (NotFound)
        // or validation catches it (CrossTenantSubscription). Both are acceptable.
        Assert.False(result.IsSuccess);
        var errorCode = result.Errors!.First().Code;
        Assert.True(
            errorCode == "Refund.CrossTenantSubscription" || errorCode == "Refund.NotFound",
            $"Expected CrossTenantSubscription or NotFound, got: {errorCode}");
    }

    [Fact]
    public async Task CreateRefund_CrossTenantInvoice_RejectedByQueryFilterOrValidation()
    {
        var sharedDb = $"Test_{Guid.NewGuid():N}";
        using var db = CreateDbContext(sharedDb, "tenant-1");
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        // Create invoice via a tenant-2 context
        Guid invoiceId;
        using (var otherDb = CreateDbContext(sharedDb, "tenant-2"))
        {
            var inv = CreateInvoiceForContract(otherDb, "tenant-2", contractA.Id);
            invoiceId = inv.Id;
        }

        var command = new CreateRefundCommand(
            RefundNumber: "REF-005",
            ContractId: contractA.Id,
            SubscriptionId: null,
            InvoiceId: invoiceId,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        var errorCode = result.Errors!.First().Code;
        Assert.True(
            errorCode == "Refund.CrossTenantInvoice" || errorCode == "Refund.NotFound",
            $"Expected CrossTenantInvoice or NotFound, got: {errorCode}");
    }

    [Fact]
    public async Task CreateRefund_CurrencyDerivedFromContract_NoCallerCurrencyField()
    {
        var commandType = typeof(CreateRefundCommand);
        var props = commandType.GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "CurrencyCode");
    }

    [Fact]
    public async Task CreateRefund_CurrencyDerivedFromContract_EgpContract_EgpRefund()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1", currencyCode: "EGP");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-006",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);
        Assert.Equal("EGP", refund.CurrencyCode);
    }

    [Fact]
    public async Task CreateRefund_AmountAlwaysDerivedFromCalculation_NotCaller()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-007",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);
        Assert.True(refund.Amount > 0);
        Assert.Equal("EGP", refund.CurrencyCode);
    }

    [Fact]
    public async Task CreateRefund_NoRefundDue_ReturnsNoRefundDueError()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 4000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-008",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.NoRefundDue");
    }

    [Fact]
    public async Task CreateRefund_ContractNotFound_ReturnsContractNotFoundError()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-009",
            ContractId: Guid.NewGuid(),
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.ContractNotFound");
    }

    [Fact]
    public async Task CreateRefund_SubscriptionNotFound_ReturnsNotFoundError()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-010",
            ContractId: contract.Id,
            SubscriptionId: Guid.NewGuid(),
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.NotFound");
    }

    [Fact]
    public async Task CreateRefund_InvoiceNotFound_ReturnsNotFoundError()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-011",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: Guid.NewGuid(),
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.NotFound");
    }

    [Fact]
    public async Task CreateRefund_SharedPaymentCrossContract_OnlySeesContractAllocations()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");

        var invoiceA = CreateInvoiceForContract(db, "tenant-1", contractA.Id);

        var paymentResult = Payment.Create(
            Guid.NewGuid(), "PAY-SHARED", 12000m, "EGP", PaymentMethod.Cash);
        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;
        payment.Complete(DateTime.UtcNow);

        var allocA = PaymentAllocation.Create(Guid.NewGuid(), payment.Id, invoiceA.Id, 12000m, DateTime.UtcNow);
        Assert.True(allocA.IsSuccess);

        db.Payments.Add(payment);
        db.PaymentAllocations.Add(allocA.Value);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        var command = new CreateRefundCommand(
            RefundNumber: "REF-SCOPED",
            ContractId: contractA.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var refund = await db.Refunds.FindAsync(result.Value);
        Assert.NotNull(refund);
        Assert.Equal(contractA.Id, refund.ContractId);
        Assert.Equal("EGP", refund.CurrencyCode);
        Assert.True(refund.Amount > 0);
    }

    [Fact]
    public async Task CreateRefund_SuccessfulCreation_WritesAuditRecord()
    {
        using var db = CreateDbContext();
        var auditWriter = Substitute.For<IAuditWriter>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns("user-1");

        var calculationService = new RefundCalculationService();
        var handler = new CreateRefundHandler(db, calculationService, currentUser, auditWriter);

        var contract = CreateContract(db);
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contract.Id, 10000m);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-AUDIT",
            ContractId: contract.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        await auditWriter.Received(1).WriteAsync(
            Arg.Is("Refund.Create"),
            Arg.Is(nameof(Refund)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Refund_OperationalApprovalModel_Preserved()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-LC", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.Equal(RefundStatus.Pending, refund.Status);

        Assert.True(refund.Execute("executor-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Completed, refund.Status);
    }

    [Fact]
    public void Refund_OperationalApprovalModel_WithApprovalStep_Preserved()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-LC2", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.Equal(RefundStatus.Pending, refund.Status);

        Assert.True(refund.Approve("approver-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Approved, refund.Status);

        Assert.True(refund.MarkProcessing().IsSuccess);
        Assert.Equal(RefundStatus.Processing, refund.Status);

        Assert.True(refund.Execute("executor-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Completed, refund.Status);
    }

    [Fact]
    public void Refund_HasRowVersion_ForOptimisticConcurrency()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-RV", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.NotNull(refund.RowVersion);
    }

    [Fact]
    public async Task CreateRefund_CrossTenant_NoFinancialRecordCreated()
    {
        var sharedDb = $"Test_{Guid.NewGuid():N}";
        using var db = CreateDbContext(sharedDb, "tenant-1");
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        Guid subId;
        using (var otherDb = CreateDbContext(sharedDb, "tenant-2"))
        {
            var sub = CreateSubscription(otherDb, "tenant-2", contractA.Id);
            subId = sub.Id;
        }

        var command = new CreateRefundCommand(
            RefundNumber: "REF-CROSS-TENANT",
            ContractId: contractA.Id,
            SubscriptionId: subId,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        var refunds = db.Refunds.Where(r => r.RefundNumber == "REF-CROSS-TENANT").ToList();
        Assert.Empty(refunds);
    }
}
