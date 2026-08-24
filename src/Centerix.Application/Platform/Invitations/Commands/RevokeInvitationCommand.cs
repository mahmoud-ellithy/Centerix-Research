namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record RevokeInvitationCommand(Guid InvitationId) : IRequest<Result<Updated>>;

public class RevokeInvitationHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant) : IRequestHandler<RevokeInvitationCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        RevokeInvitationCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Verify the user has permission to revoke invitations
        if (!currentUser.TenantPermissions.Contains(PermissionConstants.Invitations.Revoke))
            return TenantMembershipErrors.UnauthorizedToInvite;

        // 2. Find the invitation
        var invitation = await dbContext.TenantInvitations
            .FirstOrDefaultAsync(
                i => i.Id == request.InvitationId
                  && i.TenantId == currentTenant.TenantId,
                cancellationToken);

        if (invitation is null)
            return TenantMembershipErrors.InvitationNotFound;

        // 3. Revoke the invitation
        var result = invitation.Revoke(currentUser.UserId);
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Updated;
    }
}
