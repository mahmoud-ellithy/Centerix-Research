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

    public static Error UnauthorizedToInvite =>
        Error.Forbidden("TenantInvitation.Unauthorized", "You do not have permission to create invitations for this tenant");

    public static Error InvalidToken =>
        Error.Unauthorized("TenantInvitation.InvalidToken", "Invalid invitation token");

    public static Error RoleNotFound =>
        Error.Validation("TenantInvitation.RoleNotFound", "The specified role does not exist");
}
