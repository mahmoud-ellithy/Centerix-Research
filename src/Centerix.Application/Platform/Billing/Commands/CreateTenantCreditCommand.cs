namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record CreateTenantCreditCommand(
    decimal Amount,
    byte SourceType,
    Guid? SourceId) : IRequest<Result<Created>>;

public class CreateTenantCreditHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantCreditCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTenantCreditCommand request,
        CancellationToken cancellationToken)
    {
        var creditResult = TenantCredit.Create(
            Guid.NewGuid(),
            request.Amount,
            (CreditSourceType)request.SourceType,
            request.SourceId);

        if (!creditResult.IsSuccess)
        {
            return creditResult.Errors!;
        }

        dbContext.TenantCredits.Add(creditResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantCredit.Create",
            entityType: nameof(TenantCredit),
            entityId: creditResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                creditResult.Value.Amount,
                SourceType = creditResult.Value.SourceType.ToString(),
                creditResult.Value.SourceId,
                Status = creditResult.Value.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}

public record ApplyCreditToInvoiceCommand(
    Guid CreditId,
    Guid InvoiceId,
    decimal Amount) : IRequest<Result<Updated>>;

public class ApplyCreditToInvoiceHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<ApplyCreditToInvoiceCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        ApplyCreditToInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        var credit = await dbContext.TenantCredits.FindAsync([request.CreditId], cancellationToken);
        if (credit is null)
        {
            return TenantCreditErrors.NotFound;
        }

        if (credit.Status != CreditStatus.Available)
        {
            return TenantCreditErrors.NotAvailable;
        }

        if (request.Amount <= 0 || request.Amount > credit.Amount)
        {
            return Error.Validation("TenantCredit.InvalidApplicationAmount",
                "Application amount must be greater than zero and not exceed the available credit.");
        }

        var invoice = await dbContext.Invoices.FindAsync([request.InvoiceId], cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.InvoiceId}' was not found.");
        }

        if (invoice.Status == InvoiceStatus.Draft || invoice.Status == InvoiceStatus.Cancelled)
        {
            return Error.Conflict("Invoice.CannotApplyCreditToDraftOrCancelled",
                "Credit cannot be applied to draft or cancelled invoices.");
        }

        var remainingAmount = invoice.GetRemainingAmount();
        if (request.Amount > remainingAmount)
        {
            return Error.Validation("TenantCredit.ExceedsInvoiceRemaining",
                "Credit amount exceeds the invoice outstanding balance.");
        }

        var applyResult = credit.ApplyToInvoice(request.InvoiceId);
        if (!applyResult.IsSuccess)
        {
            return applyResult.Errors!;
        }

        dbContext.TenantCredits.Update(credit);

        // Create a CreditUsage ledger entry to record the credit application
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == credit.TenantId)
            .SumAsync(e => e.EntryType == LedgerEntryType.InvoiceCharge ? e.Amount : -e.Amount, cancellationToken);

        var ledgerEntry = CustomerLedgerEntry.CreateCreditCreation(
            Guid.NewGuid(),
            credit.Id,
            request.Amount,
            "EGP",
            previousBalance,
            DateTime.UtcNow,
            $"Credit applied to invoice: {request.Amount} EGP");

        if (ledgerEntry.IsSuccess)
        {
            dbContext.CustomerLedgerEntries.Add(ledgerEntry.Value);
        }

        dbContext.StampAddedTenantIds(credit.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantCredit.ApplyToInvoice",
            entityType: nameof(TenantCredit),
            entityId: credit.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                CreditId = credit.Id,
                InvoiceId = request.InvoiceId,
                request.Amount,
                CreditStatus = credit.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
