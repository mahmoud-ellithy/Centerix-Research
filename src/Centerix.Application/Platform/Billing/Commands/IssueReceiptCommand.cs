namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record IssueReceiptCommand(Guid PaymentId, string ReceiptNumber) : IRequest<Result<Guid>>;

public class IssueReceiptHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<IssueReceiptCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        IssueReceiptCommand request,
        CancellationToken cancellationToken)
    {
        // Transactions are only used on relational providers (SQL Server). The EF InMemory
        // provider does not support transactions, so we skip them there — the business-logic
        // invariants are still validated. On SQL Server, the transaction guarantees atomicity.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        var payment = await dbContext.Payments
            .Include(p => p.Receipts)
            .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        // Payment must be completed before issuing receipt
        if (!payment.IsCompleted)
        {
            return PaymentErrors.CannotReceiptPendingPayment;
        }

        // Receipt amount is always the Payment.Amount — allocations don't change it.
        // 1 Payment -> 1 Receipt; the unique index on PaymentId is the final guarantee.
        // Application guard below gives a clear error on retry.
        if (payment.Receipts.Any())
        {
            return PaymentErrors.ReceiptAlreadyExists;
        }

        var receiptResult = PaymentReceipt.Issue(
            Guid.NewGuid(),
            request.ReceiptNumber,
            request.PaymentId,
            payment.Amount,
            payment.CurrencyCode,
            payment.Method,
            payment.ExternalReference,
            DateTime.UtcNow);

        if (!receiptResult.IsSuccess)
        {
            return receiptResult.Errors!;
        }

        var receipt = receiptResult.Value;

        dbContext.PaymentReceipts.Add(receipt);

        // Stamp the authorized tenant id on newly-added entities. On SQL Server this mirrors
        // what the SaveChanges interceptor does; on InMemory (used by fast unit tests) the
        // interceptor does not run, so we apply it explicitly here to avoid a TenantId
        // required failure.
        dbContext.StampAddedTenantIds(payment.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Receipt.Issue",
            entityType: nameof(PaymentReceipt),
            entityId: receipt.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                receipt.ReceiptNumber,
                receipt.PaymentId,
                receipt.Amount,
                Status = receipt.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return receipt.Id;
    }
}
