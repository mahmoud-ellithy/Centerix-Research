namespace Centerix.Application.Students;

public class AcademicYearDto
{
    public int Id { get; set; }
    public int StageId { get; set; }
    public string YearCode { get; set; } = default!;
    public string YearName { get; set; } = default!;
    public string? StageName { get; set; }
}
