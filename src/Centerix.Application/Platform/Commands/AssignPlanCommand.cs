namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLATFORM-ONLY workflow: assigns (or re-assigns) a commercial plan to a tenant by cancelling
/// any non-terminal subscription and creating+activating a NEW snapshotted subscription.
/// Atomic across subscription rows, tenant ValidUpTo and the registry projection.
/// </summary>
public record AssignPlanCommand(
    Guid TenantId,
    int PlanId,
    bool AutoRenew = false) : IRequest<Result<Created>>;

public class AssignPlanValidator : AbstractValidator<AssignPlanCommand>
{
    public AssignPlanValidator()
    {
        RuleFor(x => x.PlanId).GreaterThan(0);
    }
}

public class AssignPlanHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ISubscriptionFactory subscriptionFactory,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AssignPlanHandler> logger) : IRequestHandler<AssignPlanCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(AssignPlanCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        // Commercial eligibility: only approved tenants hold subscriptions.
        if (tenant.LifecycleStatus is Domain.Platform.Tenants.Enums.LifecycleStatus.PendingApproval
                or Domain.Platform.Tenants.Enums.LifecycleStatus.Rejected
                or Domain.Platform.Tenants.Enums.LifecycleStatus.Cancelled)
            return Error.Conflict("Subscription.TenantNotEligible",
                $"Tenant in status '{tenant.LifecycleStatus}' cannot receive a subscription.");

        // Supersede any non-terminal subscription (history preserved; DB unique index enforces).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var superseded = new List<Guid>();
        var existingSubscriptions = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .Where(tp => tp.TenantId == tenant.Id.ToString())
            .ToListAsync(cancellationToken);

        foreach (var stale in existingSubscriptions.Where(s =>
                     s.Status == Domain.Platform.Subscriptions.Enums.SubscriptionStatus.Active ||
                     s.Status == Domain.Platform.Subscriptions.Enums.SubscriptionStatus.Suspended))
        {
            var cancelResult = stale.Cancel(now);
            if (!cancelResult.IsSuccess)
                return cancelResult.Errors!;
            superseded.Add(stale.Id);
        }

        var subscriptionResult = await subscriptionFactory.CreateActivatedAsync(
            tenant.Id.ToString(),
            request.PlanId,
            now,
            request.AutoRenew,
            cancellationToken);

        if (!subscriptionResult.IsSuccess)
            return subscriptionResult.Errors!;

        var subscription = subscriptionResult.Value;
        dbContext.TenantPlans.Add(subscription);
        tenant.SetValidUpTo(subscription.EffectiveEndsAtUtc);

        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        logger.LogInformation(
            "Plan {PlanId} assigned to tenant {TenantId}; superseded [{Superseded}]; active until {End}",
            request.PlanId, tenant.Id, string.Join(",", superseded), subscription.EffectiveEndsAtUtc);

        await auditWriter.WriteAsync(
            action: "Subscription.Assign",
            entityType: nameof(Domain.Platform.Subscriptions.TenantPlan),
            entityId: subscription.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                tenant.Id,
                PlanId = request.PlanId,
                subscription.SnapshotPrice,
                subscription.SnapshotCurrency,
                subscription.DurationMonths,
                subscription.BonusMonths,
                subscription.EffectiveEndsAtUtc,
                Superseded = superseded
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
