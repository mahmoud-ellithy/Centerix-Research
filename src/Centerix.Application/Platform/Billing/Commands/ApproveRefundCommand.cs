namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Refunds;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to approve a refund request.
/// </summary>
public record ApproveRefundCommand(
    Guid RefundId) : IRequest<Result<Updated>>;

public class ApproveRefundHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<ApproveRefundCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        ApproveRefundCommand request,
        CancellationToken cancellationToken)
    {
        var refund = await dbContext.Refunds
            .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

        if (refund is null)
        {
            return RefundErrors.NotFound;
        }

        var previousStatus = refund.Status;

        var approveResult = refund.Approve(currentUserService.UserId!, DateTime.UtcNow);
        if (!approveResult.IsSuccess)
        {
            return approveResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Refund.Approve",
            entityType: nameof(Refund),
            entityId: refund.Id.ToString(),
            newValue: AuditPayload.Serialize(new { Status = refund.Status.ToString() }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
