namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

public record AcceptInvitationCommand(string Token) : IRequest<Result<Created>>;

public class AcceptInvitationHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUser,
    IIdentityService identityService) : IRequestHandler<AcceptInvitationCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        AcceptInvitationCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return TenantMembershipErrors.InvalidToken;

        var tokenHash = HashToken(request.Token);

        // 1. Find the invitation by token hash
        var invitation = await dbContext.TenantInvitations
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

        if (invitation is null)
            return TenantMembershipErrors.InvalidToken;

        // 2. Validate invitation status
        if (invitation.Status == InvitationStatus.Accepted)
            return TenantMembershipErrors.InvitationAlreadyAccepted;

        if (invitation.Status == InvitationStatus.Revoked)
            return TenantMembershipErrors.InvitationRevoked;

        if (invitation.Status == InvitationStatus.Expired)
            return TenantMembershipErrors.InvitationExpired;

        // 3. Check expiration
        if (invitation.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            invitation.MarkExpired();
            await dbContext.SaveChangesAsync(cancellationToken);
            return TenantMembershipErrors.InvitationExpired;
        }

        // 4. Find the existing IdentityUser by email
        var userId = await identityService.FindUserIdByEmailAsync(invitation.NormalizedEmail);
        if (userId is null)
            return Error.NotFound("Invitation.UserNotFound",
                "No account found for this email. Please register first.");

        // 5. Verify the invitation was sent to the current user
        if (userId != currentUser.UserId)
            return Error.Forbidden("Invitation.UserMismatch",
                "This invitation was sent to a different email address.");

        // 6. Check if already an active member
        var existingMembership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == invitation.TenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (existingMembership is not null)
            return TenantMembershipErrors.AlreadyMember;

        // 7. Create TenantMembership
        var membershipResult = TenantMembership.Create(
            userId,
            invitation.TenantId,
            invitation.RoleName,
            TenantMembershipStatus.Active);

        if (!membershipResult.IsSuccess)
            return membershipResult.Errors!;

        // Check if a revoked/suspended membership exists and reactivate it
        var inactiveMembership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == invitation.TenantId,
                cancellationToken);

        if (inactiveMembership is not null)
        {
            inactiveMembership.Activate();
        }
        else
        {
            dbContext.TenantMemberships.Add(membershipResult.Value);
        }

        // 8. Mark invitation as accepted
        var acceptResult = invitation.Accept(userId);
        if (!acceptResult.IsSuccess)
            return acceptResult.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Created;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
