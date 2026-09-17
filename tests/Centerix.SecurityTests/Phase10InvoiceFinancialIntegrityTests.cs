namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
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
/// Task 10: Invoice Financial Integrity & Immutability tests.
/// Tests invoice immutability, payment integrity, overpayment handling,
/// credit application, and concurrency.
/// </summary>
public class Phase10InvoiceFinancialIntegrityTests
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

    private static async Task<Invoice> CreateDraftInvoiceAsync(AppDbContext db, string tenantId, decimal totalAmount = 12000m)
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            $"INV-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
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

    private static async Task<Payment> CreateCompletedPaymentAsync(AppDbContext db, string tenantId, decimal amount = 13000m)
    {
        var payment = Payment.Create(
            Guid.NewGuid(),
            $"PAY-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            amount,
            "EGP",
            PaymentMethod.Cash).Value;

        payment.Complete(DateTime.UtcNow);
        db.Payments.Add(payment);
        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();
        return payment;
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

    // ==================================================================
    // Invoice Immutability — AddLine / RemoveLine after issuance
    // ==================================================================

    [Fact]
    public async Task DraftInvoice_AddLine_Succeeds()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var handler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DraftInvoice_RemoveLine_Succeeds()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        var line = await db.InvoiceLines.FirstAsync(l => l.InvoiceId == invoice.Id);

        var removeHandler = new RemoveInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await removeHandler.Handle(new RemoveInvoiceLineCommand(invoice.Id, line.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task IssuedInvoice_AddLine_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotAddLineNonDraft");
    }

    [Fact]
    public async Task SentInvoice_AddLine_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Sent;
        await db.SaveChangesAsync();

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotAddLineNonDraft");
    }

    [Fact]
    public async Task PaidInvoice_AddLine_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Paid;
        await db.SaveChangesAsync();

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotAddLineNonDraft");
    }

    [Fact]
    public async Task IssuedInvoice_RemoveLine_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var line = await db.InvoiceLines.FirstAsync(l => l.InvoiceId == invoice.Id);
        var removeHandler = new RemoveInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await removeHandler.Handle(new RemoveInvoiceLineCommand(invoice.Id, line.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotRemoveLineNonDraft");
    }

    [Fact]
    public async Task PartiallyPaidInvoice_RemoveLine_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.PartiallyPaid;
        await db.SaveChangesAsync();

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        var result = await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotAddLineNonDraft");
    }

    // ==================================================================
    // Draft Behavior — editable while Draft
    // ==================================================================

    [Fact]
    public async Task DraftInvoice_MultipleLines_CanBeAddedAndRemoved()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var addHandler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());

        await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id, (byte)InvoiceLineSourceType.Subscription, null, "Line 1", 1, 500m, null), CancellationToken.None);
        await addHandler.Handle(new AddInvoiceLineCommand(
            invoice.Id, (byte)InvoiceLineSourceType.AddOn, null, "Line 2", 2, 250m, null), CancellationToken.None);

        Assert.Equal(2, await db.InvoiceLines.CountAsync(l => l.InvoiceId == invoice.Id));

        var line = await db.InvoiceLines.FirstAsync(l => l.InvoiceId == invoice.Id);
        var removeHandler = new RemoveInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        await removeHandler.Handle(new RemoveInvoiceLineCommand(invoice.Id, line.Id), CancellationToken.None);

        Assert.Equal(1, await db.InvoiceLines.CountAsync(l => l.InvoiceId == invoice.Id));
    }

    // ==================================================================
    // Payment Integrity — Remaining = Total - Paid
    // ==================================================================

    [Fact]
    public async Task Invoice_PartialPayment_RemainingIsCorrect()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 4000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        var result = await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 4000m), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(8000m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);
    }

    [Fact]
    public async Task Invoice_FullPayment_BecomesPaid()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 12000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        var result = await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);
    }

    [Fact]
    public async Task Invoice_TwoPartialPayments_BecomesPaid()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment1 = await CreateCompletedPaymentAsync(db, "tenant-imm", 4000m);
        var payment2 = await CreateCompletedPaymentAsync(db, "tenant-imm", 8000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());

        await handler.Handle(new AllocatePaymentCommand(payment1.Id, invoice.Id, 4000m), CancellationToken.None);
        var result2 = await handler.Handle(new AllocatePaymentCommand(payment2.Id, invoice.Id, 8000m), CancellationToken.None);

        Assert.True(result2.IsSuccess);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);
    }

    // ==================================================================
    // Overpayment — Excess becomes TenantCredit
    // ==================================================================

    [Fact]
    public async Task Overpayment_CreatesCreditForExcess()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 13000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        var result = await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 13000m), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);

        var paymentEntity = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(paymentEntity);
        Assert.Equal(13000m, paymentEntity.Amount);

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(1000m, overpaymentCredit.Amount);
        Assert.Equal(CreditStatus.Available, overpaymentCredit.Status);
    }

    [Fact]
    public async Task Overpayment_InvoiceRemainsPaid_CreditIsAvailable()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 15000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 15000m), CancellationToken.None);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);
        Assert.Equal(0m, updatedInvoice.GetRemainingAmount());

        var credits = await db.TenantCredits
            .Where(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id)
            .ToListAsync();
        Assert.Single(credits);
        Assert.Equal(3000m, credits[0].Amount);
        Assert.Equal(CreditStatus.Available, credits[0].Status);
    }

    [Fact]
    public async Task ExactPayment_NoCreditCreated()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 12000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 12000m), CancellationToken.None);

        var credits = await db.TenantCredits
            .Where(c => c.SourceType == CreditSourceType.Overpayment)
            .ToListAsync();
        Assert.Empty(credits);
    }

    // ==================================================================
    // Credit Application
    // ==================================================================

    [Fact]
    public async Task ApplyCredit_ToIssuedInvoice_Succeeds()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 1000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(credit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.Applied, updatedCredit.Status);
        Assert.Equal(0m, updatedCredit.RemainingAmount);
    }

    [Fact]
    public async Task ApplyCredit_ExceedsAvailable_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 500m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.InvalidApplicationAmount");
    }

    [Fact]
    public async Task ApplyCredit_ExceedsInvoiceRemaining_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 50000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 50000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.ExceedsInvoiceRemaining");
    }

    [Fact]
    public async Task ApplyCredit_ToDraftInvoice_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 1000m);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    [Fact]
    public async Task ApplyCredit_AlreadyApplied_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 1000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantCredit.NotAvailable");
    }

    [Fact]
    public async Task ApplyCredit_ToCancelledInvoice_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 1000m);

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Cancelled;
        await db.SaveChangesAsync();

        var handler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(credit.Id, invoice.Id, 1000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotApplyCreditToDraftOrCancelled");
    }

    // ==================================================================
    // Invoice Cancellation — Only Draft can be cancelled
    // ==================================================================

    [Fact]
    public async Task DraftInvoice_Cancel_Succeeds()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var handler = new CancelInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new CancelInvoiceCommand(invoice.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(InvoiceStatus.Cancelled, updatedInvoice.Status);
    }

    [Fact]
    public async Task IssuedInvoice_Cancel_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var cancelHandler = new CancelInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await cancelHandler.Handle(new CancelInvoiceCommand(invoice.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotCancelNonDraft");
    }

    [Fact]
    public async Task PaidInvoice_Cancel_IsRejected()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);

        db.Entry(invoice).Property(i => i.Status).CurrentValue = InvoiceStatus.Paid;
        await db.SaveChangesAsync();

        var cancelHandler = new CancelInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await cancelHandler.Handle(new CancelInvoiceCommand(invoice.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.CannotCancelNonDraft");
    }

    // ==================================================================
    // Invoice Immutability — Scalar properties cannot change after creation
    // ==================================================================

    [Fact]
    public async Task Invoice_SnapshotFields_AreImmutable()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var reloaded = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(reloaded);

        Assert.Equal(invoice.InvoiceNumber, reloaded.InvoiceNumber);
        Assert.Equal(invoice.PeriodStart, reloaded.PeriodStart);
        Assert.Equal(invoice.PeriodEnd, reloaded.PeriodEnd);
        Assert.Equal(invoice.Subtotal, reloaded.Subtotal);
        Assert.Equal(invoice.DiscountAmount, reloaded.DiscountAmount);
        Assert.Equal(invoice.TaxAmount, reloaded.TaxAmount);
        Assert.Equal(invoice.TotalAmount, reloaded.TotalAmount);
    }

    // ==================================================================
    // TenantCredit — RowVersion exists
    // ==================================================================

    [Fact]
    public async Task TenantCredit_HasRowVersion()
    {
        using var db = CreateDbContext("tenant-imm");
        var credit = await CreateAvailableCreditAsync(db, "tenant-imm", 1000m);

        Assert.NotNull(credit.RowVersion);
    }

    // ==================================================================
    // Payment integrity — PaidAmount derived from allocations
    // ==================================================================

    [Fact]
    public async Task Invoice_PaidAmount_DerivedFromAllocations()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 5000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice.Id, DateTime.UtcNow, null), CancellationToken.None);

        var handler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        await handler.Handle(new AllocatePaymentCommand(payment.Id, invoice.Id, 5000m), CancellationToken.None);

        var updatedInvoice = await db.Invoices
            .Include(i => i.PaymentAllocations)
            .FirstAsync(i => i.Id == invoice.Id);

        Assert.Equal(5000m, updatedInvoice.GetPaidAmount());
        Assert.Equal(7000m, updatedInvoice.GetRemainingAmount());
    }

    // ==================================================================
    // Cross-tenant — Tenant A cannot modify Tenant B's Invoice
    // ==================================================================

    [Fact]
    public async Task CrossTenant_AddLine_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");

        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B");

        var handler = new AddInvoiceLineHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new AddInvoiceLineCommand(
            invoiceB.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Cross-tenant line",
            1,
            500m,
            null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.NotFound");
    }

    [Fact]
    public async Task CrossTenant_RemoveLine_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");

        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B");

        var addHandler = new AddInvoiceLineHandler(dbB, Substitute.For<IAuditWriter>());
        await addHandler.Handle(new AddInvoiceLineCommand(
            invoiceB.Id, (byte)InvoiceLineSourceType.Subscription, null, "Line", 1, 500m, null), CancellationToken.None);

        var line = await dbB.InvoiceLines.FirstAsync(l => l.InvoiceId == invoiceB.Id);

        var removeHandler = new RemoveInvoiceLineHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await removeHandler.Handle(new RemoveInvoiceLineCommand(invoiceB.Id, line.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.NotFound");
    }

    [Fact]
    public async Task CrossTenant_Issue_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");

        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B");

        var handler = new IssueInvoiceHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new IssueInvoiceCommand(invoiceB.Id, DateTime.UtcNow, null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.NotFound");
    }

    [Fact]
    public async Task CrossTenant_Cancel_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");

        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B");

        var handler = new CancelInvoiceHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new CancelInvoiceCommand(invoiceB.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.NotFound");
    }

    [Fact]
    public async Task CrossTenant_ApplyCredit_IsRejected()
    {
        using var dbA = CreateDbContext("tenant-A");
        using var dbB = CreateDbContext("tenant-B");

        var invoiceB = await CreateDraftInvoiceAsync(dbB, "tenant-B");
        var creditA = await CreateAvailableCreditAsync(dbA, "tenant-A", 1000m);

        var handler = new ApplyCreditToInvoiceHandler(dbA, Substitute.For<IAuditWriter>());
        var result = await handler.Handle(new ApplyCreditToInvoiceCommand(creditA.Id, invoiceB.Id, 1000m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.NotFound");
    }

    // ==================================================================
    // InvoiceLine — total is deterministic
    // ==================================================================

    [Fact]
    public async Task AddInvoiceLine_LineTotalIsDeterministic()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice = await CreateDraftInvoiceAsync(db, "tenant-imm");

        var handler = new AddInvoiceLineHandler(db, Substitute.For<IAuditWriter>());
        await handler.Handle(new AddInvoiceLineCommand(
            invoice.Id,
            (byte)InvoiceLineSourceType.Subscription,
            null,
            "Test line",
            3,
            250m,
            null), CancellationToken.None);

        var line = await db.InvoiceLines.FirstAsync(l => l.InvoiceId == invoice.Id);
        Assert.Equal(750m, line.LineTotal);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(250m, line.UnitPrice);
    }

    // ==================================================================
    // Overpayment credit — Can be applied to another invoice
    // ==================================================================

    [Fact]
    public async Task OverpaymentCredit_CanBeAppliedToAnotherInvoice()
    {
        using var db = CreateDbContext("tenant-imm");
        var invoice1 = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var invoice2 = await CreateDraftInvoiceAsync(db, "tenant-imm", 12000m);
        var payment = await CreateCompletedPaymentAsync(db, "tenant-imm", 13000m);

        var issueHandler = new IssueInvoiceHandler(db, Substitute.For<IAuditWriter>());
        await issueHandler.Handle(new IssueInvoiceCommand(invoice1.Id, DateTime.UtcNow, null), CancellationToken.None);
        await issueHandler.Handle(new IssueInvoiceCommand(invoice2.Id, DateTime.UtcNow, null), CancellationToken.None);

        var allocHandler = new AllocatePaymentHandler(
            db, Substitute.For<IAuditWriter>(), Substitute.For<ISubscriptionReconciliationService>());
        await allocHandler.Handle(new AllocatePaymentCommand(payment.Id, invoice1.Id, 13000m), CancellationToken.None);

        var overpaymentCredit = await db.TenantCredits
            .FirstOrDefaultAsync(c => c.SourceType == CreditSourceType.Overpayment && c.SourceId == payment.Id);
        Assert.NotNull(overpaymentCredit);
        Assert.Equal(1000m, overpaymentCredit.Amount);

        var creditHandler = new ApplyCreditToInvoiceHandler(db, Substitute.For<IAuditWriter>());
        var result = await creditHandler.Handle(new ApplyCreditToInvoiceCommand(
            overpaymentCredit.Id, invoice2.Id, 1000m), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var updatedCredit = await db.TenantCredits.FindAsync(overpaymentCredit.Id);
        Assert.NotNull(updatedCredit);
        Assert.Equal(CreditStatus.Applied, updatedCredit.Status);
    }
}
