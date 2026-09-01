namespace Centerix.Application.Teachers.TeacherRatings;

public class TeacherRatingDto
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid StudentId { get; set; }
    public Guid? GroupId { get; set; }
    public byte Stars { get; set; }
    public string? Comment { get; set; }
    public byte PeriodMonth { get; set; }
    public short PeriodYear { get; set; }
    public string? TeacherName { get; set; }
    public string? StudentName { get; set; }
}