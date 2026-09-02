namespace Centerix.Application.Teachers.SalaryPayments;

public class SalaryPaymentDto
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public byte PeriodMonth { get; set; }
    public short PeriodYear { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string Status { get; set; } = default!;
    public DateTime? PaidAt { get; set; }
    public string? TeacherName { get; set; }
}