namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

public record CreateInvitationCommand(
    string Email,
    string RoleName,
    int ExpirationDays = 7) : IRequest<Result<Guid>>;

public class CreateInvitationValidator : AbstractValidator<CreateInvitationCommand>
{
    public CreateInvitationValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format");

        RuleFor(x => x.RoleName)
            .NotEmpty().WithMessage("Role name is required");

        RuleFor(x => x.ExpirationDays)
            .InclusiveBetween(1, 30).WithMessage("Expiration must be between 1 and 30 days");
    }
}

public class CreateInvitationHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IIdentityService identityService,
    IRoleService roleService,
    IEmailSender emailSender,
    IInvitationLinkBuilder invitationLinkBuilder) : IRequestHandler<CreateInvitationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateInvitationCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Verify the authenticated user belongs to the current tenant
        var membership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(
                m => m.UserId == currentUser.UserId
                  && m.TenantId == currentTenant.TenantId
                  && m.Status == TenantMembershipStatus.Active,
                cancellationToken);

        if (membership is null)
            return TenantMembershipErrors.UnauthorizedToInvite;

        // 2. Verify the user has invitation permission
        if (!currentUser.TenantPermissions.Contains(PermissionConstants.Invitations.Create))
            return TenantMembershipErrors.UnauthorizedToInvite;

        // 3. Validate the target role exists
        var roleExists = await roleService.ExistsAsync(request.RoleName);
        if (!roleExists)
            return TenantMembershipErrors.RoleNotFound;

        // 4. Normalize email consistently with ASP.NET Identity
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();

        // 5. Prevent duplicate active invitations for the same (Tenant, NormalizedEmail)
        var existingInvitation = await dbContext.TenantInvitations
            .FirstOrDefaultAsync(
                i => i.TenantId == currentTenant.TenantId
                  && i.NormalizedEmail == normalizedEmail
                  && i.Status == InvitationStatus.Pending,
                cancellationToken);

        if (existingInvitation is not null)
            return TenantMembershipErrors.DuplicateActiveInvitation;

        // 6. Check if user is already an active member
        var existingUserId = await identityService.FindUserIdByEmailAsync(request.Email.Trim());
        if (existingUserId is not null)
        {
            var existingMembership = await dbContext.TenantMemberships
                .FirstOrDefaultAsync(
                    m => m.UserId == existingUserId
                      && m.TenantId == currentTenant.TenantId
                      && m.Status == TenantMembershipStatus.Active,
                    cancellationToken);

            if (existingMembership is not null)
                return TenantMembershipErrors.AlreadyMember;
        }

        // 7. Generate cryptographically secure invitation token
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = HashToken(token);

        // 8. Create the invitation
        var invitationResult = TenantInvitation.Create(
            id: Guid.NewGuid(),
            tenantId: currentTenant.TenantId,
            email: request.Email.Trim(),
            invitedByUserId: currentUser.UserId,
            roleName: request.RoleName,
            tokenHash: tokenHash,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(request.ExpirationDays));

        if (!invitationResult.IsSuccess)
            return invitationResult.Errors!;

        dbContext.TenantInvitations.Add(invitationResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        // 9. Send invitation email (development mode: logs to console).
        // The base URL is environment configuration (Invitations:BaseUrl) — never hardcoded —
        // so each environment links to its own front end.
        var acceptUrl = invitationLinkBuilder.BuildAcceptLink(token);
        await emailSender.SendAsync(
            request.Email.Trim(),
            "You've been invited to join a center",
            $"<p>You've been invited to join a center as <strong>{request.RoleName}</strong>.</p>" +
            $"<p><a href=\"{acceptUrl}\">Click here to accept the invitation</a></p>" +
            $"<p>This invitation expires in {request.ExpirationDays} days.</p>",
            cancellationToken);

        return invitationResult.Value.Id;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
