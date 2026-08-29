namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

public record RegisterFromInvitationCommand(
    string Token,
    string Password,
    string? FirstName = null,
    string? LastName = null) : IRequest<Result<Created>>;

public class RegisterFromInvitationValidator : AbstractValidator<RegisterFromInvitationCommand>
{
    public RegisterFromInvitationValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Invitation token is required");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters");
    }
}

/// <summary>
/// Registers a brand-new user from an invitation token and provisions their tenant membership
/// in ONE atomic database transaction.
/// </summary>
/// <remarks>
/// Atomicity: <see cref="IdentityService"/> resolves <see cref="Microsoft.AspNetCore.Identity.UserManager{IdentityUser}"/>
/// over the SAME scoped <c>AppDbContext</c> instance that backs <see cref="IAppDbContext"/> (both are
/// registered in the same DI scope, Identity via <c>AddEntityFrameworkStores&lt;AppDbContext&gt;()</c>), so
/// Identity inserts, the membership insert and the invitation state change all flow through one
/// connection. Wrapping the whole operation in a single explicit transaction means a failure at any
/// step rolls back EVERYTHING — no orphan IdentityUser, no membership row, and the invitation stays
/// Pending (still usable). Compensating deletes are not needed and have been removed.
///
/// Concurrency: the invitation is claimed via an atomic conditional UPDATE
/// (<c>Status = Accepted WHERE Status = Pending</c>) inside the transaction — exactly one
/// concurrent caller wins; losers return a conflict and their uncommitted writes roll back.
/// Identity's default indexes are non-unique, so this claim is the serialization point for
/// double-registration. Duplicate memberships surface as (UserId, TenantId) primary-key
/// violations and roll back cleanly; TokenHash is uniquely indexed.
/// </remarks>
public class RegisterFromInvitationHandler(
    IAppDbContext dbContext,
    IIdentityService identityService,
    ILogger<RegisterFromInvitationHandler> logger) : IRequestHandler<RegisterFromInvitationCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        RegisterFromInvitationCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return TenantMembershipErrors.InvalidToken;

        var tokenHash = HashToken(request.Token);

        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken);

        // 1. Find the invitation by token hash.
        var invitation = await dbContext.TenantInvitations
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

        if (invitation is null)
            return TenantMembershipErrors.InvalidToken;

        // 2. Validate invitation status.
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
            await transaction.CommitAsync(cancellationToken);
            return TenantMembershipErrors.InvitationExpired;
        }

        // 4. Reject if the e-mail already has an account.
        var existingUserId = await identityService.FindUserIdByEmailAsync(invitation.NormalizedEmail);
        if (existingUserId is not null)
            return Error.Conflict("Invitation.UserAlreadyExists",
                "An account already exists for this email. Please log in and accept the invitation.");

        // 5. Atomic single-use claim: flip Pending → Accepted ONLY while still pending.
        // Identity's default indexes on AspNetUsers are NON-unique, so nothing else serializes
        // two concurrent registrations with the same e-mail. On relational providers this
        // conditional ExecuteUpdate is the serialization point: exactly one concurrent caller
        // gets affected rows == 1 (a single atomic UPDATE ... WHERE Status = Pending); every
        // other transaction fails here, returns conflict, and its uncommitted writes roll back.
        //
        // The EF InMemory provider does not support ExecuteUpdate* (it is used only by the fast
        // unit-test host, never in production), so there the already-loaded tracked entity acts
        // as the guard: its status was validated as Pending in step 2 and step 8 persists the
        // transition through the domain method. True multi-writer concurrency is proven against
        // real SQL Server by the Testcontainers integration suite.
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

        // 6. Create the IdentityUser (same DbContext/transaction).
        var (userId, succeeded, errors) = await identityService.CreateUserAsync(
            invitation.Email,
            invitation.NormalizedEmail,
            request.Password);

        if (!succeeded)
        {
            logger.LogWarning(
                "Registration from invitation {InvitationId} failed during user creation: {Errors}",
                invitation.Id, string.Join(", ", errors));

            // Uncommitted transaction rolls back on dispose; invitation remains usable.
            return Error.Failure("Invitation.RegistrationFailed",
                $"Failed to create account: {string.Join(", ", errors)}");
        }

        // 7. Create TenantMembership.
        var membershipResult = TenantMembership.Create(
            userId,
            invitation.TenantId,
            invitation.RoleName,
            TenantMembershipStatus.Active);

        if (!membershipResult.IsSuccess)
            return membershipResult.Errors!;

        dbContext.TenantMemberships.Add(membershipResult.Value);

        // 8. Mark invitation as accepted (tracker already holds the claimed state).
        var acceptResult = invitation.Accept(userId);
        if (!acceptResult.IsSuccess)
            return acceptResult.Errors!;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Roll back everything: user, membership and any invitation mutation.
            logger.LogError(ex,
                "Registration from invitation {InvitationId} for user {UserId} failed; transaction rolled back",
                invitation.Id, userId);
            throw;
        }

        return Result.Created;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
