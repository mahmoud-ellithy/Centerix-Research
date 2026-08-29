namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>PLATFORM-ONLY workflow: rejects a PendingApproval tenant application.</summary>
public record RejectTenantCommand(Guid TenantId, string Reason) : IRequest<Result<Updated>>;

public class RejectTenantValidator : AbstractValidator<RejectTenantCommand>
{
    public RejectTenantValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public class RejectTenantHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ITenantRegistrySync tenantRegistrySync,
    IAuditWriter auditWriter) : IRequestHandler<RejectTenantCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(RejectTenantCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        var rejectResult = tenant.Reject(request.Reason);
        if (!rejectResult.IsSuccess)
            return rejectResult.Errors!;

        await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Tenant.Reject",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            oldValue: AuditPayload.Serialize(new { PreviousStatus = LifecycleStatus.PendingApproval.ToString() }),
            newValue: AuditPayload.Serialize(new { Status = LifecycleStatus.Rejected.ToString(), tenant.SuspendedReason }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
