namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a refund request.
/// </summary>
public record CreateRefundCommand(
    string RefundNumber,
    Guid ContractId,
    Guid? SubscriptionId,
    Guid? InvoiceId,
    decimal Amount,
    string CurrencyCode,
    string Reason) : IRequest<Result<Guid>>;

public class CreateRefundHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<CreateRefundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateRefundCommand request,
        CancellationToken cancellationToken)
    {
        // Verify the contract exists and belongs to the current tenant
        var contract = await dbContext.Contracts
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
        {
            return RefundErrors.ContractNotFound;
        }

        // Create the refund
        var refundResult = Refund.Create(
            Guid.NewGuid(),
            request.RefundNumber,
            request.ContractId,
            request.SubscriptionId,
            request.InvoiceId,
            request.Amount,
            request.CurrencyCode,
            request.Reason,
            currentUserService.UserId!,
            DateTime.UtcNow);

        if (!refundResult.IsSuccess)
        {
            return refundResult.Errors!;
        }

        var refund = refundResult.Value;

        dbContext.Refunds.Add(refund);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Refund.Create",
            entityType: nameof(Refund),
            entityId: refund.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                refund.RefundNumber,
                refund.ContractId,
                refund.SubscriptionId,
                refund.InvoiceId,
                refund.Amount,
                refund.CurrencyCode,
                refund.Reason,
                Status = refund.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return refund.Id;
    }
}
