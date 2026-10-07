using System.Net;
using System.Text;
using System.Text.Json;
using Centerix.API.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// F1 — forwarded-header trust: X-Forwarded-For must never be client-authoritative for the
/// rate-limit partition key.
/// <para>
/// The login limiter partitions on the connection's remote address. Before the fix,
/// <c>ForwardedHeadersMiddleware</c> (a) could adopt a client-controlled X-Forwarded-For value
/// from a request with NO socket peer even when trust lists were configured, and (b) defaulted
/// to trusting only loopback while the limiter fell back to a shared "unknown" bucket for
/// peer-less requests. Together these let one client pick its own partition, escape the
/// 5/minute ceiling, and poison other clients' buckets.
/// </para>
/// <para>
/// Test-host plumbing: <see cref="TestRemoteIpStartupFilter"/> maps <c>X-Test-Remote-IP</c> onto
/// <c>Connection.RemoteIpAddress</c> (TestServer otherwise leaves it null). The trusted edge
/// proxies 192.0.2.1/192.0.2.2 (TEST-NET-1) are declared in the factory's forwarded-header
/// configuration; every other address used here is intentionally untrusted.
/// </para>
/// <para>
/// This class owns its factory instance (IClassFixture), hence its own rate limiter, so bucket
/// arithmetic below can never race another test class. All logins use unknown e-mail addresses:
/// the response inside the ceiling is always the uniform 401, and no account state exists to
/// disturb.
/// </para>
/// </summary>
public class ForwardedHeadersRateLimitHardeningTests : IClassFixture<TestWebApplicationFactory>
{
    private const int LoginCeiling = 5;

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ForwardedHeadersRateLimitHardeningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Baseline: a request with no socket peer and no forwarded header lands in the shared
    /// "unknown" bucket and is still capped at the policy's ceiling (5/minute).
    /// <para>
    /// Order-independent: the shared unknown bucket belongs to this fixture as a whole, so only
    /// the FIRST of the two peer-less tests to run gets a fresh bucket. The fresh-bucket
    /// arithmetic therefore asserts strictly when statuses[0] proves freshness; the ceiling
    /// assertion itself is unconditional.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoPeerNoForwardedHeader_UsesSharedUnknownBucket_UpToTheCeiling()
    {
        var email = UnknownEmail();

        var statuses = await SendLoginsAsync(LoginCeiling + 1, _ => LoginAsync(email));

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        if (statuses[0] == HttpStatusCode.Unauthorized)
        {
            Assert.True(
                statuses.Take(LoginCeiling).All(s => s == HttpStatusCode.Unauthorized),
                $"Requests inside the ceiling must reach the handler. Got: {string.Join(", ", statuses.Select(s => (int)s))}");
        }
    }

    /// <summary>
    /// A trusted proxy's X-Forwarded-For IS adopted: the partition becomes the forwarded client,
    /// not the proxy. Proof by exhaustion: after 6 requests carrying XFF claim C, a request whose
    /// OWN socket peer is C (no header) is throttled — it can only be in C's bucket if C was the
    /// partition for the first six (if forwarding were ignored, the proxy's bucket would be the
    /// exhausted one and this probe would get 401).
    /// </summary>
    [Fact]
    public async Task TrustedProxy_XForwardedForClient_IsTheRateLimitPartition()
    {
        var email = UnknownEmail();
        const string clientIp = "203.0.113.77";

        var statuses = await SendLoginsAsync(
            LoginCeiling + 1, _ => LoginAsync(email, peer: "127.0.0.1", forwardedFor: clientIp));

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        var probe = await LoginAsync(email, peer: clientIp);
        Assert.Equal(HttpStatusCode.TooManyRequests, probe);
    }

