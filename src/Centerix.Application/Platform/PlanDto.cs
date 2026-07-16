namespace Centerix.Application.Platform;

public class PlanDto
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public decimal MonthlyPrice { get; set; }
    public int MaxStudents { get; set; }
    public int MaxUsers { get; set; }
    public int MaxBranches { get; set; }
    public int MaxTeachers { get; set; }
    public int StorageGB { get; set; }
    public int SMSQuota { get; set; }
    public bool IsActive { get; set; }
}
