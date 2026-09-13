namespace Centerix.Application.Platform.Billing.Installments;

using Centerix.Domain.Platform.Billing.Installments;

public class InstallmentDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? InvoiceId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime DueDateUtc { get; set; }
    public DateTime CoveredPeriodStartUtc { get; set; }
    public DateTime CoveredPeriodEndUtc { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public decimal SettledAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public InstallmentStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
