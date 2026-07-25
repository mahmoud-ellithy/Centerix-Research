namespace Centerix.Domain.Students.Attendance;

using System.ComponentModel.DataAnnotations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;

public class AttendanceLog : AuditableEntity<long>
{
    public Guid StudentId { get; private set; }
    public Guid GroupId { get; private set; }

    public DateOnly SessionDate { get; private set; }
    public AttendanceStatus Status { get; private set; }
    public TimeOnly? CheckInTime { get; private set; }
    public bool IsOffline { get; private set; }
    public DateTime? SyncedAt { get; private set; }

    [Timestamp]
    public byte[]? RowVersion { get; internal set; }

    public Student Student { get; private set; } = default!;

    private AttendanceLog() { }

    private AttendanceLog(
        long id,
        Guid studentId,
        Guid groupId,
        DateOnly sessionDate,
        AttendanceStatus status,
        TimeOnly? checkInTime,
        bool isOffline,
        DateTime? syncedAt)
        : base(id)
    {
        StudentId = studentId;
        GroupId = groupId;
        SessionDate = sessionDate;
        Status = status;
        CheckInTime = checkInTime;
        IsOffline = isOffline;
        SyncedAt = syncedAt;
    }

    public static Result<AttendanceLog> Create(
        Guid studentId,
        Guid groupId,
        DateOnly sessionDate,
        AttendanceStatus status,
        TimeOnly? checkInTime = null,
        bool isOffline = false,
        DateTime? syncedAt = null)
    {
        var error = Validate(studentId, groupId, sessionDate, status);

        if (error is not null)
            return error;

        return new AttendanceLog(
            0,
            studentId,
            groupId,
            sessionDate,
            status,
            checkInTime,
            isOffline,
            syncedAt);
    }

    public void MarkSynced(DateTime syncedAt)
    {
        IsOffline = false;
        SyncedAt = syncedAt;
    }

    private static Error? Validate(
        Guid studentId,
        Guid groupId,
        DateOnly sessionDate,
        AttendanceStatus status)
    {
        if (studentId == Guid.Empty)
            return AttendanceLogErrors.StudentIdRequired;

        if (groupId == Guid.Empty)
            return AttendanceLogErrors.GroupIdRequired;

        if (sessionDate == default)
            return AttendanceLogErrors.SessionDateRequired;

        if (sessionDate > DateOnly.FromDateTime(DateTime.UtcNow))
            return AttendanceLogErrors.SessionDateInFuture;

        if (!Enum.IsDefined(status))
            return AttendanceLogErrors.InvalidStatus;

        return null;
    }
}
