namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;

using MediatR;

public record CompletePaymentCommand(Guid PaymentId, string? ExternalReference = null) : IRequest<Result<Updated>>;

public class CompletePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CompletePaymentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        CompletePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments.FindAsync([request.PaymentId], cancellationToken: cancellationToken);
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
