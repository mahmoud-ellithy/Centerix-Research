namespace Centerix.Application.Students.Branches.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetBranchesQuery : IRequest<IEnumerable<BranchDto>>, ICachedQuery
{
    public string GetCacheKey() => "all-branches";
}

public class GetBranchesHandler(IAppDbContext dbContext) : IRequestHandler<GetBranchesQuery, IEnumerable<BranchDto>>
{
    public async Task<IEnumerable<BranchDto>> Handle(
        GetBranchesQuery request,
        CancellationToken cancellationToken)
    {
        return await dbContext.Branches
            .ProjectToType<BranchDto>()
            .ToListAsync(cancellationToken);
    }
}
