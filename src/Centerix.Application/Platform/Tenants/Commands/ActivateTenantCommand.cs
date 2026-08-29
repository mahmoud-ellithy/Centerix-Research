namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// PLATFORM-ONLY workflow: completes provisioning of an APPROVED tenant (Provisioning → Active).
/// This is the only sanctioned path from approval to operational status.
/// </summary>
public record ActivateTenantCommand(Guid TenantId) : IRequest<Result<Updated>>;

public class ActivateTenantValidator : AbstractValidator<ActivateTenantCommand>
{
    public ActivateTenantValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
    }
}

public class ActivateTenantHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter) : IRequestHandler<ActivateTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(ActivateTenantCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        var activateResult = tenant.Activate();
        if (!activateResult.IsSuccess)
            return activateResult.Errors!;

        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Activate",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            newValue: AuditPayload.Serialize(new { Status = tenant.LifecycleStatus.ToString() }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
