namespace Centerix.Application.Students.Branches.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;

using MediatR;

public record UpdateBranchCommand(
    Guid Id,
    string Name,
    string? Address,
    string? Phone,
    Guid? ManagerId) : IRequest<Result<Updated>>;

public class UpdateBranchHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateBranchCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdateBranchCommand request, CancellationToken cancellationToken)
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

        var updateResult = branch.Update(
            request.Name,
            request.Address,
            request.Phone,
            request.ManagerId);

        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Branch.Update",
            entityType: nameof(Branch),
            entityId: branch.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                branch.Name,
                branch.Address,
                branch.Phone,
                branch.ManagerId,
                branch.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
