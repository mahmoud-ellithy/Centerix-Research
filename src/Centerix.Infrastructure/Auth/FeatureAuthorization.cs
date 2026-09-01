namespace Centerix.Infrastructure.Auth;

using Centerix.Application.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Authorization requirement expressing "the CURRENT TENANT's subscription includes feature X".
/// Deliberately separate from PermissionRequirement (user capability). The requirement carries
/// the feature code; resolution happens server-side against the subscription entitlement
/// snapshot — never from JWT claims.
/// </summary>
public class FeatureRequirement(string featureCode) : IAuthorizationRequirement
{
    public string FeatureCode { get; } = featureCode;
}

/// <summary>
/// Fails-closed handler for <see cref="FeatureRequirement"/>: access is granted ONLY when an
/// explicit platform admin bypass applies or the tenant context resolves to an ACTIVE
/// subscription containing the feature. Any ambiguity (no tenant, unknown feature, expired or
/// suspended subscription) results in denial by simply not calling context.Succeed.
/// </summary>
public class FeatureAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    IFeatureAccessService featureAccess) : AuthorizationHandler<FeatureRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FeatureRequirement requirement)
    {
        // Platform staff manage tenants regardless of their commercial state; feature gates are
        // a COMMERCIAL constraint on tenants, not on platform administrators.
        if (context.User.IsInRole("PlatformAdmin"))
        {
            context.Succeed(requirement);
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return; // No request context → fail closed.

        var currentTenant = httpContext.RequestServices.GetService(typeof(ICurrentTenant)) as ICurrentTenant;
        if (currentTenant is null || !currentTenant.IsResolved || string.IsNullOrEmpty(currentTenant.TenantId))
            return; // No verified tenant → fail closed.

        var hasFeature = await featureAccess.HasFeatureAsync(
            currentTenant.TenantId,
            requirement.FeatureCode,
            httpContext.RequestAborted);

        if (hasFeature)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Endpoint metadata: requires the tenant's subscription to include <paramref name="featureCode"/>.
/// Usage: [RequireFeature(Features.Catalog.StudentManagement)]. Composes with [HasPermission] —
/// BOTH the user permission AND the tenant feature must pass.
/// </summary>
public static class FeatureCodes
{
    /// <summary>Core student-management capability (gates Students module writes).</summary>
    public const string StudentManagement = "Students";

    /// <summary>Core teacher-management capability (gates Teachers/Subjects module writes and salary configs).</summary>
    public const string TeacherManagement = "Teachers";
}

public class RequireFeatureAttribute(string featureCode) : AuthorizeAttribute(PolicyName(featureCode))
{
    public string FeatureCode { get; } = featureCode;

    public static string PolicyName(string featureCode) => $"Feature:{featureCode}";
}
