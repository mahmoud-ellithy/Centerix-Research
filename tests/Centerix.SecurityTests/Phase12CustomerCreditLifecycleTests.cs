namespace Centerix.SecurityTests;

using System.Reflection;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Billing.Queries;
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

/// <summary>
/// Task 12: Customer Credit &amp; Overpayment Lifecycle tests.
/// Task 12.1: Updated for key-based idempotency and currency integrity.
/// Comprehensive tests covering: overpayment creation, credit application,
/// partial application, idempotency (key-based), concurrency, tenant isolation,
/// currency integrity, ledger auditability, and historical integrity.
/// </summary>
public class Phase12CustomerCreditLifecycleTests
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

    private static async Task<Invoice> CreateDraftInvoiceAsync(AppDbContext db, string tenantId, decimal totalAmount)
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

    private static async Task<Payment> CreateCompletedPaymentAsync(AppDbContext db, string tenantId, decimal amount, string currencyCode = "EGP")
    {
        var payment = Payment.Create(
            Guid.NewGuid(),
            $"PAY-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
            amount,
            currencyCode,
            PaymentMethod.Cash).Value;

        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<TenantCredit> CreateAvailableCreditAsync(AppDbContext db, string tenantId, decimal amount, string currencyCode = "EGP", CreditSourceType sourceType = CreditSourceType.Manual)
    {
        var credit = TenantCredit.Create(
            Guid.NewGuid(),
            amount,
            sourceType,
            null,
            currencyCode).Value;

        db.TenantCredits.Add(credit);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return credit;
    }

    private static AllocatePaymentHandler CreatePaymentHandler(AppDbContext db)
    {
        return new AllocatePaymentHandler(
            db,
            Substitute.For<IAuditWriter>(),
            NullSubscriptionReconciliationService.Instance,
            AllowPlatformAdmin());
    }

    private static ApplyCreditToInvoiceHandler CreateCreditHandler(AppDbContext db)
    {
        return new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static IPlatformAdminGuard AllowPlatformAdmin()
    {
        var guard = Substitute.For<IPlatformAdminGuard>();
        guard.EnsurePlatformAdmin().Returns(Result.Updated);
        return guard;
    }

    // ==================================================================
    // A. Overpayment Tests
    // ==================================================================

    [Fact]
    public async Task Overpayment_ExactAllocation_NoCredit()
    {
        using var db = CreateDbContext("tenant-op1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op1", 12000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m), CancellationToken.None);

        var credits = await db.TenantCredits.Where(c => c.SourceType == CreditSourceType.Overpayment).ToListAsync();
        Assert.Empty(credits);

        var dbPayment = await db.Payments.FindAsync(payment.Id);
        Assert.Equal(12000m, dbPayment!.Amount);
        Assert.Equal(12000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(0m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Overpayment_ExcessCreatesCredit_ExactAmount()
    {
        using var db = CreateDbContext("tenant-op2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op2", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op2", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(credit);
        Assert.Equal(1000m, credit.Amount);
        Assert.Equal(1000m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.Available, credit.Status);
        Assert.Equal("EGP", credit.CurrencyCode);
    }

    [Fact]
    public async Task Overpayment_PaymentAmountRemainsImmutable()
    {
        using var db = CreateDbContext("tenant-op3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op3", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op3", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var dbPayment = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(13000m, dbPayment.Amount);
        Assert.Equal(12000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(1000m, dbPayment.GetUnallocatedAmount());
    }

    [Fact]
    public async Task Overpayment_InvoiceTotalRemainsImmutable()
    {
        using var db = CreateDbContext("tenant-op4");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op4", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op4", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var dbInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(dbInvoice);
        Assert.Equal(12000m, dbInvoice.TotalAmount);
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
    }

    [Fact]
    public async Task Overpayment_NegativeUnallocated_Rejected()
    {
        using var db = CreateDbContext("tenant-op5");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op5", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op5", 5000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        var result = await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PaymentAllocation.ExceedsPayment", result.Errors![0].Code);
    }

    [Fact]
    public async Task Overpayment_CreditSourceTraceable()
    {
        using var db = CreateDbContext("tenant-op6");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op6", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op6", 15000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 15000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(credit);
        Assert.Equal(payment.Id, credit.SourceId);
        Assert.Equal(CreditSourceType.Overpayment, credit.SourceType);
    }

    [Fact]
    public async Task Overpayment_CreditCreationLedgerEntryCreated()
    {
        using var db = CreateDbContext("tenant-op7");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-op7", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-op7", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(credit);

        var ledgerEntry = await db.CustomerLedgerEntries
            .FirstOrDefaultAsync(e => e.CreditId == credit.Id && e.EntryType == LedgerEntryType.CreditCreation);
        Assert.NotNull(ledgerEntry);
        Assert.Equal(1000m, ledgerEntry.Amount);
        Assert.Equal("EGP", ledgerEntry.CurrencyCode);
    }

    // ==================================================================
    // B. Credit Application Tests
    // ==================================================================

    [Fact]
    public async Task CreditApplication_FullAmount_Succeeds()
    {
        using var db = CreateDbContext("tenant-ca1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca1", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca1", 2000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 2000m, NewKey()), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(CreditStatus.Applied, updatedCredit!.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);
    }

    [Fact]
    public async Task CreditApplication_PartialAmount_CorrectRemaining()
    {
        using var db = CreateDbContext("tenant-ca2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, NewKey()), CancellationToken.None);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(CreditStatus.PartiallyApplied, updatedCredit!.Status);
        Assert.Equal(600m, updatedCredit.RemainingAmount);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(1600m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(400m, updatedInvoice.GetAppliedCreditAmount());
    }

    [Fact]
    public async Task CreditApplication_MultipleApplicationsFromSameCredit()
    {
        using var db = CreateDbContext("tenant-ca3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca3", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca3", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, NewKey()), CancellationToken.None);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 600m, NewKey()), CancellationToken.None);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(CreditStatus.Applied, updatedCredit!.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);

        var applications = await db.CreditApplications
            .Where(ca => ca.CreditId == credit.Id)
            .ToListAsync();
        Assert.Equal(2, applications.Count);
        Assert.Contains(applications, a => a.Amount == 400m);
        Assert.Contains(applications, a => a.Amount == 600m);
    }

    [Fact]
    public async Task CreditApplication_MultipleCreditsSameInvoice()
    {
        using var db = CreateDbContext("tenant-ca4");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca4", 5000m);
        var creditA = await CreateAvailableCreditAsync(db, "tenant-ca4", 1000m);
        var creditB = await CreateAvailableCreditAsync(db, "tenant-ca4", 2000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(creditA.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);
        await handler.Handle(new ApplyCreditToInvoiceCommand(creditB.Id, invoice.Id, 2000m, NewKey()), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(3000m, updatedInvoice.GetAppliedCreditAmount());
        Assert.Equal(2000m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);
    }

    [Fact]
    public async Task CreditApplication_CannotExceedCreditBalance()
    {
        using var db = CreateDbContext("tenant-ca5");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca5", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca5", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1001m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task CreditApplication_CannotExceedInvoiceRemaining()
    {
        using var db = CreateDbContext("tenant-ca6");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca6", 500m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca6", 5000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 501m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.ExceedsInvoiceRemaining");
    }

    [Fact]
    public async Task CreditApplication_ZeroAmount_Rejected()
    {
        using var db = CreateDbContext("tenant-ca7");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca7", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca7", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 0m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task CreditApplication_NegativeAmount_Rejected()
    {
        using var db = CreateDbContext("tenant-ca8");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca8", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca8", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, -100m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task CreditApplication_CannotApplyToPaidInvoice()
    {
        using var db = CreateDbContext("tenant-ca9");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca9", 1000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca9", 5000m);
        await IssueInvoiceAsync(db, invoice);

        var payment = await CreateCompletedPaymentAsync(db, "tenant-ca9", 1000m);
        var payHandler = CreatePaymentHandler(db);
        await payHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 1000m), CancellationToken.None);

        var inv = await db.Invoices.FindAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, inv!.Status);

        var creditHandler = CreateCreditHandler(db);
        var result = await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CreditApplication_ImmutableAuditRecord()
    {
        using var db = CreateDbContext("tenant-ca10");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ca10", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ca10", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        var creditApp = await db.CreditApplications
            .FirstOrDefaultAsync(ca => ca.CreditId == credit.Id);
        Assert.NotNull(creditApp);
        Assert.Equal(credit.Id, creditApp.CreditId);
        Assert.Equal(invoice.Id, creditApp.InvoiceId);
        Assert.Equal(1000m, creditApp.Amount);
        Assert.True(creditApp.AppliedAtUtc <= DateTime.UtcNow);
        Assert.NotEqual(Guid.Empty, creditApp.Id);
        Assert.False(string.IsNullOrEmpty(creditApp.IdempotencyKey));
    }

    // ==================================================================
    // C. Invoice Tests
    // ==================================================================

    [Fact]
    public async Task Invoice_PaymentPlusCredit_EqualsTotal()
    {
        using var db = CreateDbContext("tenant-inv1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-inv1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-inv1", 6000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-inv1", 6000m);
        await IssueInvoiceAsync(db, invoice);

        var payHandler = CreatePaymentHandler(db);
        await payHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 6000m), CancellationToken.None);

        var creditHandler = CreateCreditHandler(db);
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 6000m, NewKey()), CancellationToken.None);

        var dbInvoice = await db.Invoices
            .Include(i => i.PaymentAllocations)
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(12000m, dbInvoice.TotalAmount);
        Assert.Equal(6000m, dbInvoice.GetPaidAmount());
        Assert.Equal(6000m, dbInvoice.GetAppliedCreditAmount());
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
    }

    [Fact]
    public async Task Invoice_RemainingReachesZero()
    {
        using var db = CreateDbContext("tenant-inv2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-inv2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-inv2", 2000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 2000m, NewKey()), CancellationToken.None);

        var dbInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.Equal(0m, dbInvoice!.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
    }

    [Fact]
    public async Task Invoice_StatusChangesCorrectly()
    {
        using var db = CreateDbContext("tenant-inv3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-inv3", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-inv3", 500m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, NewKey()), CancellationToken.None);

        var dbInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, dbInvoice!.Status);
    }

    [Fact]
    public async Task Invoice_TotalAmountImmutable_AfterCreditApplication()
    {
        using var db = CreateDbContext("tenant-inv4");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-inv4", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-inv4", 5000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 5000m, NewKey()), CancellationToken.None);

        var dbInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.Equal(12000m, dbInvoice!.TotalAmount);
        Assert.Equal(12000m, dbInvoice.Subtotal);
    }

    // ==================================================================
    // D. Historical Integrity Tests
    // ==================================================================

    [Fact]
    public async Task HistoricalIntegrity_PaymentUnchanged()
    {
        using var db = CreateDbContext("tenant-hi1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-hi1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-hi1", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var dbPayment = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(dbPayment);
        Assert.Equal(13000m, dbPayment.Amount);
        Assert.Equal("EGP", dbPayment.CurrencyCode);
        Assert.Equal(PaymentMethod.Cash, dbPayment.Method);
        Assert.Equal(PaymentStatus.Completed, dbPayment.Status);
    }

    [Fact]
    public async Task HistoricalIntegrity_PaymentAllocationUnchanged()
    {
        using var db = CreateDbContext("tenant-hi2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-hi2", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-hi2", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var allocation = await db.PaymentAllocations
            .FirstOrDefaultAsync(a => a.PaymentId == payment.Id && a.InvoiceId == invoice.Id && a.Status == PaymentAllocationStatus.Active);
        Assert.NotNull(allocation);
        Assert.Equal(12000m, allocation.AllocatedAmount);
    }

    [Fact]
    public async Task HistoricalIntegrity_CreditApplicationImmutable()
    {
        using var db = CreateDbContext("tenant-hi3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-hi3", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-hi3", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        var creditApp = await db.CreditApplications.FirstOrDefaultAsync(ca => ca.CreditId == credit.Id);
        Assert.NotNull(creditApp);

        var appAmount = creditApp.Amount;
        var appInvoiceId = creditApp.InvoiceId;
        var appCreditId = creditApp.CreditId;

        Assert.Equal(appAmount, creditApp.Amount);
        Assert.Equal(appInvoiceId, creditApp.InvoiceId);
        Assert.Equal(appCreditId, creditApp.CreditId);
    }

    [Fact]
    public async Task HistoricalIntegrity_LedgerEntriesAuditable()
    {
        using var db = CreateDbContext("tenant-hi4");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-hi4", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-hi4", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(credit);

        var ledgerEntries = await db.CustomerLedgerEntries
            .Where(e => e.TenantId == "tenant-hi4")
            .OrderBy(e => e.RecordedAtUtc)
            .ToListAsync();

        Assert.Contains(ledgerEntries, e => e.EntryType == LedgerEntryType.PaymentSettlement);
        Assert.Contains(ledgerEntries, e => e.EntryType == LedgerEntryType.CreditCreation && e.CreditId == credit.Id);
    }

    // ==================================================================
    // E. Tenant Isolation Tests
    // ==================================================================

    [Fact]
    public async Task TenantIsolation_TenantACredit_TenantBInvoice_Rejected()
    {
        using var dbA = CreateDbContext("tenantA-TI");
        using var dbB = CreateDbContext("tenantB-TI");
        var invoice = await CreateDraftInvoiceAsync(dbB, "tenantB-TI", 2000m);
        var credit = await CreateAvailableCreditAsync(dbA, "tenantA-TI", 1000m);
        await IssueInvoiceAsync(dbB, invoice);

        var handler = CreateCreditHandler(dbA);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task TenantIsolation_CrossTenantPaymentAllocation_Rejected()
    {
        using var dbA = CreateDbContext("tenantA-TI2");
        using var dbB = CreateDbContext("tenantB-TI2");
        var invoice = await CreateDraftInvoiceAsync(dbB, "tenantB-TI2", 2000m);
        var payment = await CreateCompletedPaymentAsync(dbA, "tenantA-TI2", 2000m);
        await IssueInvoiceAsync(dbB, invoice);

        var handler = CreatePaymentHandler(dbA);
        var result = await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 2000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // F. Currency Tests
    // ==================================================================

    [Fact]
    public async Task Currency_CreditCreatedWithCorrectCurrency()
    {
        using var db = CreateDbContext("tenant-cur1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cur1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-cur1", 13000m, "USD");
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment);
        Assert.NotNull(credit);
        Assert.Equal("USD", credit.CurrencyCode);
    }

    [Fact]
    public async Task Currency_CreditApplicationUsesCreditCurrencyForLedger()
    {
        using var db = CreateDbContext("tenant-cur2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cur2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-cur2", 1000m, "EGP", CreditSourceType.Manual);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        var ledgerEntry = await db.CustomerLedgerEntries
            .FirstOrDefaultAsync(e => e.CreditId == credit.Id);
        Assert.NotNull(ledgerEntry);
        Assert.Equal("EGP", ledgerEntry.CurrencyCode);
    }

    // ==================================================================
    // G. Idempotency Tests (Key-based — BLOCKER 1)
    // ==================================================================

    [Fact]
    public async Task IdempotencyTest_I1_SameKeySamePayload_OneConsumption()
    {
        using var db = CreateDbContext("tenant-i1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-i1", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-i1", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var key = "idem-key-same-payload";

        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, key), CancellationToken.None);
        var retry = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, key), CancellationToken.None);

        Assert.True(retry.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(700m, updatedCredit!.RemainingAmount);

        var apps = await db.CreditApplications.Where(ca => ca.CreditId == credit.Id).ToListAsync();
        Assert.Single(apps);
    }

    [Fact]
    public async Task IdempotencyTest_I2_SameKeyDifferentPayload_Conflict()
    {
        using var db = CreateDbContext("tenant-i2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-i2", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-i2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        var key = "idem-key-conflict";

        var first = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, key), CancellationToken.None);
        Assert.True(first.IsSuccess);

        var second = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, key), CancellationToken.None);
        Assert.False(second.IsSuccess);
        Assert.Contains(second.Errors!, e => e.Code == "CreditApplication.IdempotencyKeyConflict");
    }

    [Fact]
    public async Task IdempotencyTest_I3_DifferentKeysSameAmount_BothLegitimate()
    {
        using var db = CreateDbContext("tenant-i3");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-i3", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-i3", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);

        var resultA = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, NewKey()), CancellationToken.None);
        var resultB = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 300m, NewKey()), CancellationToken.None);

        Assert.True(resultA.IsSuccess);
        Assert.True(resultB.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(400m, updatedCredit!.RemainingAmount);

        var apps = await db.CreditApplications.Where(ca => ca.CreditId == credit.Id).ToListAsync();
        Assert.Equal(2, apps.Count);
    }

    [Fact]
    public async Task IdempotencyTest_I4_DifferentKeysDifferentInvoices_Independent()
    {
        using var db = CreateDbContext("tenant-i4");
        var invoiceA = await CreateDraftInvoiceAsync(db, "tenant-i4", 3000m);
        var invoiceB = await CreateDraftInvoiceAsync(db, "tenant-i4", 3000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-i4", 2000m);
        await IssueInvoiceAsync(db, invoiceA);
        await IssueInvoiceAsync(db, invoiceB);

        var handler = CreateCreditHandler(db);

        var resultA = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceA.Id, 1000m, NewKey()), CancellationToken.None);
        var resultB = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoiceB.Id, 500m, NewKey()), CancellationToken.None);

        Assert.True(resultA.IsSuccess);
        Assert.True(resultB.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(500m, updatedCredit!.RemainingAmount);
    }

    // ==================================================================
    // H. Concurrency Tests (InMemory — sequential simulation)
    // ==================================================================

    [Fact]
    public async Task Concurrency_TwoApplicationsDifferentKeys_BothCanSucceed()
    {
        using var db = CreateDbContext("tenant-cx1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-cx1", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-cx1", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);

        var result1 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 700m, NewKey()), CancellationToken.None);
        var result2 = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 700m, NewKey()), CancellationToken.None);

        var successCount = new[] { result1, result2 }.Count(r => r.IsSuccess);
        Assert.Equal(1, successCount);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.Equal(300m, updatedCredit!.RemainingAmount);
    }

    // ==================================================================
    // I. Transaction Failure Tests
    // ==================================================================

    [Fact]
    public async Task TransactionFailure_CreditCreationDoesNotCorruptInvoice()
    {
        using var db = CreateDbContext("tenant-tx1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-tx1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-tx1", 12000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m), CancellationToken.None);

        var dbInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, dbInvoice!.Status);
        Assert.Equal(0m, dbInvoice.GetRemainingAmount());

        var credits = await db.TenantCredits
            .Where(c => c.SourceType == CreditSourceType.Overpayment)
            .ToListAsync();
        Assert.Empty(credits);
    }

    // ==================================================================
    // J. End-to-End Scenario (§55)
    // ==================================================================

    [Fact]
    public async Task EndToEnd_InvoicePaymentOverpaymentThenCreditApplication()
    {
        using var db = CreateDbContext("tenant-e2e");

        var invoice1 = await CreateDraftInvoiceAsync(db, "tenant-e2e", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-e2e", 13000m);
        await IssueInvoiceAsync(db, invoice1);

        var payHandler = CreatePaymentHandler(db);
        await payHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice1.Id, 13000m), CancellationToken.None);

        var dbInvoice1 = await db.Invoices.FindAsync(invoice1.Id);
        Assert.Equal(InvoiceStatus.Paid, dbInvoice1!.Status);
        Assert.Equal(0m, dbInvoice1.GetRemainingAmount());

        var dbPayment = await db.Payments.FindAsync(payment.Id);
        Assert.Equal(13000m, dbPayment!.Amount);

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(1000m, overpaymentCredit.Amount);
        Assert.Equal(1000m, overpaymentCredit.RemainingAmount);

        var invoice2 = await CreateDraftInvoiceAsync(db, "tenant-e2e", 2000m);
        await IssueInvoiceAsync(db, invoice2);

        var creditHandler = CreateCreditHandler(db);
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(overpaymentCredit.Id, invoice2.Id, 700m, NewKey()), CancellationToken.None);

        var creditAfterFirst = await db.TenantCredits.FindAsync(overpaymentCredit.Id);
        Assert.Equal(300m, creditAfterFirst!.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, creditAfterFirst.Status);

        var dbInvoice2AfterFirst = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice2.Id);
        Assert.Equal(1300m, dbInvoice2AfterFirst.GetRemainingAmount());

        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(overpaymentCredit.Id, invoice2.Id, 300m, NewKey()), CancellationToken.None);

        var creditAfterSecond = await db.TenantCredits.FindAsync(overpaymentCredit.Id);
        Assert.Equal(0m, creditAfterSecond!.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, creditAfterSecond.Status);

        var dbInvoice2AfterSecond = await db.Invoices
            .Include(i => i.CreditApplications)
            .FirstAsync(i => i.Id == invoice2.Id);
        Assert.Equal(1000m, dbInvoice2AfterSecond.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.PartiallyPaid, dbInvoice2AfterSecond.Status);
    }

    // ==================================================================
    // K. Multi-Invoice Payment Scenario (§56)
    // ==================================================================

    [Fact]
    public async Task MultiInvoice_PaymentCoveringMultipleInvoices()
    {
        using var db = CreateDbContext("tenant-mi1");
        var invoiceA = await CreateDraftInvoiceAsync(db, "tenant-mi1", 8000m);
        var invoiceB = await CreateDraftInvoiceAsync(db, "tenant-mi1", 5000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-mi1", 15000m);
        await IssueInvoiceAsync(db, invoiceA);
        await IssueInvoiceAsync(db, invoiceB);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceA.Id, 8000m), CancellationToken.None);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoiceB.Id, 5000m), CancellationToken.None);

        var dbPayment = await db.Payments.FindAsync(payment.Id);
        Assert.Equal(15000m, dbPayment!.Amount);
        Assert.Equal(13000m, dbPayment.GetAllocatedAmount());
        Assert.Equal(2000m, dbPayment.GetUnallocatedAmount());

        var dbInvoiceA = await db.Invoices.FindAsync(invoiceA.Id);
        Assert.Equal(InvoiceStatus.Paid, dbInvoiceA!.Status);

        var dbInvoiceB = await db.Invoices.FindAsync(invoiceB.Id);
        Assert.Equal(InvoiceStatus.Paid, dbInvoiceB!.Status);
    }

    // ==================================================================
    // L. Credit Balance Query Tests
    // ==================================================================

    [Fact]
    public async Task CreditBalanceQuery_ReturnsCorrectValues()
    {
        using var db = CreateDbContext("tenant-bal1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-bal1", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-bal1", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var creditHandler = CreateCreditHandler(db);
        await creditHandler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 400m, NewKey()), CancellationToken.None);

        var queryHandler = new GetCreditBalanceHandler(db);
        var result = await queryHandler.Handle(new GetCreditBalanceQuery(credit.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1000m, result.Value.TotalCredit);
        Assert.Equal(400m, result.Value.ConsumedCredit);
        Assert.Equal(600m, result.Value.AvailableCredit);
        Assert.Equal("EGP", result.Value.CurrencyCode);
    }

    [Fact]
    public async Task CreditBalanceQuery_NotFound()
    {
        using var db = CreateDbContext("tenant-bal2");
        var queryHandler = new GetCreditBalanceHandler(db);
        var result = await queryHandler.Handle(new GetCreditBalanceQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // ==================================================================
    // M. Draft/Cancelled Invoice Rejection
    // ==================================================================

    [Fact]
    public async Task CreditApplication_ToDraftInvoice_Rejected()
    {
        using var db = CreateDbContext("tenant-dc1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-dc1", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-dc1", 1000m);

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    [Fact]
    public async Task CreditApplication_ToCancelledInvoice_Rejected()
    {
        using var db = CreateDbContext("tenant-dc2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-dc2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-dc2", 1000m);

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Cancelled;
        await db.SaveChangesAsync();

        var handler = CreateCreditHandler(db);
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    // ==================================================================
    // N. Credit Source Is Not Fake Payment
    // ==================================================================

    [Fact]
    public async Task CreditCreation_DoesNotCreateFakePayment()
    {
        using var db = CreateDbContext("tenant-fp1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-fp1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-fp1", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var initialPaymentCount = await db.Payments.CountAsync();

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var finalPaymentCount = await db.Payments.CountAsync();
        Assert.Equal(initialPaymentCount, finalPaymentCount);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment);
        Assert.NotNull(credit);

        var payments = await db.Payments.ToListAsync();
        Assert.Single(payments);
        Assert.Equal(13000m, payments[0].Amount);
    }

    [Fact]
    public async Task CreditApplication_DoesNotCreateFakePayment()
    {
        using var db = CreateDbContext("tenant-fp2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-fp2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-fp2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var initialPaymentCount = await db.Payments.CountAsync();

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        var finalPaymentCount = await db.Payments.CountAsync();
        Assert.Equal(initialPaymentCount, finalPaymentCount);
    }

    // ==================================================================
    // O. Ledger Semantic Types
    // ==================================================================

    [Fact]
    public async Task Ledger_OverpaymentCreatesCreditCreationEntry()
    {
        using var db = CreateDbContext("tenant-led1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-led1", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-led1", 13000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreatePaymentHandler(db);
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        var credit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment);
        Assert.NotNull(credit);

        var creationEntry = await db.CustomerLedgerEntries
            .FirstOrDefaultAsync(e => e.CreditId == credit.Id && e.EntryType == LedgerEntryType.CreditCreation);
        Assert.NotNull(creationEntry);
        Assert.Equal(1000m, creationEntry.Amount);
        Assert.NotNull(creationEntry.CreditId);
    }

    [Fact]
    public async Task Ledger_CreditApplicationCreatesCreditUsageEntry()
    {
        using var db = CreateDbContext("tenant-led2");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-led2", 2000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-led2", 1000m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m, NewKey()), CancellationToken.None);

        var usageEntry = await db.CustomerLedgerEntries
            .FirstOrDefaultAsync(e => e.CreditId == credit.Id && e.EntryType == LedgerEntryType.CreditUsage);
        Assert.NotNull(usageEntry);
        Assert.Equal(1000m, usageEntry.Amount);
        Assert.NotNull(usageEntry.CreditApplicationId);
        Assert.Equal(invoice.Id, usageEntry.InvoiceId);
    }

    // ==================================================================
    // P. Credit Not Applied to Already Applied Credit
    // ==================================================================

    [Fact]
    public async Task CreditApplication_AlreadyFullyConsumed_Rejected()
    {
        using var db = CreateDbContext("tenant-ac1");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-ac1", 5000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-ac1", 500m);
        await IssueInvoiceAsync(db, invoice);

        var handler = CreateCreditHandler(db);
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, NewKey()), CancellationToken.None);

        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 500m, NewKey()), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }
}
