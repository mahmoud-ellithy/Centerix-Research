namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// PLATFORM-ONLY workflow: renews the tenant's current subscription by appending calendar
/// months (+ optional bonus months). Renewal policy (documented decision): the new period
/// anchors at max(EffectiveEndsAtUtc, UtcNow) — early renewal preserves remaining paid time,
/// late renewal starts fresh. The tenant's ValidUpTo is kept consistent.
/// </summary>
public record RenewSubscriptionCommand(
    Guid TenantId,
    int AdditionalMonths,
    int AdditionalBonusMonths = 0) : IRequest<Result<Updated>>;

public class RenewSubscriptionValidator : AbstractValidator<RenewSubscriptionCommand>
{
    public RenewSubscriptionValidator()
    {
        RuleFor(x => x.AdditionalMonths).GreaterThan(0);
        RuleFor(x => x.AdditionalBonusMonths).GreaterThanOrEqualTo(0);
    }
}

public class RenewSubscriptionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<RenewSubscriptionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(RenewSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == request.TenantId.ToString()
                         && tp.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(tp => tp.StartsAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return Error.NotFound("Subscription.NotFound",
                $"No renewable subscription found for tenant '{request.TenantId}'.");

        var oldValue = AuditPayload.Serialize(new
        {
            subscription.DurationMonths,
            subscription.BonusMonths,
            subscription.EffectiveEndsAtUtc,
            Status = subscription.Status.ToString()
        });

        var renewResult = subscription.Renew(
            request.AdditionalMonths,
            request.AdditionalBonusMonths,
            timeProvider.GetUtcNow().UtcDateTime);

        if (!renewResult.IsSuccess)
            return renewResult.Errors!;

        // Keep the operational mirror of the commercial end date consistent.
        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is not null && subscription.Status == SubscriptionStatus.Active)
        {
            tenant.SetValidUpTo(subscription.EffectiveEndsAtUtc);
            await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Subscription.Renew",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                subscription.DurationMonths,
                subscription.BonusMonths,
                subscription.EffectiveEndsAtUtc,
                Status = subscription.Status.ToString(),
                request.AdditionalMonths,
                request.AdditionalBonusMonths
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
