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
}
