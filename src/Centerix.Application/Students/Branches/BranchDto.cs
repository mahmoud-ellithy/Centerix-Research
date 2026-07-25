namespace Centerix.Application.Students.Branches;

public class BranchDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public Guid? ManagerId { get; set; }
    public bool IsActive { get; set; }
}
