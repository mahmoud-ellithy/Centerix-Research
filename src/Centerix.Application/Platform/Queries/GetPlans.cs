namespace Centerix.Application.Platform.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetPlansQuery : IRequest<IEnumerable<PlanDto>>, ICachedQuery
{
    public string GetCacheKey() => "all-active-plans";
}

public class GetPlansHandler(IAppDbContext dbContext) : IRequestHandler<GetPlansQuery, IEnumerable<PlanDto>>
{
    public async Task<IEnumerable<PlanDto>> Handle(
        GetPlansQuery request,
        CancellationToken cancellationToken)
    {
        return await dbContext.Plans
            .Where(p => p.IsActive)
            .ProjectToType<PlanDto>()
            .ToListAsync(cancellationToken);
    }
}