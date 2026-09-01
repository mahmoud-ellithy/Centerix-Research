namespace Centerix.Application.Teachers.Subjects.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Subjects;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetSubjectsQuery(int? StageId = null) : IRequest<Result<IEnumerable<SubjectDto>>>;

public class GetSubjectsHandler(IAppDbContext dbContext) : IRequestHandler<GetSubjectsQuery, Result<IEnumerable<SubjectDto>>>
{
    public async Task<Result<IEnumerable<SubjectDto>>> Handle(
        GetSubjectsQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Subjects.AsNoTracking();

        if (request.StageId.HasValue)
            query = query.Where(s => s.StageId == request.StageId.Value);

        var subjects = await query
            .OrderBy(s => s.StageId)
            .ThenBy(s => s.Name)
            .Select(s => new SubjectDto
            {
                Id = s.Id,
                Name = s.Name,
                StageId = s.StageId,
                StageName = dbContext.AcademicStages
                    .Where(st => st.Id == s.StageId)
                    .Select(st => st.DisplayName)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return subjects;
    }
}

public record GetSubjectByIdQuery(int Id) : IRequest<Result<SubjectDto>>;

public class GetSubjectByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetSubjectByIdQuery, Result<SubjectDto>>
{
    public async Task<Result<SubjectDto>> Handle(
        GetSubjectByIdQuery request,
        CancellationToken cancellationToken)
    {
        var subject = await dbContext.Subjects
            .AsNoTracking()
            .Where(s => s.Id == request.Id)
            .Select(s => new SubjectDto
            {
                Id = s.Id,
                Name = s.Name,
                StageId = s.StageId,
                StageName = dbContext.AcademicStages
                    .Where(st => st.Id == s.StageId)
                    .Select(st => st.DisplayName)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (subject is null)
            return SubjectErrors.NotFound;

        return subject;
    }
}