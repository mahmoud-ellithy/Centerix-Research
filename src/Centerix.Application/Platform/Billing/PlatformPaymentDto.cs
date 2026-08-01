namespace Centerix.Application.Platform.Billing;

public class PlatformPaymentDto
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string? GatewayRef { get; set; }
    public DateTime PaidAt { get; set; }
    public byte Status { get; set; }
}
