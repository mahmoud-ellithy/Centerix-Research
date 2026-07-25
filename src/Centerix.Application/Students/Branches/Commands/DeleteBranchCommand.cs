namespace Centerix.Application.Students.Branches.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;

using MediatR;

public record DeleteBranchCommand(Guid Id) : IRequest<Result<Deleted>>;

public class DeleteBranchHandler(
    IAppDbContext dbContext,
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

        await auditWriter.WriteAsync(
            action: "Branch.Delete",
            entityType: nameof(Branch),
            entityId: request.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
