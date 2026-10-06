namespace Centerix.Domain.Platform.Tenants;

/// <summary>
/// Canonical names of the roles that participate in the tenant/platform role classification.
/// <para>
/// These live in the DOMAIN because a <see cref="TenantMembership"/> and a
/// <see cref="TenantInvitation"/> are domain aggregates that must enforce the classification
/// themselves - the invariant cannot be delegated to an application handler, otherwise a future
/// write path would silently reintroduce the defect.
/// </para>
/// </summary>
public static class TenantRoleNames
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string TenantUser = "TenantUser";
}

/// <summary>
/// The authorization scope of a ROLE NAME that a tenant-scoped row may carry.
/// <para>
/// Mirror image of the permission-scope classifier: a permission is either platform-scoped or
/// tenant-scoped, and a ROLE NAME that is stored in <c>TenantMembership.RoleName</c> or
/// <c>TenantInvitation.RoleName</c> must be tenant-scoped. The Identity <c>PlatformAdmin</c> role
/// is the platform-authority role - it exists to satisfy platform-scoped endpoints through
/// <c>IPlatformAdminVerifier</c>, and granting it to a tenant membership would hand every
/// permission that role holds (the whole catalog, including tenant-scoped codes) to a member of a
/// single tenant. <b>(SEC-001)</b>
/// </para>
/// </summary>
public enum TenantRoleScope
{
    /// <summary>
    /// The role name is not one of the canonical roles. Authorization is neither granted nor
    /// rejected on the basis of the name alone - custom tenant roles are legitimate and are
    /// validated by the Identity role catalog instead. Only <see cref="Platform"/> is rejected.
    /// </summary>
    Unknown = 0,

    /// <summary>A tenant role (TenantAdmin, TenantUser, or a custom tenant role). Allowed in a membership.</summary>
    Tenant = 1,

    /// <summary>A platform-authority role (PlatformAdmin). NEVER allowed in a membership or invitation.</summary>
    Platform = 2
}

/// <summary>
/// THE single authoritative role-scope decision for tenant-scoped rows (SEC-001).
/// <para>
/// Used by the domain factories (<see cref="TenantMembership.Create"/>,
/// <see cref="TenantInvitation.Create"/>) as an invariant, and by the authorization pipeline
/// (tenant guard, permission handler) as defense in depth against rows written before the
/// invariant existed.
/// </para>
/// </summary>
public static class TenantRoleScopes
{
    /// <summary>Role names that carry platform authority. Comparison is case-insensitive.</summary>
    public static readonly IReadOnlySet<string> PlatformRoleNameSet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TenantRoleNames.PlatformAdmin };

    /// <summary>Role names that are known to be tenant-scoped. Comparison is case-insensitive.</summary>
    public static readonly IReadOnlySet<string> TenantRoleNameSet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TenantRoleNames.TenantAdmin,
            TenantRoleNames.TenantUser
        };

    /// <summary>
    /// Resolves the scope of <paramref name="roleName"/>. Never throws; whitespace/null resolves
    /// to <see cref="TenantRoleScope.Unknown"/> (the caller's own defaulting rules apply).
    /// </summary>
    public static TenantRoleScope Resolve(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return TenantRoleScope.Unknown;
        }

        var trimmed = roleName.Trim();

        if (PlatformRoleNameSet.Contains(trimmed))
        {
            return TenantRoleScope.Platform;
        }

        if (TenantRoleNameSet.Contains(trimmed))
        {
            return TenantRoleScope.Tenant;
        }

        return TenantRoleScope.Unknown;
    }

    /// <summary>
    /// True only for a platform-authority role name. This is the exact condition under which a
    /// tenant membership or invitation must be rejected.
    /// </summary>
    public static bool IsPlatformScoped(string? roleName)
        => Resolve(roleName) == TenantRoleScope.Platform;

    /// <summary>True only for a canonical tenant role name.</summary>
    public static bool IsTenantScoped(string? roleName)
        => Resolve(roleName) == TenantRoleScope.Tenant;
}
