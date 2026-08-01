namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;

using MediatR;

public record ReactivateTenantCommand(Guid Id) : IRequest<Result<Updated>>;

public class ReactivateTenantHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<ReactivateTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(ReactivateTenantCommand request, CancellationToken cancellationToken)
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

        var activateResult = tenant.Activate();

        if (!activateResult.IsSuccess)
        {
            return activateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Reactivate",
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
