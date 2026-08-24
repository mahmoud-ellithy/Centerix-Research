namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Provides the current tenant context for a request.
/// </summary>
/// <remarks>
/// It is essential to distinguish two concepts:
/// <list type="bullet">
///   <item><description><see cref="ResolvedTenantId"/> — the tenant the client selected and Finbuckle resolved.
///   This is a <b>selection input</b> derived from a client-controlled value (header/host) and must
///   <b>never</b> be trusted for authorization.</description></item>
///   <item><description><see cref="TenantId"/> — the <b>AUTHORIZED / VERIFIED</b> tenant for this request.
///   It is empty until <see cref="IsAuthorized"/> becomes true (fail-closed). Every tenant-aware
///   consumer (EF query filter, <c>TenantInterceptor</c>, tenant-aware authorization and services) MUST
///   read this value, never <see cref="ResolvedTenantId"/>.</description></item>
/// </list>
/// The verified tenant is established by <see cref="AuthorizeTenant"/>, which is called by
/// <c>TenantGuardMiddleware</c> once it has confirmed the authenticated user holds an active
/// <c>TenantMembership</c> for the resolved tenant.
/// </remarks>
public interface ICurrentTenant
{
    /// <summary>The AUTHORIZED tenant for this request. Empty until <see cref="IsAuthorized"/> is true (fail-closed).</summary>
    string TenantId { get; }

    /// <summary>The tenant the client requested and Finbuckle resolved. Selection input only — never trust for authorization.</summary>
    string ResolvedTenantId { get; }

    /// <summary>True once <see cref="ResolvedTenantId"/> has been authorized for the current user.</summary>
    bool IsAuthorized { get; }

    bool IsResolved { get; }

    bool IsActive { get; }

    /// <summary>
    /// Subscription expiry of the authorized tenant, when one is configured.
    /// Business rule: <c>null</c> means the tenant has NO expiration and is never blocked by
    /// expiry; a non-null value in the past means the tenant is expired (HTTP 402).
    /// </summary>
    DateTime? ValidUpTo { get; }

    /// <summary>Marks the currently resolved tenant as authorized for this request, establishing the verified tenant context.</summary>
    void AuthorizeTenant();
}
