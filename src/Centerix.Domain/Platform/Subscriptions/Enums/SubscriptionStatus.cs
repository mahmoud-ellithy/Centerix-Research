namespace Centerix.Domain.Platform.Subscriptions.Enums;

public enum SubscriptionStatus : byte
{
    Active = 1,
    Expired = 2,
    Cancelled = 3,
    Suspended = 4,
}
