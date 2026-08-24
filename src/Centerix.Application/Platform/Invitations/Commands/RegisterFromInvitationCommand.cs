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
/// Concurrency: races surface as constraint violations inside the transaction and roll back cleanly —
/// duplicate e-mails via the unique index on AspNetUsers.NormalizedEmail/UserName, duplicate
/// memberships via the (UserId, TenantId) primary key, double-acceptance via the invitation status
/// check plus the unique TokenHash index.
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

        // 5. Create the IdentityUser (same DbContext/transaction).
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

        // 6. Create TenantMembership.
        var membershipResult = TenantMembership.Create(
            userId,
            invitation.TenantId,
            invitation.RoleName,
            TenantMembershipStatus.Active);

        if (!membershipResult.IsSuccess)
            return membershipResult.Errors!;

        dbContext.TenantMemberships.Add(membershipResult.Value);

        // 7. Mark invitation as accepted.
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
