namespace Centerix.Domain.Platform.Billing.Invoicing.Enums;

public enum PlatformPaymentStatus : byte
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Refunded = 3
}
