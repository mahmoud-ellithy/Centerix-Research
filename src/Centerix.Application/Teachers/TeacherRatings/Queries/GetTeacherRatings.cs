namespace Centerix.Application.Teachers.TeacherRatings.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.TeacherRatings;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetTeacherRatingsQuery(Guid? TeacherId = null, Guid? StudentId = null) : IRequest<Result<IEnumerable<TeacherRatingDto>>>;

public class GetTeacherRatingsHandler(IAppDbContext dbContext) : IRequestHandler<GetTeacherRatingsQuery, Result<IEnumerable<TeacherRatingDto>>>
{
    public async Task<Result<IEnumerable<TeacherRatingDto>>> Handle(
        GetTeacherRatingsQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.TeacherRatings.AsNoTracking();

        if (request.TeacherId.HasValue)
            query = query.Where(r => r.TeacherId == request.TeacherId.Value);

        if (request.StudentId.HasValue)
            query = query.Where(r => r.StudentId == request.StudentId.Value);

        var items = await query
            .OrderByDescending(r => r.PeriodYear)
            .ThenByDescending(r => r.PeriodMonth)
            .Select(r => new TeacherRatingDto
            {
                Id = r.Id,
                TeacherId = r.TeacherId,
                StudentId = r.StudentId,
                GroupId = r.GroupId,
                Stars = r.Stars,
                Comment = r.Comment,
                PeriodMonth = r.PeriodMonth,
                PeriodYear = r.PeriodYear,
                TeacherName = dbContext.Teachers
                    .Where(t => t.Id == r.TeacherId)
                    .Select(t => t.FullName)
                    .FirstOrDefault(),
                StudentName = dbContext.Students
                    .Where(s => s.Id == r.StudentId)
                    .Select(s => s.FullNameAr)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return items;
    }
}