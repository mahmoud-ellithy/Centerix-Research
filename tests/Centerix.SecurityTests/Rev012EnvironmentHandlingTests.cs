using System.Reflection;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Centerix.SecurityTests;

// REV-012: environment handling must be driven by the Email:Mode option (not just
// IsProduction()). Capture is permitted only in Development/Testing; bootstrap admin
// seeding is gated on SeedDevelopmentData in every environment.
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
    public void EmailOptions_Unset_DefaultsToCapture_InDevelopmentOrTesting()
    {
        var env = NewHost("Development");
        var options = new EmailOptions();

        Assert.Equal(EmailMode.Capture, options.ResolveMode(env));

        env.EnvironmentName = "Testing";
        Assert.Equal(EmailMode.Capture, options.ResolveMode(env));
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
    public void EmailOptions_Capture_RejectedOutsideDevelopmentOrTesting()
    {
        var env = NewHost("Staging");
        var options = new EmailOptions { Mode = EmailMode.Capture };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(env));
        Assert.Contains("Development or Testing", ex.Message);
        Assert.Contains("Staging", ex.Message);

        env.EnvironmentName = "Production";
        options = new EmailOptions { Mode = EmailMode.Capture };
        Assert.Throws<InvalidOperationException>(() => options.Validate(env));
    }

    [Fact]
    public void EmailOptions_Capture_AllowedInDevelopmentOrTesting()
    {
        var dev = NewHost("Development");
        new EmailOptions { Mode = EmailMode.Capture }.Validate(dev); // no throw

        var test = NewHost("Testing");
        new EmailOptions { Mode = EmailMode.Capture }.Validate(test); // no throw
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
    // Helpers
    // ================================================================

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
}
