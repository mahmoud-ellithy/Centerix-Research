namespace Centerix.Domain.Students.Enums;

/// <summary>
/// Per-session attendance outcome.
/// </summary>
public enum AttendanceStatus : byte
{
    Present = 0,
    Absent = 1,
    Late = 2,
    Excused = 3,
    Left = 4,
}
