namespace Centerix.Application.Platform.Billing;

public class TenantCreditDto
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public byte SourceType { get; set; }
    public byte Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
