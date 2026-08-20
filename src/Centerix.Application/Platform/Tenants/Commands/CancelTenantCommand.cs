namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;

using MediatR;

public record CancelTenantCommand(Guid Id) : IRequest<Result<Updated>>;

public class CancelTenantHandler(
    IAppDbContext dbContext,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter) : IRequestHandler<CancelTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(CancelTenantCommand request, CancellationToken cancellationToken)
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

        var cancelResult = tenant.Cancel();

        if (!cancelResult.IsSuccess)
        {
            return cancelResult.Errors!;
        }

        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Cancel",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                LifecycleStatus = tenant.LifecycleStatus.ToString(),
                tenant.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
