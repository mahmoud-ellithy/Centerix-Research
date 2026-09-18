namespace Centerix.Application.Platform.Billing;

public class TenantCreditDto
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public decimal RemainingAmount { get; set; }
    public string SourceType { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string CurrencyCode { get; set; } = "EGP";
    public Guid? SourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
