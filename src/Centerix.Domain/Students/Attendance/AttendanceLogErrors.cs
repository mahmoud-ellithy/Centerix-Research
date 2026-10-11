namespace Centerix.Domain.Students.Attendance;

using Centerix.Domain.Common.Results;

public static class AttendanceLogErrors
{
    public static Error StudentIdRequired =>
        Error.Validation("Attendance.StudentId_Required", "Student is required");

    public static Error GroupIdRequired =>
        Error.Validation("Attendance.GroupId_Required", "Group is required");

    public static Error SessionDateRequired =>
        Error.Validation("Attendance.SessionDate_Required", "Session date is required");

    public static Error SessionDateInFuture =>
        Error.Validation("Attendance.SessionDate_Future", "Session date cannot be in the future");

    public static Error InvalidStatus =>
        Error.Validation("Attendance.InvalidStatus", "Invalid attendance status");

    public static Error NotFound =>
        Error.NotFound("Attendance.NotFound", "Attendance log was not found");

    /// <summary>
    /// REV-010: an attendance log already exists for this student on this session date
    /// (UX_AttendanceLogs_Student_Session). Returned instead of a 500 on concurrent
    /// same-day duplicates.
    /// </summary>
    public static Error Duplicate =>
        Error.Conflict("Attendance.Duplicate", "An attendance log already exists for this student on this date");
}
