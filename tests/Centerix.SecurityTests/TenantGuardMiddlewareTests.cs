using Centerix.API.Infrastructure;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Centerix.SecurityTests;

public class TenantGuardMiddlewareTests : IDisposable
{
    private readonly string _dbName = $"GuardTest_{Guid.NewGuid():N}";
    private readonly ServiceProvider _serviceProvider;
    private readonly RequestDelegate _next = Substitute.For<RequestDelegate>();

    public TenantGuardMiddlewareTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<MediatR.IMediator>());
        services.AddSingleton(Substitute.For<ICurrentTenant>());
        services.AddSingleton<ILocalizer>(Substitute.For<ILocalizer>());
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private (TenantGuardMiddleware middleware, DefaultHttpContext context, ICurrentTenant currentTenant) CreateSut(
        string path = "/api/students",
        string? tenantHeader = null,
        bool isAuthenticated = true,
        string? userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.RequestServices = _serviceProvider;

        if (tenantHeader != null)
        {
            context.Request.Headers["tenant"] = tenantHeader;
        }

        if (isAuthenticated)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId ?? string.Empty)], "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }

        var currentTenant = Substitute.For<ICurrentTenant>();

        var middleware = new TenantGuardMiddleware(_next);
        return (middleware, context, currentTenant);
    }

    private async Task SeedMembershipAsync(string userId, string tenantId, TenantMembershipStatus status)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!context.TenantMemberships.Any(m => m.UserId == userId && m.TenantId == tenantId))
        {
            var membership = TenantMembership.Create(userId, tenantId, "TenantUser", status);
            if (membership.IsSuccess)
            {
                context.TenantMemberships.Add(membership.Value);
                await context.SaveChangesAsync();
            }
        }
    }

    private async Task<AppDbContext> GetDbContextAsync()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    // ============================================================
    // TEST: Bypass paths (scalar, openapi, swagger) pass through
    // ============================================================
    [Theory]
    [InlineData("/scalar/v1")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    public async Task InvokeAsync_BypassPaths_CallsNext(string path)
    {
        var (middleware, context, currentTenant) = CreateSut(path, isAuthenticated: false);

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    // ============================================================
    // TEST: Unauthenticated requests pass through (anonymous endpoints)
    // ============================================================
    [Fact]
    public async Task InvokeAsync_Unauthenticated_CallsNext()
    {
        var (middleware, context, currentTenant) = CreateSut("/api/auth/login", isAuthenticated: false);

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    // ============================================================
    // TEST: No tenant header on tenant-scoped endpoint → 403
    // ============================================================
    [Fact]
    public async Task InvokeAsync_NoTenantHeader_Returns403()
    {
        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: null);
        currentTenant.IsResolved.Returns(false);

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Tenant resolved but no membership → 403
    // ============================================================
    [Fact]
    public async Task InvokeAsync_NoMembership_Returns403()
    {
        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Membership exists but suspended → 403
    // ============================================================
    [Fact]
    public async Task InvokeAsync_SuspendedMembership_Returns403()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Suspended);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Active membership → calls AuthorizeTenant
    // ============================================================
    [Fact]
    public async Task InvokeAsync_ActiveMembership_CallsAuthorizeTenant()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");
        currentTenant.IsActive.Returns(true);
        currentTenant.ValidUpTo.Returns(DateTime.UtcNow.AddYears(1));

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.Received(1).AuthorizeTenant();
        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    // ============================================================
    // TEST: Active membership but tenant deactivated → 403
    // ============================================================
    [Fact]
    public async Task InvokeAsync_ActiveMembership_TenantDeactivated_Returns403()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");
        currentTenant.IsActive.Returns(false);
        currentTenant.ValidUpTo.Returns(DateTime.UtcNow.AddYears(1));

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.Received(1).AuthorizeTenant();
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Active membership but tenant expired → 402
    // ============================================================
    [Fact]
    public async Task InvokeAsync_ActiveMembership_TenantExpired_Returns402()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");
        currentTenant.IsActive.Returns(true);
        currentTenant.ValidUpTo.Returns(DateTime.UtcNow.AddDays(-1));

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.Received(1).AuthorizeTenant();
        Assert.Equal(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Cross-tenant attempt: User A has Tenant A membership,
    //       tries Tenant B → 403
    // ============================================================
    [Fact]
    public async Task InvokeAsync_CrossTenantAttempt_Returns403()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-b");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-b");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.DidNotReceive().AuthorizeTenant();
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Multi-tenant user: User B has A and B, requests B → allowed
    // ============================================================
    [Fact]
    public async Task InvokeAsync_MultiTenantUser_RequestsAuthorizedTenant_Allowed()
    {
        await SeedMembershipAsync("user-2", "tenant-a", TenantMembershipStatus.Active);
        await SeedMembershipAsync("user-2", "tenant-b", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-b", userId: "user-2");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-b");
        currentTenant.IsActive.Returns(true);
        currentTenant.ValidUpTo.Returns(DateTime.UtcNow.AddYears(1));

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.Received(1).AuthorizeTenant();
        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    // ============================================================
    // TEST: Revoked membership → denied
    // ============================================================
    [Fact]
    public async Task InvokeAsync_RevokedMembership_Returns403()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Revoked);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: Invited membership (not active) → denied
    // ============================================================
    [Fact]
    public async Task InvokeAsync_InvitedMembership_Returns403()
    {
        await SeedMembershipAsync("user-1", "tenant-a", TenantMembershipStatus.Invited);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-a");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-a");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // ============================================================
    // TEST: User A tenant A + user B requests with tenant B → denied
    // ============================================================
    [Fact]
    public async Task InvokeAsync_UserB_RequestsTenantB_OnlyHasTenantA_Returns403()
    {
        await SeedMembershipAsync("user-2", "tenant-a", TenantMembershipStatus.Active);

        var (middleware, context, currentTenant) = CreateSut("/api/students", tenantHeader: "tenant-b", userId: "user-2");
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-b");

        await middleware.InvokeAsync(context, currentTenant, await GetDbContextAsync());

        currentTenant.DidNotReceive().AuthorizeTenant();
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
