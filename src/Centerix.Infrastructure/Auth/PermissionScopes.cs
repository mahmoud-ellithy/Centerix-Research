namespace Centerix.Infrastructure.Auth;

/// <summary>
/// The authorization scope of a permission code. A permission belongs to exactly ONE domain:
/// either the cross-tenant PLATFORM domain or a single tenant's TENANT domain. There is no
/// "both" scope — a permission that a tenant may hold is tenant-scoped, and a permission that
/// operates on cross-tenant platform resources is platform-scoped and can only be satisfied by
/// the authoritative PlatformAdmin decision.
/// </summary>
public enum PermissionScope
{
    /// <summary>
    /// The permission code is not part of the canonical catalog and is not explicitly classified.
    /// Authorization MUST fail closed for this scope — uncertainty is never converted to Allow.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Cross-tenant platform resources: the tenant registry, platform staff/RBAC, global catalogs
    /// and platform commercial administration. Only <c>IPlatformAdminVerifier</c> may allow these.
    /// </summary>
    Platform = 1,

    /// <summary>Tenant-partitioned data. Requires an authorized tenant context plus a membership-derived grant.</summary>
    Tenant = 2
}

/// <summary>
/// THE single authoritative permission-scope decision for the whole application.
/// <para>
/// Every authorization layer (policy handler, feature handler, tenant guard) resolves scope through
/// this one type, so scope knowledge is never duplicated, re-derived or drifted between components.
/// The classification is a TOTAL function over the canonical catalog:
/// </para>
/// <list type="bullet">
///   <item><description>a code in <see cref="Permissions.PlatformScope.PermissionCodes"/> is <see cref="PermissionScope.Platform"/>;</description></item>
///   <item><description>a code in <see cref="PermissionCatalog"/> that is not platform-scoped is <see cref="PermissionScope.Tenant"/>;</description></item>
///   <item><description>anything else is <see cref="PermissionScope.Unknown"/> and authorization DENIES.</description></item>
/// </list>
/// <para>
/// Because the tenant set is derived from the catalog itself, a newly added catalog permission is
/// tenant-scoped automatically (extensible), while an unrecognised or hand-written code fails closed
/// until it is deliberately classified. Fail-closed is the security boundary: an unknown scope must
/// never widen access.
/// </para>
/// </summary>
public static class PermissionScopes
{
    private static readonly HashSet<string> PlatformCodes =
        new(Permissions.PlatformScope.PermissionCodes, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CatalogCodes =
        new(PermissionCatalog.All.Select(e => e.Code), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the authorization scope of <paramref name="permissionCode"/>. Never throws and never
    /// returns a scope that would permit tenant-derived authorization of a platform permission.
    /// </summary>
    public static PermissionScope Resolve(string? permissionCode)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            return PermissionScope.Unknown;
        }

        if (PlatformCodes.Contains(permissionCode))
        {
            return PermissionScope.Platform;
        }

        return CatalogCodes.Contains(permissionCode)
            ? PermissionScope.Tenant
            : PermissionScope.Unknown;
    }

    /// <summary>
    /// True only for <see cref="PermissionScope.Platform"/>. Note that
    /// <see cref="PermissionScope.Unknown"/> returns <c>false</c> here: callers that need to
    /// distinguish "not platform" from "unclassifiable" must use <see cref="Resolve"/> so they can
    /// deny the unknown case explicitly.
    /// </summary>
    public static bool IsPlatformScoped(string? permissionCode)
        => Resolve(permissionCode) == PermissionScope.Platform;

    /// <summary>Every catalog code together with its resolved scope (used by classification tests and tooling).</summary>
    public static IReadOnlyList<(string Code, PermissionScope Scope)> GetCatalogClassification()
        => PermissionCatalog.All
            .Select(e => (e.Code, Resolve(e.Code)))
            .ToList();
}