    /// <summary>
    /// An UNTRUSTED peer's X-Forwarded-For is ignored: the partition stays the real socket peer.
    /// Two directions:
    /// (a) exhausting a spoofing peer's bucket does not exhaust the claimed client's bucket;
    /// (b) exhausting a real client's bucket does not let a spoofing peer (claiming that client)
    /// land in it — the spoofing request gets 401 from its own fresh bucket, not 429.
    /// </summary>
    [Fact]
    public async Task UntrustedPeer_CannotClaimAnotherClientsPartition()
    {
        var email = UnknownEmail();

        // (a) Spoofing peer 203.0.113.50 claims 198.51.100.10 — all six stay in 203.0.113.50's bucket.
        var spoofingStatuses = await SendLoginsAsync(
            LoginCeiling + 1,
            _ => LoginAsync(email, peer: "203.0.113.50", forwardedFor: "198.51.100.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofingStatuses[^1]);

        var claimBucketProbe = await LoginAsync(email, peer: "198.51.100.10");
        Assert.Equal(HttpStatusCode.Unauthorized, claimBucketProbe);

        // (b) Exhaust a REAL client bucket first, then a peer claiming that client must be ignored.
        var realClientStatuses = await SendLoginsAsync(
            LoginCeiling + 1, _ => LoginAsync(email, peer: "198.51.100.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, realClientStatuses[^1]);

        var spoofIntoExhausted = await LoginAsync(
            email, peer: "203.0.113.60", forwardedFor: "198.51.100.9");
        Assert.Equal(HttpStatusCode.Unauthorized, spoofIntoExhausted);
    }

    /// <summary>
    /// Two different clients behind the SAME trusted proxy get separate partitions. If the
    /// forwarded client were ignored (partition = proxy), client B would inherit client A's
    /// exhausted bucket and be throttled.
    /// </summary>
    [Fact]
    public async Task SameProxy_TwoClients_RemainSeparatePartitions()
    {
        var email = UnknownEmail();

        var clientAStatuses = await SendLoginsAsync(
            LoginCeiling + 1,
            _ => LoginAsync(email, peer: "127.0.0.1", forwardedFor: "203.0.113.61"));
        Assert.Equal(HttpStatusCode.TooManyRequests, clientAStatuses[^1]);

        var clientB = await LoginAsync(
            email, peer: "127.0.0.1", forwardedFor: "203.0.113.62");
        Assert.Equal(HttpStatusCode.Unauthorized, clientB);
    }

    /// <summary>
    /// A request with NO socket peer must NOT have its X-Forwarded-For adopted (the shipped
    /// runtime adopts it even with non-empty trust lists — there is no peer to validate against).
    /// Proof: the six header-carrying, peer-less requests exhaust the SHARED bucket, and a
    /// subsequent request whose own peer is the claimed client is still 401 — if the claim had
    /// been adopted, that bucket would already be exhausted and the probe would be 429.
    /// </summary>
    [Fact]
    public async Task NullPeer_XForwardedFor_IsStrippedNotAdopted()
    {
        var email = UnknownEmail();
        const string claimedClient = "198.51.100.99";

        var statuses = await SendLoginsAsync(
            LoginCeiling + 1, _ => LoginAsync(email, peer: null, forwardedFor: claimedClient));

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        if (statuses[0] == HttpStatusCode.Unauthorized)
        {
            Assert.True(
                statuses.Take(LoginCeiling).All(s => s == HttpStatusCode.Unauthorized),
                $"Peer-less requests share the unknown bucket and must reach the handler. Got: {string.Join(", ", statuses.Select(s => (int)s))}");
        }

        var probe = await LoginAsync(email, peer: claimedClient);
        Assert.Equal(HttpStatusCode.Unauthorized, probe);
    }

    /// <summary>
    /// Multi-hop chain with ForwardLimit = 5: peer 192.0.2.2 (trusted) forwards
    /// "198.51.100.7, 192.0.2.1" — 192.0.2.1 is trusted and skipped, so the partition is the
    /// ORIGINATING client 198.51.100.7, not the last hop.
    /// Probes: the client's own bucket IS exhausted (429 → resolution reached the client) while
    /// the proxy peer's bucket is untouched (401 → resolution did not stop at the first hop,
    /// which is what a ForwardLimit of 1 would produce).
    /// </summary>
    [Fact]
    public async Task MultiHopChain_ResolvesToTheOriginatingClient_NotTheLastHop()
    {
        var email = UnknownEmail();
        const string chain = "198.51.100.7, 192.0.2.1";

        var statuses = await SendLoginsAsync(
            LoginCeiling + 1, _ => LoginAsync(email, peer: "192.0.2.2", forwardedFor: chain));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        var clientProbe = await LoginAsync(email, peer: "198.51.100.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, clientProbe);

        var proxyProbe = await LoginAsync(email, peer: "192.0.2.2");
        Assert.Equal(HttpStatusCode.Unauthorized, proxyProbe);
    }

    // ==================================================================
    // ClientIp normalization (partition-key canonicalization)
    // ==================================================================

    [Fact]
    public void ClientIp_Normalize_NullStaysNull()
    {
        Assert.Null(ClientIp.Normalize(null));
    }

    [Fact]
    public void ClientIp_Normalize_CanonicalizesIpv4MappedAddresses()
    {
        var plain = ClientIp.Normalize(IPAddress.Parse("1.2.3.4"));
        var mapped = ClientIp.Normalize(IPAddress.Parse("::ffff:1.2.3.4"));

        Assert.Equal("1.2.3.4", plain);
        Assert.Equal(plain, mapped);
    }

    [Fact]
    public void ClientIp_ForRateLimit_FallsBackToUnknown_OnlyWithoutAPeer()
    {
        var bare = new DefaultHttpContext();
        Assert.Equal("unknown", ClientIp.ForRateLimit(bare));

        bare.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:9.9.9.9");
        Assert.Equal("9.9.9.9", ClientIp.ForRateLimit(bare));
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>Unknown address per call: the uniform 401 inside the ceiling, with no account state.</summary>
    private static string UnknownEmail() => $"f1_{Guid.NewGuid():N}@f1.test";

    private async Task<HttpStatusCode> LoginAsync(
        string email,
        string? peer = null,
        string? forwardedFor = null)
    {
        using var response = await _client.SendAsync(LoginRequest(email, peer, forwardedFor));
        return response.StatusCode;
    }

    private async Task<List<HttpStatusCode>> SendLoginsAsync(
        int count,
        Func<int, Task<HttpStatusCode>> send)
    {
        var statuses = new List<HttpStatusCode>(count);
        for (var i = 0; i < count; i++)
        {
            statuses.Add(await send(i));
        }

        return statuses;
    }

    private static HttpRequestMessage LoginRequest(
        string email,
        string? peer,
        string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        if (peer is not null)
        {
            request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, peer);
        }

        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, password = "Str0ng!Pass1" }),
            Encoding.UTF8, "application/json");
        return request;
    }
}
