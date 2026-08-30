using System.Text;
using Centerix.Domain.Platform.Authorization;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Centerix.SecurityTests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"SecurityTestDb_{Guid.NewGuid():N}";

    /// <summary>Captured server logs for the lifetime of this factory (used by HTTP tests).</summary>
    public List<string> ServerLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Secret"] = "ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!",
                    ["JwtSettings:Issuer"] = "TestIssuer",
                    ["JwtSettings:Audience"] = "TestAudience",
                    ["JwtSettings:ExpirationInMinutes"] = "60",
                    ["Invitations:BaseUrl"] = "https://app.securitytests.local",
                    ["ConnectionStrings:DefaultConnection"] = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;"
                })
                .Build();

            config.AddConfiguration(testConfig);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new TestListLoggerProvider(ServerLogs));
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<AppDbContext>(services, ConfigureAppDatabase);

            ReplaceDbContext<TenantDbContext>(services, ConfigureTenantDatabase);

            // Production uses EFCoreStore<TenantDbContext> as the single source of truth for
            // tenant resolution; the test host mirrors that exactly so lifecycle syncs performed
            // by ITenantRegistrySync are visible to the multi-tenant pipeline (an in-memory
            // store double would silently diverge after any SyncLifecycle call).
            var storeDescriptor = services.FirstOrDefault(
                d => d.ServiceType == typeof(IMultiTenantStore<CenterixTenantInfo>));
            if (storeDescriptor != null) services.Remove(storeDescriptor);

            services.AddScoped<IMultiTenantStore<CenterixTenantInfo>,
                Finbuckle.MultiTenant.EntityFrameworkCore.Stores.EFCoreStore.EFCoreStore<TenantDbContext, CenterixTenantInfo>>();

            // Replace the development e-mail sender with a capturing double so tests can recover
            // raw invitation tokens (they are never persisted server-side).
            var emailSenderDescriptor = services.FirstOrDefault(
                d => d.ServiceType == typeof(Centerix.Application.Common.Interfaces.IEmailSender));
            if (emailSenderDescriptor != null) services.Remove(emailSenderDescriptor);

            services.AddSingleton(EmailSender);
            services.AddSingleton<Centerix.Application.Common.Interfaces.IEmailSender>(
                sp => sp.GetRequiredService<CapturingEmailSender>());
        });
    }

    /// <summary>
    /// Database configuration for <see cref="AppDbContext"/>. Defaults to EF InMemory;
    /// relational test factories override this to target a real SQL Server container.
    /// The TransactionIgnoredWarning suppression lets handlers open real transactions
    /// (no-ops on InMemory) while relational suites exercise them for real.
    /// </summary>
    protected virtual void ConfigureAppDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    /// <summary>
    /// Database configuration for <see cref="TenantDbContext"/>. Defaults to EF InMemory.
    /// The TransactionIgnoredWarning suppression lets registry-sync dual-writes open their
    /// (no-op) transactions exactly like the relational path.
    /// </summary>
    protected virtual void ConfigureTenantDatabase(IServiceProvider services, DbContextOptionsBuilder options)
        => options
            .UseInMemoryDatabase($"{_databaseName}_Tenant")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    private void ReplaceDbContext<TContext>(
        IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configureOptions) where TContext : DbContext
    {
        var toRemove = services.Where(d =>
        {
            var st = d.ServiceType;
            if (st == typeof(DbContextOptions<TContext>)) return true;
            if (st == typeof(TContext)) return true;
            // Remove any generic service where TContext is a type argument
            if (st.IsConstructedGenericType && st.GenericTypeArguments.Contains(typeof(TContext))) return true;
            return false;
        }).ToList();

        foreach (var d in toRemove) services.Remove(d);

        // NOTE: Interceptors (TenantInterceptor, AuditableEntityInterceptor) are intentionally
        // NOT wired into the test InMemory DbContext. Production wires them through
        // sp.GetServices<ISaveChangesInterceptor>() inside AddDbContext's factory, which lets
        // EF Core stamp TenantId and audit fields at save time. We don't do that here because
        // (a) the InMemory provider's interaction with save-changes interceptors is observed
        // to deadlock the test host (root cause tracked separately), and (b) Phase 3 handlers
        // stamp TenantId explicitly at the handler layer so the interceptor is not required
        // for tenant-correctness inside the test surface.
        services.AddDbContext<TContext>((sp, options) =>
        {
            configureOptions(sp, options);
        });
    }

    public async Task SeedPermissionsAsync()
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        // Seed permissions
        foreach (var entry in PermissionCatalog.All)
        {
            if (!context.Permissions.Any(p => p.Code == entry.Code))
            {
                var permission = Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description);
                if (permission.IsSuccess)
                    context.Permissions.Add(permission.Value);
            }
        }
        await context.SaveChangesAsync();

        // Seed default roles
        await EnsureRoleAsync(roleManager, "TenantAdmin", "Tenant Administrator");
        await EnsureRoleAsync(roleManager, "TenantUser", "Tenant User");

        // Assign all permissions to TenantAdmin
        var adminRole = await roleManager.FindByNameAsync("TenantAdmin");
        if (adminRole != null)
        {
            var allPermissions = context.Permissions.ToList();
            var existingRolePermissions = context.RolePermissions.Where(rp => rp.RoleId == adminRole.Id).ToList();
            var existingPermissionIds = new HashSet<int>(existingRolePermissions.Select(rp => rp.PermissionId));

            foreach (var permission in allPermissions)
            {
                if (!existingPermissionIds.Contains(permission.Id))
                {
                    context.RolePermissions.Add(RolePermission.Create(adminRole.Id, permission.Id).Value);
                }
            }
        }

        // Assign read permissions to TenantUser
        var userRole = await roleManager.FindByNameAsync("TenantUser");
        if (userRole != null)
        {
            var readPermissions = context.Permissions
                .Where(p => p.Action == "Read" || p.Action == "Manage")
                .ToList();
            var existingRolePermissions = context.RolePermissions.Where(rp => rp.RoleId == userRole.Id).ToList();
            var existingPermissionIds = new HashSet<int>(existingRolePermissions.Select(rp => rp.PermissionId));

            foreach (var permission in readPermissions)
            {
                if (!existingPermissionIds.Contains(permission.Id))
                {
                    context.RolePermissions.Add(RolePermission.Create(userRole.Id, permission.Id).Value);
                }
            }
        }
        await context.SaveChangesAsync();
    }

    private static async Task EnsureRoleAsync(RoleManager<ApplicationRole> roleManager, string code, string displayName)
    {
        if (!await roleManager.RoleExistsAsync(code))
        {
            await roleManager.CreateAsync(new ApplicationRole(code)
            {
                Code = code,
                DisplayName = displayName,
                IsSystem = true,
                NormalizedName = code.ToUpperInvariant()
            });
        }
    }

    public string GenerateTestToken(string userId, string email, IList<string> roles, IList<string>? permissions = null)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new(System.Security.Claims.ClaimTypes.Name, email),
            new(System.Security.Claims.ClaimTypes.Email, email),
        };

        foreach (var role in roles)
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));

        // Note: Permissions are no longer embedded in the JWT.
        // They are resolved per-request via TenantPermissionResolver.

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "TestIssuer",
            audience: "TestAudience",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Capturing e-mail sender shared by every host built from this factory. Call
    /// <see cref="CapturingEmailSender.Clear"/> between tests that assert on sent mail.
    /// </summary>
    public CapturingEmailSender EmailSender { get; } = new();
}

/// <summary>
/// Minimal in-memory logger provider for tests that want to inspect server-side warnings/errors.
/// </summary>
public sealed class TestListLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestListLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class TestListLogger(string category, List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Add($"[{logLevel}] {category}: {formatter(state, exception)}");
            if (exception is not null) sink.Add(exception.ToString());
        }
    }
}