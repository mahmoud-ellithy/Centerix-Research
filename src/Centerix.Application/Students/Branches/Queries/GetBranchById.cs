namespace Centerix.Application.Students.Branches.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetBranchByIdQuery(Guid Id) : IRequest<Result<BranchDto>>;

public class GetBranchByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetBranchByIdQuery, Result<BranchDto>>
{
    public async Task<Result<BranchDto>> Handle(GetBranchByIdQuery request, CancellationToken cancellationToken)
    {
        var branch = await dbContext.Branches
            .Where(b => b.Id == request.Id)
            .ProjectToType<BranchDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (branch is null)
        {
            return BranchErrors.NotFound;
        }

        return branch;
    }
}
