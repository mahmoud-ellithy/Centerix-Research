namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using MediatR;

public record DeletePlanCommand(int Id) : IRequest<Result<Deleted>>;

public class DeletePlanHandler(IAppDbContext dbContext) : IRequestHandler<DeletePlanCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(DeletePlanCommand request, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{request.Id}' was not found.");
        }

        dbContext.Plans.Remove(plan);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Deleted;
    }
}