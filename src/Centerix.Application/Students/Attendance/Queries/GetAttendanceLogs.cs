namespace Centerix.Application.Students.Attendance.Queries;

using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;
using MediatR;

public record GetAttendanceLogsQuery : IRequest<Result<IEnumerable<AttendanceLogDto>>>, ICachedQuery
{
    public string GetCacheKey() => "all-attendance-logs";
}

public class GetAttendanceLogsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetAttendanceLogsQuery, Result<IEnumerable<AttendanceLogDto>>>
{
    public async Task<Result<IEnumerable<AttendanceLogDto>>> Handle(
        GetAttendanceLogsQuery request,
        CancellationToken cancellationToken)
    {
        var logs = await dbContext.AttendanceLogs
            .AsNoTracking()
            .Include(a => a.Student)
            .Select(a => new AttendanceLogDto
            {
                Id = a.Id,
                StudentId = a.StudentId,
                GroupId = a.GroupId,
                SessionDate = a.SessionDate,
                Status = a.Status.ToString(),
                CheckInTime = a.CheckInTime,
                IsOffline = a.IsOffline,
                SyncedAt = a.SyncedAt,
                StudentName = a.Student.FullNameAr,
            })
            .ToListAsync(cancellationToken);

        return logs;
    }
}
