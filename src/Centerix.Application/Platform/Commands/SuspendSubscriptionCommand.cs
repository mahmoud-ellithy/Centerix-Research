namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>PLATFORM-ONLY workflow: suspends the tenant's active subscription (e.g. non-payment).</summary>
public record SuspendSubscriptionCommand(Guid TenantId, string? Reason = null) : IRequest<Result<Updated>>;

public class SuspendSubscriptionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<SuspendSubscriptionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(SuspendSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == request.TenantId.ToString()
                         && tp.Status == SubscriptionStatus.Active)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return Error.NotFound("Subscription.ActiveNotFound",
                $"No ACTIVE subscription found for tenant '{request.TenantId}'.");

        var oldValue = AuditPayload.Serialize(new { Status = subscription.Status.ToString() });

        var result = subscription.Suspend();
        if (!result.IsSuccess)
            return result.Errors!;

        // Suspension blocks access immediately via IsActiveAsOf checks; ValidUpTo intentionally
        // unchanged — the paid term is preserved and resumes on reactivation.
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subscription.Suspend",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                Status = subscription.Status.ToString(),
                request.Reason
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
