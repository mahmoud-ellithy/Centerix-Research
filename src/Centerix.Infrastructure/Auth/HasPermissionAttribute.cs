using Microsoft.AspNetCore.Authorization;

namespace Centerix.Infrastructure.Auth;

/// <summary>
/// Requires the named permission code (resolved per-request by
/// <see cref="PermissionAuthorizationHandler"/>, never from the token).
/// <para>
/// <b>AllowMultiple</b>: an endpoint may need MORE THAN ONE permission — a tenant-context key and
/// a platform key, for example <c>TenantCredits.Create</c> (which tenant this request may act in)
/// plus <c>PlatformCredits.Mint</c> (whether the caller may mint balance at all). ASP.NET Core's
/// <c>AuthorizationPolicy.CombineAsync</c> turns every <c>IAuthorizeData</c> on the endpoint into a
/// requirement of ONE combined policy, so the action is reachable only when ALL of them succeed.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
    public string Permission => Policy!;
}
