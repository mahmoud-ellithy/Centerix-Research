namespace Centerix.Application.Students.Attendance;

public class AttendanceLogDto
{
    public long Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid GroupId { get; set; }
    public DateOnly SessionDate { get; set; }
    public string Status { get; set; } = default!;
    public TimeOnly? CheckInTime { get; set; }
    public bool IsOffline { get; set; }
    public DateTime? SyncedAt { get; set; }
    public string? StudentName { get; set; }
}
