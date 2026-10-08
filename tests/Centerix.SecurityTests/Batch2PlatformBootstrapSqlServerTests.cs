using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Tenants;
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
/// Blocker 2: production has a real first-PlatformAdmin bootstrap path that is independent
/// of <c>SeedDevelopmentData</c>. Proves creation without development seeding, clear failure
/// on incomplete configuration, idempotency across startups, no recreation/reset of an
/// existing admin, and no elevation of normal users. All proofs run against REAL SQL
/// Server (never InMemory).
/// </summary>
[Collection("SqlServerIntegration")]
public class Batch2PlatformBootstrapSqlServerTests
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const string BootstrapPassword = "B00tstr@p!Init9";
    private const string RotatedPassword = "R0t@t3d!Pass9";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public Batch2PlatformBootstrapSqlServerTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_CreatesFirstPlatformAdmin_WithoutSeedDevelopmentData()
    {
        var email = UniqueEmail("prodboot");
        var tenant = FakeTenant(UniqueEmail("prodboot-tenant"));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var seeder = BuildInitialiser(
                scope, tenant, "Production", seedDevelopmentData: false,
                bootstrap: new PlatformAdminBootstrapOptions
                {
                    Enabled = true,
                    Email = email,
                    TemporaryPassword = BootstrapPassword
                });
            await seeder.SeedAsync();
        }

        // The first PlatformAdmin exists with the platform role and the forced-rotation claim.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var admin = await userManager.FindByEmailAsync(email);
            Assert.NotNull(admin);
            Assert.True(await userManager.IsInRoleAsync(admin!, RoleConstants.PlatformAdmin));
            var claims = await userManager.GetClaimsAsync(admin!);
            Assert.Contains(claims, c => c.Type == "password.change_required" && c.Value == "true");

            // The bootstrap password is the live credential (nothing else was invented).
            Assert.True(await userManager.CheckPasswordAsync(admin!, BootstrapPassword));
        }

        // Full controlled flow through HTTP: login is gated, rotation succeeds, login works after.
        var login = await _env.Client.SendAsync(LoginRequest(email, BootstrapPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var flowToken = JsonSerializer.Deserialize<JsonElement>(
            await login.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("changePasswordToken").GetString()!;

        var change = await ChangePasswordRequest(flowToken, BootstrapPassword, RotatedPassword);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var normalLogin = await _env.Client.SendAsync(LoginRequest(email, RotatedPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, normalLogin.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_SecondStartup_IsIdempotent_DoesNotReset()
    {
        var email = UniqueEmail("prodboot-idem");
        var tenant = FakeTenant(UniqueEmail("prodboot-idem-tenant"));
        var bootstrap = new PlatformAdminBootstrapOptions
        {
            Enabled = true,
            Email = email,
            TemporaryPassword = BootstrapPassword
        };

        using (var scope = _env.Factory.Services.CreateScope())
            await BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap).SeedAsync();

        // Rotate the password so a reset would be observable.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var admin = await userManager.FindByEmailAsync(email);
            Assert.NotNull(admin);
            foreach (var claim in (await userManager.GetClaimsAsync(admin!))
                .Where(c => c.Type == "password.change_required").ToList())
                await userManager.RemoveClaimAsync(admin!, claim);
            Assert.True((await userManager.ChangePasswordAsync(admin!, BootstrapPassword, RotatedPassword)).Succeeded);
        }

        // Second startup: must succeed silently and change nothing.
        using (var scope = _env.Factory.Services.CreateScope())
            await BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap).SeedAsync();

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var users = await userManager.Users.Where(u => u.Email == email).ToListAsync();
            Assert.Single(users);
            Assert.True(await userManager.CheckPasswordAsync(users[0], RotatedPassword));
            Assert.False(await userManager.CheckPasswordAsync(users[0], BootstrapPassword));
            Assert.True(await userManager.IsInRoleAsync(users[0], RoleConstants.PlatformAdmin));
        }

        var login = await _env.Client.SendAsync(LoginRequest(email, RotatedPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_Disabled_CreatesNothing()
    {
        var email = UniqueEmail("prodboot-off");
        var tenant = FakeTenant(UniqueEmail("prodboot-off-tenant"));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var seeder = BuildInitialiser(
                scope, tenant, "Production", seedDevelopmentData: false,
                bootstrap: new PlatformAdminBootstrapOptions { Enabled = false });
            await seeder.SeedAsync();
        }

        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.Null(await userManager.FindByEmailAsync(email));
    }

    [Theory]
    [Trait("Category", "SqlServer")]
    [InlineData("", BootstrapPassword, "PlatformAdminBootstrap:Email")]
    [InlineData("not-an-email", BootstrapPassword, "PlatformAdminBootstrap:Email")]
    [InlineData("<unique>", "", "PlatformAdminBootstrap:TemporaryPassword")]
    public async Task ProductionBootstrap_IncompleteConfiguration_FailsClearly(
        string email, string password, string expectedMessageFragment)
    {
        var tenant = FakeTenant(UniqueEmail("prodboot-badcfg"));
        var actualEmail = email == "<unique>" ? UniqueEmail("prodboot-badcfg") : email;

        using var scope = _env.Factory.Services.CreateScope();
        var seeder = BuildInitialiser(
            scope, tenant, "Production", seedDevelopmentData: false,
            bootstrap: new PlatformAdminBootstrapOptions
            {
                Enabled = true,
                Email = actualEmail,
                TemporaryPassword = password
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
        Assert.Contains(expectedMessageFragment, ex.Message);

        // Nothing was partially created.
        if (!string.IsNullOrWhiteSpace(actualEmail))
        {
            using var verify = _env.Factory.Services.CreateScope();
            var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Null(await userManager.FindByEmailAsync(actualEmail));
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_WeakPassword_FailsClearly_CreatesNothing()
    {
        var email = UniqueEmail("prodboot-weak");
        var tenant = FakeTenant(UniqueEmail("prodboot-weak-tenant"));

        using var scope = _env.Factory.Services.CreateScope();
        var seeder = BuildInitialiser(
            scope, tenant, "Production", seedDevelopmentData: false,
            bootstrap: new PlatformAdminBootstrapOptions
            {
                Enabled = true,
                Email = email,
                TemporaryPassword = "weak"
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
        Assert.Contains(email, ex.Message);

        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.Null(await userManager.FindByEmailAsync(email));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_ExistingNonAdminUser_IsNotElevated_FailsClearly()
    {
        var email = UniqueEmail("prodboot-takeover");
        await CreateUserAsync(email);
        var tenant = FakeTenant(UniqueEmail("prodboot-takeover-tenant"));

        using var scope = _env.Factory.Services.CreateScope();
        var seeder = BuildInitialiser(
            scope, tenant, "Production", seedDevelopmentData: false,
            bootstrap: new PlatformAdminBootstrapOptions
            {
                Enabled = true,
                Email = email,
                TemporaryPassword = BootstrapPassword
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
        Assert.Contains("not a PlatformAdmin", ex.Message);

        // The normal user gained nothing through this mechanism.
        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.False(await userManager.IsInRoleAsync(user!, RoleConstants.PlatformAdmin));
        Assert.True(await userManager.CheckPasswordAsync(user!, StrongPassword));
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_ExistingPlatformAdmin_IsLeftUntouched()
    {
        var email = UniqueEmail("prodboot-existing");
        await CreateUserAsync(email);
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var setupUserManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roleManager.RoleExistsAsync(RoleConstants.PlatformAdmin))
                Assert.True((await roleManager.CreateAsync(new ApplicationRole(RoleConstants.PlatformAdmin))).Succeeded);
            var freshPrecreated = await setupUserManager.FindByEmailAsync(email);
            Assert.NotNull(freshPrecreated);
            Assert.True((await setupUserManager.AddToRoleAsync(freshPrecreated!, RoleConstants.PlatformAdmin)).Succeeded);
        }

        var tenant = FakeTenant(UniqueEmail("prodboot-existing-tenant"));
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var seeder = BuildInitialiser(
                scope, tenant, "Production", seedDevelopmentData: false,
                bootstrap: new PlatformAdminBootstrapOptions
                {
                    Enabled = true,
                    Email = email,
                    TemporaryPassword = "D1fferent!Temp9"
                });
            await seeder.SeedAsync();
        }

        // Untouched: original credential still live, still admin, no forced-rotation claim added.
        using var verify = _env.Factory.Services.CreateScope();
        var userManager = verify.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByEmailAsync(email);
        Assert.NotNull(admin);
        Assert.True(await userManager.CheckPasswordAsync(admin!, StrongPassword));
        Assert.True(await userManager.IsInRoleAsync(admin!, RoleConstants.PlatformAdmin));
        var claims = await userManager.GetClaimsAsync(admin!);
        Assert.DoesNotContain(claims, c => c.Type == "password.change_required");
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_ClaimFailure_FailsAndCompensates_LeavesNothingBehind()
    {
        var email = UniqueEmail("prodboot-claimfail");
        var tenant = FakeTenant(UniqueEmail("prodboot-claimfail-tenant"));
        var bootstrap = new PlatformAdminBootstrapOptions
        {
            Enabled = true,
            Email = email,
            TemporaryPassword = BootstrapPassword
        };

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var failingManager = new FailingBootstrapUserManager(scope.ServiceProvider) { FailAddClaim = true };
            var seeder = BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap, failingManager);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
            Assert.Contains("PlatformAdmin", ex.Message);
            Assert.Contains("injected claim failure", ex.Message);
        }

        // No false "bootstrap succeeded" state: the partially created user was compensated.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Null(await userManager.FindByEmailAsync(email));
        }

        // Unknown credential: login is still plain unauthorized.
        var login = await _env.Client.SendAsync(LoginRequest(email, BootstrapPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // A healthy retry starts from a clean state and succeeds fully.
        using (var scope = _env.Factory.Services.CreateScope())
            await BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap).SeedAsync();

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var admin = await userManager.FindByEmailAsync(email);
            Assert.NotNull(admin);
            Assert.True(await userManager.IsInRoleAsync(admin!, RoleConstants.PlatformAdmin));
            var claims = await userManager.GetClaimsAsync(admin!);
            Assert.Contains(claims, c => c.Type == "password.change_required" && c.Value == "true");
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ProductionBootstrap_RoleFailure_FailsAndCompensates_LeavesNothingBehind()
    {
        var email = UniqueEmail("prodboot-rolefail");
        var tenant = FakeTenant(UniqueEmail("prodboot-rolefail-tenant"));
        var bootstrap = new PlatformAdminBootstrapOptions
        {
            Enabled = true,
            Email = email,
            TemporaryPassword = BootstrapPassword
        };

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var failingManager = new FailingBootstrapUserManager(scope.ServiceProvider) { FailAddToRole = true };
            var seeder = BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap, failingManager);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
            Assert.Contains("PlatformAdmin", ex.Message);
            Assert.Contains("injected role failure", ex.Message);
        }

        // Compensation removed the whole partially configured user (claim included).
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Null(await userManager.FindByEmailAsync(email));
        }

        var login = await _env.Client.SendAsync(LoginRequest(email, BootstrapPassword, UniqueIp()));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // A healthy retry starts from a clean state and succeeds fully.
        using (var scope = _env.Factory.Services.CreateScope())
            await BuildInitialiser(scope, tenant, "Production", seedDevelopmentData: false, bootstrap).SeedAsync();

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var admin = await userManager.FindByEmailAsync(email);
            Assert.NotNull(admin);
            Assert.True(await userManager.IsInRoleAsync(admin!, RoleConstants.PlatformAdmin));
            var claims = await userManager.GetClaimsAsync(admin!);
            Assert.Contains(claims, c => c.Type == "password.change_required" && c.Value == "true");
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

    private async Task<HttpResponseMessage> ChangePasswordRequest(
        string token, string currentPassword, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword, newPassword }), Encoding.UTF8, "application/json");
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

    private static CenterixTenantInfo FakeTenant(string email) => new()
    {
        Id = $"b2boot-seed-{Guid.NewGuid():N}",
        Identifier = $"b2boot-seed-{Guid.NewGuid():N}",
        Name = "Batch2 bootstrap test tenant",
        Email = email,
        IsActive = true,
        ValidUpTo = DateTime.UtcNow.AddYears(1),
        CreatedAt = DateTime.UtcNow
    };

    private static ApplicationDbContextInitialiser BuildInitialiser(
        IServiceScope scope,
        CenterixTenantInfo tenant,
        string environmentName,
        bool seedDevelopmentData,
        PlatformAdminBootstrapOptions bootstrap,
        UserManager<IdentityUser>? userManagerOverride = null)
    {
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<CenterixTenantInfo> { TenantInfo = tenant };

        return new ApplicationDbContextInitialiser(
            sp.GetRequiredService<ILogger<ApplicationDbContextInitialiser>>(),
            sp.GetRequiredService<AppDbContext>(),
            userManagerOverride ?? sp.GetRequiredService<UserManager<IdentityUser>>(),
            sp.GetRequiredService<RoleManager<ApplicationRole>>(),
            sp.GetRequiredService<IMultiTenantContextAccessor<CenterixTenantInfo>>(),
            new FakeHostEnvironment(environmentName),
            Options.Create(new DatabaseInitializationOptions { SeedDevelopmentData = seedDevelopmentData }),
            Options.Create(new BootstrapAdminOptions()),
            Options.Create(bootstrap));
    }

    /// <summary>
    /// Deterministic failure injection for bootstrap failure paths: a real
    /// <see cref="UserManager{IdentityUser}"/> (same stores, validators, normalizers as
    /// production) with flaggable claim/role writes. Everything else delegates to base.
    /// </summary>
    private sealed class FailingBootstrapUserManager : UserManager<IdentityUser>
    {
        public bool FailAddClaim { get; set; }

        public bool FailAddToRole { get; set; }

        public FailingBootstrapUserManager(IServiceProvider services)
            : base(
                services.GetRequiredService<IUserStore<IdentityUser>>(),
                services.GetRequiredService<IOptions<IdentityOptions>>(),
                services.GetRequiredService<IPasswordHasher<IdentityUser>>(),
                services.GetRequiredService<IEnumerable<IUserValidator<IdentityUser>>>(),
                services.GetRequiredService<IEnumerable<IPasswordValidator<IdentityUser>>>(),
                services.GetRequiredService<ILookupNormalizer>(),
                services.GetRequiredService<IdentityErrorDescriber>(),
                services,
                services.GetRequiredService<ILogger<UserManager<IdentityUser>>>())
        {
        }

        public override Task<IdentityResult> AddClaimAsync(IdentityUser user, Claim claim) =>
            FailAddClaim
                ? Task.FromResult(IdentityResult.Failed(new IdentityError
                {
                    Code = "Test.ClaimRejected",
                    Description = "injected claim failure for bootstrap test"
                }))
                : base.AddClaimAsync(user, claim);

        public override Task<IdentityResult> AddToRoleAsync(IdentityUser user, string role) =>
            FailAddToRole
                ? Task.FromResult(IdentityResult.Failed(new IdentityError
                {
                    Code = "Test.RoleRejected",
                    Description = "injected role failure for bootstrap test"
                }))
                : base.AddToRoleAsync(user, role);
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
