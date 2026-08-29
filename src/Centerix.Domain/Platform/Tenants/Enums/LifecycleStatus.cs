namespace Centerix.Domain.Platform.Tenants.Enums;

/// <summary>
/// Lifecycle of the TENANT itself (organizational state), independent of the commercial
/// subscription lifecycle (<see cref="Centerix.Domain.Platform.Subscriptions.Enums.SubscriptionStatus"/>).
///
/// State machine:
///   PendingApproval → Rejected            (platform admin rejects the application)
///   PendingApproval → Provisioning        (platform admin approves; subscription assigned)
///   Provisioning    → Active              (platform admin completes provisioning)
///   Active          → Suspended           (platform admin suspends)
///   Suspended       → Active              (platform admin reactivates)
///   Active|Suspended|Provisioning → Cancelled
/// </summary>
public enum LifecycleStatus : byte
{
    Provisioning = 0,
    Active = 1,
    Suspended = 2,
    Trial = 3,
    Cancelled = 4,

    /// <summary>Tenant application submitted; awaiting Platform Admin review. Not operational.</summary>
    PendingApproval = 5,

    /// <summary>Application rejected by Platform Admin. Terminal until business decides otherwise.</summary>
    Rejected = 6
}
