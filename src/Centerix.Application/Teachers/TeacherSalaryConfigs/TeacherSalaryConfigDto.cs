namespace Centerix.Application.Teachers.TeacherSalaryConfigs;

public class TeacherSalaryConfigDto
{
    public int Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid? GroupId { get; set; }
    public string SalaryType { get; set; } = default!;
    public decimal Value { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string? TeacherName { get; set; }
}