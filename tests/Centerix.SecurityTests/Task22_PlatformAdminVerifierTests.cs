namespace Centerix.SecurityTests;

using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// T22 — PlatformAdminVerifier unit tests. The verifier is the SINGLE authoritative
/// PlatformAdmin decision: the JWT role claim is a necessary-but-not-sufficient hint; the
/// Identity store (role membership + account state) is re-validated on every call.
/// Every uncertain state must resolve to DENY (fail-closed).
/// </summary>
public class Task22_PlatformAdminVerifierTests
{
    [Fact]
    public async Task ValidPlatformAdmin_Allowed()
    {
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "pa@test.com");
        await EnsureRoleAsync(roleManager, "PlatformAdmin");
        await userManager.AddToRoleAsync(user, "PlatformAdmin");

        Assert.True(await verifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));
    }

    [Fact]
    public async Task ValidNonPlatformUser_Denied()
    {
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "ta@test.com");
        await EnsureRoleAsync(roleManager, "TenantAdmin");
        await userManager.AddToRoleAsync(user, "TenantAdmin");

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(user.Id, "TenantAdmin")));
    }

    [Fact]
    public async Task TenantAdmin_WithForgedPlatformAdminClaim_Denied()
    {
        // The claim says PlatformAdmin, but the authoritative store says TenantAdmin → DENY.
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "ta2@test.com");
        await EnsureRoleAsync(roleManager, "TenantAdmin");
        await userManager.AddToRoleAsync(user, "TenantAdmin");

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));
    }

    [Fact]
    public async Task TenantUser_WithForgedPlatformAdminClaim_Denied()
    {
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "tu@test.com");
        await EnsureRoleAsync(roleManager, "TenantUser");
        await userManager.AddToRoleAsync(user, "TenantUser");

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));
    }

    [Fact]
    public async Task RevokedPlatformAdmin_StaleClaim_Denied()
    {
        // Token was legitimately issued while the user was a PlatformAdmin; the role was then
        // revoked in the identity store. The unexpired token must stop working immediately.
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "revoked@test.com");
        await EnsureRoleAsync(roleManager, "PlatformAdmin");
        await userManager.AddToRoleAsync(user, "PlatformAdmin");

        Assert.True(await verifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));

        await userManager.RemoveFromRoleAsync(user, "PlatformAdmin");

        // A new verifier instance = a new request scope (the cache is per-request by design).
        var nextRequestVerifier = new PlatformAdminVerifier(
            userManager, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformAdminVerifier>.Instance);
        Assert.False(await nextRequestVerifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));
    }

    [Fact]
    public async Task LockedOutPlatformAdmin_Denied()
    {
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "locked@test.com");
        await EnsureRoleAsync(roleManager, "PlatformAdmin");
        await userManager.AddToRoleAsync(user, "PlatformAdmin");
        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(user.Id, "PlatformAdmin")));
    }

    [Fact]
    public async Task UnknownUser_Denied()
    {
        var (_, _, verifier, _) = CreateSut();

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(Guid.NewGuid().ToString(), "PlatformAdmin")));
    }

    [Fact]
    public async Task DbRoleWithoutClaim_Denied()
    {
        // The identity store grants the role but the presented principal lacks the claim:
        // the claim is a necessary condition (fail-closed; re-login picks up the new role).
        var (userManager, _, verifier, roleManager) = CreateSut();
        var user = await CreateUserAsync(userManager, "noclaim@test.com");
        await EnsureRoleAsync(roleManager, "PlatformAdmin");
        await userManager.AddToRoleAsync(user, "PlatformAdmin");

        Assert.False(await verifier.IsPlatformAdminAsync(Principal(user.Id)));
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_Denied()
    {
        var (_, _, verifier, _) = CreateSut();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
             new Claim(ClaimTypes.Role, "PlatformAdmin")])); // no authenticationType → not authenticated

        Assert.False(await verifier.IsPlatformAdminAsync(anonymous));
    }

    [Fact]
    public async Task LookupFailure_FailsClosed()
    {
        var logger = Substitute.For<ILogger<PlatformAdminVerifier>>();
        var verifier = new PlatformAdminVerifier(
            new UserManager<IdentityUser>(
                new ThrowingUserStore(), null, null, null, null, null, null, null, null),
            logger);

        var result = await verifier.IsPlatformAdminAsync(Principal(Guid.NewGuid().ToString(), "PlatformAdmin"));

        Assert.False(result);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static (UserManager<IdentityUser>, AppDbContext, PlatformAdminVerifier, RoleManager<ApplicationRole>) CreateSut()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"T22Verifier_{Guid.NewGuid():N}")
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        var db = new AppDbContext(options, mediator, currentTenant);
        services.AddSingleton(db);

        services.AddIdentityCore<IdentityUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        var sp = services.BuildServiceProvider();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var logger = sp.GetRequiredService<ILogger<PlatformAdminVerifier>>();

        return (userManager, db, new PlatformAdminVerifier(userManager, logger), roleManager);
    }

    private static async Task<IdentityUser> CreateUserAsync(UserManager<IdentityUser> userManager, string email)
    {
        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task EnsureRoleAsync(RoleManager<ApplicationRole> roleManager, string name)
    {
        if (!await roleManager.RoleExistsAsync(name))
        {
            await roleManager.CreateAsync(new ApplicationRole(name)
            {
                Code = name,
                DisplayName = name,
                IsSystem = true,
                NormalizedName = name.ToUpperInvariant()
            });
        }
    }

    private static ClaimsPrincipal Principal(string userId, params string[] roles) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }
                .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))),
            authenticationType: "Test"));

    private sealed class ThrowingUserStore : IUserStore<IdentityUser>
    {
        private static Exception Boom() => new InvalidOperationException("Simulated identity store failure");

        public Task<IdentityUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw Boom();
        public Task<IdentityUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw Boom();
        public Task<IdentityResult> CreateAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task<IdentityResult> DeleteAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task<IdentityResult> UpdateAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task<string?> GetNormalizedUserNameAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task<string> GetUserIdAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task<string?> GetUserNameAsync(IdentityUser user, CancellationToken cancellationToken) => throw Boom();
        public Task SetNormalizedUserNameAsync(IdentityUser user, string? normalizedName, CancellationToken cancellationToken) => throw Boom();
        public Task SetUserNameAsync(IdentityUser user, string? userName, CancellationToken cancellationToken) => throw Boom();
        public void Dispose() { }
    }
}
