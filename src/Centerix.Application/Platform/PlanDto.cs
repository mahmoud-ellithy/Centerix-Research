namespace Centerix.Application.Platform;

public class PlanDto
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Description { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public int DurationMonths { get; set; }
    public int BonusMonths { get; set; }
    public decimal MonthlyPrice { get; set; }
    public int MaxStudents { get; set; }
    public int MaxUsers { get; set; }
    public int MaxBranches { get; set; }
    public int MaxTeachers { get; set; }
    public int StorageGB { get; set; }
    public int SMSQuota { get; set; }
    public bool IsActive { get; set; }
}
