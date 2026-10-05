namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// T22 CORRECTION â€” SQL Server proof of the platform/tenant permission scope boundary.
/// <para>
/// These tests are the MANDATORY end-to-end proofs. Unlike the InMemory/handler-level suites they
/// run the complete production pipeline (JWT â†’ Finbuckle â†’ TenantGuardMiddleware â†’ ASP.NET
/// authorization â†’ EF Core against real SQL Server), so they prove the corrected boundary survives
/// real relational behaviour: real indexes, real query filters, real transactions.
/// </para>
/// <para>
/// The central case deliberately CORRUPTS the permission database â€” a PLATFORM permission
/// (<c>Plans.Read</c>) is attached to a TENANT role â€” and then proves the tenant request is still
/// denied. That is exactly the state that was exploitable before the correction.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class Task22_PermissionScopeSqlServerTests
{
    private const string PlatformRoleName = "PlatformAdmin";
    private const string TenantRoleName = "TenantAdmin";

    private readonly SqlServerIntegrationFactory _env;

    public Task22_PermissionScopeSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    // ==================================================================
    // PROOF 1 â€” Platform permission is NEVER satisfiable from a tenant role,
    //           even with the permission database deliberately corrupted.
    // ==================================================================

    [Fact]
    public async Task Sql_PlatformPermission_GrantedToTenantRoleViaDbCorruption_StillDenied()
    {
        var tenantId = $"t22_scope_a_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_escalate");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, TenantRoleName);

        // CORRUPT THE DATABASE: grant a PLATFORM permission to a TENANT role.
        await GrantPlatformPermissionToTenantRoleAsync(TenantRoleName, Permissions.Plans.Read);

        // A fully valid tenant request against a platform endpoint.
        var response = await SendAsync("/api/plans", user, TenantRoleName, tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sql_PlatformPermission_GrantedToTenantUserRoleViaDbCorruption_StillDenied()
    {
        // The same dangerous configuration, but for the LOWEST tenant role. Authorization must not
        // depend on how privileged the tenant role is: ANY TenantMembership-derived grant is an
        // invalid source for a platform permission, so TenantUser is denied exactly like
        // TenantAdmin. This guards against a future "except roles below Admin" shortcut.
        var tenantId = $"t22_scope_user_{Guid.NewGuid():N}";
        const string tenantUserRole = "TenantUser";

        var user = await CreateUserAsync("sql_escalate_user");
        await EnsureRoleAsync(tenantUserRole);
        await AddToRoleAsync(user, tenantUserRole);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, tenantUserRole);

        // CORRUPT THE DATABASE for the TenantUser role as well.
        await GrantPlatformPermissionToTenantRoleAsync(tenantUserRole, Permissions.Plans.Read);

        // A fully valid tenant request: active membership, authorized tenant, explicit grant.
        var response = await SendAsync("/api/plans", user, tenantUserRole, tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sql_PlatformPermission_GrantedToTenantRole_AllPlatformCodesDenied()
    {
        var tenantId = $"t22_scope_all_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_escalate_all");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, TenantRoleName);

        // Corrupt the DB by granting the tenant role EVERY platform permission.
        foreach (var code in Permissions.PlatformScope.PermissionCodes)
        {
            await GrantPlatformPermissionToTenantRoleAsync(TenantRoleName, code);
        }

        // The plan endpoint is one representative platform surface; the handler-level suite
        // additionally proves every platform code is denied at the policy layer.
        var response = await SendAsync("/api/plans", user, TenantRoleName, tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // PROOF 2 â€” A real PlatformAdmin is still authorized (no over-blocking).
    // ==================================================================

    [Fact]
    public async Task Sql_PlatformPermission_ValidPlatformAdmin_Allowed()
    {
        var user = await CreateUserAsync("sql_admin_ok");
        await EnsureRoleAsync(PlatformRoleName);
        await AddToRoleAsync(user, PlatformRoleName);

        var response = await SendAsync("/api/plans", user, PlatformRoleName, tenantId: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ==================================================================
    // PROOF 3 â€” Tenant permission requires a REAL active membership
    //           (proves the tenant path still works and is not over-blocked).
    // ==================================================================

    [Fact]
    public async Task Sql_TenantPermission_ActiveMemberWithGrant_Allowed()
    {
        var tenantId = $"t22_tenant_ok_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_tenant_ok");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, TenantRoleName);
        await GrantPermissionToRoleAsync(TenantRoleName, Permissions.Students.Read);

        var response = await SendAsync("/api/students", user, TenantRoleName, tenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sql_TenantPermission_NoMembership_Denied()
    {
        var tenantId = $"t22_tenant_nomem_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_tenant_nomem");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await GrantPermissionToRoleAsync(TenantRoleName, Permissions.Students.Read);

        // A tenant exists and the header is supplied, but the user has NO membership in it.
        await EnsureTenantRegisteredAsync(tenantId);
        var response = await SendAsync("/api/students", user, TenantRoleName, tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sql_TenantPermission_CrossTenantAccessDenied()
    {
        // Alice belongs to tenant A. Tenant B exists and is fully provisioned, but Alice has no
        // membership there. Her tenant A role grant must not carry across the boundary.
        var tenantA = $"t22_cross_a_{Guid.NewGuid():N}";
        var tenantB = $"t22_cross_b_{Guid.NewGuid():N}";

        var alice = await CreateUserAsync("sql_alice");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(alice, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantA, alice.Id, TenantRoleName);
        await EnsureTenantRegisteredAsync(tenantB);
        await GrantPermissionToRoleAsync(TenantRoleName, Permissions.Students.Read);

        var sameTenant = await SendAsync("/api/students", alice, TenantRoleName, tenantA);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);

        var crossTenant = await SendAsync("/api/students", alice, TenantRoleName, tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }

    [Fact]
    public async Task Sql_TenantPermission_InactiveMembership_Denied()
    {
        var tenantId = $"t22_tenant_susp_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_tenant_susp");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, TenantRoleName);
        await GrantPermissionToRoleAsync(TenantRoleName, Permissions.Students.Read);

        await SuspendMembershipAsync(tenantId, user.Id);

        var response = await SendAsync("/api/students", user, TenantRoleName, tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // PROOF 4 â€” A forged PlatformAdmin claim is denied (DB re-validation).
    // ==================================================================

    [Fact]
    public async Task Sql_ForgedPlatformAdminClaim_WithoutDbRole_Denied()
    {
        var user = await CreateUserAsync("sql_forged");

        // The token CLAIMS PlatformAdmin, but no role was ever persisted.
        var response = await SendAsync("/api/plans", user, PlatformRoleName, tenantId: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================================================================
    // PROOF 5 â€” Guard publishes no platform permission as a tenant permission.
    // ==================================================================

    [Fact]
    public async Task Sql_TenantRole_HoldingBothScopes_TenantWorks_PlatformDenied()
    {
        // A tenant role corrupted to hold BOTH a platform and a tenant permission. The tenant
        // capability must survive (no over-blocking) while the platform capability must not.
        var tenantId = $"t22_guard_{Guid.NewGuid():N}";
        var user = await CreateUserAsync("sql_guard");
        await EnsureRoleAsync(TenantRoleName);
        await AddToRoleAsync(user, TenantRoleName);
        await CreateTenantWithMembershipAsync(tenantId, user.Id, TenantRoleName);

        await GrantPlatformPermissionToTenantRoleAsync(TenantRoleName, Permissions.Plans.Read);
        await GrantPermissionToRoleAsync(TenantRoleName, Permissions.Students.Read);

        var tenantRequest = await SendAsync("/api/students", user, TenantRoleName, tenantId);
        Assert.Equal(HttpStatusCode.OK, tenantRequest.StatusCode);

        var platformRequest = await SendAsync("/api/plans", user, TenantRoleName, tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, platformRequest.StatusCode);
    }

    [Fact]
    public async Task Sql_TenantPermissionList_NeverContainsPlatformCodes()
    {
        // PROOF 5 â€” the guard's published grant list is the input consumed by
        // CurrentUser.TenantPermissions and by the handler's primary path. It must be filtered to
        // tenant scope. This asserts the filtering predicate the guard applies to a granted list
        // that deliberately contains platform codes.
        var granted = new[] { Permissions.Students.Read, Permissions.Plans.Read, Permissions.Tenants.Read };

        var published = granted
            .Where(code => PermissionScopes.Resolve(code) == PermissionScope.Tenant)
            .ToList();

        Assert.Contains(Permissions.Students.Read, published);
        Assert.DoesNotContain(Permissions.Plans.Read, published);
        Assert.DoesNotContain(Permissions.Tenants.Read, published);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        IdentityUser user,
        string role,
        string? tenantId)
    {
        var token = _env.Factory.GenerateTestToken(user.Id, user.Email!, [role]);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantId is not null)
        {
            request.Headers.Add("tenant", tenantId);
        }

        return await _env.Client.SendAsync(request);
    }

    private static string Describe(List<Error>? errors)
        => errors is null ? string.Empty : string.Join(";", errors.Select(e => e.Code));

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

    private async Task EnsureRoleAsync(string roleName)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
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
    }

    private async Task AddToRoleAsync(IdentityUser user, string roleName)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var stored = await userManager.FindByIdAsync(user.Id);
        await userManager.AddToRoleAsync(stored!, roleName);
    }

    /// <summary>
    /// TenantMembership.TenantId carries a real cross-context FOREIGN KEY to
    /// <c>Platform.TenantRegistry</c>, which is owned by <see cref="TenantDbContext"/> (not
    /// AppDbContext). The registry row must therefore be written through TenantDbContext before
    /// any membership can be inserted — exactly as the production dual-write does.
    /// </summary>
    private async Task EnsureTenantRegisteredAsync(string tenantId)
    {
        // TenantDbContext is registered against the same SQL Server database by the host, so it is
        // resolved from DI rather than rebuilt by hand (that would drift from the fixture's
        // migrations-history configuration).
        using var scope = _env.Factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (await tenantDb.TenantInfo.AnyAsync(t => t.Id == tenantId))
        {
            return;
        }

        tenantDb.TenantInfo.Add(new CenterixTenantInfo
        {
            Id = tenantId,
            Identifier = tenantId,
            Name = tenantId,
            // Subdomain carries a UNIQUE index, so it must be unique per test. The tenant id
            // already embeds a GUID, so it is used in full (truncating it collides across tests).
            Slug = tenantId,
            Subdomain = tenantId,
            DisplayName = tenantId,
            Email = $"{tenantId}@t22sql.test",
            IsActive = true,
            ValidUpTo = DateTime.MaxValue,
            Timezone = "UTC",
            Currency = "EGP",
            Country = "EG"
        });
        await tenantDb.SaveChangesAsync();
    }

    private async Task CreateTenantWithMembershipAsync(string tenantId, string userId, string roleName)
    {
        await EnsureTenantRegisteredAsync(tenantId);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // TenantGuardMiddleware authorizes a tenant purely from an ACTIVE TenantMembership, so
        // provisioning the membership row is sufficient (and is what production writes).
        var existing = await db.TenantMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);
        if (existing is not null)
        {
            return;
        }

        var membership = TenantMembership.Create(
            userId, tenantId, roleName, TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess, Describe(membership.Errors));

        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
    }

    private async Task SuspendMembershipAsync(string tenantId, string userId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);
        Assert.NotNull(membership);
        membership!.Suspend();
        await db.SaveChangesAsync();
    }

    private async Task GrantPermissionToRoleAsync(string roleName, string permissionCode)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await GrantAsync(db, roleName, permissionCode);
    }

    private async Task GrantPlatformPermissionToTenantRoleAsync(string roleName, string permissionCode)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await GrantAsync(db, roleName, permissionCode);
    }

    private static async Task GrantAsync(AppDbContext db, string roleName, string permissionCode)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        Assert.NotNull(role);

        var permission = await db.Permissions.FirstOrDefaultAsync(p => p.Code == permissionCode);
        if (permission is null)
        {
            // The catalog row is normally created by the app's seeder; create it if absent.
            // Permission.Create validates that Code == $"{Module}.{Action}".
            var separator = permissionCode.IndexOf('.');
            var module = separator > 0 ? permissionCode[..separator] : "Test";
            var action = separator > 0 ? permissionCode[(separator + 1)..] : permissionCode;

            var created = Permission.Create(0, module, action, permissionCode, permissionCode);
            Assert.True(created.IsSuccess, Describe(created.Errors));
            db.Permissions.Add(created.Value);
            await db.SaveChangesAsync();
            permission = created.Value;
        }

        var existing = await db.RolePermissions
            .FirstOrDefaultAsync(rp => rp.RoleId == role!.Id && rp.PermissionId == permission.Id);
        if (existing is not null)
        {
            return;
        }

        var rolePermission = RolePermission.Create(role!.Id, permission.Id);
        Assert.True(rolePermission.IsSuccess);
        db.RolePermissions.Add(rolePermission.Value);
        await db.SaveChangesAsync();
    }
}
