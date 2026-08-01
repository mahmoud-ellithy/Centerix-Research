namespace Centerix.Application.Platform.Billing;

public class InvoiceDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = default!;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public byte Status { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
