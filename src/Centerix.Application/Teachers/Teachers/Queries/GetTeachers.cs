namespace Centerix.Application.Teachers.Teachers.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Teachers;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetTeachersQuery(Guid? BranchId = null) : IRequest<Result<IEnumerable<TeacherDto>>>;

public class GetTeachersHandler(IAppDbContext dbContext) : IRequestHandler<GetTeachersQuery, Result<IEnumerable<TeacherDto>>>
{
    public async Task<Result<IEnumerable<TeacherDto>>> Handle(
        GetTeachersQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Teachers.AsNoTracking();

        if (request.BranchId.HasValue)
            query = query.Where(t => t.BranchId == request.BranchId.Value);

        var teachers = await query
            .OrderBy(t => t.FullName)
            .Select(t => new TeacherDto
            {
                Id = t.Id,
                UserId = t.UserId,
                BranchId = t.BranchId,
                FullName = t.FullName,
                Phone = t.Phone,
                Qualification = t.Qualification,
                YearsExp = t.YearsExp,
                Status = t.Status.ToString(),
                JoinedAt = t.JoinedAt,
                BranchName = dbContext.Branches
                    .Where(b => b.Id == t.BranchId)
                    .Select(b => b.Name)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return teachers;
    }
}

public record GetTeacherByIdQuery(Guid Id) : IRequest<Result<TeacherDto>>;

public class GetTeacherByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetTeacherByIdQuery, Result<TeacherDto>>
{
    public async Task<Result<TeacherDto>> Handle(
        GetTeacherByIdQuery request,
        CancellationToken cancellationToken)
    {
        var teacher = await dbContext.Teachers
            .AsNoTracking()
            .Where(t => t.Id == request.Id)
            .Select(t => new TeacherDto
            {
                Id = t.Id,
                UserId = t.UserId,
                BranchId = t.BranchId,
                FullName = t.FullName,
                Phone = t.Phone,
                Qualification = t.Qualification,
                YearsExp = t.YearsExp,
                Status = t.Status.ToString(),
                JoinedAt = t.JoinedAt,
                BranchName = dbContext.Branches
                    .Where(b => b.Id == t.BranchId)
                    .Select(b => b.Name)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (teacher is null)
            return TeacherErrors.NotFound;

        return teacher;
    }
}