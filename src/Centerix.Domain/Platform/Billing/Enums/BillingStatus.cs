namespace Centerix.Domain.Platform.Billing.Enums;

public enum BillingStatus : byte
{
    Unpaid = 0,
    Paid = 1,
    Refunded = 2,
    Failed = 3,
}
