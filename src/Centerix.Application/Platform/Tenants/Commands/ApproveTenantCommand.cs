namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLATFORM-ONLY workflow: approves a PendingApproval tenant AND creates+activates its first
/// commercial subscription in one atomic unit (tenant row + registry projection + subscription
/// snapshot). Tenant-side users can never reach this handler (IPlatformAdminGuard).
/// </summary>
public record ApproveTenantCommand(
    Guid TenantId,
    int PlanId,
    bool AutoRenew = false) : IRequest<Result<Created>>;

public class ApproveTenantValidator : AbstractValidator<ApproveTenantCommand>
{
    public ApproveTenantValidator()
    {
        RuleFor(x => x.PlanId).GreaterThan(0);
    }
}

public class ApproveTenantHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ISubscriptionFactory subscriptionFactory,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<ApproveTenantHandler> logger) : IRequestHandler<ApproveTenantCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(ApproveTenantCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        var approveResult = tenant.Approve();
        if (!approveResult.IsSuccess)
            return approveResult.Errors!;

        // Defense in depth against duplicate non-terminal subscriptions (the DB filtered unique
        // index is the hard guarantee; this keeps application state coherent on anomaly).
        var staleSubscriptions = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenant.Id.ToString())
            .ToListAsync(cancellationToken);

        foreach (var stale in staleSubscriptions.Where(s =>
                     s.Status == SubscriptionStatus.Active ||
                     s.Status == SubscriptionStatus.Suspended))
        {
            logger.LogWarning(
                "Anomalous non-terminal subscription {SubscriptionId} found while approving tenant {TenantId}; cancelling",
                stale.Id, tenant.Id);
            stale.Cancel(timeProvider.GetUtcNow().UtcDateTime);
        }

        var subscriptionResult = await subscriptionFactory.CreateActivatedAsync(
            tenant.Id.ToString(),
            request.PlanId,
            timeProvider.GetUtcNow().UtcDateTime,
            request.AutoRenew,
            cancellationToken);

        if (!subscriptionResult.IsSuccess)
            return subscriptionResult.Errors!;

        var subscription = subscriptionResult.Value;
        dbContext.TenantPlans.Add(subscription);

        // Keep ValidUpTo consistent with the subscription (single commercial source of truth).
        tenant.SetValidUpTo(subscription.EffectiveEndsAtUtc);

        // Atomic dual-context save: Platform.Tenants changes + registry projection.
        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Approve",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            oldValue: AuditPayload.Serialize(new { PreviousStatus = LifecycleStatus.PendingApproval.ToString() }),
            newValue: AuditPayload.Serialize(new
            {
                Status = LifecycleStatus.Provisioning.ToString(),
                PlanId = request.PlanId,
                SubscriptionId = subscription.Id,
                subscription.SnapshotPrice,
                subscription.SnapshotCurrency,
                subscription.DurationMonths,
                subscription.BonusMonths,
                subscription.BaseEndsAtUtc,
                subscription.EffectiveEndsAtUtc
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
