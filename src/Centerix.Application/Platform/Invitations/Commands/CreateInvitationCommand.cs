namespace Centerix.Application.Platform.Invitations.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Common;
using Centerix.Application.Platform.Invitations;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
    IInvitationIssuer invitationIssuer) : IRequestHandler<CreateInvitationCommand, Result<Guid>>
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

        // 7. REV-003: mint + persist + deliver through the shared issuer (token
        // generation, SHA-256 hash, TenantInvitation.Create, e-mail send and
        // compensation-on-send-failure). All checks above are unchanged.
        var issueResult = await invitationIssuer.IssueAsync(
            currentTenant.TenantId,
            request.Email.Trim(),
            request.RoleName,
            currentUser.UserId,
            request.ExpirationDays,
            cancellationToken);

        if (!issueResult.IsSuccess)
            return issueResult.Errors!;

        return issueResult.Value;
    }
}
