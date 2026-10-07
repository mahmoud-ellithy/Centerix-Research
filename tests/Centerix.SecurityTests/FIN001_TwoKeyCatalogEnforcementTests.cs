using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// F4 — the FIN-001 two-key flag at the authorization choke point.
/// <para>
/// Vulnerability: the second key for credit minting was enforced ONLY by two independent,
/// easy-to-drop mechanisms — the endpoint's second <c>[HasPermission]</c> attribute and the
/// handler's own <c>IPlatformAdminGuard</c>. Nothing at the single place every
/// <c>PermissionRequirement</c> passes through (<c>PermissionAuthorizationHandler</c>) knew that
/// <c>TenantCredits.Create</c> demands platform authority, so a future endpoint carrying only
/// the tenant key (or a refactor dropping either attribute) would silently become
/// tenant-permission-only — mintable by a plain TenantAdmin membership.
/// </para>
/// <para>
/// Invariant under test: a catalog code flagged <c>RequiresPlatformAuthority</c> can never be
/// satisfied by a tenant-derived grant — the handler consults
/// <c>IPlatformAdminVerifier</c> first, and the flag is a GATE (deny-without-authority), never a
/// shortcut (authority alone does not grant: the tenant branch must still authorize).
/// </para>
/// </summary>
[Trait("Category", "FIN001")]
public class FIN001_TwoKeyCatalogEnforcementTests
{
    // ==================================================================
    // A. Catalog flag exactness
    // ==================================================================

    [Fact]
    public void ExactlyOneCatalogCode_CarriesTheRequiresPlatformAuthorityFlag()
    {
        var flagged = PermissionCatalog.All
            .Where(e => e.RequiresPlatformAuthority)
            .Select(e => e.Code)
            .ToList();

        Assert.Equal(new[] { "TenantCredits.Create" }, flagged);
        Assert.True(PermissionCatalog.RequiresPlatformAuthority("TenantCredits.Create"));
    }

    [Theory]
    [InlineData("TenantCredits.Read")]
    [InlineData("TenantCredits.Apply")]
    [InlineData("PlatformCredits.Mint")]
    [InlineData("Students.Read")]
    [InlineData(null)]
    [InlineData("Not.A.Real.Code")]
    public void NonFlaggedCodes_AreNotReported(string? code)
    {
        Assert.False(PermissionCatalog.RequiresPlatformAuthority(code));
    }

    [Fact]
    public void TheFlaggedCode_RemainsTenantScoped()
    {
        // The flag ADDS a platform gate to a tenant-scoped permission; it must not move the code
        // out of tenant scope (the tenant branch is what proves WHICH tenant).
        var entry = PermissionCatalog.All.Single(e => e.Code == "TenantCredits.Create");
        Assert.Equal(PermissionScope.Tenant, entry.Scope);
        Assert.True(entry.RequiresPlatformAuthority);
    }

    // ==================================================================
    // B. Handler gate behavior (unit, NSubstitute boundaries)
    // ==================================================================

    [Fact]
    public async Task FlaggedCode_TenantGrantPresent_ButVerifierDenies_IsDenied()
    {
        // The defect shape: Items carries the tenant grant (middleware resolved it from a real
        // membership), yet the request must NOT be authorized without platform authority.
        var (handler, context, _) = Arrange(verifierResult: false);
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task FlaggedCode_TenantGrantPresent_AndVerifierApproves_IsAuthorized()
    {
        var (handler, context, _) = Arrange(verifierResult: true);
        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task UnflaggedTenantCode_IsAuthorizedFromTheTenantGrant_WithoutTheVerifier()
    {
        // Control: the flag must not become a blanket platform gate. A plain tenant permission
        // still authorizes from the tenant grant alone (verifier rigged to DENY everything).
        var (handler, context, _) = Arrange(
            verifierResult: false,
            permission: "TenantCredits.Read");
        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>
    /// Builds a handler with a live HttpContext whose Items carry the tenant permission list and
    /// whose RequestServices resolve ICurrentTenant (authorized) and IAppDbContext (a substitute
    /// that is resolved-but-unused while Items hit — the fast path returns before any query).
    /// </summary>
    private static (
        PermissionAuthorizationHandler Handler,
        AuthorizationHandlerContext Context,
        IPlatformAdminVerifier Verifier) Arrange(
        bool verifierResult,
        string permission = "TenantCredits.Create")
    {
        var verifier = Substitute.For<IPlatformAdminVerifier>();
        verifier.IsPlatformAdminAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(verifierResult);

        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.IsAuthorized.Returns(true);
        currentTenant.TenantId.Returns("f4-tenant");

        var services = new ServiceCollection();
        services.AddSingleton(currentTenant);
        services.AddSingleton(Substitute.For<IAppDbContext>());
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                authenticationType: "Test"))
        };
        httpContext.Items["TenantPermissions"] = new List<string> { "TenantCredits.Create", "TenantCredits.Read" };

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var logger = Substitute.For<ILogger<PermissionAuthorizationHandler>>();
        var handler = new PermissionAuthorizationHandler(accessor, verifier, logger);

        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            httpContext.User,
            resource: null);

        return (handler, context, verifier);
    }
}
