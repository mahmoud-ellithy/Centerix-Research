namespace Centerix.Application.Platform.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetPlanByIdQuery(int Id) : IRequest<Result<PlanDto>>;

public class GetPlanByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetPlanByIdQuery, Result<PlanDto>>
{
    public async Task<Result<PlanDto>> Handle(GetPlanByIdQuery request, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans
            .Where(p => p.Id == request.Id)
            .ProjectToType<PlanDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{request.Id}' was not found.");
        }

        return plan;
    }
}