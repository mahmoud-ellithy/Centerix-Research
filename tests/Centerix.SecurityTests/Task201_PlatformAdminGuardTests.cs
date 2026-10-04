namespace Centerix.SecurityTests;

using MediatR;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// Task 20.1 — Dedicated unit tests for PlatformAdminGuard.
/// Verifies all four authorization outcomes: PlatformAdmin, TenantAdmin, TenantUser, Unauthenticated.
/// T22: the guard now delegates the effective decision to IPlatformAdminVerifier (DB-backed);
/// the JWT role claim alone is never sufficient.
/// </summary>
public class Task201_PlatformAdminGuardTests
{
    // ==================================================================
    // Outcome 1: Platform Admin (DB-verified) → allowed
    // ==================================================================

    [Fact]
    public async Task PlatformAdmin_IsAllowed()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);

        var verifier = Substitute.For<IPlatformAdminVerifier>();
        verifier.IsPlatformAdminAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var guard = new Centerix.Infrastructure.Common.PlatformAdminGuard(currentUser, httpContextAccessor, verifier);
        var result = await guard.EnsurePlatformAdminAsync();

        Assert.True(result.IsSuccess);
    }

    // ==================================================================
    // Outcome 2: Authenticated but verifier denies (TenantAdmin) → forbidden
    // ==================================================================

    [Fact]
    public async Task TenantAdmin_IsForbidden()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);

        var guard = CreateDeniedGuard(currentUser);
        var result = await guard.EnsurePlatformAdminAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);
        Assert.Equal(Centerix.Domain.Common.Results.ErrorKind.Forbidden, result.Errors!.First().Type);
    }

    // ==================================================================
    // Outcome 3: Authenticated but verifier denies (TenantUser) → forbidden
    // ==================================================================

    [Fact]
    public async Task TenantUser_IsForbidden()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);

        var guard = CreateDeniedGuard(currentUser);
        var result = await guard.EnsurePlatformAdminAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);
        Assert.Equal(Centerix.Domain.Common.Results.ErrorKind.Forbidden, result.Errors!.First().Type);
    }

    // ==================================================================
    // Outcome 4: Unauthenticated → unauthorized (verifier never consulted)
    // ==================================================================

    [Fact]
    public async Task Unauthenticated_IsUnauthorized()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(false);

        var guard = CreateDeniedGuard(currentUser);
        var result = await guard.EnsurePlatformAdminAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);
        Assert.Equal(Centerix.Domain.Common.Results.ErrorKind.Unauthorized, result.Errors!.First().Type);
    }

    // ==================================================================
    // Outcome 5: no HttpContext principal at all → forbidden (fail-closed)
    // ==================================================================

    [Fact]
    public async Task NoHttpContext_IsForbidden()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var verifier = Substitute.For<IPlatformAdminVerifier>();

        var guard = new Centerix.Infrastructure.Common.PlatformAdminGuard(currentUser, httpContextAccessor, verifier);
        var result = await guard.EnsurePlatformAdminAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(Centerix.Domain.Common.Results.ErrorKind.Forbidden, result.Errors!.First().Type);
        await verifier.DidNotReceiveWithAnyArgs().IsPlatformAdminAsync(default!, default);
    }

    private static Centerix.Infrastructure.Common.PlatformAdminGuard CreateDeniedGuard(ICurrentUser currentUser)
    {
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);

        var verifier = Substitute.For<IPlatformAdminVerifier>();
        verifier.IsPlatformAdminAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(false);

        return new Centerix.Infrastructure.Common.PlatformAdminGuard(currentUser, httpContextAccessor, verifier);
    }

    // ==================================================================
    // AllocatePaymentHandler: non-platform caller is rejected by the guard
    // ==================================================================

    [Fact]
    public async Task AllocatePayment_NonPlatformCaller_IsRejected()
    {
        using var db = CreateDbContext("tenant-alloc");
        db.StampAddedTenantIds("tenant-alloc");
        var auditWriter = Substitute.For<IAuditWriter>();
        var reconciliation = NullSubscriptionReconciliationService.Instance;

        var platformGuard = Substitute.For<IPlatformAdminGuard>();
        platformGuard.EnsurePlatformAdminAsync(Arg.Any<CancellationToken>()).Returns(
            Centerix.Domain.Common.Results.Error.Forbidden("Platform.AdminRequired",
                "This operation is restricted to platform administrators."));

        var handler = new AllocatePaymentHandler(
            db, auditWriter, reconciliation, platformGuard);

        var result = await handler.Handle(
            new AllocatePaymentCommand(Guid.NewGuid(), Guid.NewGuid(), 1000m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Platform.AdminRequired", result.Errors!.First().Code);
    }

    [Fact]
    public async Task AllocatePayment_PlatformAdmin_IsAllowed()
    {
        using var db = CreateDbContext("tenant-alloc2");
        db.StampAddedTenantIds("tenant-alloc2");
        var auditWriter = Substitute.For<IAuditWriter>();
        var reconciliation = NullSubscriptionReconciliationService.Instance;

        var platformGuard = Substitute.For<IPlatformAdminGuard>();
        platformGuard.EnsurePlatformAdminAsync(Arg.Any<CancellationToken>()).Returns(Centerix.Domain.Common.Results.Result.Updated);

        var handler = new AllocatePaymentHandler(
            db, auditWriter, reconciliation, platformGuard);

        // The guard allows through, but the payment/invoice don't exist so it
        // returns NotFound — NOT a Forbidden error. This proves the guard passed.
        var result = await handler.Handle(
            new AllocatePaymentCommand(Guid.NewGuid(), Guid.NewGuid(), 1000m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Payment.NotFound", result.Errors!.First().Code);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"Task201Guard_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? "tenant-test");
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }
}
