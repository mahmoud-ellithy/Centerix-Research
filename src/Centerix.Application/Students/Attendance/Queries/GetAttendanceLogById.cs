namespace Centerix.Application.Students.Attendance.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;
using MediatR;

public record GetAttendanceLogByIdQuery(long Id) : IRequest<Result<AttendanceLogDto>>;

public class GetAttendanceLogByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetAttendanceLogByIdQuery, Result<AttendanceLogDto>>
{
    public async Task<Result<AttendanceLogDto>> Handle(
        GetAttendanceLogByIdQuery request,
        CancellationToken cancellationToken)
    {
        var log = await dbContext.AttendanceLogs
            .AsNoTracking()
            .Where(a => a.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);

        if (log is null)
        {
            return Error.NotFound("AttendanceLog.NotFound", $"Attendance log with id '{request.Id}' was not found.");
        }

        return log;
    }
}
