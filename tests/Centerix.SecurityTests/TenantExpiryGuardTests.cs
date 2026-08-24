using Centerix.API.Infrastructure;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Tests for the explicit tenant-expiry business rule:
///   ValidUpTo == null  → tenant has NO expiration and is never blocked by expiry;
///   ValidUpTo in past  → HTTP 402 Payment Required.
/// </summary>
public class TenantExpiryGuardTests : IDisposable
{
    private readonly string _dbName = $"ExpiryTest_{Guid.NewGuid():N}";
    private readonly ServiceProvider _serviceProvider;
    private readonly RequestDelegate _next = Substitute.For<RequestDelegate>();

    public TenantExpiryGuardTests()
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

    private async Task<(TenantGuardMiddleware Middleware, HttpContext Context, ICurrentTenant CurrentTenant)> CreateSutAsync(
        DateTime? validUpTo, string membershipStatus = nameof(TenantMembershipStatus.Active))
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/students";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        context.RequestServices = _serviceProvider;

        context.Request.Headers["tenant"] = "expiry-tenant";
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.IsResolved.Returns(true);
        currentTenant.ResolvedTenantId.Returns("expiry-tenant");
        currentTenant.TenantId.Returns("expiry-tenant"); // authorized value after AuthorizeTenant
        currentTenant.IsActive.Returns(true);
        currentTenant.ValidUpTo.Returns(validUpTo);

        // Seed an ACTIVE membership so the guard reaches the expiry check.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.TenantMemberships.Any(m => m.UserId == "user-1" && m.TenantId == "expiry-tenant"))
            {
                var membership = TenantMembership.Create("user-1", "expiry-tenant", "TenantUser", TenantMembershipStatus.Active);
                Assert.True(membership.IsSuccess);
                db.TenantMemberships.Add(membership.Value);
                await db.SaveChangesAsync();
            }
        }

        return (new TenantGuardMiddleware(_next), context, currentTenant);
    }

    [Fact]
    public async Task ValidUpTo_Null_TenantNeverExpires_CallsNext()
    {
        var (middleware, context, currentTenant) = await CreateSutAsync(validUpTo: null);

        await middleware.InvokeAsync(context, currentTenant, ScopeDbContext());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task ValidUpTo_InPast_Returns402()
    {
        var (middleware, context, currentTenant) =
            await CreateSutAsync(validUpTo: DateTime.UtcNow.AddDays(-1));

        await middleware.InvokeAsync(context, currentTenant, ScopeDbContext());

        Assert.Equal(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
        await _next.DidNotReceive()(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task ValidUpTo_InFuture_CallsNext()
    {
        var (middleware, context, currentTenant) =
            await CreateSutAsync(validUpTo: DateTime.UtcNow.AddYears(1));

        await middleware.InvokeAsync(context, currentTenant, ScopeDbContext());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        await _next.Received(1)(Arg.Any<HttpContext>());
    }

    /// <summary>
    /// Registry sentinel translation: CenterixTenantInfo stores non-nullable DateTime where
    /// MinValue means "no expiry". CurrentTenant must surface that as null.
    /// </summary>
    [Fact]
    public void CurrentTenant_MinValueSentinel_TranslatesToNull()
    {
        var tenantInfo = new CenterixTenantInfo { ValidUpTo = DateTime.MinValue };
        var multiTenantContext = Substitute.For<Finbuckle.MultiTenant.Abstractions.IMultiTenantContext<CenterixTenantInfo>>();
        multiTenantContext.TenantInfo.Returns(tenantInfo);

        var accessor = Substitute.For<Finbuckle.MultiTenant.Abstractions.IMultiTenantContextAccessor<CenterixTenantInfo>>();
        accessor.MultiTenantContext.Returns(multiTenantContext);

        var currentTenant = new Centerix.Infrastructure.Common.CurrentTenant(accessor);
        Assert.Null(currentTenant.ValidUpTo);

        var future = DateTime.UtcNow.AddDays(3);
        tenantInfo.ValidUpTo = future;
        Assert.Equal(future, currentTenant.ValidUpTo);
    }

    private AppDbContext ScopeDbContext()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db;
    }
}
