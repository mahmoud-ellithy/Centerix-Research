namespace Centerix.Application.Students.Attendance.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Attendance;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;

using MediatR;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter)
    : IRequestHandler<CreateAttendanceLogCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAttendanceLogCommand request,
        CancellationToken cancellationToken)
    {
        // REV-010: the student must belong to the caller's tenant. The Students set is
        // tenant-filtered AND soft-delete-filtered, so a foreign-tenant id and a
        // soft-deleted id both miss and surface as a clean 404 (same pattern as
        // CreateSalaryPaymentHandler) instead of a relational FK violation 500.
        var studentExists = await dbContext.Students
            .AsNoTracking()
            .AnyAsync(s => s.Id == request.StudentId, cancellationToken);
        if (!studentExists)
            return StudentErrors.NotFound;

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

        // REV-010 adjacent fix: stamp the authorized tenant id explicitly, mirroring
        // CreateSalaryPaymentHandler and every other tenant-aware handler. Production
        // stamping otherwise depends solely on TenantInterceptor, so any host that (like
        // the test host) does not run the interceptor fails this save with a missing-
        // TenantId DbUpdateException instead of creating the log.
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        // REV-010: a concurrent same-day duplicate violates UX_AttendanceLogs_Student_Session;
        // map it to a 409 instead of a 500 (same SqlException-number pattern as the
        // billing duplicate guards). The index definition itself is untouched.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateSessionException(ex))
        {
            return AttendanceLogErrors.Duplicate;
        }

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

    /// <summary>
    /// REV-010: maps a unique violation on UX_AttendanceLogs_Student_Session to a 409.
    /// SQL Server error 2601 = duplicate key in unique index, 2627 = UNIQUE KEY
    /// constraint violation; the index name pins the mapping to the session constraint.
    /// </summary>
    private static bool IsDuplicateSessionException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("UX_AttendanceLogs_Student_Session", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
