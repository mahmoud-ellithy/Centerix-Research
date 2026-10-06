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
    /// <b>(T22 final correction)</b> a catalog entry whose explicit scope is
    /// <see cref="PermissionScope.Unknown"/> also produces this result; "membership in the catalog"
    /// is NOT a synonym for <see cref="PermissionScope.Tenant"/>.
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
/// The classification is a TOTAL function over the canonical catalog AND the controller-enforced
/// platform code list:
/// </para>
/// <list type="bullet">
///   <item><description>a code in <see cref="Permissions.PlatformScope.PermissionCodes"/> is
///   <see cref="PermissionScope.Platform"/>;</description></item>
///   <item><description>a code in <see cref="PermissionCatalog"/> is classified by the entry's
///   <b>EXPLICIT</b> <see cref="PermissionScope"/> field — including
///   <see cref="PermissionScope.Unknown"/>, which keeps the entry unclassified and DENIES
///   authorization (fail-closed);</description></item>
///   <item><description>anything else is <see cref="PermissionScope.Unknown"/> and authorization
///   DENIES.</description></item>
/// </list>
/// <para>
/// (T22 final correction) the classifier NEVER infers scope from catalog membership alone. A new
/// catalog row whose <see cref="PermissionCatalog.Entry.Scope"/> is
/// <see cref="PermissionScope.Unknown"/> is treated identically to a permission that is not in the
/// catalog at all. Only an EXPLICIT <see cref="PermissionScope.Tenant"/> on the entry makes the
/// code tenant-scoped. This keeps the boundary total: any future addition that forgets to pick a
/// scope fails closed at compile time (the <see cref="PermissionScope"/> field is required) and
/// stays fail-closed at authorization time.
/// </para>
/// </summary>
public static class PermissionScopes
{
    private static readonly HashSet<string> PlatformCodes =
        new(Permissions.PlatformScope.PermissionCodes, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the authorization scope of <paramref name="permissionCode"/>. Never throws and never
    /// returns a scope that would permit tenant-derived authorization of a platform permission.
    /// <para>
    /// Algorithm (T22 final correction):
    /// <list type="number">
    ///   <item><description>whitespace/null → <see cref="PermissionScope.Unknown"/>;</description></item>
    ///   <item><description>code is in <see cref="Permissions.PlatformScope.PermissionCodes"/> (the
    ///   preserved controller-enforced platform list, including codes that are not catalog rows)
    ///   → <see cref="PermissionScope.Platform"/>;</description></item>
    ///   <item><description>code is in <see cref="PermissionCatalog"/> with explicit scope →
    ///   <see cref="PermissionCatalog.Entry.Scope"/>;</description></item>
    ///   <item><description>otherwise → <see cref="PermissionScope.Unknown"/>.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static PermissionScope Resolve(string? permissionCode)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            return PermissionScope.Unknown;
        }

        // Preserve the existing platform code list. This list includes controller-enforced codes
        // (PlatformUsers.* / PlatformRoles.* / PlatformPermissions.Read) that are NOT catalog rows
        // and must therefore be classified as Platform by direct membership in this set.
        if (PlatformCodes.Contains(permissionCode))
        {
            return PermissionScope.Platform;
        }

        // Look up the catalog entry by code (case-insensitive) and return its EXPLICIT scope.
        // An entry whose scope is PermissionScope.Unknown stays Unknown — that is the fail-closed
        // invariant the catalog now enforces. No string/module/prefix inference is performed.
        foreach (var entry in PermissionCatalog.All)
        {
            if (string.Equals(entry.Code, permissionCode, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Scope;
            }
        }

        return PermissionScope.Unknown;
    }

    /// <summary>
    /// True only for <see cref="PermissionScope.Platform"/>. Note that
    /// <see cref="PermissionScope.Unknown"/> returns <c>false</c> here: callers that need to
    /// distinguish "not platform" from "unclassifiable" must use <see cref="Resolve"/> so they can
    /// deny the unknown case explicitly.
    /// </summary>
    public static bool IsPlatformScoped(string? permissionCode)
        => Resolve(permissionCode) == PermissionScope.Platform;

    /// <summary>
    /// True only for <see cref="PermissionScope.Tenant"/>. Mirrors <see cref="IsPlatformScoped"/>:
    /// <see cref="PermissionScope.Unknown"/> returns <c>false</c>; callers that need to handle
    /// the unknown case explicitly must use <see cref="Resolve"/>.
    /// </summary>
    public static bool IsTenantScoped(string? permissionCode)
        => Resolve(permissionCode) == PermissionScope.Tenant;

    /// <summary>
    /// The single-permission code path used by <see cref="Resolve"/> and exposed for the regression
    /// test that proves "catalog membership alone" does NOT promote a permission to Tenant. The
    /// algorithm returns the entry's explicit <see cref="PermissionScope"/> unchanged.
    /// </summary>
    internal static PermissionScope ClassifyCatalogEntry(PermissionCatalog.Entry entry)
        => entry.Scope;

    /// <summary>Every catalog code together with its resolved scope (used by classification tests and tooling).</summary>
    public static IReadOnlyList<(string Code, PermissionScope Scope)> GetCatalogClassification()
        => PermissionCatalog.All
            .Select(e => (e.Code, Resolve(e.Code)))
            .ToList();
}