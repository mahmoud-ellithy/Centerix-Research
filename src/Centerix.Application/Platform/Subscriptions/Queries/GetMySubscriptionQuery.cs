namespace Centerix.Application.Platform.Subscriptions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// TENANT-SCOPED read of the caller's own current subscription snapshot. Uses the verified
/// tenant context (never a client-supplied id), so cross-tenant reads are impossible.
/// </summary>
public record GetMySubscriptionQuery() : IRequest<Result<MySubscriptionDto>>;

public record MySubscriptionDto(
    Guid? SubscriptionId,
    int? PlanId,
    decimal? SnapshotPrice,
    string? SnapshotCurrency,
    int DurationMonths,
    int BonusMonths,
    DateTime StartsAtUtc,
    DateTime BaseEndsAtUtc,
    DateTime EffectiveEndsAtUtc,
    string Status,
    bool IsActiveAsOfNow);

public class GetMySubscriptionHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    ISubscriptionStateService subscriptionState)
    : IRequestHandler<GetMySubscriptionQuery, Result<MySubscriptionDto>>
{
    public async Task<Result<MySubscriptionDto>> Handle(GetMySubscriptionQuery request, CancellationToken cancellationToken)
    {
        if (!currentTenant.IsResolved || string.IsNullOrEmpty(currentTenant.TenantId))
            return Error.Forbidden("Subscription.TenantNotResolved", "No authorized tenant context.");

        var state = await subscriptionState.GetCurrentAsync(currentTenant.TenantId, cancellationToken);

        if (state.SubscriptionId is null)
            return Error.NotFound("Subscription.NotFound", "The tenant has no subscription.");

        var subscription = await dbContext.TenantPlans
            .AsNoTracking()
            .SingleAsync(tp => tp.Id == state.SubscriptionId.Value, cancellationToken);

        return new MySubscriptionDto(
            subscription.Id,
            subscription.PlanId,
            subscription.SnapshotPrice,
            subscription.SnapshotCurrency,
            subscription.DurationMonths,
            subscription.BonusMonths,
            subscription.StartsAtUtc,
            subscription.BaseEndsAtUtc,
            subscription.EffectiveEndsAtUtc,
            subscription.Status.ToString(),
            state.IsActiveAsOfNow);
    }
}
