namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// T22 — SQL Server proof that the PlatformAdmin decision is backed by the real relational
/// identity store. No InMemory substitution: a stale, forged or locked PlatformAdmin identity
/// must be denied against SQL Server, and only a live DB role membership authorizes.
/// </summary>
[Collection("SqlServerIntegration")]
public class Task22_PlatformAdminSqlServerTests
{
    private readonly SqlServerIntegrationFactory _env;

    public Task22_PlatformAdminSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ValidPlatformAdmin_PlatformRead_Allowed()
    {
        var user = await CreateUserAsync("sql_pa");
        await AddToRoleAsync(user, "PlatformAdmin");

        var response = await GetPlansAsync(user, "PlatformAdmin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_RevokedPlatformAdmin_StaleToken_Denied()
    {
        var user = await CreateUserAsync("sql_revoked");
        await AddToRoleAsync(user, "PlatformAdmin");

        var token = _env.Factory.GenerateTestToken(user.Id, user.Email!, ["PlatformAdmin"]);
        var before = await SendPlansAsync(token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var stored = await userManager.FindByIdAsync(user.Id);
            await userManager.RemoveFromRoleAsync(stored!, "PlatformAdmin");
        }

        var after = await SendPlansAsync(token);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_LockedOutPlatformAdmin_Denied()
    {
        var user = await CreateUserAsync("sql_locked");
        await AddToRoleAsync(user, "PlatformAdmin");

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var stored = await userManager.FindByIdAsync(user.Id);
            await userManager.SetLockoutEnabledAsync(stored!, true);
            await userManager.SetLockoutEndDateAsync(stored!, DateTimeOffset.UtcNow.AddHours(1));
        }

        var response = await GetPlansAsync(user, "PlatformAdmin");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ForgedPlatformAdminClaim_WithoutDbRole_Denied()
    {
        var user = await CreateUserAsync("sql_forged");
        await AddToRoleAsync(user, "TenantAdmin");

        var response = await GetPlansAsync(user, "PlatformAdmin");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_TenantAdmin_RealToken_PlatformRead_Denied()
    {
        var user = await CreateUserAsync("sql_tenantadmin");
        await AddToRoleAsync(user, "TenantAdmin");

        var response = await GetPlansAsync(user, "TenantAdmin");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<IdentityUser> CreateUserAsync(string prefix)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var email = $"{prefix}_{Guid.NewGuid():N}@t22sql.test";
        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task AddToRoleAsync(IdentityUser user, string roleName)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new ApplicationRole(roleName)
            {
                Code = roleName,
                DisplayName = roleName,
                IsSystem = true,
                NormalizedName = roleName.ToUpperInvariant()
            });
        }

        var stored = await userManager.FindByIdAsync(user.Id);
        await userManager.AddToRoleAsync(stored!, roleName);
    }

    private async Task<HttpResponseMessage> GetPlansAsync(IdentityUser user, params string[] roles)
    {
        var token = _env.Factory.GenerateTestToken(user.Id, user.Email!, roles);
        return await SendPlansAsync(token);
    }

    private async Task<HttpResponseMessage> SendPlansAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _env.Client.SendAsync(request);
    }
}
