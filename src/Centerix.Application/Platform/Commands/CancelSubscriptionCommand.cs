namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>PLATFORM-ONLY workflow: cancels the tenant's current subscription (history preserved).</summary>
public record CancelSubscriptionCommand(Guid TenantId, string? Reason = null) : IRequest<Result<Updated>>;

public class CancelSubscriptionValidator : AbstractValidator<CancelSubscriptionCommand>
{
    public CancelSubscriptionValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public class CancelSubscriptionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<CancelSubscriptionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(CancelSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

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

        var result = subscription.Cancel(now);
        if (!result.IsSuccess)
            return result.Errors!;

        // Commercial access ends now: clear the operational mirror so the tenant guard stops
        // admitting requests on the strength of a stale expiry date. History remains auditable.
        tenant.SetValidUpTo(now);
        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subscription.Cancel",
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
