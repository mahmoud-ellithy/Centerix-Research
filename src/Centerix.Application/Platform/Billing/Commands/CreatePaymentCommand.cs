namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;

public record CreatePaymentCommand(
    string PaymentNumber,
    decimal Amount,
    string CurrencyCode,
    PaymentMethod Method,
    string? ExternalReference = null,
    string? Notes = null) : IRequest<Result<Guid>>;

public class CreatePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreatePaymentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var paymentResult = Payment.Create(
            Guid.NewGuid(),
            request.PaymentNumber,
            request.Amount,
            request.CurrencyCode,
            request.Method,
            request.ExternalReference,
            request.Notes);

        if (!paymentResult.IsSuccess)
        {
            return paymentResult.Errors!;
        }

        var payment = paymentResult.Value;

        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Payment.Create",
            entityType: nameof(Payment),
            entityId: payment.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                payment.PaymentNumber,
                payment.Amount,
                payment.CurrencyCode,
                Method = payment.Method.ToString(),
                Status = payment.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return payment.Id;
    }
}
