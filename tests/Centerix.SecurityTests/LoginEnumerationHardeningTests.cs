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
    // F2 — lock-state disclosure (uniform 401 for locked accounts)
    // ==================================================================

    /// <summary>
    /// A locked account must be indistinguishable from an unknown address: same status, same
    /// bytes. The previous implementation answered 429 with a localized "account locked" body and
    /// a <c>lockoutRemainingMinutes</c> count — enough to confirm the address exists, that it is
    /// locked, and how close the threshold is.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task LockedAccount_CorrectAndWrongPassword_AreIndistinguishableFromUnknownEmail()
    {
        var lockedEmail = UniqueEmail("locked");
        await CreateUserAsync(lockedEmail);
        await LockAsync(lockedEmail);

        var unknownEmail = UniqueEmail("unknown");

        var lockedCorrect = await SendLoginAsync(lockedEmail, StrongPassword);
        var lockedWrong = await SendLoginAsync(lockedEmail, "definitely-not-the-password");
        var miss = await SendLoginAsync(unknownEmail, StrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, lockedCorrect.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedWrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, miss.StatusCode);

        Assert.Equal(miss.Body, lockedCorrect.Body);
        Assert.Equal(miss.Body, lockedWrong.Body);
        Assert.DoesNotContain("lockout", lockedCorrect.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remaining", lockedCorrect.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hammering an already-locked account must not EXTEND its lock. The failure branch (which
    /// calls AccessFailedAsync) must sit behind the lockout gate, so wrong passwords against a
    /// locked account never touch the failed-attempt counter or the lockout clock.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task AttemptsAgainstALockedAccount_DoNotExtendTheLockout()
    {
        var lockedEmail = UniqueEmail("extend");
        await CreateUserAsync(lockedEmail);
        await LockAsync(lockedEmail);

        var (lockoutEndBefore, failuresBefore) = await ReadLockoutStateAsync(lockedEmail);

        for (var i = 0; i < 3; i++)
        {
            var attempt = await SendLoginAsync(lockedEmail, "definitely-not-the-password");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var (lockoutEndAfter, failuresAfter) = await ReadLockoutStateAsync(lockedEmail);

        Assert.Equal(lockoutEndBefore, lockoutEndAfter);
        Assert.Equal(failuresBefore, failuresAfter);
    }

    /// <summary>
    /// Timing: a locked account (known email, gate denies) must not be measurably faster than an
    /// unknown email. Both branches run the same PBKDF2 verification before answering, so the
    /// lockout gate cannot be used as a registration oracle. Floor 0.33 with 10 interleaved
    /// samples per branch — tighter floors flip on CI jitter, and the un-fixed gap (a
    /// short-circuited miss vs. no hash at all) was an order of magnitude, not 3%.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task LockedAccount_IsNotMeasurablyFasterThan_UnknownEmail()
    {
        var lockedEmail = UniqueEmail("timinglock");
        await CreateUserAsync(lockedEmail);
        await LockAsync(lockedEmail);
        var unknownEmail = UniqueEmail("timinglock");

        // Warm-up: pay JIT/startup costs before either branch is measured.
        await SendLoginAsync(lockedEmail, StrongPassword);
        await SendLoginAsync(unknownEmail, StrongPassword);

        var lockedDurations = new List<double>();
        var missDurations = new List<double>();

        for (var i = 0; i < 10; i++)
        {
            missDurations.Add(await TimeAsync(unknownEmail, StrongPassword));
            lockedDurations.Add(await TimeAsync(lockedEmail, StrongPassword));
        }

        var lockedMedian = Median(lockedDurations);
        var missMedian = Median(missDurations);

        Assert.True(
            lockedMedian >= missMedian * 0.33,
            $"A locked account answered in {lockedMedian:F1}ms (median) while an unknown email took " +
            $"{missMedian:F1}ms. The lockout gate must not short-circuit the password verification.");
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

    // F2 helpers: every helper re-fetches the user inside its own scope (UserManager attaches
    // the instance you hand it; sharing a detached instance across DbContexts is undefined).

    private async Task LockAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.SetLockoutEnabledAsync(user!, true)).Succeeded);
        Assert.True((await userManager.SetLockoutEndDateAsync(
            user!, DateTimeOffset.UtcNow.AddMinutes(15))).Succeeded);
    }

    private async Task<(DateTimeOffset? LockoutEnd, int Failures)> ReadLockoutStateAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        var lockoutEnd = await userManager.GetLockoutEndDateAsync(user!);
        var failures = await userManager.GetAccessFailedCountAsync(user!);
        return (lockoutEnd, failures);
    }

    private static double Median(List<double> samples)
    {
        var ordered = samples.OrderBy(s => s).ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }
}
