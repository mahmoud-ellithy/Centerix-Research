namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
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
/// Task 10.1: Credit Application Financial Correction tests.
/// Tests partial credit, multiple applications, invoice balance integration,
/// ledger semantics, cross-tenant isolation, and invoice status.
/// </summary>
public class Phase10_1CreditApplicationCorrectionTests
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

    private static async Task<Invoice> CreateDraftInvoiceAsync(AppDbContext db, string tenantId, decimal totalAmount = 2000m)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            $"INV-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            totalAmount,
            0,
            0,
            totalAmount).Value;

        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return invoice;
    }

    private static async Task<Invoice> IssueInvoiceAsync(AppDbContext db, Invoice invoice)
    {
        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);
        return (await db.Invoices.FindAsync(invoice.Id))!;
    }

    private static async Task<TenantCredit> CreateAvailableCreditAsync(AppDbContext db, string tenantId, decimal amount = 1000m)
    {
        var credit = TenantCredit.Create(
            Guid.NewGuid(),
            amount,
            CreditSourceType.Manual).Value;

        db.TenantCredits.Add(credit);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return credit;
    }

    private static async Task<Payment> CreateCompletedPaymentAsync(AppDbContext db, string tenantId, decimal amount)
    {
        var payment = Payment.Create(
            Guid.NewGuid(),
            $"PAY-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
            amount,
            "EGP",
            PaymentMethod.Cash).Value;

        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment;
    }

    // ==================================================================
    // Partial Credit Application
    // ==================================================================

    [Fact]
    public async Task PartialCredit_Apply400Of1000_CreditRemaining600()
    {
        using var db = CreateDbContext("tenant-pc");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-pc", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-pc", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.PartiallyApplied, updatedCredit.Status);
        Assert.Equal(600m, updatedCredit.RemainingAmount);

        var creditApplication = await db.CreditApplications
            .FirstOrDefaultAsync(ca => ca.CreditId == credit.Id);
        Assert.NotNull(creditApplication);
        Assert.Equal(400m, creditApplication.Amount);
        Assert.Equal(invoice.Id, creditApplication.InvoiceId);
    }

    [Fact]
    public async Task PartialCredit_ApplyRemaining600_CreditFullyConsumed()
    {
        using var db = CreateDbContext("tenant-pc2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-pc2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-pc2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var result2 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 600m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.True(result2.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.Applied, updatedCredit.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);

        var applications = await db.CreditApplications
            .Where(ca => ca.CreditId == credit.Id)
            .ToListAsync();
        Assert.Equal(2, applications.Count);
        Assert.Equal(400m, applications[0].Amount);
        Assert.Equal(600m, applications[1].Amount);
    }

    [Fact]
    public async Task PartialCredit_InvoiceReducedBy400()
    {
        using var db = CreateDbContext("tenant-pc3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-pc3", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-pc3", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(1600m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(400m, updatedInvoice.GetAppliedCreditAmount());
    }

    // ==================================================================
    // Complete Credit Application
    // ==================================================================

    [Fact]
    public async Task CompleteCredit_Apply1000Of1000_CreditFullyConsumed()
    {
        using var db = CreateDbContext("tenant-cc");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cc", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-cc", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.Applied, updatedCredit.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(1000m, updatedInvoice.GetRemainingAmount());
    }

    // ==================================================================
    // Multiple Applications (Different Invoices)
    // ==================================================================

    [Fact]
    public async Task MultipleApplications_Apply400ToInvoiceA_600ToInvoiceB()
    {
        using var db = CreateDbContext("tenant-ma");
        var invoiceA = await CreateDraftInvoiceAsync(db, "tenant-ma", 2000m);
        var invoiceB = await CreateDraftInvoiceAsync(db, "tenant-ma", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ma", 1000m);
        await IssueInvoiceAsync(db, invoiceA);
        await IssueInvoiceAsync(db, invoiceB);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result1 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceA.Id, 400m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var result2 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceB.Id, 600m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.Applied, updatedCredit.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);

        var applications = await db.CreditApplications
            .Where(ca => ca.CreditId == credit.Id)
            .ToListAsync();
        Assert.Equal(2, applications.Count);
        Assert.Contains(applications, a => a.InvoiceId == invoiceA.Id && a.Amount == 400m);
        Assert.Contains(applications, a => a.InvoiceId == invoiceB.Id && a.Amount == 600m);

        var updatedInvoiceA = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoiceA.Id);
        Assert.Equal(1600m, updatedInvoiceA.GetRemainingAmount());

        var updatedInvoiceB = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoiceB.Id);
        Assert.Equal(1400m, updatedInvoiceB.GetRemainingAmount());
    }

    // ==================================================================
    // Cannot Exceed Credit
    // ==================================================================

    [Fact]
    public async Task ExceedCredit_Apply1001Of1000_IsRejected()
    {
        using var db = CreateDbContext("tenant-ec");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ec", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ec", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1001m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task ExceedCreditRemaining_Apply600OfRemaining400_IsRejected()
    {
        using var db = CreateDbContext("tenant-ec2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ec2", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ec2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 600m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var result2 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 600m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result2.IsSuccess);
        Assert.Contains(result2.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    // ==================================================================
    // Cannot Exceed Invoice Remaining
    // ==================================================================

    [Fact]
    public async Task ExceedInvoice_Apply501ToInvoiceRemaining500_IsRejected()
    {
        using var db = CreateDbContext("tenant-ei");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ei", 500m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ei", 5000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 501m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.ExceedsInvoiceRemaining");
    }

    [Fact]
    public async Task ApplyExactlyInvoiceRemaining_Succeeds()
    {
        using var db = CreateDbContext("tenant-ei2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ei2", 500m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ei2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
    }

    // ==================================================================
    // Invoice Status — Paid via Payment + Credit
    // ==================================================================

    [Fact]
    public async Task InvoiceStatus_Payment1000_Credit1000_Total2000_IsPaid()
    {
        using var db = CreateDbContext("tenant-is");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-is", 2000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-is", 1000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-is", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var paymentHandler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), NullSubscriptionReconciliationService.Instance, AllowPlatformAdmin());
        await paymentHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 1000m), CancellationToken.None);

        var creditHandler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.PaymentAllocations)
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);
    }

    [Fact]
    public async Task InvoiceStatus_Payment1000_Credit500_Total2000_IsPartiallyPaid()
    {
        using var db = CreateDbContext("tenant-is2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-is2", 2000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-is2", 1000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-is2", 500m);
        await IssueInvoiceAsync(db, invoice);

        var paymentHandler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), NullSubscriptionReconciliationService.Instance, AllowPlatformAdmin());
        await paymentHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 1000m), CancellationToken.None);

        var creditHandler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.PaymentAllocations)
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(500m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);
    }

    private static IPlatformAdminGuard AllowPlatformAdmin()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }

    // ==================================================================
    // Ledger Semantics — CreditUsage not CreditCreation
    // ==================================================================

    [Fact]
    public async Task LedgerEntry_IsCreditUsage_NotCreditCreation()
    {
        using var db = CreateDbContext("tenant-le");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-le", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-le", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var ledgerEntries = await db.CustomerLedgerEntries
            .Where(e => e.CreditId == credit.Id)
            .ToListAsync();
        Assert.Single(ledgerEntries);
        Assert.Equal(LedgerEntryType.CreditUsage, ledgerEntries[0].EntryType);
        Assert.Equal(credit.Id, ledgerEntries[0].CreditId);
    }

    [Fact]
    public async Task LedgerEntry_RecordsCreditApplicationId()
    {
        using var db = CreateDbContext("tenant-le2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-le2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-le2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var creditApplication = await db.CreditApplications
            .FirstOrDefaultAsync(ca => ca.CreditId == credit.Id);
        Assert.NotNull(creditApplication);

        var ledgerEntry = await db.CustomerLedgerEntries
            .FirstOrDefaultAsync(e => e.CreditId == credit.Id);
        Assert.NotNull(ledgerEntry);
        Assert.Equal(creditApplication.Id, ledgerEntry.CreditApplicationId);
        Assert.Equal(LedgerEntryType.CreditUsage, ledgerEntry.EntryType);
    }

    // ==================================================================
    // Credit Application is Immutable Audit Record
    // ==================================================================

    [Fact]
    public async Task CreditApplication_IsImmutableAuditRecord()
    {
        using var db = CreateDbContext("tenant-ia");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ia", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ia", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var creditApplication = await db.CreditApplications
            .FirstOrDefaultAsync(ca => ca.CreditId == credit.Id);
        Assert.NotNull(creditApplication);
        Assert.Equal(credit.Id, creditApplication.CreditId);
        Assert.Equal(invoice.Id, creditApplication.InvoiceId);
        Assert.Equal(1000m, creditApplication.Amount);
        Assert.True(creditApplication.AppliedAtUtc <= DateTime.UtcNow);
        Assert.NotEqual(Guid.Empty, creditApplication.Id);
    }

    // ==================================================================
    // Cross-Tenant Isolation
    // ==================================================================

    [Fact]
    public async Task CrossTenant_TenantACredit_TenantBInvoice_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");
        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B", 2000m);
        var creditA = await CreateAvailableCreditAsync(dbA, "tenant-A", 1000m);
        await IssueInvoiceAsync(dbB, invoiceB);

        var handler = new ApplyCreditToInvoiceHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(creditA.Id, invoiceB.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        // Cross-tenant: either invoice is not visible (query filter blocks) or explicit cross-tenant check
        Assert.True(
            result.Errors!.Any(e => e.Code == "TenantCredit.CrossTenant") ||
            result.Errors!.Any(e => e.Code == "Invoice.NotFound"),
            $"Expected cross-tenant rejection. Got: {string.Join(", ", result.Errors!.Select(e => e.Code))}");
    }

    // ==================================================================
    // Cannot Apply to Draft or Cancelled Invoice
    // ==================================================================

    [Fact]
    public async Task ApplyCredit_ToDraftInvoice_IsRejected()
    {
        using var db = CreateDbContext("tenant-dc");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-dc", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-dc", 1000m);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    [Fact]
    public async Task ApplyCredit_ToCancelledInvoice_IsRejected()
    {
        using var db = CreateDbContext("tenant-dc2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-dc2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-dc2", 1000m);

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Cancelled;
        await db.SaveChangesAsync();

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    // ==================================================================
    // Cannot Apply Already Fully Consumed Credit
    // ==================================================================

    [Fact]
    public async Task ApplyCredit_AlreadyFullyConsumed_IsRejected()
    {
        using var db = CreateDbContext("tenant-af");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-af", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-af", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var result2 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result2.IsSuccess);
        Assert.Contains(result2.Errors!, e => e.Code == "TenantCredit.NotAvailable");
    }

    // ==================================================================
    // Payment + Credit Combined — Invoice Balance
    // ==================================================================

    [Fact]
    public async Task CombinedSettlement_PaymentAndCredit_ReflectsCorrectBalance()
    {
        using var db = CreateDbContext("tenant-cs");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cs", 5000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-cs", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-cs", 1500m);
        await IssueInvoiceAsync(db, invoice);

        var paymentHandler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), NullSubscriptionReconciliationService.Instance, AllowPlatformAdmin());
        await paymentHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 2000m), CancellationToken.None);

        var creditHandler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1500m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.PaymentAllocations)
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(5000m, updatedInvoice.TotalAmount);
        Assert.Equal(2000m, updatedInvoice.GetPaidAmount());
        Assert.Equal(1500m, updatedInvoice.GetAppliedCreditAmount());
        Assert.Equal(1500m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);
    }

    // ==================================================================
    // Credit RemainingAmount tracks correctly
    // ==================================================================

    [Fact]
    public async Task CreditRemainingAmount_DecrementsCorrectly()
    {
        using var db = CreateDbContext("tenant-cr");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cr", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-cr", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());

        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var creditAfterFirst = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(700m, creditAfterFirst!.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, creditAfterFirst.Status);

        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 200m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var creditAfterSecond = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(500m, creditAfterSecond!.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, creditAfterSecond.Status);

        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var creditAfterThird = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(0m, creditAfterThird!.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, creditAfterThird.Status);
    }

    // ==================================================================
    // Application Amount Must Be Positive
    // ==================================================================

    [Fact]
    public async Task ApplyCredit_ZeroAmount_IsRejected()
    {
        using var db = CreateDbContext("tenant-zp");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-zp", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-zp", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 0m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task ApplyCredit_NegativeAmount_IsRejected()
    {
        using var db = CreateDbContext("tenant-zp2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-zp2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-zp2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, -100m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    // ==================================================================
    // Multiple CreditApplications are preserved for same credit
    // ==================================================================

    [Fact]
    public async Task MultipleApplications_AllPreserved_HistoricallyVisible()
    {
        using var db = CreateDbContext("tenant-hv");
        var invoiceA = await CreateDraftInvoiceAsync(db, "tenant-hv", 1000m);
        var invoiceB = await CreateDraftInvoiceAsync(db, "tenant-hv", 1000m);
        var invoiceC = await CreateDraftInvoiceAsync(db, "tenant-hv", 1000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-hv", 500m);
        await IssueInvoiceAsync(db, invoiceA);
        await IssueInvoiceAsync(db, invoiceB);
        await IssueInvoiceAsync(db, invoiceC);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceA.Id, 200m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceB.Id, 200m, Guid.NewGuid().ToString("N")), CancellationToken.None);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceC.Id, 100m, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var applications = await db.CreditApplications
            .Where(ca => ca.CreditId == credit.Id)
            .OrderBy(ca => ca.AppliedAtUtc)
            .ToListAsync();
        Assert.Equal(3, applications.Count);
        Assert.Equal(200m, applications[0].Amount);
        Assert.Equal(invoiceA.Id, applications[0].InvoiceId);
        Assert.Equal(200m, applications[1].Amount);
        Assert.Equal(invoiceB.Id, applications[1].InvoiceId);
        Assert.Equal(100m, applications[2].Amount);
        Assert.Equal(invoiceC.Id, applications[2].InvoiceId);
    }
}
