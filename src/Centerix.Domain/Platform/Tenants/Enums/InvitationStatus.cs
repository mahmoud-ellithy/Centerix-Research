namespace Centerix.Domain.Platform.Tenants.Enums;

/// <summary>
/// Lifecycle state of a tenant invitation.
/// </summary>
public enum InvitationStatus : byte
{
    /// <summary>Invitation has been sent and is awaiting acceptance.</summary>
    Pending = 0,

    /// <summary>Invitation has been accepted and membership created.</summary>
    Accepted = 1,

    /// <summary>Invitation has expired and can no longer be accepted.</summary>
    Expired = 2,

    /// <summary>Invitation has been revoked by an administrator.</summary>
    Revoked = 3
}
