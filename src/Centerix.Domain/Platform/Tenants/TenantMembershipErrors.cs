namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common.Results;

public static class TenantMembershipErrors
{
    public static Error UserIdRequired =>
        Error.Validation("TenantMembership.UserId_Required", "User ID is required");

    public static Error TenantIdRequired =>
        Error.Validation("TenantMembership.TenantId_Required", "Tenant ID is required");

    public static Error AlreadyMember =>
        Error.Conflict("TenantMembership.AlreadyMember", "User is already an active member of this tenant");

    public static Error NotMember =>
        Error.NotFound("TenantMembership.NotMember", "User is not a member of this tenant");

    public static Error InvitationNotFound =>
        Error.NotFound("TenantInvitation.NotFound", "Invitation not found");

    public static Error InvitationExpired =>
        Error.Conflict("TenantInvitation.Expired", "This invitation has expired");

    public static Error InvitationAlreadyAccepted =>
        Error.Conflict("TenantInvitation.AlreadyAccepted", "This invitation has already been accepted");

    public static Error InvitationRevoked =>
        Error.Conflict("TenantInvitation.Revoked", "This invitation has been revoked");

    public static Error DuplicateActiveInvitation =>
        Error.Conflict("TenantInvitation.Duplicate", "An active invitation already exists for this email in this tenant");

    /// <summary>
    /// CFG-001: invitation e-mail delivery failed. The invitation was persisted first and a
    /// compensation (revoke so it no longer remains Pending) was attempted; either way the
    /// request reports FAILURE and never claims success. No Outbox/DATA-001 involved.
    /// </summary>
    public static Error InvitationDeliveryFailed =>
        Error.Failure("TenantInvitation.DeliveryFailed", "The invitation could not be delivered by email. No invitation was left pending.");

    public static Error UnauthorizedToInvite =>
        Error.Forbidden("TenantInvitation.Unauthorized", "You do not have permission to create invitations for this tenant");

    public static Error InvalidToken =>
        Error.Unauthorized("TenantInvitation.InvalidToken", "Invalid invitation token");

    public static Error RoleNotFound =>
        Error.Validation("TenantInvitation.RoleNotFound", "The specified role does not exist");

    /// <summary>
    /// SEC-001: a platform-authority role (PlatformAdmin) can never be bound to a tenant
    /// membership or invitation. Such a row would be readable by the tenant permission resolver
    /// and would publish every permission that role holds - including platform-scoped codes -
    /// as a tenant-derived grant.
    /// </summary>
    public static Error PlatformRoleNotAllowed =>
        Error.Forbidden(
            "TenantMembership.PlatformRoleNotAllowed",
            "Platform roles cannot be granted a tenant membership or invitation");

    /// <summary>
    /// F3: only the canonical tenant roles (TenantAdmin, TenantUser) may be bound to a tenant
    /// membership or invitation. A custom Identity role - even one backed by real RolePermission
    /// grants - must not become a membership RoleName, because membership role grants flow
    /// through the tenant permission resolver exactly like the production-seeded matrices.
    /// </summary>
    public static Error RoleNotAllowed =>
        Error.Forbidden(
            "TenantMembership.RoleNotAllowed",
            "Only the canonical tenant roles (TenantAdmin, TenantUser) can be granted a tenant membership or invitation");
}
