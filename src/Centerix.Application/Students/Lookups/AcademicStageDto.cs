namespace Centerix.Application.Students.Lookups;

public class AcademicStageDto
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public byte SortOrder { get; set; }
}
