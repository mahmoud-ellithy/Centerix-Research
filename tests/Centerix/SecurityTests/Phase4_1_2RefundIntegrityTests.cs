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
/// CODER TASK 4.1.2 — Refund Financial Integrity Hardening tests.
/// Validates:
/// 1. Subscription belongs to Refund Contract
/// 2. Invoice belongs to Refund Contract
/// 3. Tenant isolation across related entities
/// 4. Currency derived from Contract (caller cannot override)
/// 5. Arbitrary refund amount remains impossible
/// </summary>
public class Phase4_1_2RefundIntegrityTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AppDbContext CreateDbContext()
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns("tenant-1");
        currentTenant.IsAuthorized.Returns(true);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static Contract CreateContract(
        AppDbContext db,
        string tenantId = "tenant-1",
        string currencyCode = "EGP",
        ContractStatus status = ContractStatus.Active)
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

        contract.AddPricingTier(ContractPricingTier.Create(
            Guid.NewGuid(), contract.Id, 6, 5220m, currencyCode, 1000m, 1).Value);
        contract.AddPricingTier(ContractPricingTier.Create(
            Guid.NewGuid(), contract.Id, 12, 10000m, currencyCode, 1000m, 2).Value);

        if (status == ContractStatus.Active || status == ContractStatus.Suspended ||
            status == ContractStatus.Terminated || status == ContractStatus.Expired)
        {
            contract.SubmitForApproval();
            contract.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        db.Contracts.Add(contract);
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
        Guid contractId)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-" + Guid.NewGuid().ToString("N")[..8],
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
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
            Guid.NewGuid(),
            paymentNumber,
            amount,
            "EGP",
            PaymentMethod.Cash);
        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;

        var completeResult = payment.Complete(DateTime.UtcNow);
        Assert.True(completeResult.IsSuccess);

        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            payment.Id,
            invoice.Id,
            amount,
            DateTime.UtcNow);
        Assert.True(allocation.IsSuccess);

        db.Payments.Add(payment);
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

    // ==================================================================
    // 10.1 Valid relationship — Contract A + Subscription A + Invoice A
    // ==================================================================

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

    // ==================================================================
    // 10.2 Wrong Subscription — Contract A + Subscription B → reject
    // ==================================================================

    [Fact]
    public async Task CreateRefund_WrongSubscription_RejectedWithSubscriptionContractMismatch()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        var contractB = CreateContract(db, tenantId: "tenant-1");
        var subscriptionB = CreateSubscription(db, "tenant-1", contractB.Id);

        // Subscription B belongs to Contract B, not Contract A
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

    // ==================================================================
    // 10.3 Wrong Invoice — Contract A + Invoice B → reject
    // ==================================================================

    [Fact]
    public async Task CreateRefund_WrongInvoice_RejectedWithInvoiceContractMismatch()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        var contractB = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        // Invoice belongs to Contract B
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

    // ==================================================================
    // 10.4 Wrong Tenant — cross-tenant Subscription attempt
    // ==================================================================

    [Fact]
    public async Task CreateRefund_CrossTenantSubscription_RejectedWithCrossTenantSubscription()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");

        // Subscription belongs to tenant-2 but contract is tenant-1
        var subscriptionOther = CreateSubscription(db, "tenant-2", contractA.Id);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-004",
            ContractId: contractA.Id,
            SubscriptionId: subscriptionOther.Id,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.CrossTenantSubscription");
    }

    [Fact]
    public async Task CreateRefund_CrossTenantInvoice_RejectedWithCrossTenantInvoice()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        // Invoice belongs to tenant-2 but contract is tenant-1
        var invoiceOther = Invoice.Create(
            Guid.NewGuid(),
            "INV-CROSS",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            10000m, 0, 0, 10000m,
            contractId: contractA.Id).Value;
        invoiceOther.Issue(DateTime.UtcNow);
        db.Invoices.Add(invoiceOther);
        db.StampAddedTenantIds("tenant-2");
        db.SaveChanges();

        var command = new CreateRefundCommand(
            RefundNumber: "REF-005",
            ContractId: contractA.Id,
            SubscriptionId: null,
            InvoiceId: invoiceOther.Id,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Refund.CrossTenantInvoice");
    }

    // ==================================================================
    // 10.5 Currency Cannot Be Overridden — Contract EGP, Refund must be EGP
    // ==================================================================

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
    public async Task CreateRefund_CurrencyDerivedFromContract_NoCallerCurrencyField()
    {
        // Verify the command no longer has a CurrencyCode field.
        // This is a compile-time guarantee — if someone adds CurrencyCode back,
        // the test won't compile. The command is a record, so we can inspect its properties.
        var commandType = typeof(CreateRefundCommand);
        var props = commandType.GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "CurrencyCode");
    }

    // ==================================================================
    // Arbitrary amount remains impossible (calculation-derived)
    // ==================================================================

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
        Assert.True(refund.Amount > 0, "Refund amount must be positive");
        Assert.Equal("EGP", refund.CurrencyCode);
    }

    // ==================================================================
    // No refund due — customer owes money
    // ==================================================================

    [Fact]
    public async Task CreateRefund_NoRefundDue_ReturnsNoRefundDueError()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contract = CreateContract(db, tenantId: "tenant-1");

        // Only pay 4,000 — after 6 months, customer owes money
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

    // ==================================================================
    // Contract not found
    // ==================================================================

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

    // ==================================================================
    // Subscription not found
    // ==================================================================

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

    // ==================================================================
    // Invoice not found
    // ==================================================================

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

    // ==================================================================
    // Shared Payment cross-contract isolation preserved
    // ==================================================================

    [Fact]
    public async Task CreateRefund_SharedPaymentCrossContract_OnlySeesContractAllocations()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        var contractB = CreateContract(db, tenantId: "tenant-1");

        // Create a shared payment: 10,000 total
        // 6,000 allocated to Contract A's invoice, 4,000 to Contract B's invoice
        var invoiceA = CreateInvoiceForContract(db, "tenant-1", contractA.Id);
        var invoiceB = CreateInvoiceForContract(db, "tenant-1", contractB.Id);

        var paymentResult = Payment.Create(
            Guid.NewGuid(), "PAY-SHARED", 10000m, "EGP", PaymentMethod.Cash);
        Assert.True(paymentResult.IsSuccess);
        var payment = paymentResult.Value;
        payment.Complete(DateTime.UtcNow);

        var allocA = PaymentAllocation.Create(Guid.NewGuid(), payment.Id, invoiceA.Id, 6000m, DateTime.UtcNow);
        Assert.True(allocA.IsSuccess);
        var allocB = PaymentAllocation.Create(Guid.NewGuid(), payment.Id, invoiceB.Id, 4000m, DateTime.UtcNow);
        Assert.True(allocB.IsSuccess);

        db.Payments.Add(payment);
        db.StampAddedTenantIds("tenant-1");
        db.SaveChanges();

        // Cancel Contract A — should only see 6,000
        var commandA = new CreateRefundCommand(
            RefundNumber: "REF-SHARED-A",
            ContractId: contractA.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var resultA = await handler.Handle(commandA, CancellationToken.None);

        Assert.True(resultA.IsSuccess);
        var refundA = await db.Refunds.FindAsync(resultA.Value);
        Assert.NotNull(refundA);

        // Cancel Contract B — should only see 4,000
        var commandB = new CreateRefundCommand(
            RefundNumber: "REF-SHARED-B",
            ContractId: contractB.Id,
            SubscriptionId: null,
            InvoiceId: null,
            Reason: "Early cancellation");

        var resultB = await handler.Handle(commandB, CancellationToken.None);

        Assert.True(resultB.IsSuccess);
        var refundB = await db.Refunds.FindAsync(resultB.Value);
        Assert.NotNull(refundB);

        // Cross-contract contamination check: the two refunds must be independent
        Assert.NotEqual(refundA.Amount, refundB.Amount);
    }

    // ==================================================================
    // Audit record is written on successful creation
    // ==================================================================

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
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ==================================================================
    // Optional approval model preserved
    // ==================================================================

    [Fact]
    public void Refund_OperationalApprovalModel_Preserved()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-LC", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        Assert.Equal(RefundStatus.Pending, refund.Status);

        // Direct Pending → Completed (optional approval)
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

        // Pending → Approved → Processing → Completed
        Assert.True(refund.Approve("approver-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Approved, refund.Status);

        Assert.True(refund.MarkProcessing().IsSuccess);
        Assert.Equal(RefundStatus.Processing, refund.Status);

        Assert.True(refund.Execute("executor-1", DateTime.UtcNow).IsSuccess);
        Assert.Equal(RefundStatus.Completed, refund.Status);
    }

    // ==================================================================
    // Concurrency: RowVersion exists on Refund entity
    // ==================================================================

    [Fact]
    public void Refund_HasRowVersion_ForOptimisticConcurrency()
    {
        var refund = Refund.Create(
            Guid.NewGuid(), "REF-RV", Guid.NewGuid(), null, null,
            100m, "EGP", "Reason", "user-1", DateTime.UtcNow).Value;

        // RowVersion should be initialized (empty byte array from default)
        Assert.NotNull(refund.RowVersion);
    }

    // ==================================================================
    // Cross-tenant: no financial record created
    // ==================================================================

    [Fact]
    public async Task CreateRefund_CrossTenant_NoFinancialRecordCreated()
    {
        using var db = CreateDbContext();
        var handler = CreateHandler(db);

        var contractA = CreateContract(db, tenantId: "tenant-1");
        CreateCompletedPaymentWithAllocation(db, "tenant-1", contractA.Id, 10000m);

        // Subscription from different tenant
        var subscriptionOther = CreateSubscription(db, "tenant-2", contractA.Id);

        var command = new CreateRefundCommand(
            RefundNumber: "REF-CROSS-TENANT",
            ContractId: contractA.Id,
            SubscriptionId: subscriptionOther.Id,
            InvoiceId: null,
            Reason: "Early cancellation");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        // No refund should be persisted
        var refunds = db.Refunds.Where(r => r.RefundNumber == "REF-CROSS-TENANT").ToList();
        Assert.Empty(refunds);
    }
}
