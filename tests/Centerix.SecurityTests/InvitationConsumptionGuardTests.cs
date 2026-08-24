using Centerix.API.Infrastructure;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Guard behavior for the two invitation-consumption endpoints: the caller BY DEFINITION has no
/// TenantMembership yet, so the guard must waive the membership precondition for exactly these
/// routes while still enforcing it everywhere else.
/// </summary>
public class InvitationConsumptionGuardTests : IDisposable
{
    private readonly string _dbName = $"InvGuardTest_{Guid.NewGuid():N}";
    private readonly ServiceProvider _serviceProvider;
    private readonly RequestDelegate _next = Substitute.For<RequestDelegate>();

    public InvitationConsumptionGuardTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<MediatR.IMediator>());
        services.AddSingleton(Substitute.For<ICurrentTenant>());
        var localizer = Substitute.For<ILocalizer>();
        localizer.Translate(Arg.Any<string>()).Returns(c => c.Arg<string>());
        services.AddSingleton(localizer);
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose() => _serviceProvider.Dispose();

    private (TenantGuardMiddleware Middleware, DefaultHttpContext Context, ICurrentTenant CurrentTenant) CreateSut(
        string method, string path, bool isAuthenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.RequestServices = _serviceProvider;

        if (isAuthenticated)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "invitee-1")], "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }

        var currentTenant = Substitute.For<ICurrentTenant>();
        return (new TenantGuardMiddleware(_next), context, currentTenant);
    }

    [Theory]
    [InlineData("/api/invitations/register")]
    [InlineData("/api/invitations/abc123/accept")]
    public async Task InvitationConsumptionEndpoints_BypassMembershipPrecondition(string path)
    {
        // Authenticated but with NO membership anywhere.
        var (middleware, context, currentTenant) = CreateSut("POST", path, isAuthenticated: true);

        await middleware.InvokeAsync(context, currentTenant, DbContext());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task RegisterEndpoint_Unauthenticated_PassesThroughToAuthorization()
    {
        var (middleware, context, currentTenant) = CreateSut("POST", "/api/invitations/register", isAuthenticated: false);

        await middleware.InvokeAsync(context, currentTenant, DbContext());

        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    [Theory]
    [InlineData("GET", "/api/invitations")]                       // list → permission + membership required
    [InlineData("POST", "/api/invitations")]                      // create → permission + membership required
    [InlineData("POST", "/api/invitations/GUID/revoke")]          // revoke → permission + membership required
    [InlineData("GET", "/api/invitations/abc123/accept")]         // wrong verb → not a consumption route
    public async Task OtherInvitationRoutes_StillRequireMembership(string method, string path)
    {
        var (middleware, context, currentTenant) = CreateSut(method, path, isAuthenticated: true);
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("tenant-x");

        await middleware.InvokeAsync(context, currentTenant, DbContext());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        await _next.DidNotReceive()(Arg.Any<HttpContext>());
    }

    private AppDbContext DbContext()
    {
        // Intentionally NOT disposing the scope: the middleware uses the context after this
        // method returns; disposal belongs to the container (same pattern as existing suites).
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }
}
