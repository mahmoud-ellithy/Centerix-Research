namespace Centerix.Application.Students.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using Microsoft.EntityFrameworkCore;
using MediatR;

public record GetAcademicYearByIdQuery(int Id) : IRequest<Result<AcademicYearDto>>;

public class GetAcademicYearByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetAcademicYearByIdQuery, Result<AcademicYearDto>>
{
    public async Task<Result<AcademicYearDto>> Handle(
        GetAcademicYearByIdQuery request,
        CancellationToken cancellationToken)
    {
        var year = await dbContext.AcademicYears
            .Include(y => y.Stage)
            .Where(y => y.Id == request.Id)
            .Select(y => new AcademicYearDto
            {
                Id = y.Id,
                StageId = y.StageId,
                YearCode = y.YearCode,
                YearName = y.YearName,
                StageName = y.Stage.DisplayName
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (year is null)
        {
            return AcademicYearErrors.NotFound;
        }

        return year;
    }
}
