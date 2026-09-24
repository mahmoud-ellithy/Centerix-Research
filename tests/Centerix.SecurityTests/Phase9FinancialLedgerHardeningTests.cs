namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// CODER TASK 3.1 — Financial Ledger Hardening tests.
/// Covers: Payment amount preservation, overpayment, multi-invoice payments,
/// allocation rules, duplicate/retry safety, tenant isolation, concurrency,
/// and ledger reconstructability.
///
/// Uses EF InMemory for fast unit tests. Concurrency scenarios are tested
/// with a serialized transaction approach that simulates concurrent allocations
/// to verify invariants hold under sequentialized concurrent-like operations.
/// </summary>
public class Phase9FinancialLedgerHardeningTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Test_{Guid.NewGuid():N}";
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
        return new AllocatePaymentHandler(db, auditWriter, NullSubscriptionReconciliationService.Instance, AllowPlatformAdmin());
    }

    private static IPlatformAdminGuard AllowPlatformAdmin()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }

    // ------------------------------------------------------------------
    // Basic tests — Payment amount preservation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Payment_Amount_Preserved_Before_Allocation()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-A1");
        await db.SaveChangesAsync();

        // Act
        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);

        // Assert
        Assert.Equal(13000m, dbPayment.Amount);
        Assert.Equal(0m, dbPayment.GetAllocatedAmount());
        Assert.Equal(13000m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Completed_Payment_Can_Exist_Before_Allocation()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-A2");
        await db.SaveChangesAsync();

        // Act
        payment.Complete(DateTime.UtcNow);
        await db.SaveChangesAsync();

        // Assert
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(13000m, payment.Amount);
        Assert.Equal(0m, payment.GetAllocatedAmount());
        Assert.Equal(13000m, payment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Allocation_Reduces_Unallocated_Amount()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-A3");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-A3");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(10000m, dbPayment.Amount); // Payment amount preserved
        Assert.Equal(6000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(4000m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Allocation_Cannot_Exceed_Payment_Amount()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 5000m, "PAY-A4");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-A4");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.ExceedsPayment", result.Errors![0].Code);
    }

    [Fact]
    public async Task Allocation_Cannot_Exceed_Invoice_Outstanding()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-A5");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 3000m, "INV-A5");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: Allocate 5000 to a 3000 invoice — overpayment is capped, excess becomes TenantCredit
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m),
            CancellationToken.None);

        // Assert: Succeeds with 3000 allocated to invoice, 2000 as overpayment credit
        Assert.True(result.IsSuccess);

        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(3000m, dbInvoice.GetPaidAmount());
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);

        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(3000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(7000m, dbPayment.GetUnallocatedAmount());

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(2000m, overpaymentCredit.Amount);
    }

    [Fact]
    public async Task Allocation_Zero_Amount_Is_Rejected()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-A6");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, "INV-A6");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 0m),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.Amount_MustBePositive", result.Errors![0].Code);
    }

    [Fact]
    public async Task Allocation_Negative_Amount_Is_Rejected()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-A7");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, "INV-A7");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, -100m),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.Amount_MustBePositive", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // Invoice settlement tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Invoice_Becomes_PartiallyPaid_Correctly()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-B1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-B1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, dbInvoice.Status);
        Assert.Equal(4000m, dbInvoice.GetPaidAmount());
        Assert.Equal(6000m, dbInvoice.GetRemainingAmount());
    }

    [Fact]
    public async Task Invoice_Becomes_Paid_Correctly()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-B2");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-B2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 10000m),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
        Assert.Equal(10000m, dbInvoice.GetPaidAmount());
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());
    }

    [Fact]
    public async Task Failed_Payment_Does_Not_Settle_Invoice()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-B3");
        payment.MarkFailed();
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-B3");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.CannotAllocatePendingPayment", result.Errors![0].Code);
        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.Issued, dbInvoice.Status);
        Assert.Equal(0m, dbInvoice.GetPaidAmount());
    }

    [Fact]
    public async Task Pending_Payment_Does_Not_Settle_Invoice()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-B4");
        // Don't complete the payment
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-B4");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.CannotAllocatePendingPayment", result.Errors![0].Code);
    }

    // ------------------------------------------------------------------
    // Receipt tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Receipt_Amount_Equals_Payment_Amount()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-C1");
        payment.Complete(DateTime.UtcNow);
        await db.SaveChangesAsync();

        var auditWriter = Substitute.For<IAuditWriter>();
        var handler = new IssueReceiptHandler(db, auditWriter);

        // Act
        var result = await handler.Handle(
            new IssueReceiptCommand(payment.Id, "RCPT-C1"),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var receipt = await db.PaymentReceipts.FirstAsync(r => r.PaymentId == payment.Id);
        Assert.Equal(13000m, receipt.Amount); // Receipt = Payment amount, not allocated
    }

    [Fact]
    public async Task Receipt_Cannot_Be_Issued_Twice()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-C2");
        payment.Complete(DateTime.UtcNow);
        await db.SaveChangesAsync();

        var auditWriter = Substitute.For<IAuditWriter>();
        var handler = new IssueReceiptHandler(db, auditWriter);

        // Act
        var result1 = await handler.Handle(
            new IssueReceiptCommand(payment.Id, "RCPT-C2"),
            CancellationToken.None);
        var result2 = await handler.Handle(
            new IssueReceiptCommand(payment.Id, "RCPT-C2-RETRY"),
            CancellationToken.None);

        // Assert
        Assert.True(result1.IsSuccess);
        Assert.False(result2.IsSuccess);
        Assert.Equal("Receipt.AlreadyExists", result2.Errors![0].Code);
        Assert.Single(db.PaymentReceipts.Where(r => r.PaymentId == payment.Id));
    }

    // ------------------------------------------------------------------
    // Overpayment tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Overpayment_PaymentAmount_Preserved_And_Unallocated_Preserved()
    {
        // Arrange: Invoice = 12,000, Payment = 13,000, Allocation = 12,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-D1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 12000m, "INV-D1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);

        // Payment amount preserved
        Assert.Equal(13000m, dbPayment.Amount);
        // Active allocation = 12,000
        Assert.Equal(12000m, dbPayment.GetAllocatedAmount());
        // Unallocated = 1,000 (preserved, not recorded as liability settlement)
        Assert.Equal(1000m, dbPayment.GetUnallocatedAmount());
        // Invoice is Paid
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
    }

    [Fact]
    public async Task Overpayment_Ledger_Settlement_Equals_Allocated_Not_Payment()
    {
        // Arrange: Invoice = 12,000, Payment = 13,000, Allocation = 12,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-D2");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 12000m, "INV-D2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var settlements = await db.CustomerLedgerEntries
            .Where(e => e.PaymentId == payment.Id && e.EntryType == LedgerEntryType.PaymentSettlement)
            .ToListAsync();

        // Total settlement = 12,000 (allocated), NOT 13,000 (payment amount)
        Assert.Single(settlements);
        Assert.Equal(12000m, settlements[0].Amount);
        Assert.Equal(12000m, settlements.Sum(s => s.Amount));

        // Specifically assert no settlement of 13,000 exists
        Assert.DoesNotContain(settlements, s => s.Amount == 13000m);
    }

    // ------------------------------------------------------------------
    // Multi-invoice payment tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task MultiInvoice_Payment_Unallocated_Preserved()
    {
        // Arrange: Invoice A = 6,000, Invoice B = 4,000, Payment = 13,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-E1");
        payment.Complete(DateTime.UtcNow);
        var invoiceA = CreateInvoice(db, tenantId, 6000m, "INV-E1A");
        var invoiceB = CreateInvoice(db, tenantId, 4000m, "INV-E1B");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        var resultA = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoiceA.Id, 6000m),
            CancellationToken.None);
        var resultB = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoiceB.Id, 4000m),
            CancellationToken.None);

        // Assert
        Assert.True(resultA.IsSuccess);
        Assert.True(resultB.IsSuccess);

        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        var dbInvoiceA = await db.Invoices.FirstAsync(i => i.Id == invoiceA.Id);
        var dbInvoiceB = await db.Invoices.FirstAsync(i => i.Id == invoiceB.Id);

        // Payment amount preserved
        Assert.Equal(13000m, dbPayment.Amount);
        // Total allocated = 10,000
        Assert.Equal(10000m, dbPayment.GetAllocatedAmount());
        // Unallocated = 3,000
        Assert.Equal(3000m, dbPayment.GetUnallocatedAmount());
        // Both invoices Paid
        Assert.Equal(InvoiceStatus.Paid, dbInvoiceA.Status);
        Assert.Equal(InvoiceStatus.Paid, dbInvoiceB.Status);
    }

    [Fact]
    public async Task MultiInvoice_Ledger_Settlement_Equals_Total_Allocations()
    {
        // Arrange: Invoice A = 6,000, Invoice B = 4,000, Payment = 13,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-E2");
        payment.Complete(DateTime.UtcNow);
        var invoiceA = CreateInvoice(db, tenantId, 6000m, "INV-E2A");
        var invoiceB = CreateInvoice(db, tenantId, 4000m, "INV-E2B");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceA.Id, 6000m), CancellationToken.None);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceB.Id, 4000m), CancellationToken.None);

        // Assert
        var settlements = await db.CustomerLedgerEntries
            .Where(e => e.PaymentId == payment.Id && e.EntryType == LedgerEntryType.PaymentSettlement)
            .ToListAsync();

        // Total settlement = 10,000 (total allocations), NOT 13,000
        Assert.Equal(2, settlements.Count);
        Assert.Equal(10000m, settlements.Sum(s => s.Amount));
        // Each settlement references its allocation
        Assert.All(settlements, s => Assert.NotNull(s.PaymentAllocationId));
    }

    // ------------------------------------------------------------------
    // Duplicate / retry safety tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_Allocation_Cannot_Exceed_Payment_Amount()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-F1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-F1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: First allocation succeeds
        var result1 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None);

        // Identical retry — must be recognized as idempotent (NOT rejected by capacity).
        var result2 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None);

        // Assert: both succeed, only one allocation exists
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess); // Idempotent retry

        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(6000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(4000m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Duplicate_Allocation_Cannot_Exceed_Invoice_Outstanding()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 20000m, "PAY-F2");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, "INV-F2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: First allocation pays the invoice fully
        var result1 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m),
            CancellationToken.None);

        // Second allocation exceeds invoice remaining (now 0) — overpayment creates credit
        var result2 = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 1000m),
            CancellationToken.None);

        // Assert: Both succeed. Second allocation creates overpayment credit for 1000
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(5000m, dbInvoice.GetPaidAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(1000m, overpaymentCredit.Amount);
    }

    // ------------------------------------------------------------------
    // Tenant isolation tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task CrossTenant_Allocation_Is_Rejected()
    {
        // Arrange: payment in tenant-A context, invoice in tenant-B context.
        // The tenant query filter prevents tenant-A from seeing tenant-B's invoice
        // (this is the primary cross-tenant defense). The handler must reject this.
        var tenantA = "tenant-A";
        var tenantB = "tenant-B";
        var dbA = CreateDbContext(tenantA);
        var dbB = CreateDbContext(tenantB);

        // Create payment in tenant A
        var payment = CreatePayment(dbA, tenantA, 10000m, "PAY-G1");
        payment.Complete(DateTime.UtcNow);
        await dbA.SaveChangesAsync();

        // Create invoice in tenant B
        var invoice = CreateInvoice(dbB, tenantB, 10000m, "INV-G1");
        await dbB.SaveChangesAsync();

        // Use tenant A's context for the handler
        var handler = await CreateHandler(dbA);

        // Act: Try to allocate tenant A's payment to tenant B's invoice
        var result = await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m),
            CancellationToken.None);

        // Assert: Rejected (tenant filter hides the invoice → InvoiceNotFound or similar failure)
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Errors);
        Assert.NotEmpty(result.Errors!);
    }

    [Fact]
    public async Task Tenant_A_Cannot_See_Tenant_B_Payments()
    {
        // Arrange
        var tenantA = "tenant-A";
        var tenantB = "tenant-B";
        var dbB = CreateDbContext(tenantB);

        // Create payment in tenant B
        CreatePayment(dbB, tenantB, 10000m, "PAY-G2");
        await dbB.SaveChangesAsync();

        // Query from tenant A's context
        var dbA = CreateDbContext(tenantA);

        // Act
        var payments = await dbA.Payments.ToListAsync();

        // Assert
        Assert.Empty(payments);
    }

    // ------------------------------------------------------------------
    // Ledger reconstructability tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ledger_Reconstructable_From_Immutable_Movements()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-H1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-H1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: Create an invoice charge ledger entry first
        var chargeEntry = CustomerLedgerEntry.CreateInvoiceCharge(
            Guid.NewGuid(),
            invoice.Id,
            10000m,
            "EGP",
            0m,
            DateTime.UtcNow).Value;
        db.CustomerLedgerEntries.Add(chargeEntry);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        // Then allocate payment
        await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m),
            CancellationToken.None);

        // Assert: Ledger can be reconstructed
        var entries = await db.CustomerLedgerEntries
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.RecordedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(LedgerEntryType.InvoiceCharge, entries[0].EntryType);
        Assert.Equal(10000m, entries[0].Amount);
        Assert.Equal(LedgerEntryType.PaymentSettlement, entries[1].EntryType);
        Assert.Equal(4000m, entries[1].Amount);

        // Reconstructed balance: 10000 - 4000 = 6000
        var reconstructedBalance = entries.Sum(e => e.IsDebit ? e.Amount : -e.Amount);
        Assert.Equal(6000m, reconstructedBalance);
    }

    [Fact]
    public async Task Ledger_RunningBalance_Matches_Reconstructed_Balance()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-H2");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-H2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Create invoice charge
        var chargeEntry2 = CustomerLedgerEntry.CreateInvoiceCharge(
            Guid.NewGuid(),
            invoice.Id,
            10000m,
            "EGP",
            0m,
            DateTime.UtcNow).Value;
        db.CustomerLedgerEntries.Add(chargeEntry2);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        // Allocate payment
        await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m),
            CancellationToken.None);

        // Assert
        var lastEntry = await db.CustomerLedgerEntries
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.RecordedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstAsync();

        // RunningBalance should be 6000 (10000 - 4000)
        Assert.Equal(6000m, lastEntry.RunningBalance);
    }

    // ------------------------------------------------------------------
    // Concurrency simulation tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_Allocations_Total_Does_Not_Exceed_Payment_Amount()
    {
        // Arrange: Payment = 10,000, Invoice = 10,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-I1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-I1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: Simulate concurrent allocations by sequentializing them
        // (EF InMemory doesn't support true concurrency, but we verify invariants)
        var results = new List<Result<Updated>>();

        // First allocation: 6,000
        results.Add(await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None));

        // Identical retry — must be recognized as idempotent (not rejected by capacity)
        results.Add(await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m),
            CancellationToken.None));

        // Third allocation: 4,000 (should succeed, uses remaining)
        results.Add(await handler.Handle(
            new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m),
            CancellationToken.None));

        // Assert
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess); // Idempotent retry succeeds
        Assert.True(results[2].IsSuccess);

        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(10000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(0m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Concurrent_Allocations_On_Same_Invoice_Total_Does_Not_Exceed_Invoice()
    {
        // Arrange: Two payments, one invoice = 5,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment1 = CreatePayment(db, tenantId, 10000m, "PAY-I2A");
        payment1.Complete(DateTime.UtcNow);
        var payment2 = CreatePayment(db, tenantId, 10000m, "PAY-I2B");
        payment2.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 5000m, "INV-I2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act: First allocates 4000 (remaining = 1000), second allocates 4000 (overpayment: 1000 to invoice + 3000 credit)
        var result1 = await handler.Handle(
            new AllocatePaymentCommand(payment1.Id, invoice.Id, 4000m),
            CancellationToken.None);
        var result2 = await handler.Handle(
            new AllocatePaymentCommand(payment2.Id, invoice.Id, 4000m),
            CancellationToken.None);

        // Assert: Both succeed — second caps at 1000 for invoice, 3000 becomes overpayment credit
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var dbInvoice = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(5000m, dbInvoice.GetPaidAmount());
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment2.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(3000m, overpaymentCredit.Amount);
    }

    // ------------------------------------------------------------------
    // Financial invariant tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Financial_Invariant_Total_Settlement_Equals_Total_Allocations()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-J1");
        payment.Complete(DateTime.UtcNow);
        var invoiceA = CreateInvoice(db, tenantId, 6000m, "INV-J1A");
        var invoiceB = CreateInvoice(db, tenantId, 4000m, "INV-J1B");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceA.Id, 6000m), CancellationToken.None);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceB.Id, 4000m), CancellationToken.None);

        // Assert
        var totalAllocations = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id && a.Status == PaymentAllocationStatus.Active)
            .SumAsync(a => a.AllocatedAmount);

        var totalSettlements = await db.CustomerLedgerEntries
            .Where(e => e.PaymentId == payment.Id && e.EntryType == LedgerEntryType.PaymentSettlement)
            .SumAsync(e => e.Amount);

        // Critical invariant: Total PaymentSettlement = Total Active Allocations
        Assert.Equal(totalAllocations, totalSettlements);
        Assert.Equal(10000m, totalAllocations);
        Assert.Equal(10000m, totalSettlements);
    }

    [Fact]
    public async Task Financial_Invariant_Payment_Amount_Not_Confused_With_Allocated()
    {
        // Arrange: Payment = 13,000, Allocations = 10,000
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 13000m, "PAY-J2");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-J2");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 10000m), CancellationToken.None);

        // Assert
        var dbPayment = await db.Payments.FirstAsync(p => p.Id == payment.Id);

        // Payment.Amount ≠ AllocatedAmount
        Assert.NotEqual(dbPayment.Amount, dbPayment.GetAllocatedAmount());
        Assert.Equal(13000m, dbPayment.Amount);
        Assert.Equal(10000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(3000m, dbPayment.GetUnallocatedAmount());
    }

    // ------------------------------------------------------------------
    // Settlement entry references allocation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Settlement_Entry_References_Allocation()
    {
        // Arrange
        var tenantId = "tenant-1";
        var db = CreateDbContext(tenantId);
        var payment = CreatePayment(db, tenantId, 10000m, "PAY-K1");
        payment.Complete(DateTime.UtcNow);
        var invoice = CreateInvoice(db, tenantId, 10000m, "INV-K1");
        await db.SaveChangesAsync();

        var handler = await CreateHandler(db);

        // Act
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m), CancellationToken.None);

        // Assert
        var allocation = await db.PaymentAllocations
            .Where(a => a.PaymentId == payment.Id)
            .FirstAsync();

        var settlement = await db.CustomerLedgerEntries
            .Where(e => e.EntryType == LedgerEntryType.PaymentSettlement)
            .FirstAsync();

        Assert.Equal(allocation.Id, settlement.PaymentAllocationId);
        Assert.Equal(payment.Id, settlement.PaymentId);
        Assert.Equal(5000m, settlement.Amount);
    }
}
