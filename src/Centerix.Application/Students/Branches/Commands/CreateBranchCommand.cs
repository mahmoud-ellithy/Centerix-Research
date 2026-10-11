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
        // REV-011: same transaction wrapper as CreateStudentHandler (reserve + save commit
        // atomically on relational providers) without changing success/error semantics: the
        // transaction — not manual ReleaseAsync — undoes the reservation on failure paths.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        // Commercial gate: plan/override limit on active subscription (atomic slot reservation
        // inside the ambient transaction above).
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
            return branchResult.Errors!;

        dbContext.Branches.Add(branchResult.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

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