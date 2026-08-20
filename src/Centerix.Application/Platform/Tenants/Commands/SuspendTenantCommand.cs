namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;

using MediatR;

public record SuspendTenantCommand(Guid Id, string Reason) : IRequest<Result<Updated>>;

public class SuspendTenantHandler(
    IAppDbContext dbContext,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter) : IRequestHandler<SuspendTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(SuspendTenantCommand request, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound("Tenant.NotFound", $"Tenant with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            LifecycleStatus = tenant.LifecycleStatus.ToString(),
            tenant.IsActive
        });

        var suspendResult = tenant.Suspend(request.Reason);

        if (!suspendResult.IsSuccess)
        {
            return suspendResult.Errors!;
        }

        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Suspend",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                LifecycleStatus = tenant.LifecycleStatus.ToString(),
                tenant.SuspendedReason,
                tenant.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
