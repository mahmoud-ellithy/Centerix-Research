namespace Centerix.Application.Students.Branches.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;

using MediatR;

public record CreateBranchCommand(
    string Name,
    string? Address,
    string? Phone,
    Guid? ManagerId,
    bool IsActive = true) : IRequest<Result<Created>>;

public class CreateBranchHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateBranchCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateBranchCommand request,
        CancellationToken cancellationToken)
    {
        var branchResult = Branch.Create(
            Guid.NewGuid(),
            request.Name,
            request.Address,
            request.Phone,
            request.ManagerId,
            request.IsActive);

        if (!branchResult.IsSuccess)
        {
            return branchResult.Errors!;
        }

        dbContext.Branches.Add(branchResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

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
