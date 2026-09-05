namespace Centerix.Application.Platform.Billing;

public class PaymentDto
{
    public Guid Id { get; set; }
    public string PaymentNumber { get; set; } = default!;
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public byte Method { get; set; }
    public byte Status { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ExternalReference { get; set; }
    public decimal AllocatedAmount { get; set; }
    public decimal UnallocatedAmount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PaymentAllocationDto
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal AllocatedAmount { get; set; }
    public byte Status { get; set; }
    public DateTime AllocatedAtUtc { get; set; }
}

public class ReceiptDto
{
    public Guid Id { get; set; }
    public string ReceiptNumber { get; set; } = default!;
    public Guid PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public byte Method { get; set; }
    public byte Status { get; set; }
    public DateTime IssuedAtUtc { get; set; }
}

public class CustomerLedgerEntryDto
{
    public Guid Id { get; set; }
    public byte EntryType { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public decimal RunningBalance { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public string Description { get; set; } = default!;
    public DateTime RecordedAtUtc { get; set; }
}
