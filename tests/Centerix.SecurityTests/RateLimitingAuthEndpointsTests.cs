using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Rate-limit coverage for the two ANONYMOUS endpoints that were previously unlimited.
/// <para>
/// Only <c>/api/auth/login</c> was rate limited. <c>/api/auth/refresh</c> and
/// <c>/api/invitations/register</c> are both anonymous and both take an attacker-supplied
/// credential, so they were the remaining unauthenticated CPU/DB amplification paths - and the
/// only two a caller could hammer without ever holding a session.
/// </para>
/// <para>
/// Every request in these tests carries its own remote IP, so the assertions are about a single
/// partition and can never be disturbed by (or disturb) the shared "unknown" partition other tests
/// fall back to when they send no client-IP header.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class RateLimitingAuthEndpointsTests
{
    private const int RefreshCeiling = 30;
    private const int RegisterCeiling = 60;

    private readonly SqlServerIntegrationFactory _env;

    public RateLimitingAuthEndpointsTests(SqlServerIntegrationFactory env) => _env = env;

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Refresh_BeyondItsCeiling_IsRejectedWith429()
    {
        var remoteIp = RemoteIp();

        var statuses = await SendAsync(
            count: RefreshCeiling + 1,
            send: _ => _env.Client.SendAsync(RefreshRequest(BogusRefreshToken(), remoteIp)));

        Assert.True(
            statuses[0] == HttpStatusCode.Unauthorized,
            "The first refresh inside the ceiling must reach the handler and fail normally, " +
            $"not be throttled (got {(int)statuses[0]}).");

        Assert.True(
            statuses[^1] == HttpStatusCode.TooManyRequests,
            $"The {RefreshCeiling + 1}th refresh from one source within a minute must be throttled " +
            $"(got {(int)statuses[^1]}).");

        Assert.False(
            statuses.Take(RefreshCeiling).Contains(HttpStatusCode.TooManyRequests),
            "Requests inside the ceiling must never be throttled.");
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task InvitationRegister_BeyondItsCeiling_IsRejectedWith429()
    {
        var remoteIp = RemoteIp();

        var statuses = await SendAsync(
            count: RegisterCeiling + 1,
            send: _ => _env.Client.SendAsync(RegisterRequest(BogusInvitationToken(), remoteIp)));

        Assert.True(
            statuses[0] == HttpStatusCode.Unauthorized,
            "The first registration attempt inside the ceiling must reach the handler " +
            $"(got {(int)statuses[0]}).");

        Assert.True(
            statuses[^1] == HttpStatusCode.TooManyRequests,
            $"The {RegisterCeiling + 1}th registration attempt from one source within a minute " +
            $"must be throttled (got {(int)statuses[^1]}).");

        Assert.False(
            statuses.Take(RegisterCeiling).Contains(HttpStatusCode.TooManyRequests),
            "Requests inside the ceiling must never be throttled.");
    }

    /// <summary>
    /// The ceiling is per source: throttling one IP must not stop a different source from using
    /// the same endpoint. This is what keeps the limit an abuse control and not an outage switch.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ThrottlingOneSource_DoesNotAffectAnotherSource()
    {
        var throttledIp = RemoteIp();
        var otherIp = RemoteIp();

        await SendAsync(
            count: RefreshCeiling + 1,
            send: _ => _env.Client.SendAsync(RefreshRequest(BogusRefreshToken(), throttledIp)));

        var blocked = await _env.Client.SendAsync(RefreshRequest(BogusRefreshToken(), throttledIp));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        var unaffected = await _env.Client.SendAsync(RefreshRequest(BogusRefreshToken(), otherIp));
        Assert.Equal(HttpStatusCode.Unauthorized, unaffected.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
    }

    private static string BogusRefreshToken() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Guid.NewGuid().ToString("N");

    private static string BogusInvitationToken() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Guid.NewGuid().ToString("N");

    private static HttpRequestMessage RefreshRequest(string refreshToken, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage RegisterRequest(string token, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations/register");
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { token, password = "Str0ng!Pass1" }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<List<HttpStatusCode>> SendAsync(
        int count,
        Func<int, Task<HttpResponseMessage>> send)
    {
        var statuses = new List<HttpStatusCode>(count);
        for (var i = 0; i < count; i++)
        {
            using var response = await send(i);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }
}
