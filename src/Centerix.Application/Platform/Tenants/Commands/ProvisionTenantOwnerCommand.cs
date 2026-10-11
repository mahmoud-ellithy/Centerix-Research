namespace Centerix.Application.Platform.Tenants.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Invitations;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REV-003 — PLATFORM-ONLY: provisions the first membership path for a tenant by issuing a
/// <c>TenantAdmin</c> invitation to the tenant's <c>OwnerEmail</c>. This closes the bootstrap
/// gap where <c>POST /api/tenants</c> created the tenant but nothing could create its first
/// member (<c>CreateInvitationHandler</c> requires the inviter to hold an active membership,
/// which a PlatformAdmin can never hold per SEC-001).
/// Deliberately does NOT depend on <c>ICurrentTenant</c> or on any membership row.
/// </summary>
public record ProvisionTenantOwnerCommand(Guid TenantId) : IRequest<Result<Guid>>;

public class ProvisionTenantOwnerHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ICurrentUser currentUser,
    IIdentityService identityService,
    IRoleService roleService,
    IInvitationIssuer invitationIssuer,
    IAuditWriter auditWriter) : IRequestHandler<ProvisionTenantOwnerCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        ProvisionTenantOwnerCommand request,
        CancellationToken cancellationToken)
    {
        var guardResult = await platformAdminGuard.EnsurePlatformAdminAsync(cancellationToken);
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        // Platform-side load: the Tenants registry is platform-scoped, never tenant-filtered.
        var tenant = await dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant is null)
            return Error.NotFound("Tenant.NotFound", $"Tenant '{request.TenantId}' was not found.");

        // Only an approved tenant (Approve → Provisioning, then Active; Trial counts as
        // approved) may receive its owner invitation. Pending/Rejected/Cancelled/Suspended
        // are rejected with Conflict.
        if (tenant.LifecycleStatus is not (
            LifecycleStatus.Provisioning or LifecycleStatus.Active or LifecycleStatus.Trial))
            return TenantErrors.ProvisionOwnerNotAllowed(tenant.LifecycleStatus);

        // The TenantAdmin Identity role must exist — the invitation can never be fulfilled
        // without it (same fail-fast contract as CreateInvitationHandler).
        if (!await roleService.ExistsAsync(TenantRoleNames.TenantAdmin))
            return TenantMembershipErrors.RoleNotFound;

        var tenantId = tenant.Id.ToString();
        var normalizedEmail = tenant.OwnerEmail.Trim().ToUpperInvariant();

        // The owner is already an Active member → nothing to provision.
        var existingUserId = await identityService.FindUserIdByEmailAsync(tenant.OwnerEmail.Trim());
        if (existingUserId is not null)
        {
            var existingMembership = await dbContext.TenantMemberships
                .FirstOrDefaultAsync(
                    m => m.UserId == existingUserId
                      && m.TenantId == tenantId
                      && m.Status == TenantMembershipStatus.Active,
                    cancellationToken);

            if (existingMembership is not null)
                return TenantMembershipErrors.AlreadyMember;
        }

        // Resend semantics: revoke a still-Pending invitation for the same (tenant, email)
        // so exactly one Pending owner invitation exists after this call.
        var pendingInvitation = await dbContext.TenantInvitations
            .FirstOrDefaultAsync(
                i => i.TenantId == tenantId
                  && i.NormalizedEmail == normalizedEmail
                  && i.Status == InvitationStatus.Pending,
                cancellationToken);

        if (pendingInvitation is not null)
        {
            var revokeResult = pendingInvitation.Revoke(currentUser.UserId);
            if (!revokeResult.IsSuccess)
                return revokeResult.Errors!;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // REV-003: mint + persist + deliver through the shared issuer (7-day expiry).
        var issueResult = await invitationIssuer.IssueAsync(
            tenantId,
            tenant.OwnerEmail.Trim(),
            TenantRoleNames.TenantAdmin,
            currentUser.UserId,
            expirationDays: 7,
            cancellationToken);

        if (!issueResult.IsSuccess)
            return issueResult.Errors!;

        await auditWriter.WriteAsync(
            action: "Tenant.ProvisionOwner",
            entityType: nameof(Tenant),
            entityId: tenant.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                tenant.OwnerEmail,
                Role = TenantRoleNames.TenantAdmin,
                InvitationId = issueResult.Value
            }),
            cancellationToken: cancellationToken);

        return issueResult.Value;
    }
}
