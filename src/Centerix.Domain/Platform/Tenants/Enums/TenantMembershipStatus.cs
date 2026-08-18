namespace Centerix.Domain.Platform.Tenants.Enums;

/// <summary>
/// Lifecycle state of a user's membership within a tenant.
/// A user may hold memberships in multiple tenants, each with its own status.
/// </summary>
public enum TenantMembershipStatus : byte
{
    /// <summary>Membership is active and the user may operate within the tenant.</summary>
    Active = 0,

    /// <summary>Membership has been offered but not yet accepted/activated.</summary>
    Invited = 1,

    /// <summary>Membership is temporarily disabled (e.g. policy, investigation) but retained.</summary>
    Suspended = 2,

    /// <summary>Membership has been permanently revoked. Treated as non-authoritative for access.</summary>
    Revoked = 3
}
