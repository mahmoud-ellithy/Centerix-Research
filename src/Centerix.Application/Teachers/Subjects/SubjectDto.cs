namespace Centerix.Application.Teachers.Subjects;

public class SubjectDto
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int StageId { get; set; }
    public string? StageName { get; set; }
}