namespace Centerix.Application.Students.Attendance.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Enums;

using MediatR;

public record CreateAttendanceLogCommand(
    Guid StudentId,
    Guid GroupId,
    DateOnly SessionDate,
    AttendanceStatus Status,
    TimeOnly? CheckInTime,
    bool IsOffline,
    DateTime? SyncedAt) : IRequest<Result<Created>>;

public class CreateAttendanceLogHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter)
    : IRequestHandler<CreateAttendanceLogCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAttendanceLogCommand request,
        CancellationToken cancellationToken)
    {
        var result = AttendanceLog.Create(
            request.StudentId,
            request.GroupId,
            request.SessionDate,
            request.Status,
            request.CheckInTime,
            request.IsOffline,
            request.SyncedAt);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.AttendanceLogs.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AttendanceLog.Create",
            entityType: nameof(AttendanceLog),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.StudentId,
                result.Value.GroupId,
                result.Value.SessionDate,
                Status = result.Value.Status.ToString(),
                result.Value.CheckInTime,
                result.Value.IsOffline,
                result.Value.SyncedAt
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
