using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Login user-enumeration hardening.
/// <para>
/// <c>/api/auth/login</c> answered an unknown email immediately but ran a PBKDF2 verification for a
/// known one. The gap is tens of milliseconds - trivially measurable over the network - so the
/// endpoint doubled as an oracle for "is this address registered?".
/// </para>
/// <para>
/// The invariants under test: a miss and a wrong-password hit are indistinguishable by status and
/// body, and a miss costs the same order of time as a hit (it performs the same password hashing
/// work rather than short-circuiting).
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class LoginEnumerationHardeningTests
{
    private const string StrongPassword = "Str0ng!Pass1";
    private const int SamplesPerBranch = 6;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SqlServerIntegrationFactory _env;

    public LoginEnumerationHardeningTests(SqlServerIntegrationFactory env) => _env = env;

    /// <summary>
    /// Deterministic: unknown email and wrong password must be byte-for-byte indistinguishable.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task UnknownEmail_And_WrongPassword_ReturnTheSameStatusAndBody()
    {
        var knownEmail = UniqueEmail("known");
        await CreateUserAsync(knownEmail);
        var unknownEmail = UniqueEmail("unknown");

        var miss = await SendLoginAsync(unknownEmail, StrongPassword);
        var wrongPassword = await SendLoginAsync(knownEmail, "definitely-not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, miss.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(wrongPassword.Body, miss.Body);
    }

    /// <summary>
    /// Timing: a miss must now cost the same order of magnitude as a wrong-password hit, because it
    /// runs the same PBKDF2 verification instead of returning early. A 0.5 floor still fails loudly
    /// (the un-fixed endpoint scores roughly 0.1-0.25) while tolerating normal host jitter.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task UnknownEmail_IsNotMeasurablyFasterThan_AWrongPassword()
    {
        var knownEmail = UniqueEmail("timing");
        await CreateUserAsync(knownEmail);
        var unknownEmail = UniqueEmail("timing");

        // Warm-up: pay JIT/startup costs before either branch is measured.
        await SendLoginAsync(unknownEmail, StrongPassword);
        await SendLoginAsync(knownEmail, "definitely-not-the-password");

        var missDurations = new List<double>();
        var hitDurations = new List<double>();

        // Interleave the two branches so CPU/thermal drift affects both equally.
        for (var i = 0; i < SamplesPerBranch; i++)
        {
            missDurations.Add(await TimeAsync(unknownEmail, StrongPassword));
            hitDurations.Add(await TimeAsync(knownEmail, "definitely-not-the-password"));
        }

        var missAverage = missDurations.Average();
        var hitAverage = hitDurations.Average();

        Assert.True(
            missAverage >= hitAverage * 0.5,
            $"An unknown email answered in {missAverage:F1}ms on average while a known email with a " +
            $"wrong password took {hitAverage:F1}ms. A miss must not be measurably faster: the " +
            "timing gap is what lets an attacker enumerate registered addresses.");
    }

    /// <summary>
    /// The extra hashing on the miss path must never turn into a rejection - the endpoint still
    /// behaves like an endpoint, not like a fixed-delay decoy that also rate-limits itself.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task MissPath_StillReturnsTheStandardInvalidCredentialsPayload()
    {
        var response = await SendLoginAsync(UniqueEmail("payload"), StrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.Body, JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload!.ContainsKey("error"), $"Response body was: {response.Body}");
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@enum.test";

    /// <summary>Distinct per-call client IP so the 5/minute login ceiling can never throttle a sample.</summary>
    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
    }

    private static HttpRequestMessage LoginRequest(string email, string password, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendLoginAsync(string email, string password)
    {
        using var response = await _env.Client.SendAsync(
            LoginRequest(email, password, RemoteIp()));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<double> TimeAsync(string email, string password)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await _env.Client.SendAsync(
            LoginRequest(email, password, RemoteIp()));
        await response.Content.ReadAsStringAsync();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private async Task CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, password);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));

        // Prove the row landed (and gives the query path something real to hit).
        using var verify = _env.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.Users.SingleOrDefaultAsync(u => u.Id == user.Id));
    }
}
