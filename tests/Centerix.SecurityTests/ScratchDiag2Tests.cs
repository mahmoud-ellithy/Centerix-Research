using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

namespace Centerix.SecurityTests;

public class ScratchDiag2Tests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ScratchDiag2Tests(TestWebApplicationFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task Diag_Me_ShouldWork()
    {
        var identifier = $"p2d-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var tenantDb = sp.GetRequiredService<TenantDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var entry in PermissionCatalog.All)
            if (!db.Permissions.Any(p => p.Code == entry.Code))
            {
                var perm = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (perm.IsSuccess) db.Permissions.Add(perm.Value);
            }
        await db.SaveChangesAsync();

        await EnsureRoleAsync(roleManager, "PlatformAdmin");
        await EnsureRoleAsync(roleManager, "TenantAdmin");

        var readPid = db.Permissions.Single(p => p.Code == Permissions.TenantPlans.Read).Id;
        var tenantAdminRole = await roleManager.FindByNameAsync("TenantAdmin");
        if (!db.RolePermissions.Any(rp => rp.RoleId == tenantAdminRole!.Id && rp.PermissionId == readPid))
            db.RolePermissions.Add(RolePermission.Create(tenantAdminRole!.Id, readPid).Value);

        var planResult = Centerix.Domain.Platform.Plans.Plan.Create(
            0, $"D{Guid.NewGuid():N}"[..20], "Plan", 100m, 50, 5, 2, 10, 20, 100, true, null, "USD", 3, 2);
        db.Plans.Add(planResult.Value);
        await db.SaveChangesAsync();

        var tenant = Tenant.Create(Guid.NewGuid(), identifier, identifier, identifier, "EG", "EGP",
            "Africa/Cairo", "O", "W", "o@p2d.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        tenantDb.TenantInfo.Add(new CenterixTenantInfo
        {
            Id = tenant.Id.ToString(), Identifier = identifier, Name = identifier,
            Email = "p2d@test.com", IsActive = false, ValidUpTo = DateTime.MinValue, CreatedAt = DateTime.UtcNow
        });
        await tenantDb.SaveChangesAsync();

        var tenantAdmin = await CreateTestUserAsync(userManager, $"admin-{identifier}", "TenantAdmin", tenant.Id.ToString(), db);

        var platformAdmin = await CreatePlatformAdminAsync(userManager);
        var platformToken = _factory.GenerateTestToken(platformAdmin.Id, platformAdmin.Email!, ["PlatformAdmin"]);
        var tenantToken = _factory.GenerateTestToken(tenantAdmin.Id, tenantAdmin.Email!, ["TenantAdmin"]);

        var client = _factory.CreateClient();

        // Approve via HTTP (platform admin required)
        var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenant.Id}/approve")
        {
            Content = JsonContent.Create(new { tenantId = tenant.Id, planId = planResult.Value.Id }, options: s_json)
        };
        approveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        var approveResp = await client.SendAsync(approveReq);
        var approveBody = await approveResp.Content.ReadAsStringAsync();
        Assert.True(approveResp.IsSuccessStatusCode, $"Approve: {approveResp.StatusCode} :: {approveBody}");

        // Activate via HTTP (platform admin required)
        var activateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenant.Id}/activate");
        activateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);
        var activateResp = await client.SendAsync(activateReq);
        var activateBody = await activateResp.Content.ReadAsStringAsync();
        Assert.True(activateResp.IsSuccessStatusCode, $"Activate: {activateResp.StatusCode} :: {activateBody}");

        // DIAG: verify TenantDbContext state
        using (var diagScope = _factory.Services.CreateScope())
        {
            var diagTenantDb = diagScope.ServiceProvider.GetRequiredService<TenantDbContext>();
            var info = await diagTenantDb.TenantInfo.FindAsync(tenant.Id.ToString());
            Assert.NotNull(info);
            Assert.True(info.IsActive,
                $"DIAG FAIL: CenterixTenantInfo.IsActive=false. Status={info.Status}, Identifier={info.Identifier}");
        }

        // HTTP GET /me
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/tenantplans/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantToken);
        req.Headers.Add("tenant", identifier);
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{resp.StatusCode} :: {body}");
    }

    private static async Task EnsureRoleAsync(RoleManager<ApplicationRole> roleManager, string name)
    {
        if (await roleManager.FindByNameAsync(name) is null)
            await roleManager.CreateAsync(new ApplicationRole(name)
            {
                Code = name, DisplayName = name, IsSystem = true, NormalizedName = name.ToUpperInvariant()
            });
    }

    private static async Task<IdentityUser> CreatePlatformAdminAsync(UserManager<IdentityUser> userManager)
    {
        var email = $"pa-{Guid.NewGuid():N}@p2d.test";
        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
        await userManager.CreateAsync(user);
        await userManager.AddToRoleAsync(user, "PlatformAdmin");
        return user;
    }

    private static async Task<IdentityUser> CreateTestUserAsync(
        UserManager<IdentityUser> userManager, string prefix, string role, string tenantId, AppDbContext db)
    {
        var email = $"{prefix}@p2d.test";
        var user = new IdentityUser
        {
            Email = email, UserName = email, EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
        await userManager.CreateAsync(user);
        await userManager.AddToRoleAsync(user, role);
        if (!db.TenantMemberships.Any(m => m.UserId == user.Id && m.TenantId == tenantId))
            db.TenantMemberships.Add(TenantMembership.Create(user.Id, tenantId, role, TenantMembershipStatus.Active).Value);
        await db.SaveChangesAsync();
        return user;
    }
}
