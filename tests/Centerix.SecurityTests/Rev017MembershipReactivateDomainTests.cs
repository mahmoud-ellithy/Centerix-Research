using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Xunit;

namespace Centerix.SecurityTests;

// REV-017 (domain): TenantMembership.Reactivate applies the invited role under the
// SEC-001/F3 write contract and only ever revives Revoked rows.
public class Rev017MembershipReactivateDomainTests
{
    [Fact]
    [Trait("Category", "Rev017")]
    public void Reactivate_RevokedRow_AppliesInvitedRole_AndActivates()
    {
        var membership = TenantMembership.Create(
            "user-1", "tenant-1", "TenantAdmin", TenantMembershipStatus.Revoked).Value;

        var result = membership.Reactivate("TenantUser");

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public void Reactivate_SuspendedRow_Returns403_RoleAndStatusUnchanged()
    {
        var membership = TenantMembership.Create(
            "user-1", "tenant-1", "TenantUser", TenantMembershipStatus.Suspended).Value;

        var result = membership.Reactivate("TenantAdmin");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!,
            e => e.Code == "TenantMembership.Suspended" &&
                 e.Type == Centerix.Domain.Common.Results.ErrorKind.Forbidden);
        Assert.Equal(TenantMembershipStatus.Suspended, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public void Reactivate_ActiveRow_ReturnsAlreadyMember()
    {
        var membership = TenantMembership.Create(
            "user-1", "tenant-1", "TenantUser", TenantMembershipStatus.Active).Value;

        var result = membership.Reactivate("TenantUser");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.AlreadyMember");
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public void Reactivate_PlatformRole_IsRejected_AsForbidden()
    {
        var membership = TenantMembership.Create(
            "user-1", "tenant-1", "TenantUser", TenantMembershipStatus.Revoked).Value;

        var result = membership.Reactivate("PlatformAdmin");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.PlatformRoleNotAllowed");
        Assert.Equal(TenantMembershipStatus.Revoked, membership.Status);
    }

    [Fact]
    [Trait("Category", "Rev017")]
    public void Reactivate_NonCanonicalRole_IsRejected_AsForbidden()
    {
        var membership = TenantMembership.Create(
            "user-1", "tenant-1", "TenantUser", TenantMembershipStatus.Revoked).Value;

        var result = membership.Reactivate("OpsManager");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.RoleNotAllowed");
        Assert.Equal(TenantMembershipStatus.Revoked, membership.Status);
    }
}
