namespace Centerix.Domain.Platform.Subscriptions.Enums;

/// <summary>
/// Commercial lifecycle of one <see cref="TenantPlan"/> subscription row.
/// Independent from the tenant lifecycle (LifecycleStatus).
/// Expiration is evaluated lazily against EffectiveEndsAtUtc — access decisions NEVER depend on
/// a background job having flipped this persisted status.
/// </summary>
public enum SubscriptionStatus : byte
{
    /// <summary>Created but not yet commercially activated by a Platform Admin.</summary>
    Pending = 0,
    Active = 1,
    Expired = 2,
    Cancelled = 3,
    Suspended = 4,

    /// <summary>
    /// System-derived: at least one financial obligation is overdue but the applicable
    /// Grace Period has not yet expired. Cannot be set directly by any administrator.
    /// </summary>
    PastDue = 5,
}
