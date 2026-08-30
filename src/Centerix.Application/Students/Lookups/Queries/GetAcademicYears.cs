namespace Centerix.Application.Students.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;
using MediatR;

public record GetAcademicYearsQuery : IRequest<IEnumerable<AcademicYearDto>>, ICachedQuery
{
    public string GetCacheKey() => "all-academic-years";
}

public class GetAcademicYearsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetAcademicYearsQuery, IEnumerable<AcademicYearDto>>
{
    public async Task<IEnumerable<AcademicYearDto>> Handle(
        GetAcademicYearsQuery request,
        CancellationToken cancellationToken)
    {
        return await dbContext.AcademicYears
            .Include(y => y.Stage)
            .Select(y => new AcademicYearDto
            {
                Id = y.Id,
                StageId = y.StageId,
                YearCode = y.YearCode,
                YearName = y.YearName,
                StageName = y.Stage.DisplayName
            })
            .ToListAsync(cancellationToken);
    }
}
