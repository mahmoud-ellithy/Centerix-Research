namespace Centerix.Application.Platform.Billing;

public class InvoiceLineDto
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public byte SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public string Description { get; set; } = default!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int? ProratedDays { get; set; }
    public decimal LineTotal { get; set; }
}
