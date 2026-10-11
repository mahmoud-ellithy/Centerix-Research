namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

public record AcceptInvitationCommand(string Token) : IRequest<Result<Created>>;

/// <summary>
/// Accepts an invitation for an already-registered user and provisions their tenant
/// membership in ONE atomic database transaction (REV-017).
/// Atomicity: the invitation is claimed via a conditional UPDATE (Pending → Accepted)
/// inside the transaction — exactly one concurrent caller wins; losers return a conflict
/// and their uncommitted writes roll back. A membership PK violation from a residual race
/// is mapped to a clean conflict, never a 500.
/// Reactivation (REV-017): an existing Revoked membership is reactivated IN PLACE via
/// <c>TenantMembership.Reactivate(invitation.RoleName)</c> so the returning member ends
/// with exactly the invited role; a Suspended member can never self-reactivate.
/// </summary>
public class AcceptInvitationHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUser,
    ILimitService limitService,
    IIdentityService identityService,
    ILogger<AcceptInvitationHandler> logger) : IRequestHandler<AcceptInvitationCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        AcceptInvitationCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return TenantMembershipErrors.InvalidToken;

        var tokenHash = HashToken(request.Token);

        // REV-017: the whole accept (claim + reservation + membership + invitation state)
        // commits or rolls back atomically on relational providers. On InMemory the
        // transaction is a no-op and the already-loaded tracked entity guards the claim.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

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

        // 3. Check expiration (transition persisted only on commit).
        if (invitation.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            invitation.MarkExpired();
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
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

        // 5b. SEC-001/F3 pre-check on the invited role BEFORE any state change: a legacy
        // invitation row carrying a platform-authority or non-canonical role (written before
        // the write contract existed) must fail with its 403 here — regardless of quota
        // state — and must never reach the reservation, the claim or a membership write.
        // Valid invitations always carry canonical tenant roles, so this is invisible to
        // them; Create/Reactivate re-enforce the same rules at the write point.
        if (TenantRoleScopes.IsPlatformScoped(invitation.RoleName))
            return TenantMembershipErrors.PlatformRoleNotAllowed;

        if (!TenantRoleScopes.IsCanonicalTenantRole(invitation.RoleName))
            return TenantMembershipErrors.RoleNotAllowed;

        // 6. REV-017: atomic single-use claim — flip Pending → Accepted ONLY while still
        // pending (same conditional ExecuteUpdate used by RegisterFromInvitationHandler, with
        // the same InMemory fallback). Exactly one concurrent caller wins; every loser
        // returns a conflict and its uncommitted writes roll back.
        int claimed;
        if (dbContext.IsRelational)
        {
            claimed = await dbContext.TenantInvitations
                .Where(i => i.Id == invitation.Id && i.Status == InvitationStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(i => i.Status, InvitationStatus.Accepted),
                    cancellationToken);
        }
        else if (invitation.Status == InvitationStatus.Pending)
        {
            claimed = 1;
        }
        else
        {
            claimed = 0;
        }

        if (claimed != 1)
            return TenantMembershipErrors.InvitationAlreadyAccepted;

        // 7. Check if already an active member
        var existingMembership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == invitation.TenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (existingMembership is not null)
            return TenantMembershipErrors.AlreadyMember;

        // 8. REV-011: reserve a Users slot inside the same transaction (a returning revoked
        // member counts as a new user). Failure rolls everything back, so the invitation
        // stays usable. First-owner exemption: zero Active memberships never fail for a
        // missing/inactive subscription; best-effort track instead.
        var activeMembers = await dbContext.TenantMemberships
            .CountAsync(
                m => m.TenantId == invitation.TenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (activeMembers == 0)
        {
            await limitService.IncrementAsync(invitation.TenantId, LimitTypeCodes.Users, cancellationToken);
        }
        else
        {
            var usersLimitResult = await limitService.ReserveAsync(
                invitation.TenantId, LimitTypeCodes.Users, cancellationToken);
            if (!usersLimitResult.IsSuccess)
                return usersLimitResult.Errors!;
        }

        // 9. REV-017: reactivate an existing Revoked membership in place (applying the invited
        // role) or create a fresh one. Suspended rows are rejected inside Reactivate and can
        // never self-reactivate through this path.
        var inactiveMembership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(
                m => m.UserId == userId
                  && m.TenantId == invitation.TenantId,
                cancellationToken);

        if (inactiveMembership is not null)
        {
            var reactivateResult = inactiveMembership.Reactivate(invitation.RoleName);
            if (!reactivateResult.IsSuccess)
                return reactivateResult.Errors!;
        }
        else
        {
            var membershipResult = TenantMembership.Create(
                userId,
                invitation.TenantId,
                invitation.RoleName,
                TenantMembershipStatus.Active);

            if (!membershipResult.IsSuccess)
                return membershipResult.Errors!;

            dbContext.TenantMemberships.Add(membershipResult.Value);
        }

        // 10. Mark invitation as accepted (the tracker holds the claimed state; this stamps
        // the acceptance metadata exactly like RegisterFromInvitationHandler).
        var acceptResult = invitation.Accept(userId);
        if (!acceptResult.IsSuccess)
            return acceptResult.Errors!;

        // REV-017: a residual membership PK race (two invitations, same user+tenant, both
        // past the Active check) surfaces here as a PK violation — report a clean conflict,
        // never a 500. Anything else still propagates and rolls back.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsMembershipConflictException(ex))
        {
            logger.LogWarning(
                ex,
                "Concurrent membership insert for user {UserId} in tenant {TenantId}; reporting AlreadyMember",
                userId, invitation.TenantId);
            return TenantMembershipErrors.AlreadyMember;
        }

        return Result.Created;
    }

    /// <summary>
    /// REV-017: maps a concurrent-insert PRIMARY KEY violation on (UserId, TenantId) to a
    /// clean conflict. SQL Server error 2627 = PK/UNIQUE violation (2601 accepted for the
    /// same family); the PK name pins the mapping to the membership row only.
    /// </summary>
    private static bool IsMembershipConflictException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("PK_TenantMemberships", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
