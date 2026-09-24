using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 20.2 — JWT secret closure verification.
///
/// Verifies the startup validation contract required after removing all committed
/// JWT signing secrets from git-tracked configuration:
///   1. The host REFUSES to start when the secret is missing (null — the exact
///      production default in appsettings.json and appsettings.Development.json).
///   2. The host REFUSES to start when the secret is present but too short
///      (&lt; 32 chars) — an invalid secret must not be accepted either.
///
/// Validation is enforced by JwtSettings.Validate() wired through
/// AddOptions&lt;JwtSettings&gt;().Validate(...).ValidateOnStart() in
/// Centerix.Infrastructure.DependencyInjection, so the failure must surface
/// during host startup — before the server accepts any request.
/// </summary>
public class JwtSecretStartupValidationTests
{
    private sealed class InvalidSecretWebApplicationFactory(string? secret) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Registered AFTER the base factory's configuration, so this overrides
            // the valid test secret with the invalid value under test. The "Testing"
            // environment never loads User Secrets, mirroring a production host that
            // lacks the secret environment variable.
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Secret"] = secret
                });
            });
        }
    }

    [Fact]
    public async Task Host_RefusesStartup_WhenJwtSecretMissing()
    {
        await using var factory = new InvalidSecretWebApplicationFactory(null);

        var exception = await Record.ExceptionAsync(() =>
            Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("JWT Secret is not configured", exception.ToString());
    }

    [Fact]
    public async Task Host_RefusesStartup_WhenJwtSecretTooShort()
    {
        await using var factory = new InvalidSecretWebApplicationFactory("short-secret");

        var exception = await Record.ExceptionAsync(() =>
            Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
        Assert.Contains("JWT Secret must be at least 32 characters", exception.ToString());
    }

    [Fact]
    public async Task Host_Starts_WithValidSecret_FromExternalConfiguration()
    {
        // Control case: the same factory with an externally supplied (in-memory,
        // standing in for env var / User Secrets / secret store) 32+ char secret
        // starts successfully. Proves the refusal above is caused by the missing
        // secret, not by the test host wiring itself.
        await using var factory = new InvalidSecretWebApplicationFactory(
            "ExternalSecretValueThatIsDefinitelyAtLeast32CharsLong!");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
