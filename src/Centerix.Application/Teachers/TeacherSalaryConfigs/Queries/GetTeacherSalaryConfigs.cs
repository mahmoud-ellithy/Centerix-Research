namespace Centerix.Application.Teachers.TeacherSalaryConfigs.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.TeacherSalaryConfigs;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetTeacherSalaryConfigsQuery(Guid? TeacherId = null) : IRequest<Result<IEnumerable<TeacherSalaryConfigDto>>>;

public class GetTeacherSalaryConfigsHandler(IAppDbContext dbContext) : IRequestHandler<GetTeacherSalaryConfigsQuery, Result<IEnumerable<TeacherSalaryConfigDto>>>
{
    public async Task<Result<IEnumerable<TeacherSalaryConfigDto>>> Handle(
        GetTeacherSalaryConfigsQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.TeacherSalaryConfigs.AsNoTracking();

        if (request.TeacherId.HasValue)
            query = query.Where(c => c.TeacherId == request.TeacherId.Value);

        var items = await query
            .OrderByDescending(c => c.EffectiveFrom)
            .Select(c => new TeacherSalaryConfigDto
            {
                Id = c.Id,
                TeacherId = c.TeacherId,
                GroupId = c.GroupId,
                SalaryType = c.SalaryType.ToString(),
                Value = c.Value,
                EffectiveFrom = c.EffectiveFrom,
                TeacherName = dbContext.Teachers
                    .Where(t => t.Id == c.TeacherId)
                    .Select(t => t.FullName)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return items;
    }
}

public record GetTeacherSalaryConfigByIdQuery(int Id) : IRequest<Result<TeacherSalaryConfigDto>>;

public class GetTeacherSalaryConfigByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetTeacherSalaryConfigByIdQuery, Result<TeacherSalaryConfigDto>>
{
    public async Task<Result<TeacherSalaryConfigDto>> Handle(
        GetTeacherSalaryConfigByIdQuery request,
        CancellationToken cancellationToken)
    {
        var config = await dbContext.TeacherSalaryConfigs
            .AsNoTracking()
            .Where(c => c.Id == request.Id)
            .Select(c => new TeacherSalaryConfigDto
            {
                Id = c.Id,
                TeacherId = c.TeacherId,
                GroupId = c.GroupId,
                SalaryType = c.SalaryType.ToString(),
                Value = c.Value,
                EffectiveFrom = c.EffectiveFrom,
                TeacherName = dbContext.Teachers
                    .Where(t => t.Id == c.TeacherId)
                    .Select(t => t.FullName)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
            return TeacherSalaryConfigErrors.NotFound;

        return config;
    }
}