namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    IInvitationLinkBuilder invitationLinkBuilder,
    ILogger<CreateInvitationHandler> logger) : IRequestHandler<CreateInvitationCommand, Result<Guid>>
{
    /// <summary>
    /// CFG-001 bounded-consistency invitation delivery. There is deliberately NO Outbox
    /// (DATA-001 is out of scope), so delivery follows persist → commit → send →
    /// compensate:
    /// 1. Persist the Pending invitation.
    /// 2. Commit (SaveChanges).
    /// 3. Attempt e-mail delivery.
    /// 4. On delivery failure, attempt compensation (revoke so the invitation no
    ///    longer remains Pending).
    /// 5. If compensation succeeds, the invitation is NOT Pending and the request
    ///    returns FAILURE.
    /// 6. If compensation itself fails, the request still returns FAILURE and NEVER
    ///    claims success; the invitation Id, TenantId, NormalizedEmail, send exception
    ///    AND compensation exception are all logged for operator reconciliation.
    /// BOUNDED CONSISTENCY LIMITATION: between step 2 and step 4 (or when compensation
    /// fails, or the process crashes in that window) a Pending invitation row can exist
    /// without a delivered e-mail, or — if compensation failed — a Pending row that will
    /// never be delivered. Operators MUST periodically reconcile invitations stuck in
    /// Pending past their expected delivery (re-send or revoke them). A transactional
    /// outbox would close this window but is explicitly deferred (no DATA-001).
    /// </summary>
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

        // 3. SEC-001: reject a platform-authority target role at the application boundary with an
        // explicit 403 before any other work happens. Platform authority is never delegated to a
        // tenant membership, so it can never be granted through an invitation either. (The same
        // rule is re-enforced as a domain invariant inside TenantInvitation.Create.)
        if (TenantRoleScopes.IsPlatformScoped(request.RoleName))
            return TenantMembershipErrors.PlatformRoleNotAllowed;

        // 3b. F3: only the canonical tenant roles (TenantAdmin, TenantUser) may be invited. This
        // runs BEFORE the role-exists lookup so a non-canonical name answers 403 (RoleNotAllowed)
        // rather than 404 (RoleNotFound): the role may well exist in Identity - the contract is
        // about which roles can ever become a tenant membership, not about the catalog.
        // (Re-enforced as a domain invariant inside TenantInvitation.Create.)
        if (!TenantRoleScopes.IsCanonicalTenantRole(request.RoleName))
            return TenantMembershipErrors.RoleNotAllowed;

        // 4. Validate the target role exists
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
        var invitationId = invitationResult.Value.Id;
        var invitationTenantId = invitationResult.Value.TenantId;
        var invitationNormalizedEmail = invitationResult.Value.NormalizedEmail;
        await dbContext.SaveChangesAsync(cancellationToken);

        // 9. Send invitation email (SMTP in production, development/capturing sender
        // elsewhere). The base URL is environment configuration (Invitations:BaseUrl) —
        // never hardcoded — so each environment links to its own front end.
        var acceptUrl = invitationLinkBuilder.BuildAcceptLink(token);
        try
        {
            await emailSender.SendAsync(
                request.Email.Trim(),
                "You've been invited to join a center",
                $"<p>You've been invited to join a center as <strong>{request.RoleName}</strong>.</p>" +
                $"<p><a href=\"{acceptUrl}\">Click here to accept the invitation</a></p>" +
                $"<p>This invitation expires in {request.ExpirationDays} days.</p>",
                cancellationToken);
        }
        catch (Exception sendEx)
        {
            // Delivery failed AFTER commit: attempt compensation so the invitation does
            // not remain Pending, but report FAILURE either way — never success.
            try
            {
                var pending = await dbContext.TenantInvitations
                    .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken);
                if (pending is not null && pending.Status == InvitationStatus.Pending)
                {
                    pending.Revoke(currentUser.UserId);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                logger.LogWarning(
                    sendEx,
                    "Invitation email delivery failed; compensation succeeded. " +
                    "InvitationId={InvitationId}, TenantId={TenantId}, NormalizedEmail={NormalizedEmail}.",
                    invitationId, invitationTenantId, invitationNormalizedEmail);
            }
            catch (Exception compensationEx)
            {
                logger.LogError(
                    compensationEx,
                    "Invitation email delivery failed AND compensation failed. " +
                    "InvitationId={InvitationId}, TenantId={TenantId}, NormalizedEmail={NormalizedEmail}, " +
                    "SendException={SendException}. Operator reconciliation required: revoke or re-send the Pending invitation.",
                    invitationId, invitationTenantId, invitationNormalizedEmail, sendEx.ToString());
            }

            return TenantMembershipErrors.InvitationDeliveryFailed;
        }

        return invitationResult.Value.Id;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
