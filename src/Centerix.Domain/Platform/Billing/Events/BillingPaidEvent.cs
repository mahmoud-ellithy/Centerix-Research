namespace Centerix.Domain.Platform.Billing.Events;

using Centerix.Domain.Common;

public class BillingPaidEvent(Guid billingId, int planId, decimal amount) : DomainEvent
{
    public Guid BillingId { get; } = billingId;
    public int PlanId { get; } = planId;
    public decimal Amount { get; } = amount;
}
