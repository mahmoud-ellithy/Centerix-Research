using Centerix.API.Infrastructure;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Centerix.SecurityTests;

// REV-019: TenantGuardMiddleware previously wrapped its membership check in
// `if (!string.IsNullOrEmpty(userId)) { ... }` and STILL called AuthorizeTenant()
// afterwards. An authenticated request without a NameIdentifier therefore fell through
// into the authorized path — the very gap the new guard closes.
public class Rev019TenantGuardFailClosedTests : IDisposable
{
    private readonly string _dbName = $"Rev019GuardTest_{Guid.NewGuid():N}";
    private readonly ServiceProvider _serviceProvider;
    private readonly RequestDelegate _next = Substitute.For<RequestDelegate>();

    public Rev019TenantGuardFailClosedTests()
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

    private (TenantGuardMiddleware Middleware, DefaultHttpContext Context, ICurrentTenant CurrentTenant)
        CreateSut(bool authenticated, string? userId, bool hasTenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/students";
        context.Request.Method = "GET";
        context.RequestServices = _serviceProvider;

        if (hasTenant)
        {
            context.Request.Headers["tenant"] = "tenant-rev019";
        }

        if (authenticated)
        {
            // Authenticated without ANY claim set: simulates a misconfigured JWT,
            // a stripped ClaimsPrincipal, or a test injecting ClaimsPrincipal with
            // an empty ClaimTypes.NameIdentifier value (matches the original bug path).
            if (userId is not null)
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId)],
                    "TestAuth");
                context.User = new ClaimsPrincipal(identity);
            }
            else
            {
                var identity = new ClaimsIdentity([], "TestAuth");
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, string.Empty));
                context.User = new ClaimsPrincipal(identity);
            }
        }

        var currentTenant = Substitute.For<ICurrentTenant>();
        var middleware = new TenantGuardMiddleware(_next);

        if (hasTenant)
        {
            currentTenant.IsResolved.Returns(true);
            currentTenant.ResolvedTenantId.Returns("tenant-rev019");
        }

        return (middleware, context, currentTenant);
    }

    private AppDbContext GetDbContext()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    // ─────────────────────────────────────────────────────────────────
    // Bug-scenario assertions.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authenticated_NoNameIdentifier_ResolvedTenant_Returns403_AndNeverCallsAuthorizeTenant()
    {
        var (middleware, context, currentTenant) = CreateSut(
            authenticated: true,
            userId: null,
            hasTenant: true);

        await middleware.InvokeAsync(context, currentTenant, GetDbContext());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        currentTenant.DidNotReceive().AuthorizeTenant();
        await _next.DidNotReceive()(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task Authenticated_EmptyNameIdentifier_ResolvedTenant_Returns403()
    {
        var (middleware, context, currentTenant) = CreateSut(
            authenticated: true,
            userId: string.Empty,
            hasTenant: true);

        await middleware.InvokeAsync(context, currentTenant, GetDbContext());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        currentTenant.DidNotReceive().AuthorizeTenant();
    }

    [Fact]
    public async Task Authenticated_NullNameIdentifier_DoesNotTouchMembershipQuery()
    {
        var (middleware, context, currentTenant) = CreateSut(
            authenticated: true,
            userId: null,
            hasTenant: true);

        // The previous implementation would still hit the membership query (with userId = empty),
        // find no row, and then call AuthorizeTenant() unconditionally. The new implementation
        // returns 403 BEFORE the membership query, ensuring the tenant is never authorized.
        await middleware.InvokeAsync(context, currentTenant, GetDbContext());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        currentTenant.DidNotReceive().AuthorizeTenant();
    }

    // ─────────────────────────────────────────────────────────────────
    // Regression guard: the happy path still calls AuthorizeTenant()
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authenticated_WithNameIdentifier_AndActiveMembership_CallsAuthorizeTenant()
    {
        // Seed an active membership for user-id-1 in tenant-rev019.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(
                "user-id-1", "tenant-rev019", "TenantUser", TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        var (middleware, context, currentTenant) = CreateSut(
            authenticated: true,
            userId: "user-id-1",
            hasTenant: true);
        currentTenant.IsActive.Returns(true);
        currentTenant.ValidUpTo.Returns(DateTime.UtcNow.AddYears(1));

        await middleware.InvokeAsync(context, currentTenant, GetDbContext());

        currentTenant.Received(1).AuthorizeTenant();
        await _next.Received(1)(Arg.Any<HttpContext>());
    }
}
