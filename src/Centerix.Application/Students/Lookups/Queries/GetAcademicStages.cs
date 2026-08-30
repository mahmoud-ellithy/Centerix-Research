namespace Centerix.Application.Students.Lookups.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetAcademicStagesQuery : IRequest<IEnumerable<AcademicStageDto>>, ICachedQuery
{
    public string GetCacheKey() => "all-academic-stages";
}

public class GetAcademicStagesHandler(IAppDbContext dbContext) : IRequestHandler<GetAcademicStagesQuery, IEnumerable<AcademicStageDto>>
{
    public async Task<IEnumerable<AcademicStageDto>> Handle(
        GetAcademicStagesQuery request,
        CancellationToken cancellationToken)
    {
        return await dbContext.AcademicStages
            .OrderBy(s => s.SortOrder)
            .ProjectToType<AcademicStageDto>()
            .ToListAsync(cancellationToken);
    }
}
