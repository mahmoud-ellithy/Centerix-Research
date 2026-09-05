namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record CompletePaymentCommand(Guid PaymentId, string? ExternalReference = null) : IRequest<Result<Updated>>;

public class CompletePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CompletePaymentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        CompletePaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Transactions are only used on relational providers (SQL Server). The EF InMemory
        // provider does not support transactions, so we skip them there — the business-logic
        // invariants are still validated. On SQL Server, the transaction guarantees atomicity.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        var payment = await dbContext.Payments
            .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            Status = payment.Status.ToString(),
            payment.ExternalReference
        });

        var completeResult = payment.Complete(DateTime.UtcNow, request.ExternalReference);
        if (!completeResult.IsSuccess)
        {
            return completeResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Payment.Complete",
            entityType: nameof(Payment),
            entityId: payment.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                Status = payment.Status.ToString(),
                payment.CompletedAtUtc,
                payment.ExternalReference
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
