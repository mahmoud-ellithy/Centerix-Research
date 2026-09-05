namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;

using MediatR;

public record IssueReceiptCommand(Guid PaymentId, string ReceiptNumber) : IRequest<Result<Guid>>;

public class IssueReceiptHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<IssueReceiptCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        IssueReceiptCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments.FindAsync([request.PaymentId], cancellationToken: cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        // Payment must be completed before issuing receipt
        if (!payment.IsCompleted)
        {
            return PaymentErrors.CannotReceiptPendingPayment;
        }

        // Check if receipt already exists for this payment
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
        await dbContext.SaveChangesAsync(cancellationToken);

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
