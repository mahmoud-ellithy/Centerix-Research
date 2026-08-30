namespace Centerix.Application.Students.Branches.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Students.Branches;

using MediatR;

public record CreateBranchCommand(
    string Name,
    string? Address,
    string? Phone,
    Guid? ManagerId,
    bool IsActive = true) : IRequest<Result<Created>>;

/// <summary>
/// Reference wiring of the reusable Phase 2 enforcement pipeline: the FEATURE gate lives on the
/// endpoint ([RequireFeature]) while the LIMIT gate runs here — permission alone is not enough
/// when the tenant's subscription quota is exhausted. Mirrors the pattern used by
/// <see cref="Centerix.Application.Students.Students.Commands.CreateStudentHandler"/>.
/// </summary>
public class CreateBranchHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    ILimitService limitService,
    IAuditWriter auditWriter) : IRequestHandler<CreateBranchCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateBranchCommand request,
        CancellationToken cancellationToken)
    {
        // Commercial gate: plan/override limit on active subscription (atomic slot reservation).
        var limitResult = await limitService.ReserveAsync(
            currentTenant.TenantId!, LimitTypeCodes.Branches, cancellationToken);
        if (!limitResult.IsSuccess)
            return limitResult.Errors!;

        var branchResult = Branch.Create(
            Guid.NewGuid(),
            request.Name,
            request.Address,
            request.Phone,
            request.ManagerId,
            request.IsActive);

        if (!branchResult.IsSuccess)
        {
            await limitService.ReleaseAsync(currentTenant.TenantId!, LimitTypeCodes.Branches, cancellationToken);
            return branchResult.Errors!;
        }

        dbContext.Branches.Add(branchResult.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Rollback releases the reserved counter slot along with the uncommitted insert.
            await limitService.ReleaseAsync(currentTenant.TenantId!, LimitTypeCodes.Branches, cancellationToken);
            throw;
        }

        await auditWriter.WriteAsync(
            action: "Branch.Create",
            entityType: nameof(Branch),
            entityId: branchResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                branchResult.Value.Name,
                branchResult.Value.Address,
                branchResult.Value.Phone,
                branchResult.Value.ManagerId,
                branchResult.Value.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}