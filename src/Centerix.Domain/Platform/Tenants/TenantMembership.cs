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
    public TenantMembershipStatus Status { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }

    private TenantMembership() { }

    private TenantMembership(string userId, string tenantId, TenantMembershipStatus status)
    {
        UserId = userId;
        TenantId = tenantId;
        Status = status;
        JoinedAtUtc = DateTimeOffset.UtcNow;
    }

    public static Result<TenantMembership> Create(
        string userId,
        string tenantId,
        TenantMembershipStatus status = TenantMembershipStatus.Active)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Error.Validation("TenantMembership.UserId_Required", "User ID is required");

        if (string.IsNullOrWhiteSpace(tenantId))
            return Error.Validation("TenantMembership.TenantId_Required", "Tenant ID is required");

        return new TenantMembership(userId, tenantId, status);
    }

    public Result<Updated> Activate()
    {
        if (Status == TenantMembershipStatus.Active)
            return Result.Updated;

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
