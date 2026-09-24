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
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<ApproveRefundCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        ApproveRefundCommand request,
        CancellationToken cancellationToken)
    {
        // Task 19 — PLATFORM authorization boundary.
        // Refund approval is a platform-side commercial authority decision, not a
        // tenant-side operation. Controller-level HasPermission is necessary but not
        // sufficient: a tenant admin holding an over-broad tenant permission must not
        // be able to approve a refund by reaching the handler through a different route.
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

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
