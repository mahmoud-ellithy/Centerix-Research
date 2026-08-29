namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// PLATFORM-ONLY workflow: (re)activates the tenant's current subscription from Pending or
/// Suspended status. Expired subscriptions are NOT reactivatable without renewal.
/// </summary>
public record ActivateSubscriptionCommand(Guid TenantId) : IRequest<Result<Updated>>;

public class ActivateSubscriptionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<ActivateSubscriptionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(ActivateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == request.TenantId.ToString())
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return Error.NotFound("Subscription.NotFound",
                $"No subscription found for tenant '{request.TenantId}'.");

        var oldValue = AuditPayload.Serialize(new { Status = subscription.Status.ToString() });

        var result = subscription.Activate(now);
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subscription.Activate",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new { Status = subscription.Status.ToString() }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
