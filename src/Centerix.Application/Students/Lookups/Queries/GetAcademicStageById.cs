namespace Centerix.Application.Students.Lookups.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetAcademicStageByIdQuery(int Id) : IRequest<Result<AcademicStageDto>>;

public class GetAcademicStageByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetAcademicStageByIdQuery, Result<AcademicStageDto>>
{
    public async Task<Result<AcademicStageDto>> Handle(GetAcademicStageByIdQuery request, CancellationToken cancellationToken)
    {
        var stage = await dbContext.AcademicStages
            .Where(s => s.Id == request.Id)
            .ProjectToType<AcademicStageDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (stage is null)
        {
            return AcademicStageErrors.NotFound;
        }

        return stage;
    }
}
