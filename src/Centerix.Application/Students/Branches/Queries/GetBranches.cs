namespace Centerix.Application.Students.Branches.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetBranchesQuery : IRequest<Result<IEnumerable<BranchDto>>>, ICachedQuery
{
    public string GetCacheKey() => "all-branches";
}

public class GetBranchesHandler(IAppDbContext dbContext) : IRequestHandler<GetBranchesQuery, Result<IEnumerable<BranchDto>>>
{
    public async Task<Result<IEnumerable<BranchDto>>> Handle(
        GetBranchesQuery request,
        CancellationToken cancellationToken)
    {
        var branches = await dbContext.Branches
            .ProjectToType<BranchDto>()
            .ToListAsync(cancellationToken);

        return branches;
    }
}
