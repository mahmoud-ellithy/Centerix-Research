namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;

/// <summary>
/// Junction establishing that an ASP.NET Identity user (<c>AspNetUsers</c>) belongs to a
/// tenant, identified by the <b>runtime</b> Finbuckle tenant id (<c>Platform.TenantRegistry.Id</c>,
/// a <see cref="string"/>). The domain <c>Tenant</c> entity (<c>Platform.Tenants</c>) is not used
/// here because it is not populated/seeded at runtime; the Finbuckle registry is the live source
/// of tenant identity consumed by <c>CurrentTenant</c> and the <c>IHasTenantId</c> query filter.
/// </summary>
/// <remarks>
/// This entity is intentionally <b>not</b> <see cref="IHasTenantId"/>. It must remain visible across
/// all tenant contexts so that membership can be verified for any resolved tenant (this is the
/// foundation for the future cross-tenant access control). Scoping it with the tenant query filter
/// or <c>TenantInterceptor</c> would make the table unreadable for the very check it exists to serve.
/// A user may belong to multiple tenants; the composite key (UserId, TenantId) enforces uniqueness
/// while permitting multiple rows per user.
/// </remarks>
public class TenantMembership : Entity
{
    public string UserId { get; private set; } = default!;
    public string TenantId { get; private set; } = default!;
    public string RoleName { get; private set; } = default!;
    public TenantMembershipStatus Status { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }

    private TenantMembership() { }

    private TenantMembership(string userId, string tenantId, string roleName, TenantMembershipStatus status)
    {
        UserId = userId;
        TenantId = tenantId;
        RoleName = roleName;
        Status = status;
        JoinedAtUtc = DateTimeOffset.UtcNow;
    }

    public static Result<TenantMembership> Create(
        string userId,
        string tenantId,
        string roleName = "TenantUser",
        TenantMembershipStatus status = TenantMembershipStatus.Active)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Error.Validation("TenantMembership.UserId_Required", "User ID is required");

        if (string.IsNullOrWhiteSpace(tenantId))
            return Error.Validation("TenantMembership.TenantId_Required", "Tenant ID is required");

        if (string.IsNullOrWhiteSpace(roleName))
            roleName = TenantRoleNames.TenantUser;

        // SEC-001 (domain invariant): a TenantMembership is a TENANT-scoped assertion. Binding a
        // platform-authority role to it would make the tenant permission resolver publish every
        // permission that role holds as a tenant-derived grant. Platform authority is expressed
        // through the Identity role + IPlatformAdminVerifier, never through a membership row.
        if (TenantRoleScopes.IsPlatformScoped(roleName))
            return TenantMembershipErrors.PlatformRoleNotAllowed;

        // F3 (domain invariant): only the two canonical tenant roles may be bound to a
        // membership. Any other name - a custom Identity role, a casing/whitespace variant -
        // is REJECTED, not defaulted, so the grants reachable through the tenant permission
        // resolver stay exactly the two production-seeded matrices (TenantAdmin's and
        // TenantUser's). Checked AFTER the platform rule so SEC-001 keeps its dedicated error.
        if (!TenantRoleScopes.IsCanonicalTenantRole(roleName))
            return TenantMembershipErrors.RoleNotAllowed;

        return new TenantMembership(userId, tenantId, roleName, status);
    }

    public Result<Updated> Activate()
    {
        if (Status == TenantMembershipStatus.Active)
            return Result.Updated;

        Status = TenantMembershipStatus.Active;
        return Result.Updated;
    }

    /// <summary>
    /// REV-017: reactivates a Revoked membership through an invitation, APPLYING the invited
    /// role. Only Revoked rows may return this way: a Suspended member must be restored by
    /// an administrator (accepting an invitation must never self-reactivate a suspension),
    /// and an Active member is already a member. The role is validated with the same
    /// SEC-001/F3 rules as <see cref="Create"/>, so a returning member always ends with
    /// exactly the invited role — a revoked ex-TenantAdmin re-invited as TenantUser returns
    /// as TenantUser, never as TenantAdmin.
    /// </summary>
    public Result<Updated> Reactivate(string roleName)
    {
        if (Status == TenantMembershipStatus.Active)
            return TenantMembershipErrors.AlreadyMember;

        // Invitation acceptance is not an administrative restore path: anything that is not
        // a Revoked return (Suspended, or any other non-Active state) is rejected here.
        if (Status != TenantMembershipStatus.Revoked)
            return TenantMembershipErrors.MembershipSuspended;

        if (string.IsNullOrWhiteSpace(roleName))
            roleName = TenantRoleNames.TenantUser;

        // SEC-001 + F3 (same rules as Create): the applied role can never be a
        // platform-authority or non-canonical role.
        if (TenantRoleScopes.IsPlatformScoped(roleName))
            return TenantMembershipErrors.PlatformRoleNotAllowed;

        if (!TenantRoleScopes.IsCanonicalTenantRole(roleName))
            return TenantMembershipErrors.RoleNotAllowed;

        RoleName = roleName;
        Status = TenantMembershipStatus.Active;
        return Result.Updated;
    }

    public Result<Updated> Suspend()
    {
        if (Status == TenantMembershipStatus.Suspended)
            return Result.Updated;

        Status = TenantMembershipStatus.Suspended;
        return Result.Updated;
    }

    public Result<Updated> Revoke()
    {
        if (Status == TenantMembershipStatus.Revoked)
            return Result.Updated;

        Status = TenantMembershipStatus.Revoked;
        return Result.Updated;
    }
}
