namespace Centerix.Application.Teachers.Teachers;

public class TeacherDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid BranchId { get; set; }
    public string FullName { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public string? Qualification { get; set; }
    public byte? YearsExp { get; set; }
    public string Status { get; set; } = default!;
    public DateOnly JoinedAt { get; set; }
    public string? BranchName { get; set; }
}