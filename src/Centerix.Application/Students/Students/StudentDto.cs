namespace Centerix.Application.Students.Students;

public class StudentDto
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public int StageId { get; set; }
    public int YearId { get; set; }
    public string FullNameAr { get; set; } = default!;
    public string? FullNameEn { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Phone { get; set; }
    public string QRCode { get; set; } = default!;
    public string? DiscountType { get; set; }
    public decimal? DiscountValue { get; set; }
    public string Status { get; set; } = default!;
    public DateOnly EnrolledAt { get; set; }
    public string? BranchName { get; set; }
    public string? StageName { get; set; }
    public string? YearName { get; set; }
}
