namespace Centerix.Application.Students.Branches.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Students.Branches;

using MediatR;

public record DeleteBranchCommand(Guid Id) : IRequest<Result<Deleted>>;

public class DeleteBranchHandler(
    IAppDbContext dbContext,
    ILimitService limitService,
    IAuditWriter auditWriter) : IRequestHandler<DeleteBranchCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(DeleteBranchCommand request, CancellationToken cancellationToken)
    {
        var branch = await dbContext.Branches.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (branch is null)
        {
            return BranchErrors.NotFound;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            branch.Name,
            branch.Address,
            branch.Phone,
            branch.ManagerId,
            branch.IsActive
        });

        var deleteResult = branch.SoftDelete();
        if (!deleteResult.IsSuccess)
        {
            return deleteResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // REV-011: soft-deleting a branch returns the slot to the active counter (mirrors
        // DeleteTeacherCommand). Only reached after a successful save, so an
        // already-deleted/not-found branch never releases. The entity's own TenantId is
        // used because this handler is tenant-filtered but has no ICurrentTenant.
        await limitService.ReleaseAsync(branch.TenantId, LimitTypeCodes.Branches, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Branch.Delete",
            entityType: nameof(Branch),
            entityId: request.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
