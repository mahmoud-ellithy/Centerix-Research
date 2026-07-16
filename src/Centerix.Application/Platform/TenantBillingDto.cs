namespace Centerix.Application.Platform;

public class TenantBillingDto
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public int PlanId { get; set; }
    public decimal AmountEGP { get; set; }
    public string Method { get; set; } = default!;
    public byte Status { get; set; }
    public string? StatusLabel { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? InvoiceRef { get; set; }
    public DateTime CreatedAt { get; set; }
    public PlanDto? Plan { get; set; }
}
