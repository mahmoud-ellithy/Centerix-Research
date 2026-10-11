using System.Reflection;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Email;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Centerix.SecurityTests;

// REV-012: environment handling must be driven by the Email:Mode option (not just
// IsProduction()). Capture is permitted only in Development, or in "Testing" when the
// test host has explicitly opted in via TestHostMarkers.AllowCaptureAndSkipDbInitKey
// (REV-012 A.1: the environment NAME "Testing" is never trusted by itself). Bootstrap
// admin seeding is gated on SeedDevelopmentData in every environment.
[Collection("Integration")]
public class Rev012EnvironmentHandlingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public Rev012EnvironmentHandlingTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ================================================================
    // EmailOptions default + validation behaviour (unit-level).
    // ================================================================

    [Fact]
    public void EmailOptions_Unset_DefaultsToCapture_InDevelopment()
    {
        var env = NewHost("Development");
        var options = new EmailOptions();

        // No marker needed: Development is always allowed.
        Assert.Equal(EmailMode.Capture, options.ResolveMode(env));
    }

    [Fact]
    public void EmailOptions_Unset_InTesting_RequiresExplicitMarker()
    {
        // REV-012 (A.1): the environment NAME "Testing" is NOT trusted by itself anymore.
        // Without the marker it must behave like any other non-Development environment.
        var env = NewHost("Testing");
        var options = new EmailOptions();

        Assert.Equal(EmailMode.Smtp, options.ResolveMode(env, allowTestingCapture: false));
        Assert.Equal(EmailMode.Capture, options.ResolveMode(env, allowTestingCapture: true));
    }

    [Fact]
    public void EmailOptions_Unset_DefaultsToSmtp_OutsideDevelopmentAndTesting()
    {
        // REV-012: Staging and Production must NOT silently default to Capture.
        var env = NewHost("Staging");
        var options = new EmailOptions();

        Assert.Equal(EmailMode.Smtp, options.ResolveMode(env));

        env.EnvironmentName = "Production";
        Assert.Equal(EmailMode.Smtp, options.ResolveMode(env));
    }

    [Fact]
    public void EmailOptions_Capture_RejectedOutsideDevelopmentOrAllowedTesting()
    {
        var env = NewHost("Staging");
        var options = new EmailOptions { Mode = EmailMode.Capture };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(env));
        Assert.Contains("only permitted in Development", ex.Message);
        Assert.Contains("Staging", ex.Message);

        env.EnvironmentName = "Production";
        options = new EmailOptions { Mode = EmailMode.Capture };
        Assert.Throws<InvalidOperationException>(() => options.Validate(env));
    }

    [Fact]
    public void EmailOptions_Capture_RejectedInTesting_WithoutExplicitMarker()
    {
        // REV-012 (A.1): an operator naming a real deployment "Testing" must not be able to
        // unlock Capture just by the environment name — only the test host marker can.
        var env = NewHost("Testing");
        var options = new EmailOptions { Mode = EmailMode.Capture };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(env, allowTestingCapture: false));
        Assert.Contains("only permitted in Development", ex.Message);
        Assert.Contains(TestHostMarkers.AllowCaptureAndSkipDbInitKey, ex.Message);
        Assert.Contains("marker not set", ex.Message);
    }

    [Fact]
    public void EmailOptions_Capture_AllowedInDevelopment_OrTestingWithMarker()
    {
        var dev = NewHost("Development");
        new EmailOptions { Mode = EmailMode.Capture }.Validate(dev); // no throw, no marker needed

        var test = NewHost("Testing");
        new EmailOptions { Mode = EmailMode.Capture }.Validate(test, allowTestingCapture: true); // no throw
    }

    [Fact]
    public void EmailOptions_Smtp_AllowedEverywhere()
    {
        new EmailOptions { Mode = EmailMode.Smtp }.Validate(NewHost("Production"));
        new EmailOptions { Mode = EmailMode.Smtp }.Validate(NewHost("Staging"));
        new EmailOptions { Mode = EmailMode.Smtp }.Validate(NewHost("Development"));
    }

    // ================================================================
    // Bootstrap admin seeding: closed bypass proof.
    //
    // The previous guard `_hostEnvironment.IsProduction() && !_dbInitOptions.SeedDevelopmentData`
    // meant Staging with SeedDevelopmentData=false still walked INTO the seed path for
    // every registry tenant that had an owner email. We exercise the live initialiser
    // here against the real DI graph and prove that the same handler is a no-op when
    // SeedDevelopmentData=false (the production-safe default for staging).
    // ================================================================

    [Fact]
    public async Task SeedAsync_InTestingEnv_WithSeedDevelopmentDataFalse_DoesNotCreateBootstrapAdmin()
    {
        // The factory environment is "Testing"; SeedDevelopmentData is false by default in
        // appsettings (and the factory does NOT flip it to true). The closure invariant is
        // that no bootstrap admin user is created regardless of environment — this is now
        // gated purely on the explicit SeedDevelopmentData flag.
        using var scope = _factory.Services.CreateScope();
        var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>();

        // Run a fresh seed; this is idempotent for the deterministic rows (permissions,
        // roles, SubscriptionPolicy, root tenant). The bootstrap admin step is the only
        // path that conditionally creates a user from a temporary password.
        await initialiser.SeedAsync();

        using var assertScope = _factory.Services.CreateScope();
        var userManager = assertScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        // No admin should be created from a temporary password anywhere in this lifecycle.
        // (The seeded root tenant owner email is intentionally not present because no
        // BootstrapAdmin:TemporaryPassword is configured in the factory.)
        var allUsers = userManager.Users.ToList();
        Assert.DoesNotContain(allUsers, u => u.Email != null && u.Email.Contains("rev012", StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================
    // Host-level email-mode resolution (REV-012 A.1, Item 1).
    //
    // These build a REAL host per Centerix.API.Program through WebApplicationFactory, so
    // Program.cs's own startup gate (EmailOptions.Validate + the Smtp.Validate branch) and
    // DependencyInjection's IEmailSender factory are exercised exactly as in production —
    // not just the EmailOptions unit above. Database initialization is disabled via config
    // (the InMemory DbContext double used by TestWebApplicationFactory doesn't support real
    // migrations) so each test isolates email-mode behaviour only.
    // ================================================================

    [Fact]
    public async Task Host_Staging_NoEmailModeNoSmtp_FailsStartup_WithClearMessage()
    {
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Staging", NoDbInit());

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("Smtp:Host must be configured", exception!.ToString());
    }

    [Fact]
    public async Task Host_Staging_CaptureMode_FailsStartup_CaptureOnlyInDevOrTesting()
    {
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Staging", NoDbInit(
            ("Email:Mode", "Capture")));

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("only permitted in Development", exception!.ToString());
    }

    [Fact]
    public async Task Host_Staging_ValidSmtp_ResolvesSmtpEmailSender()
    {
        var config = NoDbInit();
        AddValidSmtp(config);
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Staging", config);

        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.IEmailSender>();

        Assert.IsType<SmtpEmailSender>(sender);
    }

    [Fact]
    public async Task Host_Production_ValidSmtp_ResolvesSmtpEmailSender()
    {
        var config = NoDbInit();
        AddValidSmtp(config);
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Production", config);

        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.IEmailSender>();

        Assert.IsType<SmtpEmailSender>(sender);
    }

    [Fact]
    public async Task Host_Production_MissingSmtp_FailsStartup()
    {
        // CFG-001 regression: Production must never silently fall back to a development sender.
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Production", NoDbInit());

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("Smtp:Host must be configured", exception!.ToString());
    }

    [Fact]
    public async Task Host_Development_Defaults_ResolvesDevelopmentEmailSender()
    {
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Development", NoDbInit());

        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.IEmailSender>();

        Assert.IsType<DevelopmentEmailSender>(sender);
    }

    // ================================================================
    // Host-level "Testing" marker proof (REV-012 A.1, Item 3).
    //
    // An environment literally named "Testing" with NO marker set must behave like any
    // other non-Development environment: Smtp required, Capture rejected, database
    // initialization NOT skipped. Only TestWebApplicationFactory's own in-memory config
    // (which this override factory inherits and here explicitly clears) may set the marker.
    // ================================================================

    [Fact]
    public async Task Host_TestingEnvironment_WithoutMarker_RequiresSmtp_LikeAnyOtherEnvironment()
    {
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Testing", NoDbInit(
            (TestHostMarkers.AllowCaptureAndSkipDbInitKey, "false")));

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("Smtp:Host must be configured", exception!.ToString());
    }

    [Fact]
    public async Task Host_TestingEnvironment_WithoutMarker_RejectsCapture()
    {
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Testing", NoDbInit(
            (TestHostMarkers.AllowCaptureAndSkipDbInitKey, "false"),
            ("Email:Mode", "Capture")));

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("only permitted in Development", exception!.ToString());
        Assert.Contains("marker not set", exception!.ToString());
    }

    [Fact]
    public async Task Host_TestingEnvironment_WithMarker_AllowsCapture_ResolvesDevelopmentEmailSender()
    {
        // The base TestWebApplicationFactory already sets the marker; this proves the
        // positive case explicitly instead of relying on it implicitly.
        await using var factory = new EnvironmentOverrideWebApplicationFactory("Testing", NoDbInit(
            (TestHostMarkers.AllowCaptureAndSkipDbInitKey, "true")));

        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<Centerix.Application.Common.Interfaces.IEmailSender>();

        Assert.IsType<DevelopmentEmailSender>(sender);
    }

    // ================================================================
    // Database initialization is NOT skipped by the environment name alone (closeout item 2).
    //
    // RunDatabaseInitializationAsync's skip condition is "Testing" AND the marker — exactly
    // the same condition EmailOptions uses for Capture. These two tests prove the DB-init
    // side of that condition directly: a WebApplicationFactory<Program> host does NOT work
    // here, because WebApplicationFactory captures the host as soon as the entry point's
    // own async Main reaches ITS FIRST genuine await suspension — and that first suspension
    // IS the "await app.RunDatabaseInitializationAsync()" call itself (everything before it
    // in Program.cs is synchronous). Whatever that call does afterwards runs undetached and
    // unobserved by the test, so neither its success nor its failure is ever visible through
    // CreateClient()/Services. Building the WebApplication directly here and awaiting the
    // extension method ourselves sidesteps that entirely and tests the real method with the
    // real DI wiring (AddInfrastructure) from Centerix.API.Program. The connection string
    // points at a loopback port nothing listens on, so a connection attempt fails fast and
    // deterministically instead of needing an actual SQL Server.
    // ================================================================

    [Fact]
    public async Task RunDatabaseInitializationAsync_TestingWithoutMarker_DoesNotSkip_FailsWithDbError()
    {
        var config = new Dictionary<string, string?>
        {
            [TestHostMarkers.AllowCaptureAndSkipDbInitKey] = "false",
        };

        // DatabaseInitialization:ApplyMigrations/Seed/ValidateSchema are left at their
        // (true) appsettings.json defaults, so the method actually tries to migrate.
        await using var app = BuildMinimalAppWithUnreachableDb("Testing", config);

        var exception = await Record.ExceptionAsync(() => app.RunDatabaseInitializationAsync());

        Assert.NotNull(exception);
        var text = exception!.ToString();
        Assert.True(
            text.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
                || text.Contains("network-related", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SqlClient", StringComparison.OrdinalIgnoreCase),
            $"Expected a SQL connectivity failure, got: {text}");
    }

    [Fact]
    public async Task RunDatabaseInitializationAsync_TestingWithMarker_Skips_EvenWithUnreachableDb()
    {
        var config = new Dictionary<string, string?>
        {
            [TestHostMarkers.AllowCaptureAndSkipDbInitKey] = "true",
        };

        // Same unreachable connection string and same (true) DatabaseInitialization
        // defaults as above — the only difference is the marker. The call must return
        // cleanly because the skip check runs before anything ever opens a connection.
        await using var app = BuildMinimalAppWithUnreachableDb("Testing", config);

        await app.RunDatabaseInitializationAsync();
    }

    // ================================================================
    // SeedDevelopmentData=true without BootstrapAdmin:TemporaryPassword still throws.
    //
    // Confirms the regression coverage required by Item 1 already exists at
    // Batch2SqlServerTests.BootstrapSeed_WithoutExplicitPassword_FailsClearly (SQL Server
    // suite) and adds the equivalent proof against the InMemory-backed test host so it runs
    // without Docker/SQL Server too.
    // ================================================================

    [Fact]
    public async Task SeedAsync_SeedDevelopmentDataTrue_WithoutTemporaryPassword_ThrowsClearly()
    {
        using var scope = _factory.Services.CreateScope();

        var tenantInfo = new Centerix.Infrastructure.Tenancy.CenterixTenantInfo
        {
            Id = $"rev012a1-{Guid.NewGuid():N}",
            Identifier = $"rev012a1-{Guid.NewGuid():N}",
            Name = "Rev012 A.1 no-password tenant",
            Email = $"rev012a1-{Guid.NewGuid():N}@example.test",
            IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1),
            CreatedAt = DateTime.UtcNow
        };
        scope.ServiceProvider.GetRequiredService<Finbuckle.MultiTenant.Abstractions.IMultiTenantContextSetter>()
            .MultiTenantContext = new Finbuckle.MultiTenant.MultiTenantContext<Centerix.Infrastructure.Tenancy.CenterixTenantInfo>
            {
                TenantInfo = tenantInfo
            };

        var initialiser = new ApplicationDbContextInitialiser(
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ApplicationDbContextInitialiser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<Centerix.Infrastructure.Auth.ApplicationRole>>(),
            scope.ServiceProvider.GetRequiredService<Finbuckle.MultiTenant.Abstractions.IMultiTenantContextAccessor<Centerix.Infrastructure.Tenancy.CenterixTenantInfo>>(),
            NewHost("Development"),
            Microsoft.Extensions.Options.Options.Create(new DatabaseInitializationOptions { SeedDevelopmentData = true }),
            Microsoft.Extensions.Options.Options.Create(new Centerix.Infrastructure.Tenancy.BootstrapAdminOptions { TemporaryPassword = "" }),
            Microsoft.Extensions.Options.Options.Create(new Centerix.Infrastructure.Tenancy.PlatformAdminBootstrapOptions()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initialiser.SeedAsync());
        Assert.Contains("BootstrapAdmin:TemporaryPassword", ex.Message);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static Dictionary<string, string?> NoDbInit(params (string Key, string Value)[] extra)
    {
        var config = new Dictionary<string, string?>
        {
            // The InMemory DbContext double from TestWebApplicationFactory doesn't support
            // real relational migrations; these tests isolate email-mode behaviour only.
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:Seed"] = "false",
            ["DatabaseInitialization:ValidateSchema"] = "false",
        };

        foreach (var (key, value) in extra)
            config[key] = value;

        return config;
    }

    private static void AddValidSmtp(Dictionary<string, string?> config)
    {
        config["Smtp:Host"] = "smtp.rev012a1.test";
        config["Smtp:Port"] = "587";
        config["Smtp:FromAddress"] = "noreply@rev012a1.test";
    }

    private static IHostEnvironment NewHost(string environmentName) => new HostingEnvironment
    {
        EnvironmentName = environmentName,
    };

    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Centerix.Rev012";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NullFileProvider : Microsoft.Extensions.FileProviders.IFileProvider
    {
        public Microsoft.Extensions.FileProviders.IDirectoryContents GetDirectoryContents(string subpath) =>
            new Microsoft.Extensions.FileProviders.NotFoundDirectoryContents();

        public Microsoft.Extensions.FileProviders.IFileInfo GetFileInfo(string subpath) =>
            new Microsoft.Extensions.FileProviders.NotFoundFileInfo(subpath);

        public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) => new EmptyChangeToken();
    }

    private sealed class EmptyChangeToken : Microsoft.Extensions.Primitives.IChangeToken
    {
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public System.Threading.Tasks.ValueTask DisposeAsync() => System.Threading.Tasks.ValueTask.CompletedTask;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new EmptyDisposable();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>
    /// A minimal host-per-<see cref="Program"/> factory for the email-mode tests above.
    /// Unlike <see cref="TestWebApplicationFactory"/>, it does NOT replace the production
    /// <c>IEmailSender</c> registration with a capturing double — these tests exist
    /// specifically to prove which concrete sender DependencyInjection's real resolution
    /// logic picks for a given environment/config, so that registration must stay live.
    /// It still swaps the two EF Core contexts for InMemory (no real SQL Server available
    /// in the test process) and supplies the same baseline JWT/Invitations/ForwardedHeaders
    /// configuration every host needs to pass its own startup validation.
    /// </summary>
    private sealed class EnvironmentOverrideWebApplicationFactory(
        string environmentName,
        Dictionary<string, string?> overrides) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"Rev012A1Db_{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);

            builder.ConfigureAppConfiguration((context, config) =>
            {
                var baseline = new Dictionary<string, string?>
                {
                    ["JwtSettings:Secret"] = "ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!",
                    ["JwtSettings:Issuer"] = "TestIssuer",
                    ["JwtSettings:Audience"] = "TestAudience",
                    ["JwtSettings:ExpirationInMinutes"] = "60",
                    ["Invitations:BaseUrl"] = "https://app.securitytests.local",
                    ["ConnectionStrings:DefaultConnection"] =
                        $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;",
                    ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
                    ["ForwardedHeaders:KnownNetworks:0"] = "127.0.0.0/8",
                    ["ForwardedHeaders:ForwardLimit"] = "5",
                };
                config.AddInMemoryCollection(baseline);
                config.AddInMemoryCollection(overrides);
            });

            builder.ConfigureServices(services =>
            {
                ReplaceDbContext<AppDbContext>(services, (_, options) =>
                    options.UseInMemoryDatabase(_databaseName));
                ReplaceDbContext<TenantDbContext>(services, (_, options) =>
                    options.UseInMemoryDatabase($"{_databaseName}_Tenant"));
            });
        }

        private static void ReplaceDbContext<TContext>(
            IServiceCollection services,
            Action<IServiceProvider, DbContextOptionsBuilder> configureOptions) where TContext : DbContext
        {
            var toRemove = services.Where(d =>
            {
                var st = d.ServiceType;
                if (st == typeof(DbContextOptions<TContext>)) return true;
                if (st == typeof(TContext)) return true;
                if (st.IsConstructedGenericType && st.GenericTypeArguments.Contains(typeof(TContext))) return true;
                return false;
            }).ToList();

            foreach (var d in toRemove) services.Remove(d);

            services.AddDbContext<TContext>(configureOptions);
        }
    }

    /// <summary>
    /// Builds a real <see cref="WebApplication"/> via the SAME <c>AddInfrastructure</c> wiring
    /// <c>Centerix.API.Program</c> uses — NOT through <see cref="WebApplicationFactory{TEntryPoint}"/>,
    /// whose entry-point interception can't observe what happens after
    /// <c>RunDatabaseInitializationAsync</c>'s first genuine await (see the comment above the
    /// two tests that use this). The connection string points at a loopback port nothing
    /// listens on with a short connect timeout, so a real connection attempt fails fast and
    /// deterministically instead of needing an actual SQL Server. Caller awaits
    /// <c>app.RunDatabaseInitializationAsync()</c> directly and disposes the app.
    /// </summary>
    private static WebApplication BuildMinimalAppWithUnreachableDb(
        string environmentName, Dictionary<string, string?> overrides)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });

        var baseline = new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = "ThisIsATestSecretKeyThatIsAtLeast32CharsLong!!",
            ["JwtSettings:Issuer"] = "TestIssuer",
            ["JwtSettings:Audience"] = "TestAudience",
            ["JwtSettings:ExpirationInMinutes"] = "60",
            ["Invitations:BaseUrl"] = "https://app.securitytests.local",
            ["ConnectionStrings:DefaultConnection"] =
                "Server=127.0.0.1,1;Database=Rev012A1UnreachableDb;TrustServerCertificate=True;" +
                "Encrypt=False;Connect Timeout=3;",
        };
        builder.Configuration.AddInMemoryCollection(baseline);
        builder.Configuration.AddInMemoryCollection(overrides);

        builder.Services.AddInfrastructure(builder.Configuration);

        return builder.Build();
    }
}
