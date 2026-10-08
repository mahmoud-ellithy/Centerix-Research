using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Batch 2 relational/security verification against REAL SQL Server (never InMemory).
/// Exercises the same production controllers, services, handlers and authorization
/// boundaries as production: login/refresh/change-password gates (NEW-1), database
/// initialization, migration/schema validation and idempotent/explicit seeding (NEW-2),
/// and invitation persistence + delivery compensation (CFG-001).
/// </summary>
[Collection("SqlServerIntegration")]
public class Batch2SqlServerTests
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const string NewStrongPassword = "N3w!Str0ng#Pass2";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public Batch2SqlServerTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    // ==================================================================
    // NEW-2: fresh initialization, migrations, validation, idempotent seed
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task DatabaseInitialization_NoPendingMigrations_OnBothContexts()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        Assert.Empty(await appDb.Database.GetPendingMigrationsAsync());
        Assert.Empty(await tenantDb.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// NEW-2 idempotency on SQL Server: running the production required-seed path
    /// (<see cref="ApplicationDbContextInitialiser.SeedAsync"/>, the same method the
    /// tenant seeder invokes per tenant) twice duplicates nothing. Scoped to one tenant
    /// whose bootstrap user already exists so the run exercises the required-data path
    /// rather than bootstrap creation (covered separately below).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RequiredSeed_IsIdempotent_RepeatedInitializationDuplicatesNothing()
    {
        var tenantEmail = UniqueEmail("b2sql-idem");
        var tenant = FakeTenant(tenantEmail);
        await RegisterTenantAsync(tenant);
        await CreateUserAsync(tenantEmail);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var seeder = BuildInitialiser(
                scope, tenant, "Production", seedDevelopmentData: false, bootstrapPassword: "");
            await seeder.SeedAsync();
        }

        int permissionsBefore, rolesBefore, assignmentsBefore, policiesBefore;
        HashSet<int> policyIdsBefore;
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            permissionsBefore = await db.Permissions.CountAsync();
            rolesBefore = await db.Roles.CountAsync();
            assignmentsBefore = await db.RolePermissions.CountAsync();
            policiesBefore = await db.SubscriptionPolicies.CountAsync();
            policyIdsBefore = (await db.SubscriptionPolicies.Select(p => p.Id).ToListAsync()).ToHashSet();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var seeder = BuildInitialiser(
                scope, tenant, "Production", seedDevelopmentData: false, bootstrapPassword: "");
            await seeder.SeedAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(permissionsBefore, await db.Permissions.CountAsync());
            Assert.Equal(rolesBefore, await db.Roles.CountAsync());
            Assert.Equal(assignmentsBefore, await db.RolePermissions.CountAsync());
            Assert.Equal(policiesBefore, await db.SubscriptionPolicies.CountAsync());

            // No new policy row was minted (the database is shared with other suites, so
            // the set of ids — not a singleton count — is the idempotency invariant), and
            // the required default row seeded by this path is present.
            var policyIdsAfter = (await db.SubscriptionPolicies.Select(p => p.Id).ToListAsync()).ToHashSet();
            Assert.Equal(policyIdsBefore, policyIdsAfter);
            Assert.Contains(await db.SubscriptionPolicies.ToListAsync(), p => p.GracePeriodDays == 7);
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionSeed_WithoutDevelopmentData_CreatesNoBootstrapUser()
    {
        var tenantEmail = UniqueEmail("b2sql-prod-seed");
        var tenant = FakeTenant(tenantEmail);

        using var scope = _env.Factory.Services.CreateScope();
        var initialiser = BuildInitialiser(
            scope,
            tenant,
            environmentName: "Production",
            seedDevelopmentData: false,
            bootstrapPassword: "");

        await initialiser.SeedAsync();

        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.Null(await userManager.FindByEmailAsync(tenantEmail));

        // Required seed still ran (permission catalog present) — only dev data was skipped.
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Permissions.AnyAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task BootstrapSeed_WithoutExplicitPassword_FailsClearly()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var initialiser = BuildInitialiser(
            scope,
            FakeTenant(UniqueEmail("b2sql-nopwd")),
            environmentName: "Development",
            seedDevelopmentData: true,
            bootstrapPassword: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initialiser.SeedAsync());
        Assert.Contains("BootstrapAdmin:TemporaryPassword", ex.Message);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task BootstrapSeed_WithExplicitPassword_CreatesFlaggedAdmin()
    {
        var tenantEmail = UniqueEmail("b2sql-bootstrap");
        var tenant = FakeTenant(tenantEmail);
        await RegisterTenantAsync(tenant);

        using var scope = _env.Factory.Services.CreateScope();
        var initialiser = BuildInitialiser(
            scope,
            tenant,
            environmentName: "Development",
            seedDevelopmentData: true,
            bootstrapPassword: "B2Sql!Bootstrap#9");

        await initialiser.SeedAsync();

        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync(tenantEmail);
        Assert.NotNull(admin);
        var claims = await userManager.GetClaimsAsync(admin!);
        Assert.Contains(claims, c => c.Type == "password.change_required" && c.Value == "true");

        // Membership satisfies the TenantRegistry FK: the tenant was registered first,
        // mirroring the production ordering (registry row before membership rows).
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.TenantMemberships.FirstOrDefaultAsync(
            m => m.UserId == admin!.Id && m.TenantId == tenant.Id));
    }

    // ==================================================================
    // NEW-1 on SQL Server: login / refresh / change-password / lockout
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_Login_ChangeRequired_IssuesNoRefreshToken_AndFlowRecovers()
    {
        var email = UniqueEmail("b2sql-login");
        var user = await CreateUserAsync(email);
        await AddChangeRequiredClaimAsync(user.Id);

        var response = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("changePasswordToken", body);
        Assert.DoesNotContain("refreshToken", body);
        var flowToken = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions)
            .GetProperty("changePasswordToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(flowToken));

        Assert.Equal(0, await CountRefreshRowsAsync(user.Id));

        // Controlled flow completes rotation and returns the normal pair.
        var change = await ChangePasswordRequest(flowToken!, StrongPassword, NewStrongPassword, tenantHeader: null);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var login = await _env.Client.SendAsync(LoginRequest(email, NewStrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("refreshToken", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_Refresh_ChangeRequired_MintsNothing()
    {
        var email = UniqueEmail("b2sql-refresh");
        var user = await CreateUserAsync(email);

        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions);
        var accessToken = loginBody.GetProperty("accessToken").GetString()!;
        var refreshToken = loginBody.GetProperty("refreshToken").GetString()!;
        Assert.Equal(1, await CountRefreshRowsAsync(user.Id));

        await AddChangeRequiredClaimAsync(user.Id);

        var refresh = await _env.Client.SendAsync(RefreshRequest(refreshToken, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        var refreshBody = await refresh.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", refreshBody);
        Assert.DoesNotContain("refreshToken", refreshBody);
        Assert.Equal(1, await CountRefreshRowsAsync(user.Id));

        // The pre-existing access token still completes the controlled flow.
        var change = await ChangePasswordRequest(accessToken, StrongPassword, NewStrongPassword, tenantHeader: null);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ChangePassword_Success_ReturnsPair_RevokesOldSession()
    {
        var email = UniqueEmail("b2sql-change");
        var user = await CreateUserAsync(email);

        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        var loginBody = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions);
        var accessToken = loginBody.GetProperty("accessToken").GetString()!;
        var oldRefresh = loginBody.GetProperty("refreshToken").GetString()!;

        var change = await ChangePasswordRequest(accessToken, StrongPassword, NewStrongPassword, tenantHeader: null);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var pair = JsonSerializer.Deserialize<JsonElement>(await change.Content.ReadAsStringAsync(), JsonOptions);
        var newRefresh = pair.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("accessToken").GetString()));

        // Fresh pair rotates; pre-change session is dead.
        Assert.Equal(HttpStatusCode.OK,
            (await _env.Client.SendAsync(RefreshRequest(newRefresh, UniqueIp()))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK,
            (await _env.Client.SendAsync(RefreshRequest(oldRefresh, UniqueIp()))).StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ChangePassword_FailedAttempt_FeedsLockout_AndChangesNothing()
    {
        var email = UniqueEmail("b2sql-fail");
        var user = await CreateUserAsync(email);

        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        var loginBody = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions);
        var accessToken = loginBody.GetProperty("accessToken").GetString()!;
        var refreshToken = loginBody.GetProperty("refreshToken").GetString()!;

        var failed = await ChangePasswordRequest(accessToken, "Wr0ng!Pass9", NewStrongPassword, tenantHeader: null);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.Equal(1, await userManager.GetAccessFailedCountAsync(dbUser!));
            var claims = await userManager.GetClaimsAsync(dbUser!);
            Assert.DoesNotContain(claims, c => c.Type == "password.change_required");
        }

        // Session untouched: rotation still succeeds (no requirement flag on this user).
        Assert.Equal(HttpStatusCode.OK,
            (await _env.Client.SendAsync(RefreshRequest(refreshToken, UniqueIp()))).StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ChangePassword_CrossUser_ChangesOnlySelf()
    {
        var emailA = UniqueEmail("b2sql-a");
        var emailB = UniqueEmail("b2sql-b");
        var userA = await CreateUserAsync(emailA);
        await CreateUserAsync(emailB);

        var loginA = await _env.Client.SendAsync(LoginRequest(emailA, StrongPassword, UniqueIp()));
        var tokenA = JsonSerializer.Deserialize<JsonElement>(
            await loginA.Content.ReadAsStringAsync(), JsonOptions).GetProperty("accessToken").GetString()!;

        // Attack-shaped payload naming user B: the endpoint has no such field.
        var change = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        change.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword = StrongPassword, newPassword = NewStrongPassword, userId = userA.Id.Replace('a', 'b') }),
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await _env.Client.SendAsync(change)).StatusCode);

        // B is unaffected: old password still logs in.
        var loginB = await _env.Client.SendAsync(LoginRequest(emailB, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);

        // A rotated: old password rejected, new password works.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _env.Client.SendAsync(LoginRequest(emailA, StrongPassword, UniqueIp()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _env.Client.SendAsync(LoginRequest(emailA, NewStrongPassword, UniqueIp()))).StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_RequirementGatesTenantEndpoint_AndFlowTokenIsEndpointBound()
    {
        const string tenantId = "b2sql-gate-tenant";
        await EnsureTenantAsync(tenantId);
        await _env.Factory.SeedPermissionsAsync();

        var email = UniqueEmail("b2sql-gated");
        var user = await CreateUserAsync(email);
        await EnsureMembershipAsync(tenantId, user.Id, "TenantAdmin");
        await AddChangeRequiredClaimAsync(user.Id);

        // Correct password but flagged → 403 flow contract with a real flow token.
        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var flowToken = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;

        // The genuine flow token cannot access tenant endpoints (endpoint-bound).
        var gated = new HttpRequestMessage(HttpMethod.Get, "/api/invitations");
        gated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", flowToken);
        gated.Headers.Add("tenant", tenantId);
        var gatedResponse = await _env.Client.SendAsync(gated);
        Assert.Equal(HttpStatusCode.Forbidden, gatedResponse.StatusCode);
        Assert.Contains("PasswordChangeRequired", await gatedResponse.Content.ReadAsStringAsync());

        // After rotation through the flow, the tenant endpoint works normally.
        Assert.Equal(HttpStatusCode.OK,
            (await ChangePasswordRequest(flowToken, StrongPassword, NewStrongPassword, tenantHeader: null)).StatusCode);
        var normalLogin = await _env.Client.SendAsync(LoginRequest(email, NewStrongPassword, UniqueIp()));
        var normalToken = JsonSerializer.Deserialize<JsonElement>(
            await normalLogin.Content.ReadAsStringAsync(), JsonOptions).GetProperty("accessToken").GetString()!;
        var normal = new HttpRequestMessage(HttpMethod.Get, "/api/invitations");
        normal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalToken);
        normal.Headers.Add("tenant", tenantId);
        Assert.Equal(HttpStatusCode.OK, (await _env.Client.SendAsync(normal)).StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_ChangePassword_LockedOutAccount_IsRejected()
    {
        var email = UniqueEmail("b2sql-locked");
        var user = await CreateUserAsync(email);

        var login = await _env.Client.SendAsync(LoginRequest(email, StrongPassword, UniqueIp()));
        var accessToken = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions).GetProperty("accessToken").GetString()!;

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            await userManager.SetLockoutEndDateAsync(dbUser!, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        var change = await ChangePasswordRequest(accessToken, StrongPassword, NewStrongPassword, tenantHeader: null);
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
    }

    // ==================================================================
    // CFG-001 on SQL Server: invitation persistence + compensation
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Sql_Invitation_DeliveryFailure_ReturnsFailure_AndLeavesNoPending()
    {
        const string tenantId = "b2sql-inv-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var failingEmail = UniqueEmail("b2sql-inv-fail") + ".invalid";
        var okEmail = UniqueEmail("b2sql-inv-ok");

        _env.EmailSender.Clear();
        _env.EmailSender.ShouldFail = to => to.Contains("fail", StringComparison.OrdinalIgnoreCase);
        try
        {
            var failResponse = await SendInvitationAsync(adminToken, tenantId, failingEmail);
            Assert.NotEqual(HttpStatusCode.Created, failResponse.StatusCode);
            Assert.True((int)failResponse.StatusCode >= 400);

            using (var scope = _env.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.TenantInvitations.FirstOrDefaultAsync(i =>
                    i.TenantId == tenantId
                    && i.NormalizedEmail == failingEmail.Trim().ToUpperInvariant()
                    && i.Status == InvitationStatus.Pending);
                Assert.Null(pending);
            }

            Assert.Equal(HttpStatusCode.Created,
                (await SendInvitationAsync(adminToken, tenantId, okEmail)).StatusCode);
        }
        finally
        {
            _env.EmailSender.Clear();
        }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@sql.test";

    private static string UniqueIp() =>
        $"10.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}.{Random.Shared.Next(1, 200)}";

    private static HttpRequestMessage LoginRequest(string email, string password, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password }), Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage RefreshRequest(string refreshToken, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken }), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpResponseMessage> ChangePasswordRequest(
        string token, string currentPassword, string newPassword, string? tenantHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
            request.Headers.Add("tenant", tenantHeader);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword, newPassword }), Encoding.UTF8, "application/json");
        return await _env.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendInvitationAsync(string adminToken, string tenantId, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Add("tenant", tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, roleName = "TenantUser", expirationDays = 7 }),
            Encoding.UTF8, "application/json");
        return await _env.Client.SendAsync(request);
    }

    private async Task<IdentityUser> CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
            return existing;

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task AddChangeRequiredClaimAsync(string userId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(userId);
        var result = await userManager.AddClaimAsync(user!, new Claim("password.change_required", "true"));
        Assert.True(result.Succeeded);
    }

    private async Task<int> CountRefreshRowsAsync(string userId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.CountAsync(rt => rt.UserId == userId);
    }

    private async Task EnsureTenantAsync(string id)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(id) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = id,
                Identifier = id,
                Name = id,
                Email = $"{id}@registry.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task EnsureMembershipAsync(string tenantId, string userId, string roleName)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.TenantMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);
        if (existing is null)
        {
            var membership = TenantMembership.Create(userId, tenantId, roleName, TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }
    }

    private async Task<string> SeedAdminAsync(string tenantId)
    {
        await _env.Factory.SeedPermissionsAsync();

        var adminEmail = UniqueEmail("b2sql-admin");
        var user = await CreateUserAsync(adminEmail);
        await EnsureMembershipAsync(tenantId, user.Id, "TenantAdmin");

        return _env.Factory.GenerateTestToken(user.Id, user.Email!, ["TenantAdmin"]);
    }

    private static CenterixTenantInfo FakeTenant(string email) => new()
    {
        Id = $"b2sql-seed-{Guid.NewGuid():N}",
        Identifier = $"b2sql-seed-{Guid.NewGuid():N}",
        Name = "Batch2 seed test tenant",
        Email = email,
        IsActive = true,
        ValidUpTo = DateTime.UtcNow.AddYears(1),
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Registers the tenant in the SQL-backed registry (EFCoreStore), mirroring production
    /// ordering where the registry row precedes membership rows (FK).
    /// </summary>
    private async Task RegisterTenantAsync(CenterixTenantInfo tenant)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        Assert.True(await store.TryAddAsync(tenant));
    }

    private static ApplicationDbContextInitialiser BuildInitialiser(
        IServiceScope scope,
        CenterixTenantInfo tenant,
        string environmentName,
        bool seedDevelopmentData,
        string bootstrapPassword)
    {
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<CenterixTenantInfo> { TenantInfo = tenant };

        return new ApplicationDbContextInitialiser(
            sp.GetRequiredService<ILogger<ApplicationDbContextInitialiser>>(),
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<UserManager<IdentityUser>>(),
            sp.GetRequiredService<RoleManager<ApplicationRole>>(),
            sp.GetRequiredService<IMultiTenantContextAccessor<CenterixTenantInfo>>(),
            new FakeHostEnvironment(environmentName),
            Options.Create(new DatabaseInitializationOptions { SeedDevelopmentData = seedDevelopmentData }),
            Options.Create(new BootstrapAdminOptions { TemporaryPassword = bootstrapPassword }),
            Options.Create(new PlatformAdminBootstrapOptions()));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Centerix.Batch2.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
